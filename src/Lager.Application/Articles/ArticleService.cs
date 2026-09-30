using Lager.Application.Abstractions;
using Lager.Contracts.Articles;
using Lager.Domain.Articles;

namespace Lager.Application.Articles;

/// <summary>
/// Artikelstamm: Lesen (samt Suche und Scan-Auflösung), Anlegen, Ändern.
///
/// Regeln beim Speichern (fachliche Fehler tragen einen Code in <c>Data["code"]</c>, siehe ExceptionProblemMapper):
///  - SKU und GTIN sind eindeutig, unabhängig von Groß-/Kleinschreibung und Leerraum: 409. Die doppelte GTIN trägt den Code
///    <c>duplicate_gtin</c>; die doppelte SKU behält den Standardcode <c>conflict</c> (der WP12-Test der Fehlerabbildung legt ihn
///    fest). Die GTIN wird vorher geprüft (Ziffern, Länge, Prüfziffer): 400 <c>invalid_gtin</c>.
///  - Bundle-Komponenten: jede Komponente höchstens einmal (400 <c>duplicate_bundle_component</c>), muss existieren
///    (400 <c>unknown_bundle_component</c>); Selbstreferenz und Zyklen über beliebig viele Ebenen werden abgelehnt
///    (409 <c>bundle_cycle</c>), ebenso eine Verschachtelung tiefer als beim Kommissionieren/Packen unterstützt
///    (409 <c>bundle_too_deep</c>).
/// </summary>
public class ArticleService
{
    /// <summary>Wie tief Bundles ineinander liegen dürfen: dieselbe Grenze wie beim Auflösen in Picken, Packen und Retouren.</summary>
    private const int MaxBundleDepth = 5;

    private readonly IArticleRepository _articles;
    private readonly IUnitOfWork _uow;

    public ArticleService(IArticleRepository articles, IUnitOfWork uow)
    {
        _articles = articles;
        _uow = uow;
    }

    /// <summary>
    /// Alle Artikel, mit <paramref name="search"/> nur die, in deren Name, SKU, Alternativ-SKU oder GTIN der Suchtext
    /// vorkommt (Teilstring, Groß-/Kleinschreibung egal). Weiter ohne Paging.
    /// </summary>
    public async Task<IReadOnlyList<ArticleDto>> ListAsync(string? search = null, CancellationToken ct = default)
    {
        var items = await _articles.ListAsync(ct);
        if (!string.IsNullOrWhiteSpace(search))
            items = items.Where(a => a.MatchesSearch(search)).ToList();
        return await ToDtosAsync(items, ct);
    }

