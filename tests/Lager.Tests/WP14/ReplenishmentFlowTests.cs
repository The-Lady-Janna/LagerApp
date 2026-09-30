using Lager.Application.Reports;
using Lager.Contracts.Stock;
using Lager.Domain.Stock;
using Lager.Domain.Warehouse;
using Microsoft.EntityFrameworkCore;

namespace Lager.Tests.WP14;

/// <summary>
/// Nachschub: der Scan erkennt auch komplett leere Hot-Pick-Plätze, schlägt Max - Bestand vor (begrenzt durch die
/// Quelle) und wählt die Quelle FEFO. Das Umlagern behält Charge und MHD (je Charge ein Out- und ein In-Movement).
/// Alle Scans sind auf den Hot-Pick-Platz des Tests beschränkt (OnlyForBinId): die Tests teilen sich eine Datenbank.
/// </summary>
public class ReplenishmentFlowTests : IClassFixture<StockApiFixture>
{
    private readonly StockWorld _w;

    public ReplenishmentFlowTests(StockApiFixture fixture) => _w = fixture.World;

    private Task<IReadOnlyList<ReplenishmentTaskDto>> ScanAsync(StockWorld.Bin hot) =>
        _w.ReplenishmentAsync(s => s.ScanAsync(new ScanReplenishmentRequest(hot.Id)));

    /// <summary>Hot-Pick-Platz (Schwelle 10, Zielbestand 20) und ein Reserve-Platz.</summary>
    private async Task<(StockWorld.Bin Hot, StockWorld.Bin Reserve)> BinsAsync(int threshold = 10)
    {
        var site = await _w.AddSiteAsync();
        return (await _w.AddBinAsync(site, type: BinType.HotPick, threshold: threshold),
                await _w.AddBinAsync(site, type: BinType.Reserve));
    }

    [Fact]
    public async Task A_completely_empty_hot_pick_bin_gets_a_replenishment_task()
    {
        var (hot, reserve) = await BinsAsync();
        var article = await _w.AddArticleAsync();
        await _w.AddStockAsync(article, hot, 0);          // leer gepickt: die Zeile bleibt mit Menge 0 stehen
        await _w.AddStockAsync(article, reserve, 100);

        var tasks = await ScanAsync(hot);

        var task = Assert.Single(tasks);
        Assert.Equal((article, reserve.Id, hot.Id), (task.ArticleId, task.SourceBinId, task.TargetBinId));
        Assert.Equal(20, task.SuggestedQty);              // Zielbestand 2 x 10 - aktueller Bestand 0

        // der zweite Scan legt keine Doppelaufgabe an
        Assert.Empty(await ScanAsync(hot));
    }

    [Fact]
    public async Task The_suggested_quantity_fills_up_to_the_target_and_never_more_than_the_source_holds()
    {
        var (hot, reserve) = await BinsAsync();
        var article = await _w.AddArticleAsync();
        await _w.AddStockAsync(article, hot, 8);          // unter der Schwelle 10
        await _w.AddStockAsync(article, reserve, 100);

        // früher: Schwelle x 2 = 20 unabhängig vom Bestand, der Platz wäre auf 28 gekommen
        Assert.Equal(12, Assert.Single(await ScanAsync(hot)).SuggestedQty);

        var (hot2, reserve2) = await BinsAsync();
        var article2 = await _w.AddArticleAsync();
        await _w.AddStockAsync(article2, hot2, 0);
        await _w.AddStockAsync(article2, reserve2, 5);    // Quelle hat weniger als gebraucht
        Assert.Equal(5, Assert.Single(await ScanAsync(hot2)).SuggestedQty);
    }

    [Fact]
    public async Task The_threshold_applies_to_the_total_of_all_lots_and_bins_at_or_above_it_need_no_task()
    {
        var (hot, reserve) = await BinsAsync();
        var article = await _w.AddArticleAsync();
        await _w.AddStockAsync(article, hot, 6, "LOT-A", StockWorld.InDays(50));
        await _w.AddStockAsync(article, hot, 5, "LOT-B", StockWorld.InDays(60));   // zusammen 11 >= 10
        await _w.AddStockAsync(article, reserve, 100);

        Assert.Empty(await ScanAsync(hot));
    }

