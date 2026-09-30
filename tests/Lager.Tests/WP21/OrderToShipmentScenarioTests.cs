using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Auth;
using Lager.Contracts.Shipping;
using Lager.Domain.Stock;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP21;

/// <summary>
/// Szenario 2: der Hauptweg Bestellung -> Pickliste -> Picken abschließen -> Packen -> Sendung -> Versendet. Jeder Schritt
/// läuft mit der Rolle, die ihn im Lager tatsächlich ausführt (Manager erzeugt, Picker meldet fertig, Packer packt und versendet).
/// Geprüft wird der Zustand nach jedem Schritt: Bestellstatus, Bestand, Ledger.
/// </summary>
public class OrderToShipmentScenarioTests : IClassFixture<WorkflowFixture>
{
    private readonly WorkflowFixture _fx;
    private WorldBuilder W => _fx.W;
    private WorldBuilder.World Site => _fx.Warehouse;

    public OrderToShipmentScenarioTests(WorkflowFixture fixture) => _fx = fixture;

    [Fact]
    public async Task Order_to_picklist_to_pack_to_shipment_reduces_stock_once_writes_movements_and_ends_shipped()
    {
        var manager = await W.ClientAsync("Manager");
        var picker = await W.ClientAsync("Picker");
        var packer = await W.ClientAsync("Packer");
        var article = await W.AddArticleAsync(priceCents: 120);
        var expiry = WorldBuilder.InDays(45);
        await manager.ReceiveAsync(
            new ApiCalls.Receipt(article.Id, Site.PickA, 40, "LOT-1", expiry),
            new ApiCalls.Receipt(article.Id, Site.PickB, 10));

        // Bestellung: New, der Bestand deckt sie
        var order = await manager.PlaceOrderAsync(article.Id, 12);
        Assert.Equal("New", order.Status);
        Assert.True(order.HasStockNow);

        // Pickliste: die Bestellung geht auf Picking, gebucht wird noch nichts
        var list = await manager.GenerateAsync(order.Id);
        Assert.Equal("Pending", list.Status);
        Assert.StartsWith("PL-", list.PickListNumber);
        Assert.Equal(12, list.Items.Sum(i => i.Quantity));
        Assert.All(list.Items, i => Assert.Equal(order.OrderNumber, i.OrderNumber));
        Assert.Equal("Picking", (await manager.GetOrderAsync(order.Id)).Status);
        Assert.Equal(50, await manager.TotalStockAsync(article.Id));

        // Picken abgeschlossen: der Picker steht an der Liste (Basis der Picker-Auswertung)
        var pickerName = (await (await picker.GetAsync("/api/auth/me")).ExpectAsync<UserDto>()).Username;
        var picked = await picker.MarkPickedAsync(list.Id);
        Assert.Equal(("Picked", pickerName), (picked.Status, picked.AssignedTo));
        Assert.Equal("Picked", (await manager.GetOrderAsync(order.Id)).Status);
        Assert.Equal(50, await manager.TotalStockAsync(article.Id));

        // Packen: erst hier wird abgebucht - genau einmal, mit einer Movement je Bestandszeile (FEFO, Charge und MHD bleiben)
        var packed = await packer.PackAsync(list);
        Assert.Equal("Completed", packed.Status);
        Assert.All(packed.Items, i => Assert.Equal(i.Quantity, i.ConfirmedQuantity));
        Assert.Equal("Packed", (await manager.GetOrderAsync(order.Id)).Status);
        Assert.Equal(38, await manager.TotalStockAsync(article.Id));

        var picks = (await W.MovementsAsync(article.Id)).Where(m => m.Reason == StockMovementReason.Pick).ToList();
        Assert.Equal(-12, picks.Sum(m => m.QuantityDelta));
        Assert.All(picks, m =>
        {
            Assert.Equal(("PickList", (Guid?)list.Id, 120), (m.ReferenceType, m.ReferenceId, m.UnitCostCents));
            Assert.True(m.QuantityDelta < 0);
        });
        Assert.Empty(await W.LedgerViolationsAsync(article.Id));

        // Sendung: anlegen, Tracking, versenden -> die Bestellung ist Shipped
        var shipment = await packer.ShipAsync(order.Id);
        Assert.Equal("Shipped", shipment.Status);
        Assert.Equal(order.Id, shipment.OrderId);
        Assert.NotNull(shipment.ShippedAt);
        Assert.StartsWith("TRACK-", shipment.TrackingNumber);
        Assert.Equal("Shipped", (await manager.GetOrderAsync(order.Id)).Status);
        Assert.Equal("Shipped", (await (await manager.GetAsync($"/api/shipments/{shipment.Id}")).ExpectAsync<ShipmentDto>()).Status);

        // Versand ändert den Bestand nicht mehr
        Assert.Equal(38, await manager.TotalStockAsync(article.Id));
        Assert.Empty(await W.LedgerViolationsAsync(article.Id));

        // Lieferschein: ein PDF
        var label = await manager.GetAsync($"/api/picklists/{list.Id}/shipping-label.pdf");
        Assert.Equal(HttpStatusCode.OK, label.StatusCode);
        Assert.Equal("application/pdf", label.Content.Headers.ContentType!.MediaType);
        Assert.Equal("%PDF", System.Text.Encoding.ASCII.GetString((await label.Content.ReadAsByteArrayAsync()).AsSpan(0, 4)));
    }

