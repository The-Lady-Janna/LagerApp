using Lager.Application.Abstractions;
using Lager.Application.Stock;
using Lager.Contracts.Inventory;
using Lager.Contracts.Returns;
using Lager.Contracts.Stock;
using Lager.Domain.Stock;
using Lager.Domain.Warehouse;

namespace Lager.Tests.WP14;

/// <summary>
/// Invariante des einheitlichen Buchungswegs: Nach einer gemischten Buchungsfolge (Wareneingang, Korrektur mit und
/// ohne Charge, Nachschub, Inventur, Retouren aller QC-Ergebnisse) ist die Summe aller Movement-Deltas je Artikel gleich
/// der Summe der Bestandsmengen - und sogar je (Lagerplatz, Charge) gleich der Menge der Bestandszeile. Charge und
/// MHD jeder Movement stammen aus der betroffenen Bestandszeile.
/// </summary>
public class LedgerInvariantTests : IClassFixture<StockApiFixture>
{
    private readonly StockWorld _w;

    public LedgerInvariantTests(StockApiFixture fixture) => _w = fixture.World;

    [Fact]
    public async Task After_a_mixed_booking_sequence_the_sum_of_the_ledger_deltas_equals_the_sum_of_the_stock_quantities()
    {
        var site = await _w.AddSiteAsync();
        var reserve = await _w.AddBinAsync(site, type: BinType.Reserve);
        var hot = await _w.AddBinAsync(site, type: BinType.HotPick, threshold: 10);
        var plain = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync(priceCents: 100);
        var expiryA = StockWorld.InDays(60);
        var expiryB = StockWorld.InDays(120);

        // Wareneingang: zwei Chargen und Ware ohne Charge in verschiedenen Plätzen
        await _w.ReceiveAsync(
            (article, reserve, 50, "LOT-A", expiryA),
            (article, reserve, 30, "LOT-B", expiryB),
            (article, plain, 20, null, null),
            (article, hot, 4, "LOT-A", expiryA));

        // Korrekturen: mit Charge, ohne Charge (zuerst die Zeile ohne Charge), ohne Charge über mehrere Chargen (FEFO)
        await _w.StockAsync(s => s.AdjustAsync(new AdjustStockRequest(article, reserve.Id, +5, "LOT-A", expiryA)));
        await _w.StockAsync(s => s.AdjustAsync(new AdjustStockRequest(article, plain.Id, -7, null, null)));
        await _w.StockAsync(s => s.AdjustAsync(new AdjustStockRequest(article, reserve.Id, -10, null, null)));

        // Nachschub: 12 Stück vom Reserve- in den Hot-Pick-Platz (Charge bleibt erhalten)
        var task = (await _w.ReplenishmentAsync(s => s.ScanAsync(new ScanReplenishmentRequest(hot.Id)))).Single();
        await _w.ReplenishmentAsync(s => s.CompleteAsync(task.Id, new CompleteReplenishmentRequest(12)));

        // Inventur im Reserve-Platz mit einer Bewegung während der Zählung
        var count = await _w.InventoryAsync(s => s.StartAsync(new StartInventoryRequest(StockWorld.Unique("Inventur"), reserve.Id)));
        await _w.StockAsync(s => s.AdjustAsync(new AdjustStockRequest(article, reserve.Id, -3, "LOT-A", expiryA)));
        foreach (var line in count.Lines)
            await _w.InventoryAsync(s => s.SetCountAsync(count.Id, line.Id, new SetCountRequest(line.LotNumber == "LOT-B" ? 31 : 30, null)));
        await _w.InventoryAsync(s => s.ReconcileAsync(count.Id));

        // Retoure: A-Ware, B-Ware und Defekt
        var ret = await _w.ReturnsAsync(s => s.CreateAsync(new CreateReturnShipmentRequest(null, null, null, new[]
        {
            new CreateReturnLineRequest(article, 2, "LOT-A"),
            new CreateReturnLineRequest(article, 1, "LOT-A"),
            new CreateReturnLineRequest(article, 3),
        })));
        var results = new (string Result, Guid? Bin)[] { ("Sellable", plain.Id), ("BGrade", null), ("Defect", null) };
        for (var i = 0; i < ret.Lines.Count; i++)
        {
            var (result, bin) = results[i];
            var lineId = ret.Lines[i].Id;
            await _w.ReturnsAsync(s => s.SetQcAsync(ret.Id, lineId, new SetQcRequest(result, bin, null)));
        }
        await _w.ReturnsAsync(s => s.ProcessAsync(ret.Id));

        var rows = await _w.RowsAsync(article);
        var movements = await _w.MovementsAsync(article);

        // je Artikel
        Assert.Equal(rows.Sum(r => r.Quantity), movements.Sum(m => m.QuantityDelta));
        Assert.True(rows.Sum(r => r.Quantity) > 0);

        // je (Lagerplatz, Charge): jede Bestandszeile trägt genau die Summe ihrer Bewegungen
        var deltas = movements
            .GroupBy(m => (m.BinId, Lot: StockItem.NormalizeLot(m.LotNumber)))
            .ToDictionary(g => g.Key, g => g.Sum(m => m.QuantityDelta));
        foreach (var row in rows)
            Assert.Equal(deltas.GetValueOrDefault((row.StorageLocationId, StockItem.NormalizeLot(row.LotNumber))), row.Quantity);
        Assert.All(deltas, kv => Assert.True(
            kv.Value == 0 || rows.Any(r => r.StorageLocationId == kv.Key.BinId && StockItem.NormalizeLot(r.LotNumber) == kv.Key.Lot),
            $"Bewegungen ohne Bestandszeile: {kv.Key}"));

        // Charge und MHD im Ledger stammen aus der Zeile: jede Chargen-Movement hat das MHD ihrer Charge
        Assert.All(movements.Where(m => m.LotNumber == "LOT-A"), m => Assert.Equal(expiryA, m.ExpiryDate));
        Assert.All(movements.Where(m => m.LotNumber == "LOT-B"), m => Assert.Equal(expiryB, m.ExpiryDate));
        Assert.All(movements, m => Assert.NotEqual(0, m.QuantityDelta));

        // Die Nachschub-Umlagerung ist netto 0, die Sperr-/Ausschussbuchungen ebenso
        Assert.Equal(0, movements.Where(m => m.Reason is StockMovementReason.ReplenishmentIn or StockMovementReason.ReplenishmentOut).Sum(m => m.QuantityDelta));
        Assert.Equal(0, movements.Where(m => m.Reason is StockMovementReason.ReturnB or StockMovementReason.ReturnScrap).Sum(m => m.QuantityDelta));

        // Erwarteter Endbestand nach Hand gerechnet: Reserve A 50+5-10(FEFO)-12-3=30 -> gezählt 30, B 30 -> 31;
        // Hot A 4+12=16; Plain lotless 20-7=13 (+2 A-Ware Retoure mit Charge A in eigener Zeile).
        Assert.Equal(30, rows.Single(r => r.StorageLocationId == reserve.Id && r.LotNumber == "LOT-A").Quantity);
        Assert.Equal(31, rows.Single(r => r.StorageLocationId == reserve.Id && r.LotNumber == "LOT-B").Quantity);
        Assert.Equal(16, rows.Single(r => r.StorageLocationId == hot.Id && r.LotNumber == "LOT-A").Quantity);
        Assert.Equal(13, rows.Single(r => r.StorageLocationId == plain.Id && r.LotNumber is null).Quantity);
        Assert.Equal(2, rows.Single(r => r.StorageLocationId == plain.Id && r.LotNumber == "LOT-A").Quantity);
    }