    [Fact]
    public async Task The_source_is_chosen_FEFO_and_expired_stock_is_not_used()
    {
        var site = await _w.AddSiteAsync();
        var hot = await _w.AddBinAsync(site, type: BinType.HotPick, threshold: 10);
        var late = await _w.AddBinAsync(site, type: BinType.Reserve);
        var early = await _w.AddBinAsync(site, type: BinType.Reserve);
        var expired = await _w.AddBinAsync(site, type: BinType.Reserve);
        var article = await _w.AddArticleAsync();
        await _w.AddStockAsync(article, hot, 0);
        await _w.AddStockAsync(article, late, 500, "LOT-LATE", StockWorld.InDays(400));
        await _w.AddStockAsync(article, early, 40, "LOT-EARLY", StockWorld.InDays(30));
        await _w.AddStockAsync(article, expired, 900, "LOT-OLD", StockWorld.InDays(-5));

        var task = Assert.Single(await ScanAsync(hot));

        Assert.Equal(early.Id, task.SourceBinId);         // frühestes MHD gewinnt gegen die größere Menge
        Assert.Equal(20, task.SuggestedQty);
    }

    [Fact]
    public async Task Completing_a_task_moves_the_stock_and_keeps_lot_and_expiry_across_lots()
    {
        var (hot, reserve) = await BinsAsync();
        var article = await _w.AddArticleAsync(priceCents: 150);
        var expiryY = StockWorld.InDays(40);
        var expiryZ = StockWorld.InDays(90);
        await _w.AddStockAsync(article, hot, 0);
        await _w.AddStockAsync(article, reserve, 3, "LOT-Y", expiryY);
        await _w.AddStockAsync(article, reserve, 40, "LOT-Z", expiryZ);
        var task = Assert.Single(await ScanAsync(hot));

        // 10 Stück: zuerst die Charge mit dem früheren MHD (3), der Rest aus der nächsten (7)
        var done = await _w.ReplenishmentAsync(s => s.CompleteAsync(task.Id, new CompleteReplenishmentRequest(10)));

        Assert.Equal("Completed", done!.Status);
        var hotRows = await _w.RowsAsync(article, hot);
        Assert.Equal(new[] { ("LOT-Y", 3, expiryY), ("LOT-Z", 7, expiryZ) },
            hotRows.Where(r => r.LotNumber is not null).Select(r => (r.LotNumber!, r.Quantity, r.ExpiryDate!.Value)));
        var reserveRows = await _w.RowsAsync(article, reserve);
        Assert.Equal(0, reserveRows.Single(r => r.LotNumber == "LOT-Y").Quantity);
        Assert.Equal(33, reserveRows.Single(r => r.LotNumber == "LOT-Z").Quantity);

        // je Charge ein Out- und ein In-Movement mit derselben Charge/MHD: netto 0, Kosten = aktueller Artikelpreis
        var movements = await _w.MovementsAsync(article);
        Assert.Equal(4, movements.Count);
        foreach (var lot in new[] { "LOT-Y", "LOT-Z" })
        {
            var pair = movements.Where(m => m.LotNumber == lot).ToList();
            Assert.Equal(2, pair.Count);
            Assert.Equal(0, pair.Sum(m => m.QuantityDelta));
            Assert.Single(pair, m => m.Reason == StockMovementReason.ReplenishmentOut && m.BinId == reserve.Id);
            Assert.Single(pair, m => m.Reason == StockMovementReason.ReplenishmentIn && m.BinId == hot.Id);
            Assert.All(pair, m => Assert.Equal(150, m.UnitCostCents));
        }
        Assert.Equal(expiryY, movements.First(m => m.LotNumber == "LOT-Y").ExpiryDate);
    }

