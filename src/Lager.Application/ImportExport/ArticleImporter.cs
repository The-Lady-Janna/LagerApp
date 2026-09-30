using Lager.Application.Abstractions;
using Lager.Application.Articles;
using Lager.Contracts.Articles;
using Lager.Domain.Articles;

namespace Lager.Application.ImportExport;

/// <summary>Ein Artikel, der angelegt (<c>Create</c>) oder geändert (<c>Update</c>) werden soll.</summary>
internal sealed record ArticleChange(int Line, string Key, Guid? ArticleId, CreateArticleRequest? Create, UpdateArticleRequest? Update)
    : PlannedChange(Line, Key);

/// <summary>
/// Artikelimport: Upsert je SKU, idempotent.
///  - Kennung ist die SKU (ohne Beachtung der Schreibweise). Gibt es sie, werden die Felder der Datei übernommen; Felder, deren
///    SPALTE in der Datei fehlt, bleiben unverändert (eine Preisliste mit nur "Sku;PurchasePrice" löscht keine Beschreibungen).
///    Eine vorhandene, aber leere Zelle setzt das Feld zurück (GTIN entfernen, Saison-Fenster aufheben ...).
///  - Stimmt der Artikel schon mit der Datei überein, passiert nichts (kein Speichern, kein Audit, "unverändert"): ein zweiter
///    Import derselben Datei ändert also nichts, auch GTIN, Alternativ-SKUs und Saison-Fenster nicht.
///  - Geschrieben wird über <see cref="ArticleService"/> (<c>CreateAsync</c>/<c>UpdateAsync</c>) mit denselben Regeln wie über die API. Weil
///    der Dienst die Validatoren der API nicht kennt, prüft dieser Import deren Grenzen selbst (Längen, Bereiche, Reihenfolge der
///    Schwellen), bevor er etwas schreibt. Eine GTIN wird mit <see cref="Gtin"/> geprüft (Ziffern, Länge, Prüfziffer) und auf
///    Eindeutigkeit - gegen die Datenbank und gegen die übrigen Zeilen der Datei.
///  - Bundle-Komponenten stehen nicht in der Datei: neue Artikel sind keine Bundles, vorhandene behalten ihre Komponenten.
/// </summary>
internal sealed class ArticleImporter : IImportHandler
{
    // Grenzen der Request-Validatoren der API (Lager.Api/Validation: CreateArticleRequestValidator, ValidationLimits).
    private const int MaxSku = 64;
    private const int MaxName = 256;
    private const int MaxDescription = 2000;
    private const int MaxDimensionMm = 1_000_000;
    private const int MaxWeightGrams = 100_000_000;
    private const int MaxStockLevel = 100_000_000;
    private const int MaxStackQuantity = 1_000_000;
    private const int MaxAlternativeSkus = 500;
    private const int MaxAlternativeSkuLength = 64;
    private const int MaxAlternativeSkusStored = 1000;

    private static readonly char[] ListSeparators = { '|', ',', ';' };

    private readonly ArticleService _articles;
    private readonly IArticleRepository _repository;
    private readonly ISupplierRepository _suppliers;

    public ArticleImporter(ArticleService articles, IArticleRepository repository, ISupplierRepository suppliers)
    {
        _articles = articles;
        _repository = repository;
        _suppliers = suppliers;
    }

    /// <summary>Alle Felder eines Artikels, die die Datei bestimmt - zum Vergleichen (Record-Gleichheit) und zum Bauen der Anfrage.</summary>
    private sealed record State(
        string Name, string? Description, int Length, int Width, int Height, int WeightGrams,
        bool IsStackable, StackingAxis Axis, int IncrementMm, int? MaxStackCount,
        int MinStock, int ReorderPoint, int MaxStock, int PriceCents, Guid? SupplierId,
        string? AlternativeCsv, DateTime? ValidFrom, DateTime? ValidUntil, string? Gtin)
    {
        /// <summary>Werte eines neuen Artikels, dessen Spalte in der Datei fehlt (wie <see cref="StackingInfo.NotStackable"/>).</summary>
        public static readonly State Defaults = new(
            string.Empty, null, 0, 0, 0, 0, false, StackingAxis.Z, 0, 1, 0, 0, 0, 0, null, null, null, null, null);

        public static State Of(Article a) => new(
            a.Name, Blank(a.Description), a.Dimensions.LengthMm, a.Dimensions.WidthMm, a.Dimensions.HeightMm, a.WeightGrams,
            a.Stacking.IsStackable, a.Stacking.StackingAxis, a.Stacking.StackingIncrementMm, a.Stacking.MaxStackCount,
            a.MinStock, a.ReorderPoint, a.MaxStock, a.PurchasePriceCents, a.PrimarySupplierId,
            Blank(a.AlternativeSkusCsv), Seconds(a.ValidFrom), Seconds(a.ValidUntil), a.Gtin);
    }

