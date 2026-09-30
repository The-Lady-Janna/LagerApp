using System.Net;
using Lager.Contracts.Reports;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP23;

/// <summary>
/// GET /api/reports/charge/{lot} (Charge-Trace) mit zwei Chargen desselben Artikels im selben Lagerplatz: jede Charge zeigt nur ihre
/// eigenen Wareneingänge, ihren Bestand, ihre Bewegungen und die daraus ableitbaren Bestellungen - mit Lagerplatz-Codes für die Anzeige.
/// </summary>
public class ChargeTraceTests : IClassFixture<Wp23Fixture>
{
    private readonly Wp23Fixture _fx;
    private WorldBuilder W => _fx.W;
    private WorldBuilder.World Site => _fx.Warehouse;

    public ChargeTraceTests(Wp23Fixture fixture) => _fx = fixture;

    private static async Task<ChargeTraceDto> TraceAsync(HttpClient client, string lot) =>
        await (await client.GetAsync($"/api/reports/charge/{Uri.EscapeDataString(lot)}")).ExpectAsync<ChargeTraceDto>();

    [Fact]
    public async Task Two_lots_in_one_bin_are_traced_separately_with_inbound_stock_movements_and_orders()
    {
        var admin = await W.AdminAsync();
        var article = await W.AddArticleAsync();
        var lotA = WorldBuilder.Unique("LOT-A");
        var lotB = WorldBuilder.Unique("LOT-B");
        var expiryA = WorldBuilder.InDays(40);
        var expiryB = WorldBuilder.InDays(120);
        var shipment = await admin.ReceiveWithLinesAsync(
            new Wp23Api.Line(article.Id, Site.PickA, 10, lotA, expiryA),
            new Wp23Api.Line(article.Id, Site.PickA, 5, lotB, expiryB));

        // Bestellung über 4 Stück: FEFO nimmt die Charge mit dem früheren MHD (A)
        var order = await admin.PackedOrderAsync(article.Id, 4);

        var a = await TraceAsync(admin, lotA);
        Assert.Equal(lotA, a.LotNumber);
        var inbound = Assert.Single(a.Inbounds);
        Assert.Equal((shipment.ShipmentNumber, 10, expiryA, "Received", Site.PickA.Code),
            (inbound.ShipmentNumber, inbound.Quantity, inbound.ExpiryDate, inbound.Status, inbound.TargetBinCode));
        var stock = Assert.Single(a.CurrentStock);
        Assert.Equal((6, expiryA, Site.PickA.Code), (stock.Quantity, stock.ExpiryDate, stock.BinCode));
        Assert.Equal((6, 10), (a.CurrentStockTotal, a.InboundTotal));
        // Ledger: Zugang +10 (Inbound) und Abgang -4 (Pick), chronologisch, mit Lagerplatz-Code und Artikel
        Assert.Equal(new[] { (10, "Inbound"), (-4, "Pick") }, a.Movements.Select(m => (m.QuantityDelta, m.Reason)));
        Assert.All(a.Movements, m => Assert.Equal((Site.PickA.Code, article.Sku), (m.BinCode, m.ArticleSku)));
        Assert.Equal("PickList", a.Movements[1].ReferenceType);
        // betroffene Bestellung: über die Pick-Buchung -> Pickliste -> Position
        var affected = Assert.Single(a.Orders!);
        Assert.Equal((order.Id, order.OrderNumber, "Kunde WP23"), (affected.OrderId, affected.OrderNumber, affected.CustomerReference));
        Assert.False(string.IsNullOrEmpty(affected.PickListNumber));

        // Charge B: unberührt vom Pick - nur ihr Eingang, ihr Bestand, ihre eine Bewegung, keine Bestellung
        var b = await TraceAsync(admin, lotB);
        Assert.Equal((5, 5), (b.CurrentStockTotal, b.InboundTotal));
        Assert.Equal(expiryB, Assert.Single(b.CurrentStock).ExpiryDate);
        Assert.Equal(new[] { (5, "Inbound") }, b.Movements.Select(m => (m.QuantityDelta, m.Reason)));
        Assert.Empty(b.Orders!);
    }

    [Fact]
    public async Task A_consumed_lot_is_still_traceable_an_unknown_lot_is_404_and_a_blank_one_400()
    {
        var admin = await W.AdminAsync();
        var article = await W.AddArticleAsync();
        var lot = WorldBuilder.Unique("LOT-X");
        await admin.ReceiveWithLinesAsync(new Wp23Api.Line(article.Id, Site.PickB, 3, lot, WorldBuilder.InDays(20)));
        var order = await admin.PackedOrderAsync(article.Id, 3);

        // der Bestand ist weg, der Ledger bleibt: Eingang, Abgang und die Bestellung sind weiter auffindbar
        var trace = await TraceAsync(admin, lot);
        Assert.Empty(trace.CurrentStock);
        Assert.Equal((0, 3), (trace.CurrentStockTotal, trace.InboundTotal));
        Assert.Equal(new[] { 3, -3 }, trace.Movements.Select(m => m.QuantityDelta));
        Assert.Equal(order.Id, Assert.Single(trace.Orders!).OrderId);

        // Leerraum um die Nummer stört nicht (wie beim Bestand: getrimmt)
        Assert.Equal(lot, (await TraceAsync(admin, $"  {lot} ")).LotNumber);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/reports/charge/GIBT-ES-NICHT")).StatusCode);
        // eine leere Nummer ist kein Suchbegriff: 400 statt 404
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/reports/charge/%20")).StatusCode);
    }

    [Fact]
    public async Task A_lot_number_with_special_characters_is_found_when_the_client_encodes_it()
    {
        var admin = await W.AdminAsync();
        var article = await W.AddArticleAsync();
        var lot = WorldBuilder.Unique("2026-09 #A+B é");
        await admin.ReceiveWithLinesAsync(new Wp23Api.Line(article.Id, Site.PickA, 2, lot, WorldBuilder.InDays(90)));

        var trace = await TraceAsync(admin, lot);

        Assert.Equal(lot, trace.LotNumber);
        Assert.Equal(2, trace.CurrentStockTotal);
    }

    [Fact]
    public async Task Draft_and_cancelled_receipts_are_listed_with_their_status_but_do_not_count_as_received()
    {
        var admin = await W.AdminAsync();
        var article = await W.AddArticleAsync();
        var lot = WorldBuilder.Unique("LOT-D");
        await admin.ReceiveWithLinesAsync(new Wp23Api.Line(article.Id, Site.PickA, 7, lot, WorldBuilder.InDays(60)));
        // zweite Lieferung derselben Charge bleibt ein Entwurf, eine dritte wird storniert
        await admin.DraftWithLinesAsync(new Wp23Api.Line(article.Id, Site.PickA, 100, lot, WorldBuilder.InDays(60)));
        var cancelled = await admin.DraftWithLinesAsync(new Wp23Api.Line(article.Id, Site.PickA, 50, lot, WorldBuilder.InDays(60)));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/inbound/{cancelled.Id}/cancel", null)).StatusCode);

        var trace = await TraceAsync(admin, lot);

        Assert.Equal(3, trace.Inbounds.Count);
        Assert.Equal(new[] { "Cancelled", "Draft", "Received" }, trace.Inbounds.Select(i => i.Status).Order(StringComparer.Ordinal));
        Assert.Equal((7, 7), (trace.InboundTotal, trace.CurrentStockTotal));
    }
}
