using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Inbound;
using Lager.Contracts.Purchasing;
using Lager.Domain.Stock;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP21;

/// <summary>
/// Szenarien 1 und 7: Wareneingang mit Charge und MHD (Bestand je Charge, Ledger) und der Einkaufsweg
/// Bestellung -> Wareneingang -> Buchen (auch in zwei Teillieferungen). Alles über HTTP mit den passenden Rollen.
/// </summary>
public class GoodsReceiptScenarioTests : IClassFixture<WorkflowFixture>
{
    private readonly WorkflowFixture _fx;
    private WorldBuilder W => _fx.W;
    private WorldBuilder.World Site => _fx.Warehouse;

    public GoodsReceiptScenarioTests(WorkflowFixture fixture) => _fx = fixture;

    [Fact]
    public async Task Goods_receipt_with_lot_and_expiry_books_stock_per_lot_and_one_movement_per_line()
    {
        var receiver = await W.ClientAsync("Receiver");
        var article = await W.AddArticleAsync(priceCents: 250);
        var expiryA = WorldBuilder.InDays(60);
        var expiryB = WorldBuilder.InDays(120);

        // Entwurf: gebucht ist noch nichts
        var draft = await receiver.DraftInboundAsync(
            new ApiCalls.Receipt(article.Id, Site.PickA, 30, "LOT-A", expiryA),
            new ApiCalls.Receipt(article.Id, Site.PickA, 20, "LOT-B", expiryB),
            new ApiCalls.Receipt(article.Id, Site.PickB, 5));
        Assert.Equal("Draft", draft.Status);
        Assert.Equal(3, draft.Lines.Count);
        Assert.Empty(await receiver.StockOfAsync(article.Id));

        // Buchen: Bestand je (Platz, Charge), Charge und MHD stehen an der Bestandszeile
        var received = await (await receiver.PostAsync($"/api/inbound/{draft.Id}/receive", null)).ExpectAsync<InboundShipmentDto>();
        Assert.Equal("Received", received.Status);
        Assert.NotNull(received.ReceivedAt);

        var stock = await receiver.StockOfAsync(article.Id);
        Assert.Equal(3, stock.Count);
        var lotA = Assert.Single(stock, s => s.LotNumber == "LOT-A");
        var lotB = Assert.Single(stock, s => s.LotNumber == "LOT-B");
        var lotless = Assert.Single(stock, s => s.LotNumber is null);
        Assert.Equal((30, expiryA, Site.PickA.Id), (lotA.Quantity, lotA.ExpiryDate, lotA.StorageLocationId));
        Assert.Equal((20, expiryB, Site.PickA.Id), (lotB.Quantity, lotB.ExpiryDate, lotB.StorageLocationId));
        Assert.Equal((5, (DateTime?)null, Site.PickB.Id), (lotless.Quantity, lotless.ExpiryDate, lotless.StorageLocationId));

        // Ledger: je Zeile genau eine Movement (Inbound, Verweis auf die Lieferung, Kosten-Snapshot = Artikelpreis)
        var movements = await W.MovementsAsync(article.Id);
        Assert.Equal(3, movements.Count);
        Assert.All(movements, m =>
        {
            Assert.Equal(StockMovementReason.Inbound, m.Reason);
            Assert.Equal(("InboundShipment", (Guid?)draft.Id, 250), (m.ReferenceType, m.ReferenceId, m.UnitCostCents));
        });
        Assert.Contains(movements, m => m is { LotNumber: "LOT-A", QuantityDelta: 30 } && m.ExpiryDate == expiryA);
        Assert.Contains(movements, m => m is { LotNumber: "LOT-B", QuantityDelta: 20 } && m.ExpiryDate == expiryB);
        Assert.Empty(await W.LedgerViolationsAsync(article.Id));
    }

