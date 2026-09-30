using Lager.Application.Abstractions;
using Lager.Application.Stock;
using Lager.Contracts.Returns;
using Lager.Domain.Articles;
using Lager.Domain.Orders;
using Lager.Domain.Returns;
using Lager.Domain.Stock;

namespace Lager.Application.Returns;

/// <summary>
/// RMA-Flow: anlegen → QC pro Zeile → Process. Die QC-Entscheidung steuert die Buchung beim Abschluss (alles über
/// <see cref="StockBooking"/>, Charge/MHD bleiben erhalten):
///  - Sellable (A-Ware): zurück in den Bestand des Ziel-Lagerplatzes (Movement Return).
///  - BGrade (B-Ware): Sperrbuchung (Movement ReturnB) - kein Zugang zum Verkaufsbestand.
///  - Defect / Destroy: Ausschussbuchung (Movement ReturnScrap) - kein Zugang zum Verkaufsbestand.
/// Mit Bestellbezug wird gegen die Bestellung geprüft: nur Artikel der Bestellung (Bundles in ihre Komponenten
/// aufgelöst), höchstens die gelieferte Menge abzüglich früherer, nicht stornierter Retouren.
/// </summary>
public class ReturnService
{
    private const int MaxBundleDepth = 5;

    private readonly IReturnRepository _repo;
    private readonly IArticleRepository _articles;
    private readonly IStockRepository _stock;
    private readonly IStockMovementRepository _movements;
    private readonly IOrderRepository _orders;
    private readonly IWarehouseRepository _warehouse;
    private readonly IUnitOfWork _uow;

    public ReturnService(IReturnRepository repo, IArticleRepository articles, IStockRepository stock,
        IStockMovementRepository movements, IOrderRepository orders, IWarehouseRepository warehouse, IUnitOfWork uow)
    {
        _repo = repo;
        _articles = articles;
        _stock = stock;
        _movements = movements;
        _orders = orders;
        _warehouse = warehouse;
        _uow = uow;
    }

    public async Task<IReadOnlyList<ReturnShipmentDto>> ListAsync(CancellationToken ct = default) =>
        (await _repo.ListAsync(ct)).Select(ToDto).ToList();

