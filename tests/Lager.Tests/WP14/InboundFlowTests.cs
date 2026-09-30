using Lager.Contracts.Inbound;
using Lager.Domain.Inbound;
using Lager.Domain.Stock;
using Microsoft.EntityFrameworkCore;

namespace Lager.Tests.WP14;

/// <summary>
/// Wareneingang: lot-genaue Buchung über den einheitlichen Buchungsweg. Zwei Chargen im selben Lagerplatz sind
/// zwei Bestandszeilen mit Charge und MHD, gleiche Chargen werden zusammengeführt, dieselbe Charge mit anderem MHD
/// wird abgelehnt, und ein zweites Buchen bucht nichts mehr.
/// </summary>
public class InboundFlowTests : IClassFixture<StockApiFixture>
{
    private readonly StockWorld _w;

    public InboundFlowTests(StockApiFixture fixture) => _w = fixture.World;

    private static readonly DateTime ExpiryA = StockWorld.InDays(100);
    private static readonly DateTime ExpiryB = StockWorld.InDays(200);

    [Fact]
    public async Task Two_receipts_with_lot_A_and_lot_B_in_the_same_bin_create_two_stock_rows_and_two_movements()
    {
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync(priceCents: 100);

        await _w.ReceiveAsync((article, bin, 5, "LOT-A", ExpiryA));
        await _w.ReceiveAsync((article, bin, 7, "LOT-B", ExpiryB));

        var rows = await _w.RowsAsync(article, bin);
        Assert.Equal(2, rows.Count);
        var a = Assert.Single(rows, r => r.LotNumber == "LOT-A");
        var b = Assert.Single(rows, r => r.LotNumber == "LOT-B");
        Assert.Equal((5, ExpiryA), (a.Quantity, a.ExpiryDate));
        Assert.Equal((7, ExpiryB), (b.Quantity, b.ExpiryDate));

        var movements = await _w.MovementsAsync(article);
        Assert.Equal(2, movements.Count);
        Assert.All(movements, m =>
        {
            Assert.Equal(StockMovementReason.Inbound, m.Reason);
            Assert.Equal("InboundShipment", m.ReferenceType);
            Assert.Equal(bin.Id, m.BinId);
        });
        Assert.Contains(movements, m => m is { LotNumber: "LOT-A", QuantityDelta: 5 } && m.ExpiryDate == ExpiryA);
        Assert.Contains(movements, m => m is { LotNumber: "LOT-B", QuantityDelta: 7 } && m.ExpiryDate == ExpiryB);

        // Eine dritte Lieferung derselben Charge A (gleiches MHD) landet auf derselben Zeile: kein Duplikat.
        await _w.ReceiveAsync((article, bin, 3, "LOT-A", ExpiryA));
        rows = await _w.RowsAsync(article, bin);
        Assert.Equal(2, rows.Count);
        Assert.Equal(8, rows.Single(r => r.LotNumber == "LOT-A").Quantity);
        Assert.Equal(3, (await _w.MovementsAsync(article)).Count);
    }

    [Fact]
    public async Task One_shipment_with_the_same_lot_twice_and_blank_lots_merges_in_memory_without_duplicate_rows()
    {
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync();

        // Zwei Zeilen derselben Charge/MHD (die erste ist beim Suchen noch nicht gespeichert) und zwei chargenlose
        // Zeilen, einmal "" und einmal null: vier Zeilen, zwei Bestandszeilen.
        await _w.ReceiveAsync(
            (article, bin, 4, "LOT-A", ExpiryA),
            (article, bin, 6, "LOT-A", ExpiryA),
            (article, bin, 2, "", null),
            (article, bin, 3, null, null));

        var rows = await _w.RowsAsync(article, bin);
        Assert.Equal(2, rows.Count);
        Assert.Equal(10, rows.Single(r => r.LotNumber == "LOT-A").Quantity);
        var lotless = rows.Single(r => r.LotNumber is null);
        Assert.Equal(5, lotless.Quantity);
        Assert.Equal(4, (await _w.MovementsAsync(article)).Count);
    }