    [Fact]
    public async Task StockBooking_rejects_zero_oversized_and_unknown_bookings_without_changing_anything()
    {
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync();
        await _w.AddStockAsync(article, bin, 5, "LOT-A", StockWorld.InDays(30));

        async Task BookAsync(int delta, string? lot, DateTime? expiry)
        {
            using var scope = _w.NewScope();
            await StockBooking.BookAsync(scope.Get<IStockRepository>(), scope.Get<IStockMovementRepository>(),
                article, bin.Id, delta, StockMovementReason.Adjust, null, null, lot, expiry, 0);
        }

        await ErrorAssert.ThrowsWithCodeAsync<ArgumentException>("quantity_zero", () => BookAsync(0, "LOT-A", null));
        await ErrorAssert.ThrowsWithCodeAsync<ArgumentException>("quantity_out_of_range", () => BookAsync(StockBooking.MaxQuantity + 1, "LOT-A", null));
        await ErrorAssert.ThrowsWithCodeAsync<InvalidOperationException>("insufficient_stock", () => BookAsync(-6, "LOT-A", null));
        await ErrorAssert.ThrowsWithCodeAsync<InvalidOperationException>("stock_not_found", () => BookAsync(-1, "LOT-UNKNOWN", null));
        await ErrorAssert.ThrowsWithCodeAsync<InvalidOperationException>("stock_not_found", () => BookAsync(-1, null, null));
        await ErrorAssert.ThrowsWithCodeAsync<InvalidOperationException>("lot_expiry_mismatch", () => BookAsync(+1, "LOT-A", StockWorld.InDays(31)));

        Assert.Equal(5, await _w.QuantityAsync(article));
        Assert.Empty(await _w.MovementsAsync(article));
    }