    public async Task<ReturnShipmentDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var r = await _repo.GetWithLinesAsync(id, ct);
        return r is null ? null : ToDto(r);
    }

    public async Task<ReturnShipmentDto> CreateAsync(CreateReturnShipmentRequest req, CancellationToken ct = default)
    {
        var lines = req.Lines ?? Array.Empty<CreateReturnLineRequest>();

        // Erst alles prüfen, dann die Nummer ziehen: eine abgelehnte Retoure verbraucht keine RMA-Nummer.
        var articleMap = await _articles.GetManyAsync(lines.Select(l => l.ArticleId).Distinct(), ct);
        var built = new List<ReturnLine>();
        foreach (var l in lines)
        {
            if (!articleMap.TryGetValue(l.ArticleId, out var article))
                throw StockErrors.NotFound("article_not_found", $"Artikel {l.ArticleId} nicht gefunden");
            EnsureNotBundle(article);
            built.Add(new ReturnLine(article.Id, article.Sku, l.Quantity, l.LotNumber));
        }

        if (req.OrderId is Guid orderId)
            await EnsureWithinDeliveredAsync(orderId, built, alreadyCounted: null, ct);

        var seq = await _repo.NextSequenceAsync(ct);
        var rma = $"RMA-{DateTime.UtcNow:yyyyMMdd}-{seq:D5}";
        var ret = new ReturnShipment(rma, req.OrderId, req.CustomerReference, req.Notes);
        foreach (var l in built)
            ret.AddLine(l.ArticleId, l.ArticleSku, l.Quantity, l.LotNumber);

        await _repo.AddAsync(ret, ct);
        await _uow.SaveChangesAsync(ct);
        return ToDto(ret);
    }

    public async Task<ReturnShipmentDto?> AddLineAsync(Guid id, AddReturnLineRequest req, CancellationToken ct = default)
    {
        var ret = await _repo.GetWithLinesAsync(id, ct);
        if (ret is null) return null;
        var article = await _articles.GetAsync(req.ArticleId, ct)
            ?? throw StockErrors.NotFound("article_not_found", "Artikel nicht gefunden");
        EnsureNotBundle(article);
        ret.AddLine(article.Id, article.Sku, req.Quantity, req.LotNumber);

        // Prüfung nach dem Anlegen: die Menge zählt mit allen Zeilen dieser Retoure (die frühere Fassung dieser
        // Retoure steht nicht doppelt in den "früheren Retouren", siehe alreadyCounted).
        if (ret.OrderId is Guid orderId)
            await EnsureWithinDeliveredAsync(orderId, ret.Lines, alreadyCounted: ret.Id, ct);

        await _uow.SaveChangesAsync(ct);
        return ToDto(ret);
    }

    public async Task<ReturnShipmentDto?> SetQcAsync(Guid id, Guid lineId, SetQcRequest req, CancellationToken ct = default)
    {
        var ret = await _repo.GetWithLinesAsync(id, ct);
        if (ret is null) return null;
        var result = ParseQcResult(req.Result);
        if (req.TargetBinId is Guid binId && await _warehouse.GetStorageLocationAsync(binId, ct) is null)
            throw StockErrors.NotFound("bin_not_found", "Lagerplatz nicht gefunden");
        ret.SetLineQc(lineId, result, req.TargetBinId, req.Notes);
        await _uow.SaveChangesAsync(ct);
        return ToDto(ret);
    }

    /// <summary>
    /// QC-Ergebnis nur als benannter Wert (Pending, Sellable, BGrade, Defect, Destroy; Groß-/Kleinschreibung egal).
    /// Zahlen wie "99" akzeptiert <see cref="Enum.TryParse{TEnum}(string, bool, out TEnum)"/> stillschweigend und würde
    /// einen undefinierten Wert ergeben, der die Zeile aus dem Prozess nimmt - hier bewusst abgelehnt.
    /// </summary>
    public static QcResult ParseQcResult(string? value) =>
        TryParseQcResult(value, out var result)
            ? result
            : throw StockErrors.Invalid("invalid_qc_result",
                $"Unbekanntes QC-Ergebnis: {value} (erlaubt: {string.Join(", ", Enum.GetNames<QcResult>())})", nameof(value));

    /// <summary>Wie <see cref="ParseQcResult"/>, ohne Ausnahme (für die Eingabevalidierung).</summary>
    public static bool TryParseQcResult(string? value, out QcResult result)
    {
        var text = value?.Trim();
        foreach (var known in Enum.GetValues<QcResult>())
        {
            if (!string.Equals(known.ToString(), text, StringComparison.OrdinalIgnoreCase)) continue;
            result = known;
            return true;
        }
        result = default;
        return false;
    }

    /// <summary>
    /// Schließt die Retoure ab und bucht je Zeile nach dem QC-Ergebnis (siehe Klassenbeschreibung). Status-Guard
    /// zuerst: nur ein Entwurf ohne offene (Pending) Zeilen lässt sich abschließen, ein zweiter Aufruf wirft und
    /// bucht nichts. Alles in EINEM SaveChanges.
    /// Sperr-/Ausschussbuchungen brauchen einen Lagerplatz für das Ledger: der Ziel-Lagerplatz der Zeile, sonst der
    /// Platz mit dem größten Bestand des Artikels, sonst der erste Lagerplatz.
    /// </summary>
    public async Task<ReturnShipmentDto?> ProcessAsync(Guid id, CancellationToken ct = default)
    {
        var ret = await _repo.GetWithLinesAsync(id, ct);
        if (ret is null) return null;

        ret.MarkProcessed();

        var articleMap = await _articles.GetManyAsync(ret.Lines.Select(l => l.ArticleId).Distinct(), ct);
        foreach (var line in ret.Lines)
        {
            var costCents = articleMap.GetValueOrDefault(line.ArticleId)?.PurchasePriceCents ?? 0;

            switch (line.QcResult)
            {
                case QcResult.Sellable:
                    // SetLineQc verlangt für Sellable einen Ziel-Lagerplatz - defensiv geprüft statt .Value.
                    var binId = line.TargetBinId
                        ?? throw StockErrors.Conflict("return_bin_required", "Sellable braucht einen Ziel-Lagerplatz");
                    await StockBooking.BookAsync(_stock, _movements,
                        line.ArticleId, binId, +line.Quantity, StockMovementReason.Return,
                        "ReturnShipment", ret.Id, line.LotNumber, null, costCents, ct);
                    break;

                case QcResult.BGrade:
                case QcResult.Defect:
                case QcResult.Destroy:
                    var reason = line.QcResult == QcResult.BGrade ? StockMovementReason.ReturnB : StockMovementReason.ReturnScrap;
                    var ledgerBin = await ResolveLedgerBinAsync(line, ct);
                    await StockBooking.RecordBlockedAsync(_stock, _movements,
                        line.ArticleId, ledgerBin, line.Quantity, reason,
                        "ReturnShipment", ret.Id, line.LotNumber, null, costCents, ct);
                    break;

                default:
                    // Ein undefinierter QC-Wert (Altdaten) darf nicht still ungebucht durchlaufen.
                    throw StockErrors.Conflict("invalid_qc_result", $"Zeile {line.ArticleSku}: unbekanntes QC-Ergebnis {(int)line.QcResult}");
            }
        }

        await _uow.SaveChangesAsync(ct);
        return ToDto(ret);
    }

    public async Task<bool> CancelAsync(Guid id, CancellationToken ct = default)
    {
        var ret = await _repo.GetWithLinesAsync(id, ct);
        if (ret is null) return false;
        ret.Cancel();
        await _uow.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Ein Bundle hat keinen eigenen Bestand (die Kommissionierung löst es in Komponenten auf): eine Retoure des Bundles
    /// selbst würde Bestand für einen Artikel ohne physische Ware buchen - auch ohne Bestellbezug. Retourniert werden
    /// die Komponenten.
    /// </summary>
    private static void EnsureNotBundle(Article article)
    {
        if (article.IsBundle)
            throw StockErrors.Conflict("return_bundle_not_allowed", $"{article.Sku} ist ein Bundle - bitte die Komponenten retournieren");
    }

    /// <summary>Lagerplatz für die Ledger-Einträge einer Sperr-/Ausschussbuchung (siehe <see cref="ProcessAsync"/>).</summary>
    private async Task<Guid> ResolveLedgerBinAsync(ReturnLine line, CancellationToken ct)
    {
        if (line.TargetBinId is Guid target) return target;

        var withStock = (await _stock.ListForArticleAsync(line.ArticleId, ct))
            .OrderByDescending(s => s.Quantity).ThenBy(s => s.Id)
            .FirstOrDefault();
        if (withStock is not null) return withStock.StorageLocationId;

        var any = (await _warehouse.ListStorageLocationsAsync(ct)).OrderBy(b => b.Code, StringComparer.Ordinal).FirstOrDefault();
        return any?.Id ?? throw StockErrors.Conflict("return_bin_required",
            $"Kein Lagerplatz für die Buchung von {line.ArticleSku} vorhanden - bitte einen Ziel-Lagerplatz angeben");
    }

    /// <summary>
    /// Prüft die Zeilen einer Retoure gegen ihre Bestellung: die Bestellung existiert und ist ausgeliefert
    /// (Packed/Shipped, der Bestand ist abgebucht), jeder Artikel gehört zur Bestellung (ein Bundle selbst wird nicht
    /// retourniert, nur seine Komponenten), und die Retourenmenge je Artikel übersteigt nicht die gelieferte Menge
    /// abzüglich der Mengen früherer, nicht stornierter Retouren derselben Bestellung.
    /// </summary>
    /// <param name="alreadyCounted">Retoure, deren gespeicherte Zeilen bei den früheren Mengen NICHT mitzählen (sie steckt schon in <paramref name="lines"/>).</param>
    private async Task EnsureWithinDeliveredAsync(Guid orderId, IEnumerable<ReturnLine> lines, Guid? alreadyCounted, CancellationToken ct)
    {
        var order = await _orders.GetAsync(orderId, ct)
            ?? throw StockErrors.NotFound("order_not_found", "Bestellung nicht gefunden");
        if (order.Status is not (OrderStatus.Packed or OrderStatus.Shipped))
            throw StockErrors.Conflict("return_order_not_delivered",
                $"Bestellung {order.OrderNumber} ist noch nicht ausgeliefert (Status {order.Status}) - eine Retoure ist erst ab Packed möglich");

        var (delivered, articles) = await DeliveredAsync(order, ct);

        var previous = (await _repo.ListForOrderAsync(orderId, ct))
            .Where(r => r.Id != alreadyCounted)
            .SelectMany(r => r.Lines)
            .GroupBy(l => l.ArticleId)
            .ToDictionary(g => g.Key, g => g.Sum(l => (long)l.Quantity));

        foreach (var group in lines.GroupBy(l => l.ArticleId))
        {
            var sku = group.First().ArticleSku;
            if (articles.TryGetValue(group.Key, out var article) && article.IsBundle)
                throw StockErrors.Conflict("return_bundle_not_allowed",
                    $"{sku} ist ein Bundle - bitte die Komponenten retournieren");
            if (!delivered.TryGetValue(group.Key, out var deliveredQty))
                throw StockErrors.Conflict("return_article_not_on_order",
                    $"Artikel {sku} gehört nicht zur Bestellung {order.OrderNumber}");

            var already = previous.GetValueOrDefault(group.Key);
            var requested = group.Sum(l => (long)l.Quantity);
            if (already + requested > deliveredQty)
                throw StockErrors.Conflict("return_quantity_exceeded",
                    $"Retourenmenge {requested} für {sku} übersteigt die gelieferte Menge {deliveredQty} " +
                    $"(bereits retourniert: {already}, Bestellung {order.OrderNumber})");
        }
    }

    /// <summary>
    /// Gelieferte Menge je physischem Artikel der Bestellung: Bundle-Zeilen werden (rekursiv) in ihre Komponenten
    /// aufgelöst - 1 Bundle x Menge x Komponentenmenge. Liefert zusätzlich alle geladenen Artikel (auch Bundles).
    /// </summary>
    private async Task<(Dictionary<Guid, long> Delivered, Dictionary<Guid, Article> Articles)> DeliveredAsync(Order order, CancellationToken ct)
    {
        var articles = new Dictionary<Guid, Article>();
        var pending = order.Lines.Select(l => l.ArticleId).ToHashSet();
        for (var depth = 0; pending.Count > 0 && depth <= MaxBundleDepth; depth++)
        {
            var loaded = await _articles.GetManyAsync(pending, ct);
            foreach (var (articleId, article) in loaded) articles[articleId] = article;
            pending = loaded.Values
                .Where(a => a.IsBundle)
                .SelectMany(a => a.BundleComponents)
                .Select(c => c.ComponentArticleId)
                .Where(articleId => !articles.ContainsKey(articleId))
                .ToHashSet();
        }

        var delivered = new Dictionary<Guid, long>();
        foreach (var line in order.Lines)
            Expand(line.ArticleId, line.Quantity, articles, delivered, 0, new HashSet<Guid>());
        return (delivered, articles);
    }

    private static void Expand(Guid articleId, long quantity, IReadOnlyDictionary<Guid, Article> articles,
        Dictionary<Guid, long> delivered, int depth, HashSet<Guid> path)
    {
        if (articles.TryGetValue(articleId, out var article) && article.IsBundle)
        {
            if (depth >= MaxBundleDepth || !path.Add(articleId))
                throw StockErrors.Conflict("bundle_cycle", $"Bundle {article.Sku} ist zyklisch oder zu tief verschachtelt");
            foreach (var component in article.BundleComponents)
                Expand(component.ComponentArticleId, quantity * component.Quantity, articles, delivered, depth + 1, path);
            path.Remove(articleId);
            return;
        }

        delivered[articleId] = delivered.GetValueOrDefault(articleId) + quantity;
    }

    private static ReturnShipmentDto ToDto(ReturnShipment r) => new(
        r.Id, r.RmaNumber, r.OrderId, r.CustomerReference, r.Notes,
        r.Status.ToString(), r.CreatedAt, r.ProcessedAt,
        r.Lines.Select(l => new ReturnLineDto(
            l.Id, l.ArticleId, l.ArticleSku, l.Quantity, l.LotNumber,
            l.QcResult.ToString(), l.TargetBinId, l.QcNotes)).ToList());
}