    [Fact]
    public async Task The_same_lot_with_a_different_expiry_is_rejected_and_books_nothing()
    {
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync();
        await _w.ReceiveAsync((article, bin, 5, "LOT-A", ExpiryA));

        var draft = await _w.DraftInboundAsync((article, bin, 4, "LOT-A", ExpiryB));
        var ex = await ErrorAssert.ThrowsWithCodeAsync<InvalidOperationException>("lot_expiry_mismatch",
            () => _w.InboundAsync(s => s.ReceiveAsync(draft)));
        Assert.Contains("LOT-A", ex.Message);

        // nichts gebucht: Bestand und Ledger unverändert, die Lieferung ist weiter ein Entwurf
        Assert.Equal(5, await _w.QuantityAsync(article));
        Assert.Single(await _w.MovementsAsync(article));
        var status = await _w.DbAsync(db => db.InboundShipments.Where(s => s.Id == draft).Select(s => s.Status).SingleAsync());
        Assert.Equal(InboundShipmentStatus.Draft, status);
    }

    [Fact]
    public async Task Conflicting_expiry_for_one_lot_inside_a_single_shipment_is_rejected_when_the_line_is_added()
    {
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync();
        var shipment = await _w.InboundAsync(s => s.CreateAsync(new CreateInboundShipmentRequest(StockWorld.Unique("WE"), null, null)));
        await _w.InboundAsync(s => s.AddLineAsync(shipment.Id, new AddInboundLineRequest(article, bin.Id, 1, "LOT-A", ExpiryA)));

        await ErrorAssert.ThrowsWithCodeAsync<InvalidOperationException>("lot_expiry_mismatch",
            () => _w.InboundAsync(s => s.AddLineAsync(shipment.Id, new AddInboundLineRequest(article, bin.Id, 1, "LOT-A", ExpiryB))));
    }

    [Fact]
    public async Task Receiving_twice_is_rejected_and_books_no_second_time()
    {
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync();
        var draft = await _w.DraftInboundAsync((article, bin, 9, null, null));

        await _w.InboundAsync(s => s.ReceiveAsync(draft));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _w.InboundAsync(s => s.ReceiveAsync(draft)));

        Assert.Equal(9, await _w.QuantityAsync(article));
        Assert.Single(await _w.MovementsAsync(article));
    }

    [Fact]
    public async Task The_line_price_is_the_cost_snapshot_of_the_movement_and_the_article_price_is_the_fallback()
    {
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync(priceCents: 100);
        var shipment = await _w.InboundAsync(s => s.CreateAsync(new CreateInboundShipmentRequest(StockWorld.Unique("WE"), null, null)));
        await _w.InboundAsync(s => s.AddLineAsync(shipment.Id, new AddInboundLineRequest(article, bin.Id, 2, "LOT-A", ExpiryA, UnitCostCents: 250)));
        await _w.InboundAsync(s => s.AddLineAsync(shipment.Id, new AddInboundLineRequest(article, bin.Id, 3, "LOT-B", ExpiryB)));

        await _w.InboundAsync(s => s.ReceiveAsync(shipment.Id));

        var movements = await _w.MovementsAsync(article);
        Assert.Equal(250, movements.Single(m => m.LotNumber == "LOT-A").UnitCostCents);
        Assert.Equal(100, movements.Single(m => m.LotNumber == "LOT-B").UnitCostCents);
    }

    [Fact]
    public async Task An_unknown_article_or_bin_is_reported_as_not_found()
    {
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync();
        var shipment = await _w.InboundAsync(s => s.CreateAsync(new CreateInboundShipmentRequest(StockWorld.Unique("WE"), null, null)));

        await ErrorAssert.ThrowsWithCodeAsync<KeyNotFoundException>("article_not_found",
            () => _w.InboundAsync(s => s.AddLineAsync(shipment.Id, new AddInboundLineRequest(Guid.NewGuid(), bin.Id, 1, null, null))));
        await ErrorAssert.ThrowsWithCodeAsync<KeyNotFoundException>("bin_not_found",
            () => _w.InboundAsync(s => s.AddLineAsync(shipment.Id, new AddInboundLineRequest(article, Guid.NewGuid(), 1, null, null))));
        Assert.Null(await _w.InboundAsync(s => s.ReceiveAsync(Guid.NewGuid())));
    }
}