    [Fact]
    public async Task Packing_fewer_than_planned_books_only_what_was_confirmed_and_more_than_planned_is_rejected()
    {
        var manager = await W.ClientAsync("Manager");
        var packer = await W.ClientAsync("Packer");
        var article = await W.AddArticleAsync();
        await manager.ReceiveAsync(new ApiCalls.Receipt(article.Id, Site.PickA, 30));
        var order = await manager.PlaceOrderAsync(article.Id, 10);
        var list = await manager.GenerateAsync(order.Id);

        // mehr als geplant: abgelehnt, nichts gebucht, die Liste bleibt offen
        var tooMany = await packer.PackRawAsync(list, i => i.Quantity + 1);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal(30, await manager.TotalStockAsync(article.Id));
        Assert.Equal("Pending", (await manager.GetPickListAsync(list.Id)).Status);

        // 7 von 10 tatsächlich gepackt: nur 7 werden abgebucht, die Bestellung gilt trotzdem als gepackt
        var packed = await packer.PackAsync(list, _ => 7);
        Assert.Equal("Completed", packed.Status);
        Assert.Equal(7, packed.Items.Single().ConfirmedQuantity);
        Assert.Equal(23, await manager.TotalStockAsync(article.Id));
        Assert.Equal(-7, (await W.MovementsAsync(article.Id)).Where(m => m.Reason == StockMovementReason.Pick).Sum(m => m.QuantityDelta));
        Assert.Equal("Packed", (await manager.GetOrderAsync(order.Id)).Status);
        Assert.Empty(await W.LedgerViolationsAsync(article.Id));
    }

    [Fact]
    public async Task Packing_more_than_the_shelf_holds_is_rejected_and_leaves_stock_and_order_untouched()
    {
        var manager = await W.ClientAsync("Manager");
        var packer = await W.ClientAsync("Packer");
        var article = await W.AddArticleAsync();
        await manager.ReceiveAsync(new ApiCalls.Receipt(article.Id, Site.PickA, 10));
        var order = await manager.PlaceOrderAsync(article.Id, 8);
        var list = await manager.GenerateAsync(order.Id);

        // Der Platz wurde nach dem Erzeugen der Liste leergeräumt (Korrektur -6): der Bestand reicht nicht mehr für 8.
        await (await manager.PostAsJsonAsync("/api/stock/adjust",
            new Lager.Contracts.Stock.AdjustStockRequest(article.Id, Site.PickA.Id, -6, null, null))).ExpectAsync<Lager.Contracts.Stock.StockItemDto>();

        var response = await packer.PackRawAsync(list);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(4, await manager.TotalStockAsync(article.Id));
        Assert.Equal("Picking", (await manager.GetOrderAsync(order.Id)).Status);
        Assert.Equal("Pending", (await manager.GetPickListAsync(list.Id)).Status);
        Assert.DoesNotContain(await W.MovementsAsync(article.Id), m => m.Reason == StockMovementReason.Pick);
        Assert.Empty(await W.LedgerViolationsAsync(article.Id));
    }
}
