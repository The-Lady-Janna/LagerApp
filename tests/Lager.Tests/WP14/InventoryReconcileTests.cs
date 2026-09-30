using Lager.Contracts.Inventory;
using Lager.Contracts.Stock;
using Lager.Domain.Inventory;
using Lager.Domain.Stock;
using Microsoft.EntityFrameworkCore;

namespace Lager.Tests.WP14;

/// <summary>
/// Inventur-Abgleich: gebucht wird der GEZÄHLTE Wert gegen den AKTUELLEN Bestand, nicht die Differenz zum
/// Snapshot beim Start. Bewegungen während der Zählung werden nicht doppelt verrechnet, ein leer gepickter oder
/// verschwundener Bestand bricht den Abgleich nicht ab, und Chargen bleiben getrennt.
/// </summary>
public class InventoryReconcileTests : IClassFixture<StockApiFixture>
{
    private readonly StockWorld _w;

    public InventoryReconcileTests(StockApiFixture fixture) => _w = fixture.World;

    private static readonly DateTime ExpiryA = StockWorld.InDays(100);
    private static readonly DateTime ExpiryB = StockWorld.InDays(200);

    /// <summary>Startet eine Inventur nur für diesen Lagerplatz.</summary>
    private Task<InventoryCountDto> StartAsync(StockWorld.Bin bin) =>
        _w.InventoryAsync(s => s.StartAsync(new StartInventoryRequest(StockWorld.Unique("Inventur"), bin.Id)));

    private Task<InventoryCountDto?> CountAsync(InventoryCountDto count, InventoryLineDto line, int counted, string? reason = null) =>
        _w.InventoryAsync(s => s.SetCountAsync(count.Id, line.Id, new SetCountRequest(counted, reason)));

    private Task<StockItemDto> AdjustAsync(Guid article, StockWorld.Bin bin, int delta, string? lot = null, DateTime? expiry = null) =>
        _w.StockAsync(s => s.AdjustAsync(new AdjustStockRequest(article, bin.Id, delta, lot, expiry)));

    [Fact]
    public async Task Reconcile_after_a_movement_during_the_count_ends_with_the_counted_quantity()
    {
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync();
        await _w.AddStockAsync(article, bin, 100);
        var count = await StartAsync(bin);
        var line = Assert.Single(count.Lines);
        Assert.Equal(100, line.ExpectedQty);

        // Zwischen Start und Abgleich werden 30 abgebucht (z. B. Pick), gezählt werden physisch 70.
        await AdjustAsync(article, bin, -30);
        await CountAsync(count, line, 70);
        var reconciled = await _w.InventoryAsync(s => s.ReconcileAsync(count.Id));

        // früher: Snapshot-Differenz -30 auf den neuen Bestand 70 -> 40; jetzt: gezählt = Bestand, keine Phantombuchung
        Assert.Equal("Reconciled", reconciled!.Status);
        Assert.Equal(70, await _w.QuantityAsync(article));
        Assert.DoesNotContain(await _w.MovementsAsync(article), m => m.Reason == StockMovementReason.Inventory);
        // die Bewegung während der Zählung ist an der Zeile vermerkt, auch ohne Korrekturbuchung
        Assert.Contains("Snapshot 100, beim Abgleich 70, gezählt 70", (await _w.InventoryAsync(s => s.GetAsync(count.Id)))!.Lines.Single().Reason);
    }

    [Fact]
    public async Task Reconcile_books_counted_minus_current_and_notes_the_change_since_the_snapshot_on_the_line()
    {
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync(priceCents: 120);
        await _w.AddStockAsync(article, bin, 100);
        var count = await StartAsync(bin);
        var line = count.Lines.Single();

        await AdjustAsync(article, bin, -30);            // aktuell 70
        await CountAsync(count, line, 65, "Karton beschädigt");
        await _w.InventoryAsync(s => s.ReconcileAsync(count.Id));

        Assert.Equal(65, await _w.QuantityAsync(article));
        var movement = (await _w.MovementsAsync(article)).Single(m => m.Reason == StockMovementReason.Inventory);
        Assert.Equal(-5, movement.QuantityDelta);                       // 65 - 70, nicht 65 - 100
        Assert.Equal(120, movement.UnitCostCents);
        Assert.Equal(count.Id, movement.ReferenceId);

        // Abweichung zum Snapshot steht pro Zeile im Grund - hinter der Bemerkung des Zählers
        var reason = (await _w.InventoryAsync(s => s.GetAsync(count.Id)))!.Lines.Single().Reason;
        Assert.StartsWith("Karton beschädigt | ", reason);
        Assert.Contains("Snapshot 100, beim Abgleich 70, gezählt 65", reason);
    }

