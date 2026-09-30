using Lager.Application.Abstractions;
using Lager.Application.Stock;
using Lager.Contracts.Inventory;
using Lager.Domain.Inventory;
using Lager.Domain.Stock;

namespace Lager.Application.Inventory;

public class InventoryService
{
    private readonly IInventoryRepository _inventory;
    private readonly IStockRepository _stock;
    private readonly IStockMovementRepository _movements;
    private readonly IArticleRepository _articles;
    private readonly IWarehouseRepository _warehouse;
    private readonly IUnitOfWork _uow;

    public InventoryService(IInventoryRepository inventory, IStockRepository stock, IStockMovementRepository movements,
        IArticleRepository articles, IWarehouseRepository warehouse, IUnitOfWork uow)
    {
        _inventory = inventory;
        _stock = stock;
        _movements = movements;
        _articles = articles;
        _warehouse = warehouse;
        _uow = uow;
    }

    public async Task<IReadOnlyList<InventoryCountDto>> ListAsync(CancellationToken ct = default) =>
        (await _inventory.ListAsync(ct)).Select(ToDto).ToList();

    public async Task<InventoryCountDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var c = await _inventory.GetWithLinesAsync(id, ct);
        return c is null ? null : ToDto(c);
    }

    /// <summary>
    /// Snapshots the current stock into a fresh InventoryCount with one line
    /// per stock row (bin, article, lot/expiry). Optionally restricted to one bin.
    /// </summary>
    public async Task<InventoryCountDto> StartAsync(StartInventoryRequest request, CancellationToken ct = default)
    {
        var allStock = await _stock.ListAllAsync(ct);
        if (request.OnlyForBinId is Guid binId)
            allStock = allStock.Where(s => s.StorageLocationId == binId).ToList();

        if (allStock.Count == 0)
            throw new InvalidOperationException("Kein Bestand zum Inventieren vorhanden");

        var articleMap = await _articles.GetManyAsync(allStock.Select(s => s.ArticleId).Distinct(), ct);
        var binMap = await _warehouse.GetStorageLocationsAsync(allStock.Select(s => s.StorageLocationId).Distinct(), ct);

        var count = new InventoryCount(request.Name);
        foreach (var s in allStock)
        {
            var article = articleMap.GetValueOrDefault(s.ArticleId);
            var bin = binMap.GetValueOrDefault(s.StorageLocationId);
            count.AddSnapshotLine(s.StorageLocationId, bin?.Code ?? "?", s.ArticleId, article?.Sku ?? "?", s.Quantity,
                s.LotNumber, s.ExpiryDate);
        }

        await _inventory.AddAsync(count, ct);
        await _uow.SaveChangesAsync(ct);
        return ToDto(count);
    }

    public async Task<InventoryCountDto?> SetCountAsync(Guid id, Guid lineId, SetCountRequest request, CancellationToken ct = default)
    {
        var count = await _inventory.GetWithLinesAsync(id, ct);
        if (count is null) return null;
        count.SetCount(lineId, request.CountedQty, request.Reason);
        await _uow.SaveChangesAsync(ct);
        return ToDto(count);
    }

    /// <summary>
    /// Gleicht die Inventur ab: bucht je Zeile den GEZÄHLTEN Wert gegen den AKTUELLEN Bestand der Bestandszeile
    /// (Delta = gezählt - jetziger Bestand), nicht die Differenz zum Snapshot beim Start. Bewegungen zwischen Start
    /// und Abgleich (Picken, Wareneingang) werden dadurch nicht doppelt verrechnet; nach dem Abgleich steht der
    /// Bestand auf der gezählten Menge. Weicht der aktuelle Bestand vom Snapshot ab, steht das im Grund der Zeile
    /// (Reason der Inventurzeile, mit Snapshot, aktuellem und gezähltem Wert); die Movement verweist über
    /// ReferenceId auf die Inventur. Zeilen ohne Delta erzeugen keine Buchung.
    /// Lot-bewusst: jede Zeile bucht ihre Bestandszeile (Charge/MHD); eine entleerte oder nicht mehr vorhandene
    /// Zeile wird gefunden bzw. neu angelegt, statt zu scheitern (kein Absturz bei Bestand 0).
    /// Status-Guard: nur eine offene Inventur lässt sich abgleichen - ein zweiter Aufruf wirft und bucht nichts.
    /// Alles in EINEM SaveChanges.
    /// </summary>
    public async Task<InventoryCountDto?> ReconcileAsync(Guid id, CancellationToken ct = default)
    {
        var count = await _inventory.GetWithLinesAsync(id, ct);
        if (count is null) return null;

        // Validate before mutating.
        count.Reconcile();

        var articleMap = await _articles.GetManyAsync(count.Lines.Select(l => l.ArticleId).Distinct(), ct);

        foreach (var line in count.Lines)
        {
            var counted = line.CountedQty ?? line.ExpectedQty;
            var row = await StockBooking.FindAsync(_stock, line.ArticleId, line.BinId, line.LotNumber, line.ExpiryDate, ct);
            var current = row?.Quantity ?? 0;

            // Bewegungen seit dem Zählbeginn sichtbar machen - auch wenn danach keine Korrektur nötig ist.
            if (current != line.ExpectedQty)
                count.AnnotateLine(line.Id,
                    $"Bestand seit Zählbeginn verändert: Snapshot {line.ExpectedQty}, beim Abgleich {current}, gezählt {counted}");

            var delta = counted - current;
            if (delta == 0) continue;

            var costCents = articleMap.GetValueOrDefault(line.ArticleId)?.PurchasePriceCents ?? 0;
            if (row is null)
                await StockBooking.BookAsync(_stock, _movements, line.ArticleId, line.BinId, delta, StockMovementReason.Inventory,
                    "InventoryCount", count.Id, line.LotNumber, line.ExpiryDate, costCents, ct);
            else
                await StockBooking.BookOnAsync(_movements, row, delta, StockMovementReason.Inventory,
                    "InventoryCount", count.Id, costCents, ct);
        }

        await _uow.SaveChangesAsync(ct);
        return ToDto(count);
    }

    public async Task<bool> CancelAsync(Guid id, CancellationToken ct = default)
    {
        var count = await _inventory.GetWithLinesAsync(id, ct);
        if (count is null) return false;
        count.Cancel();
        await _uow.SaveChangesAsync(ct);
        return true;
    }

    private static InventoryCountDto ToDto(InventoryCount c) => new(
        c.Id, c.Name, c.Status.ToString(), c.CreatedAt, c.ReconciledAt,
        c.Lines.Select(l => new InventoryLineDto(
            l.Id, l.BinId, l.BinCode, l.ArticleId, l.ArticleSku,
            l.ExpectedQty, l.CountedQty, l.Diff, l.Reason, l.LotNumber, l.ExpiryDate)).ToList());
}
