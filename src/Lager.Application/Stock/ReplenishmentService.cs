using Lager.Application.Abstractions;
using Lager.Contracts.Stock;
using Lager.Domain.Stock;
using Lager.Domain.Warehouse;

namespace Lager.Application.Stock;

/// <summary>
/// Scans Hot-Pick bins for low stock and generates replenishment tasks pulling
/// from Reserve bins. On Complete, the actual stock move is executed.
/// </summary>
public class ReplenishmentService
{
    private readonly IReplenishmentRepository _repo;
    private readonly IStockRepository _stock;
    private readonly IStockMovementRepository _movements;
    private readonly IWarehouseRepository _warehouse;
    private readonly IArticleRepository _articles;
    private readonly IUnitOfWork _uow;

    public ReplenishmentService(
        IReplenishmentRepository repo,
        IStockRepository stock,
        IStockMovementRepository movements,
        IWarehouseRepository warehouse,
        IArticleRepository articles,
        IUnitOfWork uow)
    {
        _repo = repo;
        _stock = stock;
        _movements = movements;
        _warehouse = warehouse;
        _articles = articles;
        _uow = uow;
    }

    public async Task<IReadOnlyList<ReplenishmentTaskDto>> ListAsync(CancellationToken ct = default) =>
        (await _repo.ListAsync(ct)).Select(ToDto).ToList();

    public async Task<IReadOnlyList<ReplenishmentTaskDto>> ListOpenAsync(CancellationToken ct = default) =>
        (await _repo.ListOpenAsync(ct)).Select(ToDto).ToList();

    /// <summary>
    /// Geht jeden Hot-Pick-Lagerplatz mit Schwelle durch (oder nur den einen der Anfrage) und legt für jeden Artikel
    /// unter der Schwelle eine Aufgabe an - auch für einen komplett leer gepickten Platz (die Bestandszeile bleibt
    /// mit Menge 0 stehen und zählt als "unter Schwelle"). Der Bestand eines Artikels im Platz ist die Summe seiner
    /// Chargenzeilen. Zielbestand (Max) = 2 x Schwelle; Vorschlagsmenge = Max - aktueller Bestand, begrenzt durch den
    /// Bestand der Quelle (kein Überfüllen). Quelle: der Reserve-Lagerplatz mit der FEFO-ersten Ware (frühestes MHD,
    /// abgelaufene Ware zählt nicht), bei gleichem MHD der mit dem größeren Bestand. Bestehende offene Aufgaben
    /// (Artikel x Ziel-Lagerplatz) werden übersprungen - auch die, die dieser Scan gerade angelegt hat.
    /// </summary>
    public async Task<IReadOnlyList<ReplenishmentTaskDto>> ScanAsync(ScanReplenishmentRequest req, CancellationToken ct = default)
    {
        var bins = await _warehouse.ListStorageLocationsAsync(ct);
        var hotPicks = bins.Where(b => b.BinType == BinType.HotPick && b.ReplenishmentThreshold > 0).ToList();
        if (req.OnlyForBinId is Guid only)
            hotPicks = hotPicks.Where(b => b.Id == only).ToList();
        var reserves = bins.Where(b => b.BinType == BinType.Reserve).ToDictionary(b => b.Id);
        if (hotPicks.Count == 0 || reserves.Count == 0) return Array.Empty<ReplenishmentTaskDto>();

        // ALLE Bestandszeilen inklusive der leeren (Menge 0): ein leer gepickter Hot-Pick-Platz hat nur noch so eine Zeile.
        var allStock = await _stock.ListAsync(ct);
        var hotIds = hotPicks.Select(h => h.Id).ToHashSet();
        var hotTotals = allStock
            .Where(s => hotIds.Contains(s.StorageLocationId))
            .GroupBy(s => (Bin: s.StorageLocationId, s.ArticleId))
            .ToDictionary(g => g.Key, g => g.Sum(s => (long)s.Quantity));

        var today = DateTime.UtcNow.Date;
        var reserveRows = allStock
            .Where(s => s.Quantity > 0 && reserves.ContainsKey(s.StorageLocationId) && !s.IsExpired(today))
            .ToLookup(s => s.ArticleId);

        // Bestehende offene Tasks (Artikel × Ziel-Bin) einmal laden; neu angelegte kommen im Lauf dazu.
        var openTaskKeys = (await _repo.ListOpenAsync(ct))
            .Select(t => (t.ArticleId, t.TargetBinId))
            .ToHashSet();

        var articleMap = await _articles.GetManyAsync(hotTotals.Keys.Select(k => k.ArticleId).Distinct().ToList(), ct);

        var created = new List<ReplenishmentTask>();
        foreach (var hot in hotPicks.OrderBy(h => h.Code, StringComparer.Ordinal))
        {
            var target = hot.ReplenishmentThreshold * 2;
            foreach (var ((_, articleId), total) in hotTotals
                         .Where(kv => kv.Key.Bin == hot.Id)
                         .OrderBy(kv => articleMap.GetValueOrDefault(kv.Key.ArticleId)?.Sku ?? string.Empty, StringComparer.Ordinal))
            {
                if (total >= hot.ReplenishmentThreshold) continue;
                if (openTaskKeys.Contains((articleId, hot.Id))) continue;

                // FEFO-erste Quelle: der Reserve-Lagerplatz mit dem frühesten MHD (ohne MHD zuletzt), dann dem größeren Bestand.
                var source = reserveRows[articleId]
                    .GroupBy(s => s.StorageLocationId)
                    .Select(g => new
                    {
                        Bin = reserves[g.Key],
                        Earliest = g.Min(s => s.ExpiryDate ?? DateTime.MaxValue),
                        Quantity = g.Sum(s => (long)s.Quantity),
                    })
                    .OrderBy(x => x.Earliest)
                    .ThenByDescending(x => x.Quantity)
                    .ThenBy(x => x.Bin.Code, StringComparer.Ordinal)
                    .ThenBy(x => x.Bin.Id)
                    .FirstOrDefault();
                if (source is null) continue;

                var needed = target - total;
                var suggested = (int)Math.Min(Math.Max(needed, 1), Math.Min(source.Quantity, ReplenishmentTask.MaxQuantity));
                var article = articleMap.GetValueOrDefault(articleId);
                var task = new ReplenishmentTask(
                    articleId, article?.Sku ?? "?",
                    source.Bin.Id, source.Bin.Code,
                    hot.Id, hot.Code,
                    suggested);
                await _repo.AddAsync(task, ct);
                created.Add(task);
                openTaskKeys.Add((articleId, hot.Id));
            }
        }

        if (created.Count > 0) await _uow.SaveChangesAsync(ct);
        return created.Select(ToDto).ToList();
    }

