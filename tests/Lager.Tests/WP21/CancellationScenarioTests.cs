using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Inbound;
using Lager.Contracts.Purchasing;
using Lager.Contracts.Shipping;
using Lager.Domain.Stock;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP21;

/// <summary>
/// Szenario 3: die Storno-Pfade. Bis zum Packen ist noch nichts gebucht, ein Storno hat also keinen Bestandseffekt und
/// nimmt die Positionen von den Picklisten; ab Packed ist der Bestand abgebucht und die Bestellung nicht mehr stornierbar.
/// Dazu: Sendung, Wareneingang und Einkaufsbestellung.
/// </summary>
public class CancellationScenarioTests : IClassFixture<WorkflowFixture>
{
    private readonly WorkflowFixture _fx;
    private WorldBuilder W => _fx.W;
    private WorldBuilder.World Site => _fx.Warehouse;

    public CancellationScenarioTests(WorkflowFixture fixture) => _fx = fixture;

    private static async Task<HttpResponseMessage> CancelOrderAsync(HttpClient manager, Guid orderId) =>
        await manager.PostAsync($"/api/orders/{orderId}/cancel", null);

    [Fact]
    public async Task Cancelling_an_order_before_packing_frees_it_without_any_stock_effect()
    {
        var manager = await W.ClientAsync("Manager");
        var picker = await W.ClientAsync("Picker");
        var packer = await W.ClientAsync("Packer");
        var article = await W.AddArticleAsync();
        await manager.ReceiveAsync(new ApiCalls.Receipt(article.Id, Site.PickA, 100));
        var movementsAtStart = (await W.MovementsAsync(article.Id)).Count;

        // New: storniert, danach nicht mehr kommissionierbar
        var fresh = await manager.PlaceOrderAsync(article.Id, 5);
        Assert.Equal("Cancelled", (await (await CancelOrderAsync(manager, fresh.Id)).ExpectAsync<Lager.Contracts.Orders.OrderDto>()).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.GenerateRawAsync(fresh.Id)).StatusCode);

        // Zwei Bestellungen auf einer Liste: das Storno der einen nimmt nur deren Positionen heraus, die andere wird normal gepackt
        var kept = await manager.PlaceOrderAsync(article.Id, 4);
        var dropped = await manager.PlaceOrderAsync(article.Id, 6);
        var shared = await manager.GenerateAsync(kept.Id, dropped.Id);
        Assert.Equal(10, shared.Items.Sum(i => i.Quantity));
        await (await CancelOrderAsync(manager, dropped.Id)).ExpectAsync<Lager.Contracts.Orders.OrderDto>();
        shared = await manager.GetPickListAsync(shared.Id);
        Assert.Equal("Pending", shared.Status);
        Assert.All(shared.Items, i => Assert.Equal(kept.OrderNumber, i.OrderNumber));
        Assert.Equal(4, shared.Items.Sum(i => i.Quantity));
        await packer.PackAsync(shared);
        Assert.Equal(96, await manager.TotalStockAsync(article.Id));
        Assert.Equal("Packed", (await manager.GetOrderAsync(kept.Id)).Status);
        Assert.Equal("Cancelled", (await manager.GetOrderAsync(dropped.Id)).Status);

        // Die einzige Bestellung einer Liste (Picking): die Liste wird mit storniert und lässt sich weder melden noch packen
        var alone = await manager.PlaceOrderAsync(article.Id, 3);
        var lonely = await manager.GenerateAsync(alone.Id);
        await (await CancelOrderAsync(manager, alone.Id)).ExpectAsync<Lager.Contracts.Orders.OrderDto>();
        Assert.Equal("Cancelled", (await manager.GetPickListAsync(lonely.Id)).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await picker.MarkPickedRawAsync(lonely.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await packer.PackRawAsync(lonely)).StatusCode);

        // Picked (Picken schon gemeldet): dasselbe
        var pickedOrder = await manager.PlaceOrderAsync(article.Id, 2);
        var pickedList = await manager.GenerateAsync(pickedOrder.Id);
        await picker.MarkPickedAsync(pickedList.Id);
        await (await CancelOrderAsync(manager, pickedOrder.Id)).ExpectAsync<Lager.Contracts.Orders.OrderDto>();
        Assert.Equal("Cancelled", (await manager.GetPickListAsync(pickedList.Id)).Status);