    [Fact]
    public async Task A_second_delivery_of_the_same_lot_merges_but_the_same_lot_with_another_expiry_is_rejected()
    {
        var receiver = await W.ClientAsync("Receiver");
        var article = await W.AddArticleAsync();
        var expiry = WorldBuilder.InDays(90);
        await receiver.ReceiveAsync(new ApiCalls.Receipt(article.Id, Site.PickA, 10, "LOT-X", expiry));

        // gleiche Charge, gleiches MHD: dieselbe Bestandszeile, kein Duplikat
        await receiver.ReceiveAsync(new ApiCalls.Receipt(article.Id, Site.PickA, 7, "LOT-X", expiry));
        var row = Assert.Single(await receiver.StockOfAsync(article.Id));
        Assert.Equal(17, row.Quantity);

        // gleiche Charge, anderes MHD: 409 mit Code, nichts gebucht, die Lieferung bleibt ein Entwurf
        var conflicting = await receiver.DraftInboundAsync(new ApiCalls.Receipt(article.Id, Site.PickA, 4, "LOT-X", expiry.AddDays(1)));
        var response = await receiver.PostAsync($"/api/inbound/{conflicting.Id}/receive", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("lot_expiry_mismatch", (await response.BodyAsync()).CodeOf());
        Assert.Equal(17, await receiver.TotalStockAsync(article.Id));
        Assert.Equal("Draft", (await (await receiver.GetAsync($"/api/inbound/{conflicting.Id}")).ExpectAsync<InboundShipmentDto>()).Status);
        Assert.Empty(await W.LedgerViolationsAsync(article.Id));
    }

    [Fact]
    public async Task Purchase_order_to_inbound_to_receive_books_the_stock_and_follows_partial_deliveries()
    {
        var manager = await W.ClientAsync("Manager");
        var receiver = await W.ClientAsync("Receiver");
        var supplierId = await W.AddSupplierAsync();
        var article = await W.AddArticleAsync(supplierId: supplierId, priceCents: 100);

        // Bestellung: anlegen (Entwurf), versenden
        var po = await (await manager.PostAsJsonAsync("/api/purchase-orders", new CreatePurchaseOrderRequest(
            supplierId, null, "E2E", new[] { new CreatePurchaseOrderLineRequest(article.Id, 100, 199) }))).ExpectAsync<PurchaseOrderDto>(HttpStatusCode.Created);
        Assert.Equal("Draft", po.Status);
        po = await (await manager.PostAsync($"/api/purchase-orders/{po.Id}/send", null)).ExpectAsync<PurchaseOrderDto>();
        Assert.Equal("Sent", po.Status);
        var poLine = po.Lines.Single();

        // Wareneingang aus der Bestellung: ein Entwurf mit der offenen Menge; ein zweiter offener Entwurf ist nicht erlaubt
        var inbound = await (await receiver.PostAsJsonAsync($"/api/purchase-orders/{po.Id}/create-inbound",
            new CreateInboundFromPurchaseOrderRequest(Site.Reserve.Id))).ExpectAsync<InboundShipmentDto>(HttpStatusCode.Created);
        Assert.Equal(po.Id, inbound.PurchaseOrderId);
        Assert.Equal((100, poLine.Id), (inbound.Lines.Single().Quantity, inbound.Lines.Single().PurchaseOrderLineId));
        var duplicate = await receiver.PostAsJsonAsync($"/api/purchase-orders/{po.Id}/create-inbound", new CreateInboundFromPurchaseOrderRequest(Site.Reserve.Id));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("inbound_draft_exists", (await duplicate.BodyAsync()).CodeOf());

        // Teillieferung: 60 Stück mit Charge und MHD (Zeile ersetzen, dieselbe Bestellzeile)
        var expiry = WorldBuilder.InDays(180);
        await (await receiver.DeleteAsync($"/api/inbound/{inbound.Id}/lines/{inbound.Lines.Single().Id}")).ExpectAsync<InboundShipmentDto>();
        await (await receiver.PostAsJsonAsync($"/api/inbound/{inbound.Id}/lines",
            new AddInboundLineRequest(article.Id, Site.Reserve.Id, 60, "LOT-PO1", expiry, poLine.Id))).ExpectAsync<InboundShipmentDto>();
        await (await receiver.PostAsync($"/api/inbound/{inbound.Id}/receive", null)).ExpectAsync<InboundShipmentDto>();

        po = await (await manager.GetAsync($"/api/purchase-orders/{po.Id}")).ExpectAsync<PurchaseOrderDto>();
        Assert.Equal("PartiallyReceived", po.Status);
        Assert.Equal(60, po.Lines.Single().ReceivedQty);
        var row = Assert.Single(await receiver.StockOfAsync(article.Id));
        Assert.Equal((60, "LOT-PO1", expiry), (row.Quantity, row.LotNumber, row.ExpiryDate));

        // Rest: der nächste Wareneingang bietet nur noch die offenen 40 an; mehr als offen ist abgelehnt
        var rest = await (await receiver.PostAsJsonAsync($"/api/purchase-orders/{po.Id}/create-inbound",
            new CreateInboundFromPurchaseOrderRequest(Site.Reserve.Id))).ExpectAsync<InboundShipmentDto>(HttpStatusCode.Created);
        Assert.Equal(40, rest.Lines.Single().Quantity);
        await (await receiver.DeleteAsync($"/api/inbound/{rest.Id}/lines/{rest.Lines.Single().Id}")).ExpectAsync<InboundShipmentDto>();
        await (await receiver.PostAsJsonAsync($"/api/inbound/{rest.Id}/lines",
            new AddInboundLineRequest(article.Id, Site.Reserve.Id, 41, "LOT-PO2", expiry, poLine.Id))).ExpectAsync<InboundShipmentDto>();
        var tooMuch = await receiver.PostAsync($"/api/inbound/{rest.Id}/receive", null);
        Assert.Equal(HttpStatusCode.Conflict, tooMuch.StatusCode);
        Assert.Equal("po_over_receipt", (await tooMuch.BodyAsync()).CodeOf());
        Assert.Equal(60, await receiver.TotalStockAsync(article.Id));

        // korrigiert auf die offenen 40: die Bestellung ist vollständig geliefert
        var lineId = (await (await receiver.GetAsync($"/api/inbound/{rest.Id}")).ExpectAsync<InboundShipmentDto>()).Lines.Single().Id;
        await (await receiver.DeleteAsync($"/api/inbound/{rest.Id}/lines/{lineId}")).ExpectAsync<InboundShipmentDto>();
        await (await receiver.PostAsJsonAsync($"/api/inbound/{rest.Id}/lines",
            new AddInboundLineRequest(article.Id, Site.Reserve.Id, 40, "LOT-PO2", expiry, poLine.Id))).ExpectAsync<InboundShipmentDto>();
        await (await receiver.PostAsync($"/api/inbound/{rest.Id}/receive", null)).ExpectAsync<InboundShipmentDto>();

        po = await (await manager.GetAsync($"/api/purchase-orders/{po.Id}")).ExpectAsync<PurchaseOrderDto>();
        Assert.Equal("Received", po.Status);
        Assert.Equal(100, po.Lines.Single().ReceivedQty);
        Assert.Equal(100, await receiver.TotalStockAsync(article.Id));
        Assert.Equal(new[] { 40, 60 }, (await receiver.StockOfAsync(article.Id)).Select(s => s.Quantity).Order());

        // Einkaufspreis der Bestellzeile ist der Kosten-Snapshot der Buchungen; Ledger und Bestand stimmen überein
        Assert.All(await W.MovementsAsync(article.Id), m => Assert.Equal(199, m.UnitCostCents));
        Assert.Empty(await W.LedgerViolationsAsync(article.Id));

        // eine vollständig gelieferte Bestellung erwartet keine Ware mehr
        var afterwards = await receiver.PostAsJsonAsync($"/api/purchase-orders/{po.Id}/create-inbound", new CreateInboundFromPurchaseOrderRequest(Site.Reserve.Id));
        Assert.Equal(HttpStatusCode.Conflict, afterwards.StatusCode);
        Assert.Equal("po_not_receivable", (await afterwards.BodyAsync()).CodeOf());
    }
}