    [Fact]
    public async Task The_charge_trace_finds_the_lot_in_both_bins_after_a_replenishment()
    {
        var (hot, reserve) = await BinsAsync();
        var article = await _w.AddArticleAsync();
        var lot = StockWorld.Unique("LOT");
        var expiry = StockWorld.InDays(150);
        await _w.AddStockAsync(article, hot, 0);
        await _w.ReceiveAsync((article, reserve, 30, lot, expiry));
        var task = Assert.Single(await ScanAsync(hot));
        await _w.ReplenishmentAsync(s => s.CompleteAsync(task.Id, new CompleteReplenishmentRequest(20)));

        // früher legte der Nachschub die Zielzeile ohne Charge an: der Bestand der Charge fehlte im Trace (und FEFO sah kein MHD)
        var trace = (await _w.CallAsync<ReportService, Lager.Contracts.Reports.ChargeTraceDto?>(s => s.ChargeTraceAsync(lot)))!;

        Assert.Equal((30, 30), (trace.InboundTotal, trace.CurrentStockTotal));
        Assert.Equal(new[] { (hot.Code, 20), (reserve.Code, 10) }.OrderBy(x => x.Code),
            trace.CurrentStock.Select(c => (c.BinCode, c.Quantity)).OrderBy(x => x.BinCode));
        Assert.Equal(expiry, (await _w.RowsAsync(article, hot)).Single(r => r.LotNumber == lot).ExpiryDate);
    }

    [Fact]
    public async Task Completing_twice_or_beyond_the_source_stock_books_nothing()
    {
        var (hot, reserve) = await BinsAsync();
        var article = await _w.AddArticleAsync();
        await _w.AddStockAsync(article, hot, 0);
        await _w.AddStockAsync(article, reserve, 6, "LOT-A", StockWorld.InDays(50));
        var task = Assert.Single(await ScanAsync(hot));

        // mehr als die Quelle hat: Fehler, nichts verändert, die Aufgabe bleibt offen
        await ErrorAssert.ThrowsWithCodeAsync<InvalidOperationException>("insufficient_stock",
            () => _w.ReplenishmentAsync(s => s.CompleteAsync(task.Id, new CompleteReplenishmentRequest(7))));
        Assert.Equal(6, (await _w.RowsAsync(article, reserve)).Single().Quantity);
        Assert.Empty(await _w.MovementsAsync(article));
        Assert.Equal(ReplenishmentStatus.Open, await _w.DbAsync(db => db.ReplenishmentTasks.Where(t => t.Id == task.Id).Select(t => t.Status).SingleAsync()));

        await _w.ReplenishmentAsync(s => s.CompleteAsync(task.Id, new CompleteReplenishmentRequest(6)));
        // Doppelklick / Retry: die erledigte Aufgabe bucht kein zweites Mal
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _w.ReplenishmentAsync(s => s.CompleteAsync(task.Id, new CompleteReplenishmentRequest(6))));
        Assert.Equal(6, (await _w.RowsAsync(article, hot)).Single(r => r.LotNumber == "LOT-A").Quantity);
        Assert.Equal(2, (await _w.MovementsAsync(article)).Count);
        Assert.Null(await _w.ReplenishmentAsync(s => s.CompleteAsync(Guid.NewGuid(), new CompleteReplenishmentRequest(1))));
    }

    [Fact]
    public async Task A_cancelled_task_cannot_be_completed_and_a_completed_one_cannot_be_cancelled()
    {
        var (hot, reserve) = await BinsAsync();
        var article = await _w.AddArticleAsync();
        await _w.AddStockAsync(article, hot, 1);
        await _w.AddStockAsync(article, reserve, 50);
        var cancelled = Assert.Single(await ScanAsync(hot));
        Assert.True(await _w.ReplenishmentAsync(s => s.CancelAsync(cancelled.Id)));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _w.ReplenishmentAsync(s => s.CompleteAsync(cancelled.Id, new CompleteReplenishmentRequest(5))));
        Assert.Empty(await _w.MovementsAsync(article));

        // nach dem Storno legt der nächste Scan eine neue Aufgabe an; erledigt lässt sie sich nicht mehr stornieren
        var again = Assert.Single(await ScanAsync(hot));
        await _w.ReplenishmentAsync(s => s.CompleteAsync(again.Id, new CompleteReplenishmentRequest(5)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _w.ReplenishmentAsync(s => s.CancelAsync(again.Id)));
    }
}
