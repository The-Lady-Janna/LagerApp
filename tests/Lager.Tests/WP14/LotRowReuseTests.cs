using Lager.Contracts.Returns;
using Lager.Domain.Stock;

namespace Lager.Tests.WP14;

/// <summary>
/// Review-Nachtrag: Eine entleerte Bestandszeile einer Charge (Menge 0, MHD gesetzt) bleibt stehen und ist über den
/// Unique-Index (Artikel, Lagerplatz, Charge) für genau diese Charge reserviert. Zugänge, die das MHD nicht nennen
/// (Retouren kennen kein MHD), müssen diese Zeile wiederverwenden, statt eine zweite anzulegen (Unique-Verletzung).
/// </summary>
public class LotRowReuseTests : IClassFixture<StockApiFixture>
{
    private readonly StockWorld _w;

    public LotRowReuseTests(StockApiFixture fixture) => _w = fixture.World;

    private static readonly DateTime Expiry = StockWorld.InDays(100);

    private async Task ReturnSellableAsync(Guid article, StockWorld.Bin bin, int quantity, string? lot)
    {
        var ret = await _w.ReturnsAsync(s => s.CreateAsync(new CreateReturnShipmentRequest(null, null, null,
            new[] { new CreateReturnLineRequest(article, quantity, lot) })));
        await _w.ReturnsAsync(s => s.SetQcAsync(ret.Id, ret.Lines.Single().Id, new SetQcRequest("Sellable", bin.Id, null)));
        await _w.ReturnsAsync(s => s.ProcessAsync(ret.Id));
    }

    [Fact]
    public async Task A_return_of_a_lot_known_elsewhere_refills_the_emptied_row_of_the_lot()
    {
        var site = await _w.AddSiteAsync();
        var emptied = await _w.AddBinAsync(site);
        var other = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync();
        await _w.AddStockAsync(article, emptied, 0, "LOT-Q", Expiry);      // Charge hier komplett gepickt: Zeile bleibt mit 0
        await _w.AddStockAsync(article, other, 5, "LOT-Q", Expiry);        // dieselbe Charge, anderswo noch da

        await ReturnSellableAsync(article, emptied, 1, "LOT-Q");

        var row = Assert.Single(await _w.RowsAsync(article, emptied));
        Assert.Equal((1, "LOT-Q", Expiry), (row.Quantity, row.LotNumber, row.ExpiryDate));
    }

    [Fact]
    public async Task A_return_of_a_sold_out_lot_refills_the_emptied_row_with_the_expiry_from_the_ledger()
    {
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync();

        // Die Charge kommt mit MHD ins Lager und wird komplett entnommen: die Zeile bleibt mit Menge 0 stehen.
        await _w.ReceiveAsync((article, bin, 3, "LOT-Q", Expiry));
        await _w.StockAsync(s => s.AdjustAsync(new Lager.Contracts.Stock.AdjustStockRequest(article, bin.Id, -3, "LOT-Q", null)));
        Assert.Equal(0, (await _w.RowsAsync(article, bin)).Single().Quantity);

        await ReturnSellableAsync(article, bin, 2, "LOT-Q");

        var row = Assert.Single(await _w.RowsAsync(article, bin));
        Assert.Equal((2, "LOT-Q", Expiry), (row.Quantity, row.LotNumber, row.ExpiryDate));
        Assert.All(await _w.MovementsAsync(article), m => Assert.Equal(Expiry, m.ExpiryDate));
    }

    [Fact]
    public async Task A_bundle_can_not_be_returned_even_without_an_order()
    {
        var component = await _w.AddArticleAsync();
        var bundle = await _w.AddBundleAsync((component, 2));

        await ErrorAssert.ThrowsWithCodeAsync<InvalidOperationException>("return_bundle_not_allowed", () =>
            _w.ReturnsAsync(s => s.CreateAsync(new CreateReturnShipmentRequest(null, null, null,
                new[] { new CreateReturnLineRequest(bundle, 1, null) }))));

        var ret = await _w.ReturnsAsync(s => s.CreateAsync(new CreateReturnShipmentRequest(null, null, null,
            new[] { new CreateReturnLineRequest(component, 1, null) })));
        await ErrorAssert.ThrowsWithCodeAsync<InvalidOperationException>("return_bundle_not_allowed", () =>
            _w.ReturnsAsync(s => s.AddLineAsync(ret.Id, new AddReturnLineRequest(bundle, 1))));
    }
}