    /// <summary>
    /// Worker confirms the move physically happened. We then mirror it in stock as a transfer that keeps lot and
    /// expiry: the source bin is booked out FEFO across its lot rows, and every removed part is booked into the
    /// target bin with the SAME lot/expiry (one Out and one In movement per lot, both via <see cref="StockBooking"/>).
    /// Status guard first: only an Open task can be completed, a repeated call books nothing. Reicht der Bestand
    /// des Quell-Lagerplatzes nicht, wirft die Methode insufficient_stock, ohne etwas zu ändern.
    /// </summary>
    public async Task<ReplenishmentTaskDto?> CompleteAsync(Guid id, CompleteReplenishmentRequest req, CancellationToken ct = default)
    {
        var task = await _repo.GetAsync(id, ct);
        if (task is null) return null;

        task.Complete(req.ActualQty);

        // Kosten: Umlagerungen sind bewertungsneutral (die Bewertung überspringt ReplenishmentOut/In); der Wert je
        // Movement ist der aktuelle Artikelpreis als Anhaltswert - kein Kosten-Snapshot der Quell-Layer.
        var article = await _articles.GetAsync(task.ArticleId, ct);
        var costCents = article?.PurchasePriceCents ?? 0;

        var taken = await StockBooking.BookOutAsync(_stock, _movements,
            task.ArticleId, task.SourceBinId, req.ActualQty, StockMovementReason.ReplenishmentOut,
            "ReplenishmentTask", task.Id, costCents, ct: ct);
        foreach (var part in taken)
            await StockBooking.BookAsync(_stock, _movements,
                task.ArticleId, task.TargetBinId, +part.Quantity, StockMovementReason.ReplenishmentIn,
                "ReplenishmentTask", task.Id, part.Row.LotNumber, part.Row.ExpiryDate, costCents, ct: ct);

        await _uow.SaveChangesAsync(ct);
        return ToDto(task);
    }

    public async Task<bool> CancelAsync(Guid id, CancellationToken ct = default)
    {
        var task = await _repo.GetAsync(id, ct);
        if (task is null) return false;
        task.Cancel();
        await _uow.SaveChangesAsync(ct);
        return true;
    }

    private static ReplenishmentTaskDto ToDto(ReplenishmentTask t) => new(
        t.Id, t.ArticleId, t.ArticleSku, t.SourceBinId, t.SourceBinCode,
        t.TargetBinId, t.TargetBinCode, t.SuggestedQty, t.CompletedQty,
        t.Status.ToString(), t.CreatedAt, t.CompletedAt);
}