    private sealed record Columns(
        int Sku, int Name, int Description, int Gtin, int Length, int Width, int Height, int Weight,
        int IsStackable, int Axis, int Increment, int MaxStack, int Min, int Reorder, int Max,
        int Price, int Supplier, int AlternativeSkus, int ValidFrom, int ValidUntil)
    {
        public static Columns Of(CsvTable t) => new(
            ImportHeaders.Require(t, "Sku"), t.IndexOf("Name"), t.IndexOf("Description"), t.IndexOf("Gtin"),
            t.IndexOf("LengthMm"), t.IndexOf("WidthMm"), t.IndexOf("HeightMm"), t.IndexOf("WeightGrams"),
            t.IndexOf("IsStackable"), t.IndexOf("StackingAxis"), t.IndexOf("StackingIncrementMm"), t.IndexOf("MaxStackCount"),
            t.IndexOf("MinStock"), t.IndexOf("ReorderPoint"), t.IndexOf("MaxStock"),
            t.IndexOf("PurchasePrice"), t.IndexOf("SupplierCode"), t.IndexOf("AlternativeSkus"), t.IndexOf("ValidFrom"), t.IndexOf("ValidUntil"));
    }

    public async Task<ImportPlan> PlanAsync(CsvTable table, CancellationToken ct)
    {
        var cols = Columns.Of(table);

        var existing = await _repository.ListAsync(ct);
        var bySku = new Dictionary<string, Article>(StringComparer.OrdinalIgnoreCase);
        var gtinOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var article in existing)
        {
            bySku.TryAdd(article.Sku, article);
            if (article.Gtin is { } g) gtinOwners.TryAdd(GtinKey(g), article.Sku);
        }