    [Fact]
    public async Task An_emptied_bin_can_be_reconciled_instead_of_failing()
    {
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync();
        await _w.AddStockAsync(article, bin, 5);
        var count = await StartAsync(bin);
        var line = count.Lines.Single();

        await AdjustAsync(article, bin, -5);             // die Zeile bleibt mit Menge 0 stehen
        await CountAsync(count, line, 0);
        var reconciled = await _w.InventoryAsync(s => s.ReconcileAsync(count.Id));   // früher: ArgumentOutOfRangeException -> 500

        Assert.Equal("Reconciled", reconciled!.Status);
        Assert.Equal(0, await _w.QuantityAsync(article));

        // gefunden statt leer: dieselbe Situation mit gezählten 2 Stück bucht +2 auf die vorhandene Nullzeile
        var article2 = await _w.AddArticleAsync();
        await _w.AddStockAsync(article2, bin, 5);
        var count2 = await StartAsync(bin);
        var line2 = count2.Lines.Single(l => l.ArticleId == article2);
        await AdjustAsync(article2, bin, -5);
        await CountAsync(count2, line2, 2);
        await _w.InventoryAsync(s => s.ReconcileAsync(count2.Id));
        Assert.Equal(2, await _w.QuantityAsync(article2));
        Assert.Single(await _w.RowsAsync(article2, bin));
    }

    [Fact]
    public async Task A_stock_row_that_no_longer_exists_is_created_again_with_its_lot()
    {
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync();
        await _w.AddStockAsync(article, bin, 8, "LOT-X", ExpiryA);
        var count = await StartAsync(bin);
        var line = count.Lines.Single();
        Assert.Equal(("LOT-X", ExpiryA), (line.LotNumber, line.ExpiryDate));

        await _w.DbAsync(async db =>
        {
            db.StockItems.RemoveRange(db.StockItems.Where(s => s.ArticleId == article));
            await db.SaveChangesAsync();
        });
        await CountAsync(count, line, 3);
        await _w.InventoryAsync(s => s.ReconcileAsync(count.Id));

        var row = Assert.Single(await _w.RowsAsync(article, bin));
        Assert.Equal((3, "LOT-X", ExpiryA), (row.Quantity, row.LotNumber, row.ExpiryDate));
        var movement = Assert.Single(await _w.MovementsAsync(article));
        Assert.Equal((3, "LOT-X", ExpiryA), (movement.QuantityDelta, movement.LotNumber, movement.ExpiryDate));
    }

    [Fact]
    public async Task Two_lots_in_one_bin_are_counted_and_booked_as_separate_lines()
    {
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync();
        await _w.AddStockAsync(article, bin, 10, "LOT-A", ExpiryA);
        await _w.AddStockAsync(article, bin, 20, "LOT-B", ExpiryB);
        var count = await StartAsync(bin);
        Assert.Equal(2, count.Lines.Count);

        await CountAsync(count, count.Lines.Single(l => l.LotNumber == "LOT-A"), 9);
        await CountAsync(count, count.Lines.Single(l => l.LotNumber == "LOT-B"), 22);
        await _w.InventoryAsync(s => s.ReconcileAsync(count.Id));

        var rows = await _w.RowsAsync(article, bin);
        Assert.Equal(9, rows.Single(r => r.LotNumber == "LOT-A").Quantity);
        Assert.Equal(22, rows.Single(r => r.LotNumber == "LOT-B").Quantity);
        var movements = (await _w.MovementsAsync(article)).Where(m => m.Reason == StockMovementReason.Inventory).ToList();
        Assert.Equal(-1, movements.Single(m => m.LotNumber == "LOT-A").QuantityDelta);
        Assert.Equal(+2, movements.Single(m => m.LotNumber == "LOT-B").QuantityDelta);
    }

    [Fact]
    public async Task Reconciling_twice_is_rejected_and_books_nothing_more()
    {
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync();
        await _w.AddStockAsync(article, bin, 10);
        var count = await StartAsync(bin);
        await CountAsync(count, count.Lines.Single(), 12);

        await _w.InventoryAsync(s => s.ReconcileAsync(count.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _w.InventoryAsync(s => s.ReconcileAsync(count.Id)));

        Assert.Equal(12, await _w.QuantityAsync(article));
        Assert.Single(await _w.MovementsAsync(article));
        var status = await _w.DbAsync(db => db.InventoryCounts.Where(c => c.Id == count.Id).Select(c => c.Status).SingleAsync());
        Assert.Equal(InventoryStatus.Reconciled, status);
    }
}