    [Fact]
    public async Task An_adjustment_without_a_lot_takes_the_row_without_lot_first_then_FEFO_and_a_named_lot_only_its_own_row()
    {
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync();
        await _w.AddStockAsync(article, bin, 4);                                   // ohne Charge
        await _w.AddStockAsync(article, bin, 10, "LOT-LATE", StockWorld.InDays(200));
        await _w.AddStockAsync(article, bin, 10, "LOT-EARLY", StockWorld.InDays(20));

        await _w.StockAsync(s => s.AdjustAsync(new AdjustStockRequest(article, bin.Id, -6, null, null)));

        var rows = await _w.RowsAsync(article, bin);
        Assert.Equal(0, rows.Single(r => r.LotNumber is null).Quantity);          // zuerst die Zeile ohne Charge (4) ...
        Assert.Equal(8, rows.Single(r => r.LotNumber == "LOT-EARLY").Quantity);   // ... dann FEFO (2 aus der früheren Charge)
        Assert.Equal(10, rows.Single(r => r.LotNumber == "LOT-LATE").Quantity);
        var movements = await _w.MovementsAsync(article);
        Assert.Equal(new[] { -4, -2 }, movements.Select(m => m.QuantityDelta).Order());   // 4 aus der Zeile ohne Charge, 2 aus der früheren Charge

        // mit Charge nur diese Zeile - reicht sie nicht, gibt es keinen Ausgleich aus anderen Chargen
        await ErrorAssert.ThrowsWithCodeAsync<InvalidOperationException>("insufficient_stock",
            () => _w.StockAsync(s => s.AdjustAsync(new AdjustStockRequest(article, bin.Id, -11, "LOT-LATE", null))));
        Assert.Equal(10, (await _w.RowsAsync(article, bin)).Single(r => r.LotNumber == "LOT-LATE").Quantity);

        // Zugang mit neuer Charge legt eine eigene Zeile an
        var created = await _w.StockAsync(s => s.AdjustAsync(new AdjustStockRequest(article, bin.Id, +3, "LOT-NEW", StockWorld.InDays(5))));
        Assert.Equal(("LOT-NEW", 3), (created.LotNumber, created.Quantity));
    }
}
