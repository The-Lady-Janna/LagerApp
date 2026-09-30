using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lager.Contracts.Articles;
using Lager.Contracts.Auth;
using Lager.Contracts.Customers;
using Lager.Contracts.Inbound;
using Lager.Contracts.Orders;
using Lager.Contracts.PickLists;
using Lager.Contracts.Purchasing;
using Lager.Contracts.Returns;
using Lager.Contracts.Suppliers;
using Lager.Contracts.Warehouse;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP30;

/// <summary>
/// Einheitliche Statuscodes beim Anlegen: 201 mit einem <c>Location</c>-Header, dessen Adresse ein GET auflöst (200) und die neue
/// Ressource liefert - auf die Ressource selbst, wo es einen Abruf per Id gibt, sonst auf die Liste, die sie enthält. Zeilen und
/// Adressen (Sub-Ressourcen ohne eigene Adresse) antworten 201 mit der Location des Eltern-Objekts und bei unbekanntem Eltern-Objekt 404.
/// </summary>
public class LocationHeaderTests : IClassFixture<LagerApiFactory>
{
    private readonly LagerApiFactory _factory;
    private readonly WorldBuilder _world;

    public LocationHeaderTests(LagerApiFactory factory)
    {
        _factory = factory;
        _world = new WorldBuilder(factory);
    }

    private static readonly Guid UnknownId = Guid.Parse("00000000-0000-4000-8000-0000000000a3");

    private static CreateArticleRequest NewArticle() =>
        new(WorldBuilder.Unique("SKU"), "Testartikel", null, new DimensionsDto(100, 100, 100), 100, new StackingInfoDto(false, "Z", 0, null));

    /// <summary>
    /// Erwartet 201, eine Location auf <paramref name="expectedPath"/> (samt Id, wenn <paramref name="withId"/>) und liefert die Antwort des
    /// GET auf diese Location (200) als JSON; der Body der Anlage kommt als zweiter Wert zurück.
    /// </summary>
    private static async Task<(JsonElement Created, JsonElement Resolved)> AssertCreatedAsync(
        HttpClient client, HttpResponseMessage response, string expectedPath, bool withId)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Erwartet 201, war {(int)response.StatusCode}: {body}");
        var created = JsonDocument.Parse(body).RootElement.Clone();

        var location = response.Headers.Location;
        Assert.NotNull(location);
        var expected = withId && created.TryGetProperty("id", out var id) ? $"{expectedPath}/{id.GetString()}" : expectedPath;
        Assert.Equal(expected, location!.AbsolutePath, ignoreCase: true);

