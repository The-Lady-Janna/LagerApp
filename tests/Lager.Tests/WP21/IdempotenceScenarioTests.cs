using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.PickLists;
using Lager.Domain.Stock;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP21;

/// <summary>
/// Szenario 8: Wiederholungen (Doppelklick, Retry) bewirken nichts Zweites. Doppeltes Packen, doppeltes Erzeugen der Pickliste
/// und doppeltes Buchen des Wareneingangs werden abgelehnt und lassen Bestand und Ledger unverändert.
/// </summary>
public class IdempotenceScenarioTests : IClassFixture<WorkflowFixture>
{
    private readonly WorkflowFixture _fx;
    private WorldBuilder W => _fx.W;
    private WorldBuilder.World Site => _fx.Warehouse;

    public IdempotenceScenarioTests(WorkflowFixture fixture) => _fx = fixture;

    [Fact]
    public async Task Repeated_generate_pack_and_receive_are_rejected_and_book_nothing_a_second_time()
    {
        var manager = await W.ClientAsync("Manager");
        var picker = await W.ClientAsync("Picker");
        var packer = await W.ClientAsync("Packer");
        var article = await W.AddArticleAsync();
        var inbound = await manager.ReceiveAsync(new ApiCalls.Receipt(article.Id, Site.PickA, 50));

        // Wareneingang zweimal buchen: das zweite Mal wird abgelehnt
        var again = await manager.PostAsync($"/api/inbound/{inbound.Id}/receive", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(50, await manager.TotalStockAsync(article.Id));

        // Pickliste zweimal erzeugen: die Bestellung steht schon auf Picking, es gibt genau eine Liste
        var order = await manager.PlaceOrderAsync(article.Id, 6);
        var list = await manager.GenerateAsync(order.Id);
        var second = await manager.GenerateRawAsync(order.Id);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal(1, await ListsWithOrderAsync(manager, order.OrderNumber));

        // Packen zweimal: genau eine Abbuchung, eine Movement
        await picker.MarkPickedAsync(list.Id);
        await packer.PackAsync(list);
        var repeated = await packer.PackRawAsync(list);
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
        Assert.Contains("bereits verpackt", await repeated.Content.ReadAsStringAsync());
        Assert.Equal(44, await manager.TotalStockAsync(article.Id));
        Assert.Equal(1, (await W.MovementsAsync(article.Id)).Count(m => m.Reason == StockMovementReason.Pick));

        // auf der verpackten Liste geht nichts mehr: melden, neu berechnen, für die verpackte Bestellung erneut erzeugen
        Assert.Equal(HttpStatusCode.Conflict, (await picker.MarkPickedRawAsync(list.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsJsonAsync($"/api/picklists/{list.Id}/recalculate", new RecalculatePickListRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.GenerateRawAsync(order.Id)).StatusCode);
        Assert.Equal("Packed", (await manager.GetOrderAsync(order.Id)).Status);
        Assert.Equal(1, await ListsWithOrderAsync(manager, order.OrderNumber));
        Assert.Equal(44, await manager.TotalStockAsync(article.Id));
        Assert.Empty(await W.LedgerViolationsAsync(article.Id));
    }

    private static async Task<int> ListsWithOrderAsync(HttpClient client, string orderNumber) =>
        (await (await client.GetAsync("/api/picklists")).ExpectAsync<List<PickListDto>>()).Count(l => l.Items.Any(i => i.OrderNumber == orderNumber));
}

/// <summary>
/// Szenario 8, Teil 2: das Zurücksetzen der Picklisten (<c>DELETE /api/picklists</c>) betrifft alle Listen der Instanz und hat
/// deshalb eine eigene Factory (eigene Testklasse mit einem einzigen Test).
/// </summary>
public class PickListResetScenarioTests : IClassFixture<WorkflowFixture>
{
    private readonly WorkflowFixture _fx;
    private WorldBuilder W => _fx.W;
    private WorldBuilder.World Site => _fx.Warehouse;

    public PickListResetScenarioTests(WorkflowFixture fixture) => _fx = fixture;

    [Fact]
    public async Task Reset_deletes_only_lists_without_booking_effect_and_is_idempotent()
    {
        var admin = await W.AdminAsync();
        var manager = await W.ClientAsync("Manager");
        var picker = await W.ClientAsync("Picker");
        var article = await W.AddArticleAsync();
        await manager.ReceiveAsync(new ApiCalls.Receipt(article.Id, Site.PickA, 100));

        var (packedOrder, packedList) = await manager.PackedOrderAsync(article.Id, 5);   // Completed: hat gebucht
        var pendingOrder = await manager.PlaceOrderAsync(article.Id, 3);
        var pendingList = await manager.GenerateAsync(pendingOrder.Id);                   // Pending
        var pickedOrder = await manager.PlaceOrderAsync(article.Id, 2);
        var pickedList = await manager.GenerateAsync(pickedOrder.Id);
        await picker.MarkPickedAsync(pickedList.Id);                                      // Picked

        // nur der Admin darf zurücksetzen
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.DeleteAsync("/api/picklists")).StatusCode);

        var result = await (await admin.DeleteAsync("/api/picklists")).ExpectAsync<ResetPickListsResult>();
        Assert.Equal((2, 1), (result.Deleted, result.SkippedCompleted));

        // die offenen Bestellungen sind wieder frei, die gepackte Liste und ihre Bestellung bleiben, der Bestand ist unberührt
        Assert.Equal("New", (await manager.GetOrderAsync(pendingOrder.Id)).Status);
        Assert.Equal("New", (await manager.GetOrderAsync(pickedOrder.Id)).Status);
        Assert.Equal("Packed", (await manager.GetOrderAsync(packedOrder.Id)).Status);
        var remaining = await (await manager.GetAsync("/api/picklists")).ExpectAsync<List<PickListDto>>();
        Assert.Equal(packedList.Id, Assert.Single(remaining).Id);
        Assert.Equal(95, await manager.TotalStockAsync(article.Id));

        // zweiter Aufruf: nichts mehr zu löschen, dasselbe Ergebnis
        var repeated = await (await admin.DeleteAsync("/api/picklists")).ExpectAsync<ResetPickListsResult>();
        Assert.Equal((0, 1), (repeated.Deleted, repeated.SkippedCompleted));

        // die freigegebene Bestellung lässt sich neu kommissionieren; die Nummer wird nicht wiederverwendet (es gibt noch eine verpackte Liste)
        var regenerated = await manager.GenerateAsync(pendingOrder.Id);
        Assert.Equal(("Pending", "Picking"), (regenerated.Status, (await manager.GetOrderAsync(pendingOrder.Id)).Status));
        Assert.True(NumberOf(regenerated.PickListNumber) > NumberOf(packedList.PickListNumber));
        Assert.Empty(await W.LedgerViolationsAsync(article.Id));
    }

    private static int NumberOf(string pickListNumber) => int.Parse(pickListNumber[(pickListNumber.LastIndexOf('-') + 1)..]);
}