    public async Task<ArticleDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var article = await _articles.GetAsync(id, ct);
        return article is null ? null : await ToDtoAsync(article, ct);
    }

    /// <summary>
    /// Löst einen gescannten oder eingegebenen Code auf Artikel auf. Zuerst zählt die Identität: die SKU (exakt, Groß-/Kleinschreibung
    /// und Leerraum am Rand egal) und die GTIN (auch in gleichwertiger Länge, ein UPC-A als EAN-13 mit führender Null).
    /// Nur wenn dort nichts passt, gilt eine Alternativ-SKU. So verdrängt die Alternativ-SKU eines Artikels nie den Artikel, dessen
    /// SKU sie ist. Ergebnis: leer (unbekannt), ein Artikel (eindeutig) oder mehrere (mehrdeutig, z. B. dieselbe Alternativ-SKU an
    /// zwei Artikeln oder eine SKU, die zugleich die GTIN eines anderen Artikels ist).
    /// </summary>
    public async Task<IReadOnlyList<ArticleDto>> ResolveByCodeAsync(string? code, CancellationToken ct = default)
    {
        var text = code?.Trim();
        if (string.IsNullOrEmpty(text)) return Array.Empty<ArticleDto>();

        var matches = new List<Article>();
        void AddDistinct(IEnumerable<Article> found)
        {
            foreach (var article in found)
                if (matches.All(m => m.Id != article.Id)) matches.Add(article);
        }

        if (await _articles.GetBySkuAsync(text, ct) is { } bySku) AddDistinct(new[] { bySku });
        if (Gtin.IsValid(text)) AddDistinct(await _articles.FindByGtinAsync(text, ct));
        if (matches.Count == 0) AddDistinct(await _articles.FindByAlternativeSkuAsync(text, ct));

        return await ToDtosAsync(matches, ct);
    }

    public async Task<ArticleDto> CreateAsync(CreateArticleRequest request, CancellationToken ct = default)
    {
        // Getrimmt prüfen UND speichern: der Article-Konstruktor trimmt ebenfalls, die Vorprüfung darf ihm nicht vorausgehen
        // (" ABC" umging sonst die Prüfung und scheiterte erst am Unique-Index als 500).
        var sku = request.Sku?.Trim() ?? string.Empty;
        if (sku.Length > 0 && await _articles.GetBySkuAsync(sku, ct) is not null)
            throw new InvalidOperationException($"Ein Artikel mit der SKU '{sku}' existiert bereits");

        var gtin = await CheckedGtinAsync(request.Gtin, exceptArticleId: null, ct);

        var article = new Article(
            sku,
            request.Name,
            ArticleMapper.ToDimensions(request.Dimensions),
            request.WeightGrams,
            ArticleMapper.ToStacking(request.Stacking),
            request.Description);
        article.SetStockThresholds(request.MinStock, request.ReorderPoint, request.MaxStock);
        article.SetPurchasing(request.PrimarySupplierId, request.PurchasePriceCents);
        article.SetAlternatives(request.AlternativeSkus ?? Array.Empty<string>());
        article.SetSeasonWindow(request.ValidFrom, request.ValidUntil);
        if (gtin is not null) article.SetGtin(gtin);
        if (request.BundleComponents is not null)
            await ReplaceBundleComponentsAsync(article, request.BundleComponents, ct);

        await _articles.AddAsync(article, ct);
        await _uow.SaveChangesAsync(ct);
        return await ToDtoAsync(article, ct);
    }

    public async Task<ArticleDto?> UpdateAsync(Guid id, UpdateArticleRequest request, CancellationToken ct = default)
    {
        var article = await _articles.GetAsync(id, ct);
        if (article is null) return null;

        // GTIN zuerst: schlägt sie fehl, ist noch nichts am Artikel verändert.
        string? newGtin = article.Gtin;
        var gtinChanges = false;
        if (request.Gtin is not null)
        {
            // Nur bei einer Änderung erneut prüfen (Eindeutigkeit); der gespeicherte Wert ist schon gültig.
            var requested = Gtin.Normalize(request.Gtin);
            if (!string.Equals(requested, article.Gtin, StringComparison.Ordinal))
            {
                newGtin = await CheckedGtinAsync(request.Gtin, article.Id, ct);
                gtinChanges = true;
            }
        }

        article.Update(
            request.Name,
            request.Description,
            ArticleMapper.ToDimensions(request.Dimensions),
            request.WeightGrams,
            ArticleMapper.ToStacking(request.Stacking));
        article.SetStockThresholds(request.MinStock, request.ReorderPoint, request.MaxStock);
        article.SetPurchasing(request.PrimarySupplierId, request.PurchasePriceCents);
        article.SetAlternatives(request.AlternativeSkus ?? Array.Empty<string>());
        article.SetSeasonWindow(request.ValidFrom, request.ValidUntil);
        if (gtinChanges) article.SetGtin(newGtin);
        if (request.BundleComponents is not null)
            await ReplaceBundleComponentsAsync(article, request.BundleComponents, ct);

        await _uow.SaveChangesAsync(ct);
        return await ToDtoAsync(article, ct);
    }

    // ---- GTIN -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Normalisiert und prüft die GTIN eines Requests und stellt sicher, dass kein ANDERER Artikel sie trägt (auch nicht in
    /// gleichwertiger Länge). Kein Wert ergibt null (= keine GTIN).
    /// </summary>
    private async Task<string?> CheckedGtinAsync(string? requested, Guid? exceptArticleId, CancellationToken ct)
    {
        var gtin = Gtin.Normalize(requested);
        if (gtin is null) return null;

        var error = Gtin.GetError(gtin);
        if (error is not null) throw Invalid("invalid_gtin", error);

        var owner = (await _articles.FindByGtinAsync(gtin, ct)).FirstOrDefault(a => a.Id != exceptArticleId);
        if (owner is not null)
            throw Rule("duplicate_gtin", $"Die GTIN {gtin} ist bereits dem Artikel '{owner.Sku}' zugeordnet");
        return gtin;
    }

    // ---- Bundle ---------------------------------------------------------------------------------------------

    /// <summary>Prüft die Komponenten (siehe Klassenkommentar) und ersetzt sie erst danach: bei einem Fehler bleibt der Artikel unverändert.</summary>
    private async Task ReplaceBundleComponentsAsync(Article article, IReadOnlyList<CreateBundleComponentRequest> requested, CancellationToken ct)
    {
        var components = requested.Select(c => (c.ComponentArticleId, c.Quantity)).ToList();
        await EnsureBundleIsValidAsync(article, components, ct);
        article.ReplaceBundleComponents(components);
    }

    private async Task EnsureBundleIsValidAsync(Article article, IReadOnlyList<(Guid ComponentArticleId, int Quantity)> components, CancellationToken ct)
    {
        if (components.Count == 0) return; // leere Liste = Bundle auflösen

        var repeated = components.GroupBy(c => c.ComponentArticleId).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (repeated.Count > 0)
            throw Invalid("duplicate_bundle_component",
                "Jede Bundle-Komponente darf höchstens einmal vorkommen (mehrfach: " + string.Join(", ", repeated) + "). Bitte die Mengen zusammenfassen.");

        if (components.Any(c => c.ComponentArticleId == article.Id))
            throw Rule("bundle_cycle", $"Ein Bundle darf sich nicht selbst enthalten ({article.Sku})");

        var direct = await _articles.GetManyAsync(components.Select(c => c.ComponentArticleId), ct);
        var unknown = components.Select(c => c.ComponentArticleId).Where(id => !direct.ContainsKey(id)).ToList();
        if (unknown.Count > 0)
            throw Invalid("unknown_bundle_component", "Unbekannte Bundle-Komponente(n): " + string.Join(", ", unknown));

        // Alle darunter liegenden Bundles ebenenweise laden (mit Obergrenze: Altdaten mit Zyklen dürfen nicht endlos laden).
        var known = new Dictionary<Guid, Article>(direct);
        var pending = NextLevel(direct.Values, known);
        for (var level = 0; pending.Count > 0 && level <= MaxBundleDepth; level++)
        {
            var loaded = await _articles.GetManyAsync(pending, ct);
            foreach (var (id, loadedArticle) in loaded) known[id] = loadedArticle;
            pending = NextLevel(loaded.Values, known);
        }

        var path = new List<string> { article.Sku };
        var deepest = 0;
        foreach (var (componentId, _) in components)
            deepest = Math.Max(deepest, BundleDepth(componentId, article, known, path));

        if (1 + deepest > MaxBundleDepth)
            throw Rule("bundle_too_deep",
                $"Bundles dürfen höchstens {MaxBundleDepth} Ebenen tief ineinander liegen; {article.Sku} käme auf {1 + deepest}");
    }

    /// <summary>Komponenten-Ids der Bundles unter <paramref name="loaded"/>, die noch nicht in <paramref name="known"/> sind.</summary>
    private static HashSet<Guid> NextLevel(IEnumerable<Article> loaded, IReadOnlyDictionary<Guid, Article> known) =>
        loaded.Where(a => a.IsBundle)
            .SelectMany(a => a.BundleComponents)
            .Select(c => c.ComponentArticleId)
            .Where(id => !known.ContainsKey(id))
            .ToHashSet();

    /// <summary>
    /// Anzahl der Bundle-Ebenen ab <paramref name="articleId"/> (0 = normaler Artikel, 1 = Bundle aus normalen Artikeln ...).
    /// Erreicht der Abstieg den geänderten Artikel selbst, entsteht ein Zyklus; trifft er unterwegs zweimal denselben Artikel
    /// (Altdaten), ist auch das ein Zyklus. <paramref name="path"/> sammelt die SKUs für die Meldung.
    /// </summary>
    private static int BundleDepth(Guid articleId, Article edited, IReadOnlyDictionary<Guid, Article> known, List<string> path)
    {
        if (articleId == edited.Id)
            throw Rule("bundle_cycle", $"Zyklus: {string.Join(" → ", path)} → {edited.Sku}");
        if (!known.TryGetValue(articleId, out var article) || !article.IsBundle) return 0;
        if (path.Contains(article.Sku, StringComparer.OrdinalIgnoreCase))
            throw Rule("bundle_cycle", $"Zyklus in den Bundle-Komponenten: {string.Join(" → ", path)} → {article.Sku}");

        path.Add(article.Sku);
        var deepest = 0;
        foreach (var component in article.BundleComponents)
            deepest = Math.Max(deepest, BundleDepth(component.ComponentArticleId, edited, known, path));
        path.RemoveAt(path.Count - 1);
        return 1 + deepest;
    }

    // ---- DTOs -----------------------------------------------------------------------------------------------

    private async Task<ArticleDto> ToDtoAsync(Article article, CancellationToken ct) =>
        (await ToDtosAsync(new[] { article }, ct))[0];

    private async Task<IReadOnlyList<ArticleDto>> ToDtosAsync(IReadOnlyList<Article> items, CancellationToken ct)
    {
        var skus = await ComponentSkusAsync(items, ct);
        var now = DateTime.UtcNow;
        return items.Select(a => ArticleMapper.ToDto(a, skus, now)).ToList();
    }

    /// <summary>SKU je Komponenten-Id aller Bundles in <paramref name="items"/>; bekannte Artikel aus der Liste selbst, der Rest per Abfrage.</summary>
    private async Task<IReadOnlyDictionary<Guid, string>> ComponentSkusAsync(IReadOnlyList<Article> items, CancellationToken ct)
    {
        var wanted = items.SelectMany(a => a.BundleComponents).Select(c => c.ComponentArticleId).ToHashSet();
        if (wanted.Count == 0) return new Dictionary<Guid, string>();

        var skus = new Dictionary<Guid, string>();
        foreach (var article in items)
            if (wanted.Contains(article.Id)) skus[article.Id] = article.Sku;

        var missing = wanted.Where(id => !skus.ContainsKey(id)).ToList();
        if (missing.Count > 0)
            foreach (var (id, article) in await _articles.GetManyAsync(missing, ct))
                skus[id] = article.Sku;
        return skus;
    }

    // ---- Fehler mit Code ------------------------------------------------------------------------------------

    /// <summary>Regelverstoß gegen den vorhandenen Bestand an Daten (409).</summary>
    private static InvalidOperationException Rule(string code, string message)
    {
        var ex = new InvalidOperationException(message);
        ex.Data["code"] = code;
        return ex;
    }

    /// <summary>Ungültige Eingabe (400).</summary>
    private static ArgumentException Invalid(string code, string message)
    {
        var ex = new ArgumentException(message);
        ex.Data["code"] = code;
        return ex;
    }
}