        var resolved = await client.GetAsync(location);
        var resolvedBody = await resolved.Content.ReadAsStringAsync();
        Assert.True(resolved.StatusCode == HttpStatusCode.OK, $"GET {location.AbsolutePath} -> {(int)resolved.StatusCode}: {resolvedBody}");
        return (created, JsonDocument.Parse(resolvedBody).RootElement.Clone());
    }

    /// <summary>Kommt die Id irgendwo im JSON (Objekt oder Liste, beliebig tief) als Wert der Eigenschaft <c>id</c> vor?</summary>
    private static bool ContainsId(JsonElement element, string id) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject().Any(p =>
            (p.NameEquals("id") && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == id) || ContainsId(p.Value, id)),
        JsonValueKind.Array => element.EnumerateArray().Any(item => ContainsId(item, id)),
        _ => false,
    };

    private static string IdOf(JsonElement created) => created.GetProperty("id").GetString()!;

    [Fact]
    public async Task Articles_suppliers_customers_and_orders_answer_201_and_the_Location_resolves_to_the_created_resource()
    {
        var admin = await _world.AdminAsync();
        var article = await _world.AddArticleAsync();

        var (createdArticle, resolvedArticle) = await AssertCreatedAsync(admin, await admin.PostAsJsonAsync("/api/articles", NewArticle()), "/api/articles", withId: true);
        Assert.Equal(IdOf(createdArticle), IdOf(resolvedArticle));

        var (createdSupplier, resolvedSupplier) = await AssertCreatedAsync(admin,
            await admin.PostAsJsonAsync("/api/suppliers", new CreateSupplierRequest(WorldBuilder.Unique("SUP"), "Lieferant")), "/api/suppliers", withId: true);
        Assert.Equal(IdOf(createdSupplier), IdOf(resolvedSupplier));

        var (createdCustomer, resolvedCustomer) = await AssertCreatedAsync(admin,
            await admin.PostAsJsonAsync("/api/customers", new CreateCustomerRequest(WorldBuilder.Unique("CUS"), "Kunde")), "/api/customers", withId: true);
        Assert.Equal(IdOf(createdCustomer), IdOf(resolvedCustomer));

        var order = new CreateOrderRequest(WorldBuilder.Unique("ORD"), "REF", new[] { new CreateOrderLineRequest(article.Id, 1) });
        var (createdOrder, resolvedOrder) = await AssertCreatedAsync(admin, await admin.PostAsJsonAsync("/api/orders/manual", order), "/api/orders", withId: true);
        Assert.Equal(IdOf(createdOrder), IdOf(resolvedOrder));

        // die externe Bestell-API (POST /api/orders) genauso
        var apiOrder = new CreateOrderRequest(WorldBuilder.Unique("API"), null, new[] { new CreateOrderLineRequest(null, 1, article.Sku) });
        var (createdApiOrder, resolvedApiOrder) = await AssertCreatedAsync(admin, await admin.PostAsJsonAsync("/api/orders", apiOrder), "/api/orders", withId: true);
        Assert.Equal(IdOf(createdApiOrder), IdOf(resolvedApiOrder));
    }

    [Fact]
    public async Task A_replayed_order_with_the_same_Idempotency_Key_is_200_without_a_Location()
    {
        var admin = await _world.AdminAsync();
        var article = await _world.AddArticleAsync();
        var key = WorldBuilder.Unique("idem");
        var order = new CreateOrderRequest(WorldBuilder.Unique("IDEM"), null, new[] { new CreateOrderLineRequest(article.Id, 1) });
        HttpRequestMessage Request() => new(HttpMethod.Post, "/api/orders")
        {
            Headers = { { "Idempotency-Key", key } },
            Content = JsonContent.Create(order),
        };

        var first = await admin.SendAsync(Request());
        var second = await admin.SendAsync(Request());

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.NotNull(first.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("true", Assert.Single(second.Headers.GetValues("Idempotent-Replayed")));
        Assert.Null(second.Headers.Location);
    }

    [Fact]
    public async Task Purchase_orders_inbound_shipments_returns_and_cart_configs_answer_201_with_a_resolvable_Location()
    {
        var admin = await _world.AdminAsync();
        var supplier = await _world.AddSupplierAsync();
        var article = await _world.AddArticleAsync();

        await AssertCreatedAsync(admin, await admin.PostAsJsonAsync("/api/purchase-orders",
            new CreatePurchaseOrderRequest(supplier, null, null, new[] { new CreatePurchaseOrderLineRequest(article.Id, 5) })),
            "/api/purchase-orders", withId: true);
        await AssertCreatedAsync(admin, await admin.PostAsJsonAsync("/api/inbound",
            new CreateInboundShipmentRequest(WorldBuilder.Unique("WE"), null, null)), "/api/inbound", withId: true);
        await AssertCreatedAsync(admin, await admin.PostAsJsonAsync("/api/returns",
            new CreateReturnShipmentRequest(null, null, null, new[] { new CreateReturnLineRequest(article.Id, 1) })), "/api/returns", withId: true);
        await AssertCreatedAsync(admin, await admin.PostAsJsonAsync("/api/cart-configs",
            new CreatePickCartConfigRequest(WorldBuilder.Unique("Wagen"), 3, 600, 400, 300, 50_000)), "/api/cart-configs", withId: true);
    }

    [Fact]
    public async Task Lines_and_addresses_are_created_with_201_and_point_to_their_parent_which_unknown_parents_answer_with_404()
    {
        var admin = await _world.AdminAsync();
        var supplier = await _world.AddSupplierAsync();
        var article = await _world.AddArticleAsync();
        var other = await _world.AddArticleAsync();

        // Adresse am Kunden
        var customer = (await (await admin.PostAsJsonAsync("/api/customers", new CreateCustomerRequest(WorldBuilder.Unique("CUS"), "Kunde")))
            .Content.ReadFromJsonAsync<CustomerDto>())!;
        var (address, customerAfter) = await AssertCreatedAsync(admin,
            await admin.PostAsJsonAsync($"/api/customers/{customer.Id}/addresses",
                new AddAddressRequest("Shipping", "Zentrale", "Hauptstraße 1", null, "12345", "Berlin")),
            "/api/customers", withId: true);
        Assert.Equal(customer.Id, Guid.Parse(IdOf(address)));
        Assert.Equal(1, address.GetProperty("addresses").GetArrayLength());
        Assert.Equal(1, customerAfter.GetProperty("addresses").GetArrayLength());

        // Zeile an der Einkaufsbestellung
        var purchaseOrder = (await (await admin.PostAsJsonAsync("/api/purchase-orders",
            new CreatePurchaseOrderRequest(supplier, null, null, new[] { new CreatePurchaseOrderLineRequest(article.Id, 5) })))
            .Content.ReadFromJsonAsync<PurchaseOrderDto>())!;
        var (poAfterAdd, poResolved) = await AssertCreatedAsync(admin,
            await admin.PostAsJsonAsync($"/api/purchase-orders/{purchaseOrder.Id}/lines", new AddPurchaseOrderLineRequest(other.Id, 3)),
            "/api/purchase-orders", withId: true);
        Assert.Equal(2, poAfterAdd.GetProperty("lines").GetArrayLength());
        Assert.Equal(2, poResolved.GetProperty("lines").GetArrayLength());

        // Zeile an der Retoure
        var returnShipment = (await (await admin.PostAsJsonAsync("/api/returns",
            new CreateReturnShipmentRequest(null, null, null, new[] { new CreateReturnLineRequest(article.Id, 1) })))
            .Content.ReadFromJsonAsync<ReturnShipmentDto>())!;
        var (returnAfterAdd, _) = await AssertCreatedAsync(admin,
            await admin.PostAsJsonAsync($"/api/returns/{returnShipment.Id}/lines", new AddReturnLineRequest(other.Id, 2)),
            "/api/returns", withId: true);
        Assert.Equal(2, returnAfterAdd.GetProperty("lines").GetArrayLength());

        // unbekanntes Eltern-Objekt: 404 als ProblemDetails, nie ein 200 oder 201
        foreach (var (url, body) in new (string, object)[]
                 {
                     ($"/api/customers/{UnknownId}/addresses", new AddAddressRequest("Shipping", "Zentrale", "Hauptstraße 1", null, "12345", "Berlin")),
                     ($"/api/purchase-orders/{UnknownId}/lines", new AddPurchaseOrderLineRequest(article.Id, 3)),
                     ($"/api/returns/{UnknownId}/lines", new AddReturnLineRequest(article.Id, 2)),
                 })
        {
            var response = await admin.PostAsJsonAsync(url, body);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("not_found", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task The_warehouse_structure_users_and_backups_answer_201_and_the_Location_contains_or_delivers_the_new_entry()
    {
        var admin = await _world.AdminAsync();

        // Lager: Abruf per Id
        var (warehouse, _) = await AssertCreatedAsync(admin,
            await admin.PostAsJsonAsync("/api/warehouse", new CreateWarehouseRequest(WorldBuilder.Unique("WH"), "Lager")), "/api/warehouse", withId: true);
        var warehouseId = Guid.Parse(IdOf(warehouse));

        // Zone, Gang, Regal, Lagerplatz, Wand, Pickpunkt: kein Abruf per Id, die Location ist die Liste, die den Eintrag enthält
        var (zone, layout) = await AssertCreatedAsync(admin,
            await admin.PostAsJsonAsync("/api/warehouse/zones", new CreateZoneRequest(warehouseId, "Z1", "Zone")), "/api/warehouse/layout", withId: false);
        Assert.True(ContainsId(layout, IdOf(zone)));

        var (aisle, layoutWithAisle) = await AssertCreatedAsync(admin,
            await admin.PostAsJsonAsync("/api/warehouse/aisles", new CreateAisleRequest(Guid.Parse(IdOf(zone)), "A1")), "/api/warehouse/layout", withId: false);
        Assert.True(ContainsId(layoutWithAisle, IdOf(aisle)));

        var (shelf, layoutWithShelf) = await AssertCreatedAsync(admin,
            await admin.PostAsJsonAsync("/api/warehouse/shelves", new CreateShelfRequest(
                Guid.Parse(IdOf(aisle)), WorldBuilder.Unique("R"), new PositionDto(0, 0, 0), 1_000, 500, 2_000, 1, 500, 500, 500, 50_000)),
            "/api/warehouse/layout", withId: false);
        Assert.True(ContainsId(layoutWithShelf, IdOf(shelf)));

        var (location, locations) = await AssertCreatedAsync(admin,
            await admin.PostAsJsonAsync("/api/warehouse/storage-locations", new CreateStorageLocationRequest(
                Guid.Parse(IdOf(shelf)), WorldBuilder.Unique("BIN"), new PositionDto(0, 0, 0), 500, 500, 500, 10_000)),
            "/api/warehouse/storage-locations", withId: false);
        Assert.True(ContainsId(locations, IdOf(location)));

        var (wall, walls) = await AssertCreatedAsync(admin,
            await admin.PostAsJsonAsync("/api/warehouse/walls", new CreateWallRequest(
                warehouseId, "Wand", new[] { new PositionDto(0, 0, 0), new PositionDto(1_000, 0, 0) }, 200)),
            "/api/warehouse/walls", withId: false);
        Assert.True(ContainsId(walls, IdOf(wall)));

        var (pickPoint, pickPoints) = await AssertCreatedAsync(admin,
            await admin.PostAsJsonAsync("/api/warehouse/pick-points", new CreatePickPointRequest(warehouseId, "Start", "Start", new PositionDto(0, 0, 0))),
            "/api/warehouse/pick-points", withId: false);
        Assert.True(ContainsId(pickPoints, IdOf(pickPoint)));

        // Benutzer: die Location ist die Benutzerliste
        var (user, users) = await AssertCreatedAsync(admin,
            await admin.PostAsJsonAsync("/api/users", new CreateUserRequest(
                WorldBuilder.Unique("user"), AuthTestExtensions.RoleUserPassword, new[] { "Viewer" }, MustChangePassword: false)),
            "/api/users", withId: false);
        Assert.True(ContainsId(users, IdOf(user)));

        // Backup: die Location ist der Download der Datei
        var createdBackup = await admin.PostAsync("/api/admin/backups", content: null);
        Assert.Equal(HttpStatusCode.Created, createdBackup.StatusCode);
        var download = await admin.GetAsync(createdBackup.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("application/octet-stream", download.Content.Headers.ContentType?.MediaType);
        var name = (await createdBackup.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("name").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/backups/{name}")).StatusCode);
    }

    [Fact]
    public async Task Browser_clients_of_another_origin_can_read_the_Location_header()
    {
        using var client = _factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "/health/live") { Headers = { { "Origin", "http://localhost:5173" } } };

        var response = await client.SendAsync(request);

        var exposed = string.Join(",", response.Headers.GetValues("Access-Control-Expose-Headers"));
        Assert.Contains("Location", exposed);
        Assert.Contains("X-Correlation-Id", exposed);
    }
}