        // Bestand: nur die gepackte Bestellung hat gebucht (4), alles andere ließ ihn unberührt
        Assert.Equal(96, await manager.TotalStockAsync(article.Id));
        var movements = await W.MovementsAsync(article.Id);
        Assert.Equal(movementsAtStart + 1, movements.Count);
        Assert.Equal(-4, movements.Single(m => m.Reason == StockMovementReason.Pick).QuantityDelta);
        Assert.Empty(await W.LedgerViolationsAsync(article.Id));
    }

    [Fact]
    public async Task A_packed_or_shipped_or_already_cancelled_order_cannot_be_cancelled_and_keeps_its_booking()
    {
        var manager = await W.ClientAsync("Manager");
        var packer = await W.ClientAsync("Packer");
        var article = await W.AddArticleAsync();
        await manager.ReceiveAsync(new ApiCalls.Receipt(article.Id, Site.PickA, 50));

        var (packedOrder, _) = await manager.PackedOrderAsync(article.Id, 5);
        var (shippedOrder, _) = await manager.PackedOrderAsync(article.Id, 3);
        await packer.ShipAsync(shippedOrder.Id);
        var cancelled = await manager.PlaceOrderAsync(article.Id, 1);
        await CancelOrderAsync(manager, cancelled.Id);

        foreach (var (order, status) in new[] { (packedOrder, "Packed"), (shippedOrder, "Shipped"), (cancelled, "Cancelled") })
        {
            var response = await CancelOrderAsync(manager, order.Id);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("order_not_cancellable", (await response.BodyAsync()).CodeOf());
            Assert.Equal(status, (await manager.GetOrderAsync(order.Id)).Status);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await CancelOrderAsync(manager, Guid.NewGuid())).StatusCode);

        // die beiden gepackten Bestellungen haben gebucht (5 + 3), die Stornoversuche ändern daran nichts
        Assert.Equal(42, await manager.TotalStockAsync(article.Id));
        Assert.Empty(await W.LedgerViolationsAsync(article.Id));
    }

    [Fact]
    public async Task Cancelling_a_shipment_keeps_the_order_packed_until_a_replacement_ships()
    {
        var manager = await W.ClientAsync("Manager");
        var packer = await W.ClientAsync("Packer");
        var article = await W.AddArticleAsync();
        await manager.ReceiveAsync(new ApiCalls.Receipt(article.Id, Site.PickA, 20));
        var (order, _) = await manager.PackedOrderAsync(article.Id, 2);

        var first = await packer.CreateShipmentAsync(order.Id);
        Assert.Equal("Ready", first.Status);
        await (await manager.PostAsync($"/api/shipments/{first.Id}/cancel", null)).ExpectStatusAsync(HttpStatusCode.NoContent);

        // die Bestellung wird nie zurückgesetzt; auf der stornierten Sendung ist nichts mehr möglich
        Assert.Equal("Packed", (await manager.GetOrderAsync(order.Id)).Status);
        Assert.Equal("Cancelled", (await (await manager.GetAsync($"/api/shipments/{first.Id}")).ExpectAsync<ShipmentDto>()).Status);
        Assert.Equal(HttpStatusCode.Conflict,
            (await packer.PostAsJsonAsync($"/api/shipments/{first.Id}/tracking", new AssignTrackingRequest("TRACK-STORNO"))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await packer.PostAsync($"/api/shipments/{first.Id}/ship", null)).StatusCode);

        // eine Ersatzsendung schließt die Bestellung ab
        var replacement = await packer.ShipAsync(order.Id);
        Assert.Equal("Shipped", replacement.Status);
        Assert.Equal("Shipped", (await manager.GetOrderAsync(order.Id)).Status);

        // für nicht gepackte Bestellungen gibt es keine Sendung
        var fresh = await manager.PlaceOrderAsync(article.Id, 1);
        var response = await packer.PostAsJsonAsync("/api/shipments", new CreateShipmentRequest(fresh.Id, null, "MANUAL", 100, 100, 100, 500));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("order_not_packed", (await response.BodyAsync()).CodeOf());
        Assert.Empty(await W.LedgerViolationsAsync(article.Id));
    }

    [Fact]
    public async Task Cancelling_an_inbound_draft_or_a_purchase_order_books_nothing_and_blocks_further_receipt()
    {
        var manager = await W.ClientAsync("Manager");
        var receiver = await W.ClientAsync("Receiver");
        var supplierId = await W.AddSupplierAsync();
        var article = await W.AddArticleAsync(supplierId: supplierId, priceCents: 50);

        // Wareneingang im Entwurf storniert: nicht mehr buchbar, kein Bestand, kein Ledger-Eintrag
        var draft = await receiver.DraftInboundAsync(new ApiCalls.Receipt(article.Id, Site.PickA, 9));
        await (await manager.PostAsync($"/api/inbound/{draft.Id}/cancel", null)).ExpectStatusAsync(HttpStatusCode.NoContent);
        Assert.Equal("Cancelled", (await (await manager.GetAsync($"/api/inbound/{draft.Id}")).ExpectAsync<InboundShipmentDto>()).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await receiver.PostAsync($"/api/inbound/{draft.Id}/receive", null)).StatusCode);
        Assert.Empty(await receiver.StockOfAsync(article.Id));
        Assert.Empty(await W.MovementsAsync(article.Id));

        // Bestellung versendet und dann storniert: sie erwartet keine Ware mehr
        var po = await (await manager.PostAsJsonAsync("/api/purchase-orders", new CreatePurchaseOrderRequest(
            supplierId, null, null, new[] { new CreatePurchaseOrderLineRequest(article.Id, 20) }))).ExpectAsync<PurchaseOrderDto>(HttpStatusCode.Created);
        await (await manager.PostAsync($"/api/purchase-orders/{po.Id}/send", null)).ExpectAsync<PurchaseOrderDto>();
        await (await manager.PostAsync($"/api/purchase-orders/{po.Id}/cancel", null)).ExpectStatusAsync(HttpStatusCode.NoContent);
        Assert.Equal("Cancelled", (await (await manager.GetAsync($"/api/purchase-orders/{po.Id}")).ExpectAsync<PurchaseOrderDto>()).Status);
        var afterwards = await receiver.PostAsJsonAsync($"/api/purchase-orders/{po.Id}/create-inbound", new CreateInboundFromPurchaseOrderRequest(Site.PickA.Id));
        Assert.Equal(HttpStatusCode.Conflict, afterwards.StatusCode);
        Assert.Equal("po_not_receivable", (await afterwards.BodyAsync()).CodeOf());
        Assert.Empty(await W.MovementsAsync(article.Id));
    }
}