        var supplierIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var supplier in await _suppliers.ListAsync(includeInactive: true, ct))
            supplierIds.TryAdd(supplier.Code, supplier.Id);

        var changes = new List<PlannedChange>();
        var errors = new List<ImportRowError>();
        var seenSkus = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fileGtins = new Dictionary<string, (string Sku, int Line)>(StringComparer.Ordinal);
        var problems = new ProblemCollector();
        int created = 0, updated = 0, unchanged = 0;

        foreach (var row in table.Rows)
        {
            problems.Clear();
            if (table.OverflowProblem(row) is { } overflow)
            {
                errors.Add(new ImportRowError(row.Line, null, "too_many_columns", overflow));
                continue;
            }

            var reader = new RowReader(row, problems);
            var sku = reader.Text(cols.Sku);
            if (sku.Length == 0)
            {
                errors.Add(new ImportRowError(row.Line, null, "sku_missing", "Die SKU fehlt."));
                continue;
            }
            if (sku.Length > MaxSku)
            {
                errors.Add(new ImportRowError(row.Line, RowReader.Shorten(sku), "sku_too_long", $"Die SKU darf höchstens {MaxSku} Zeichen lang sein."));
                continue;
            }
            if (seenSkus.TryGetValue(sku, out var firstLine))
            {
                errors.Add(new ImportRowError(row.Line, sku, "duplicate_sku_in_file", $"Die SKU '{sku}' steht schon in Zeile {firstLine}."));
                continue;
            }
            seenSkus[sku] = row.Line;

            bySku.TryGetValue(sku, out var current);
            var baseline = current is null ? State.Defaults : State.Of(current);
            var desired = Read(reader, cols, baseline, supplierIds, problems);

            if (problems.Any)
            {
                errors.Add(problems.ToError(row.Line, sku));
                continue;
            }

            // Schon so vorhanden: nichts zu tun und nichts zu prüfen (auch Altdaten mit Maßen 0 bleiben so ein "unverändert").
            if (current is not null && desired == baseline)
            {
                unchanged++;
                if (desired.Gtin is { } own) fileGtins.TryAdd(GtinKey(own), (sku, row.Line));
                continue;
            }

            Validate(sku, desired, current is null, gtinOwners, fileGtins, row.Line, problems);
            if (problems.Any)
            {
                errors.Add(problems.ToError(row.Line, sku));
                continue;
            }
            if (desired.Gtin is { } gtin) fileGtins[GtinKey(gtin)] = (sku, row.Line);

            if (current is null)
            {
                changes.Add(new ArticleChange(row.Line, sku, null, ToCreate(sku, desired), null));
                created++;
            }
            else
            {
                changes.Add(new ArticleChange(row.Line, sku, current.Id, null, ToUpdate(desired, baseline)));
                updated++;
            }
        }

        return new ImportPlan(table.Rows.Count, changes, created, updated, unchanged, errors,
            ImportHeaders.Warnings(table, CsvColumns.Articles));
    }

    public async Task ApplyAsync(IReadOnlyList<PlannedChange> changes, ApplyContext context, CancellationToken ct)
    {
        for (var i = 0; i < changes.Count; i++)
        {
            var change = (ArticleChange)changes[i];
            if (i == changes.Count - 1) context.BeforeLastChange();

            if (change.Create is not null)
            {
                await _articles.CreateAsync(change.Create, ct);
            }
            else if (await _articles.UpdateAsync(change.ArticleId!.Value, change.Update!, ct) is null)
            {
                // Zwischen Prüfung und Übernahme gelöscht: die ganze Übernahme scheitert (und wird zurückgerollt).
                var ex = new InvalidOperationException($"Der Artikel '{change.Key}' (Zeile {change.Line}) wurde zwischenzeitlich gelöscht.");
                ex.Data["code"] = "import_article_vanished";
                throw ex;
            }

            if ((i + 1) % 100 == 0) context.Transaction.ReleaseTrackedEntities();
        }
    }

    // ---- Zeile lesen ----------------------------------------------------------------------------------------

    /// <summary>Der gewünschte Stand: die Werte der Datei über dem bisherigen Stand (<paramref name="baseline"/>; fehlende Spalten ändern nichts).</summary>
    private static State Read(RowReader r, Columns c, State baseline, IReadOnlyDictionary<string, Guid> supplierIds, ProblemCollector problems)
    {
        var name = c.Name >= 0 ? r.Text(c.Name) : baseline.Name;
        var description = c.Description >= 0 ? Blank(r.Text(c.Description)) : baseline.Description;
        var gtin = c.Gtin >= 0 ? Gtin.Normalize(r.Text(c.Gtin)) : baseline.Gtin;

        var axis = baseline.Axis;
        if (c.Axis >= 0)
        {
            var text = r.Text(c.Axis);
            if (text.Length == 0) axis = StackingAxis.Z;
            else if (!Enum.TryParse(text, ignoreCase: true, out axis) || !Enum.IsDefined(axis))
            {
                problems.Add("invalid_axis", $"StackingAxis: '{RowReader.Shorten(text)}' ist ungültig (erlaubt: X, Y, Z).");
                axis = baseline.Axis;
            }
        }

        var supplierId = baseline.SupplierId;
        if (c.Supplier >= 0)
        {
            var code = r.Text(c.Supplier);
            if (code.Length == 0) supplierId = null;
            else if (supplierIds.TryGetValue(code, out var id)) supplierId = id;
            else problems.Add("unknown_supplier", $"SupplierCode: Es gibt keinen Lieferanten '{RowReader.Shorten(code)}'.");
        }

        var alternatives = c.AlternativeSkus >= 0 ? NormalizeAlternatives(r.Text(c.AlternativeSkus)) : baseline.AlternativeCsv;

        return new State(
            name, description,
            r.Int(c.Length, "LengthMm", baseline.Length), r.Int(c.Width, "WidthMm", baseline.Width), r.Int(c.Height, "HeightMm", baseline.Height),
            r.Int(c.Weight, "WeightGrams", baseline.WeightGrams),
            r.Bool(c.IsStackable, "IsStackable", baseline.IsStackable), axis,
            r.Int(c.Increment, "StackingIncrementMm", baseline.IncrementMm), r.NullableInt(c.MaxStack, "MaxStackCount", baseline.MaxStackCount),
            r.Int(c.Min, "MinStock", baseline.MinStock), r.Int(c.Reorder, "ReorderPoint", baseline.ReorderPoint), r.Int(c.Max, "MaxStock", baseline.MaxStock),
            r.Cents(c.Price, "PurchasePrice", baseline.PriceCents), supplierId,
            alternatives, Seconds(r.Date(c.ValidFrom, "ValidFrom", baseline.ValidFrom)), Seconds(r.Date(c.ValidUntil, "ValidUntil", baseline.ValidUntil)),
            gtin);
    }

    // ---- Regeln der API (Validatoren) -----------------------------------------------------------------------

    private static void Validate(
        string sku, State d, bool isNew, IReadOnlyDictionary<string, string> gtinOwners,
        IReadOnlyDictionary<string, (string Sku, int Line)> fileGtins, int line, ProblemCollector problems)
    {
        if (d.Name.Length == 0) problems.Add("name_missing", isNew ? "Der Name fehlt (für einen neuen Artikel Pflicht)." : "Der Name darf nicht leer sein.");
        else if (d.Name.Length > MaxName) problems.Add("name_too_long", $"Der Name darf höchstens {MaxName} Zeichen lang sein.");
        if (d.Description is { Length: > MaxDescription }) problems.Add("description_too_long", $"Die Beschreibung darf höchstens {MaxDescription} Zeichen lang sein.");

        foreach (var (label, value) in new[] { ("LengthMm", d.Length), ("WidthMm", d.Width), ("HeightMm", d.Height) })
            if (value is < 1 or > MaxDimensionMm)
                problems.Add("invalid_dimension", isNew && value == 0
                    ? $"{label} fehlt (ein neuer Artikel braucht Länge, Breite und Höhe in mm, größer als 0)."
                    : $"{label} muss zwischen 1 und {MaxDimensionMm} mm liegen.");

        if (d.WeightGrams is < 0 or > MaxWeightGrams) problems.Add("invalid_weight", $"WeightGrams muss zwischen 0 und {MaxWeightGrams} liegen.");
        if (d.IncrementMm is < 0 or > MaxDimensionMm) problems.Add("invalid_stacking", $"StackingIncrementMm muss zwischen 0 und {MaxDimensionMm} liegen.");
        if (d.MaxStackCount is < 1 or > MaxStackQuantity) problems.Add("invalid_stacking", $"MaxStackCount muss zwischen 1 und {MaxStackQuantity} liegen, wenn gesetzt.");

        foreach (var (label, value) in new[] { ("MinStock", d.MinStock), ("ReorderPoint", d.ReorderPoint), ("MaxStock", d.MaxStock) })
            if (value is < 0 or > MaxStockLevel) problems.Add("invalid_stock_level", $"{label} muss zwischen 0 und {MaxStockLevel} liegen.");
        if (d.ReorderPoint > 0 && d.MinStock > d.ReorderPoint) problems.Add("invalid_stock_level", "ReorderPoint darf nicht kleiner als MinStock sein.");
        if (d.MaxStock > 0 && d.ReorderPoint > d.MaxStock) problems.Add("invalid_stock_level", "MaxStock darf nicht kleiner als ReorderPoint sein.");
        if (d.MaxStock > 0 && d.MinStock > d.MaxStock) problems.Add("invalid_stock_level", "MaxStock darf nicht kleiner als MinStock sein.");

        if (d.ValidFrom is { } from && d.ValidUntil is { } until && until.Date < from.Date)
            problems.Add("invalid_season", "ValidUntil muss am oder nach ValidFrom liegen.");

        ValidateAlternatives(sku, d.AlternativeCsv, problems);

        if (d.Gtin is { } gtin)
        {
            if (Gtin.GetError(gtin) is { } error)
            {
                problems.Add("invalid_gtin", error);
            }
            else
            {
                var key = GtinKey(gtin);
                if (gtinOwners.TryGetValue(key, out var owner) && !owner.Equals(sku, StringComparison.OrdinalIgnoreCase))
                    problems.Add("duplicate_gtin", $"Die GTIN {gtin} ist bereits dem Artikel '{owner}' zugeordnet.");
                else if (fileGtins.TryGetValue(key, out var other) && !other.Sku.Equals(sku, StringComparison.OrdinalIgnoreCase))
                    problems.Add("duplicate_gtin_in_file", $"Die GTIN {gtin} steht schon in Zeile {other.Line} (Artikel '{other.Sku}').");
            }
        }
    }

    private static void ValidateAlternatives(string sku, string? csv, ProblemCollector problems)
    {
        if (csv is null) return;
        var list = csv.Split(',');
        if (list.Length > MaxAlternativeSkus) problems.Add("invalid_alternative_skus", $"AlternativeSkus: höchstens {MaxAlternativeSkus} Einträge.");
        if (list.Any(s => s.Length > MaxAlternativeSkuLength)) problems.Add("invalid_alternative_skus", $"AlternativeSkus: jeder Eintrag darf höchstens {MaxAlternativeSkuLength} Zeichen lang sein.");
        if (csv.Length > MaxAlternativeSkusStored) problems.Add("invalid_alternative_skus", $"AlternativeSkus: zusammen höchstens {MaxAlternativeSkusStored} Zeichen.");
        if (list.Any(s => s.Equals(sku, StringComparison.OrdinalIgnoreCase))) problems.Add("invalid_alternative_skus", "AlternativeSkus: ein Artikel kann nicht seine eigene Alternative sein.");
    }

    // ---- Anfragen bauen -------------------------------------------------------------------------------------

    private static string[] AlternativeArray(string? csv) => csv?.Split(',') ?? Array.Empty<string>();

    private static CreateArticleRequest ToCreate(string sku, State d) => new(
        sku, d.Name, d.Description, new DimensionsDto(d.Length, d.Width, d.Height), d.WeightGrams,
        new StackingInfoDto(d.IsStackable, d.Axis.ToString(), d.IncrementMm, d.MaxStackCount),
        d.MinStock, d.ReorderPoint, d.MaxStock, d.SupplierId, d.PriceCents, AlternativeArray(d.AlternativeCsv),
        d.ValidFrom, d.ValidUntil, BundleComponents: null, Gtin: d.Gtin);

    /// <summary>Gtin: unverändert = null (der Dienst lässt sie dann in Ruhe), entfernt = "" (leer), sonst der neue Wert.</summary>
    private static UpdateArticleRequest ToUpdate(State d, State current) => new(
        d.Name, d.Description, new DimensionsDto(d.Length, d.Width, d.Height), d.WeightGrams,
        new StackingInfoDto(d.IsStackable, d.Axis.ToString(), d.IncrementMm, d.MaxStackCount),
        d.MinStock, d.ReorderPoint, d.MaxStock, d.SupplierId, d.PriceCents, AlternativeArray(d.AlternativeCsv),
        d.ValidFrom, d.ValidUntil, BundleComponents: null, Gtin: d.Gtin == current.Gtin ? null : d.Gtin ?? string.Empty);

    // ---- Hilfen ---------------------------------------------------------------------------------------------

    /// <summary>Gleichwertige Schreibweisen derselben GTIN (UPC-A/EAN-13/GTIN-14) haben denselben Kern ohne führende Nullen.</summary>
    private static string GtinKey(string gtin) => gtin.TrimStart('0');

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>Auf Sekunden gekürzt: der Export schreibt Sekunden, ein Wert mit Bruchteilen soll nicht als Änderung gelten.</summary>
    private static DateTime? Seconds(DateTime? value) =>
        value is null ? null : new DateTime(value.Value.Ticks - value.Value.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    /// <summary>Wie <c>Article.SetAlternatives</c>: getrimmt, ohne Leereinträge und Duplikate, mit Komma verbunden; leer = null.</summary>
    private static string? NormalizeAlternatives(string text)
    {
        var list = text.Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return list.Count == 0 ? null : string.Join(",", list);
    }
}
