using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Inbound;
using Lager.Contracts.Purchasing;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP14;

/// <summary>
/// Die HTTP-Schicht der bestandsbuchenden Vorgänge: die Validatoren weisen unsinnige Eingaben mit 400 ab (bevor
/// eine Action läuft), und die neue Aktion "Wareneingang aus Bestellung" ist über die API erreichbar - mit der
/// Berechtigung des Wareneingangs.
/// </summary>
public class StockFlowApiTests : IClassFixture<StockApiFixture>
{
    private readonly StockApiFixture _fixture;
    private readonly StockWorld _w;

    public StockFlowApiTests(StockApiFixture fixture)
    {
        _fixture = fixture;
        _w = fixture.World;
    }

    private static readonly string Id = Guid.NewGuid().ToString();
    private static readonly string Other = Guid.NewGuid().ToString();

    private static object[] Lines(int count, object line) => Enumerable.Repeat(line, count).ToArray();

    public static IEnumerable<object[]> BadRequests()
    {
        // Wareneingang
        yield return new object[] { "POST", $"/api/inbound/{Id}/lines", new { articleId = Id, targetBinId = Other, quantity = 0 } };
        yield return new object[] { "POST", $"/api/inbound/{Id}/lines", new { articleId = Id, targetBinId = Other, quantity = 1_000_001 } };
        yield return new object[] { "POST", $"/api/inbound/{Id}/lines", new { articleId = Id, targetBinId = Other, quantity = 5, lotNumber = new string('x', 65) } };
        yield return new object[] { "POST", $"/api/inbound/{Id}/lines", new { articleId = Id, targetBinId = Other, quantity = 5, unitCostCents = -1 } };
        yield return new object[] { "POST", "/api/inbound", new { shipmentNumber = "", notes = (string?)null } };
        yield return new object[] { "POST", $"/api/purchase-orders/{Id}/create-inbound", new { targetBinId = Guid.Empty } };
        // Inventur
        yield return new object[] { "POST", "/api/inventory/start", new { name = "" } };
        yield return new object[] { "PUT", $"/api/inventory/{Id}/lines/{Other}", new { countedQty = -1 } };
        yield return new object[] { "PUT", $"/api/inventory/{Id}/lines/{Other}", new { countedQty = 1_000_001 } };
        // Retouren: QC nur als Name
        yield return new object[] { "PUT", $"/api/returns/{Id}/lines/{Other}/qc", new { result = "99" } };
        yield return new object[] { "PUT", $"/api/returns/{Id}/lines/{Other}/qc", new { result = "" } };
        yield return new object[] { "POST", $"/api/returns/{Id}/lines", new { articleId = Id, quantity = 0 } };
        yield return new object[] { "POST", "/api/returns", new { lines = Lines(501, new { articleId = Id, quantity = 1 }) } };
        yield return new object[] { "POST", "/api/returns", new { lines = (object?)null } };
        // Einkauf
        yield return new object[] { "POST", "/api/purchase-orders", new { supplierId = Id, lines = Lines(501, new { articleId = Id, orderedQty = 1 }) } };
        yield return new object[] { "POST", "/api/purchase-orders", new { supplierId = Id, lines = new[] { new { articleId = Id, orderedQty = 0 } } } };
        yield return new object[] { "POST", $"/api/purchase-orders/{Id}/lines", new { articleId = Id, orderedQty = 1_000_001 } };
        yield return new object[] { "POST", $"/api/purchase-orders/{Id}/lines/{Other}/receive", new { receivedQty = 0 } };
        // Nachschub
        yield return new object[] { "POST", $"/api/replenishment/{Id}/complete", new { actualQty = 0 } };
        yield return new object[] { "POST", $"/api/replenishment/{Id}/complete", new { actualQty = 1_000_001 } };
    }

    [Theory]
    [MemberData(nameof(BadRequests))]
    public async Task Invalid_input_is_rejected_with_400_before_any_action_runs(string method, string url, object body)
    {
        var admin = await _fixture.Factory.CreateClient().AsReadyAdminAsync();

        using var response = await admin.SendAsync(new HttpRequestMessage(new HttpMethod(method), url) { Content = JsonContent.Create(body) });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Valid_boundaries_pass_the_validators()
    {
        var admin = await _fixture.Factory.CreateClient().AsReadyAdminAsync();

        // Menge 1 und 1.000.000 sind erlaubt: die Anfrage kommt bis zur Fachlogik (unbekannte Lieferung -> 404 statt 400)
        foreach (var quantity in new[] { 1, 1_000_000 })
        {
            using var response = await admin.PostAsJsonAsync($"/api/inbound/{Guid.NewGuid()}/lines",
                new AddInboundLineRequest(Guid.NewGuid(), Guid.NewGuid(), quantity, new string('x', 64), null));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task Create_inbound_from_a_purchase_order_works_over_HTTP_for_a_receiver_and_the_receive_closes_the_order()
    {
        var supplier = await _w.AddSupplierAsync();
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync(priceCents: 100);
        var po = await _w.SentPurchaseOrderAsync(supplier, (article, 6, null));
        var receiver = await _fixture.Factory.CreateClientWithRolesAsync("Receiver");

        using var created = await receiver.PostAsJsonAsync($"/api/purchase-orders/{po.Id}/create-inbound", new CreateInboundFromPurchaseOrderRequest(bin.Id));

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var draft = (await created.Content.ReadFromJsonAsync<InboundShipmentDto>())!;
        Assert.Equal(po.Id, draft.PurchaseOrderId);
        Assert.EndsWith($"/api/inbound/{draft.Id}", created.Headers.Location!.AbsolutePath);
        Assert.Equal(6, Assert.Single(draft.Lines).Quantity);

        using var received = await receiver.PostAsync($"/api/inbound/{draft.Id}/receive", null);
        Assert.Equal(HttpStatusCode.OK, received.StatusCode);
        var order = (await receiver.GetFromJsonAsync<PurchaseOrderDto>($"/api/purchase-orders/{po.Id}"))!;
        Assert.Equal("Received", order.Status);
        Assert.Equal(6, await _w.QuantityAsync(article));
    }

    [Fact]
    public async Task Create_inbound_needs_the_receiver_role_and_an_existing_order()
    {
        var bin = Guid.NewGuid();
        var viewer = await _fixture.Factory.CreateClientWithRolesAsync("Viewer");
        var picker = await _fixture.Factory.CreateClientWithRolesAsync("Picker");
        var receiver = await _fixture.Factory.CreateClientWithRolesAsync("Receiver");
        var body = new CreateInboundFromPurchaseOrderRequest(bin);

        foreach (var client in new[] { viewer, picker })
        {
            using var forbidden = await client.PostAsJsonAsync($"/api/purchase-orders/{Guid.NewGuid()}/create-inbound", body);
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        using var missing = await receiver.PostAsJsonAsync($"/api/purchase-orders/{Guid.NewGuid()}/create-inbound", body);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
