using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Warehouse;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP22;

/// <summary>
/// Die neuen Endpunkte der Lager-Stammdaten sind Manager-Sache (Admin darf ebenfalls), unbekannte Ids sind 404 und die Eingaben
/// sind begrenzt (Textlängen, Abmessungen 1..100000 mm, Gewicht >= 0). Die Rollen-Matrix aus WP02 führt sie zusätzlich als Zeilen.
/// </summary>
public class WarehouseAdminAccessTests : IClassFixture<LagerApiFactory>
{
    private static readonly Guid Unknown = Guid.Parse("00000000-0000-4000-8000-0000000000b7");
    private static readonly PositionDto Origin = new(0, 0, 0);

    private readonly LagerApiFactory _factory;
    private readonly WorldBuilder _world;

    public WarehouseAdminAccessTests(LagerApiFactory factory)
    {
        _factory = factory;
        _world = new WorldBuilder(factory);
    }

    /// <summary>Jede neue Schreib-Route mit einem gültigen Body und einer unbekannten bzw. der übergebenen Id.</summary>
    private static IEnumerable<(string Name, HttpMethod Method, string Url, object? Body)> WriteRoutes(Guid id) => new (string, HttpMethod, string, object?)[]
    {
        ("POST warehouse", HttpMethod.Post, "/api/warehouse", new CreateWarehouseRequest("X-" + Guid.NewGuid().ToString("N")[..8], "Name")),
        ("PUT warehouse", HttpMethod.Put, $"/api/warehouse/{id}", new UpdateWarehouseRequest("X", "Name")),
        ("DELETE warehouse", HttpMethod.Delete, $"/api/warehouse/{id}", null),
        ("POST zones", HttpMethod.Post, "/api/warehouse/zones", new CreateZoneRequest(id, "Z", "Zone")),
        ("PUT zones", HttpMethod.Put, $"/api/warehouse/zones/{id}", new UpdateZoneRequest("Z", "Zone")),
        ("DELETE zones", HttpMethod.Delete, $"/api/warehouse/zones/{id}", null),
        ("POST aisles", HttpMethod.Post, "/api/warehouse/aisles", new CreateAisleRequest(id, "A")),
        ("PUT aisles", HttpMethod.Put, $"/api/warehouse/aisles/{id}", new UpdateAisleRequest("A")),
        ("DELETE aisles", HttpMethod.Delete, $"/api/warehouse/aisles/{id}", null),
        ("PUT shelves", HttpMethod.Put, $"/api/warehouse/shelves/{id}", new UpdateShelfRequest("S", 1_000, 500, 2_000)),
        ("DELETE shelves", HttpMethod.Delete, $"/api/warehouse/shelves/{id}", null),
        ("PUT storage-locations", HttpMethod.Put, $"/api/warehouse/storage-locations/{id}", new UpdateStorageLocationRequest("B", 600, 600, 500, 10_000)),
    };

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, object? body)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task Only_managers_and_admins_may_change_the_warehouse_structure()
    {
        var anonymous = _world.Anonymous();
        foreach (var route in WriteRoutes(Unknown))
            Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(anonymous, route.Method, route.Url, route.Body)).StatusCode);

        foreach (var role in new[] { "Viewer", "Picker", "Packer", "Receiver" })
        {
            var client = await _world.ClientAsync(role);
            foreach (var route in WriteRoutes(Unknown))
            {
                var response = await SendAsync(client, route.Method, route.Url, route.Body);
                Assert.True(response.StatusCode == HttpStatusCode.Forbidden, $"{role} darf {route.Name} nicht, war aber {(int)response.StatusCode}");
            }
        }

        // Ein Manager darf: unbekannte Ids sind dann 404 (Anlegen unter einem unbekannten Elternteil ebenso), nie 403.
        var manager = await _world.ClientAsync("Manager");
        foreach (var route in WriteRoutes(Unknown).Where(r => r.Name != "POST warehouse"))
            Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(manager, route.Method, route.Url, route.Body)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await SendAsync(manager, HttpMethod.Post, "/api/warehouse", new CreateWarehouseRequest(WorldBuilder.Unique("MGR"), "Vom Manager"))).StatusCode);
    }

    [Fact]
    public async Task Inputs_are_limited_with_field_errors_before_anything_is_written()
    {
        var admin = await _factory.CreateClient().AsReadyAdminAsync();
        var site = await admin.BuildSiteAsync(bins: 1);
        var bin = site.Bins.Single();
        var tooLong = new string('X', 65);

        var warehouse = await admin.PostAsJsonAsync("/api/warehouse", new CreateWarehouseRequest(tooLong, new string('N', 257)));
        var emptyName = await admin.PostAsJsonAsync("/api/warehouse/zones", new CreateZoneRequest(site.Warehouse.Id, "Z-X", " "));
        var badOrigin = await admin.PostAsJsonAsync("/api/warehouse/zones", new CreateZoneRequest(site.Warehouse.Id, "Z-Y", "Zone", new PositionDto(2_000_000, 0, 0)));
        var badOrientation = await admin.PostAsJsonAsync("/api/warehouse/aisles", new CreateAisleRequest(site.Zone.Id, "A-X", null, null, "Diagonal"));
        var shelf = await admin.PutAsJsonAsync($"/api/warehouse/shelves/{site.Shelf.Id}", new UpdateShelfRequest(site.Shelf.Code, 100_001, 0, 2_000));
        var binRequest = await admin.PutAsJsonAsync($"/api/warehouse/storage-locations/{bin.Id}", new UpdateStorageLocationRequest(bin.Code, 600, 100_001, -5, -1));

        var warehouseErrors = await warehouse.ErrorAsync(HttpStatusCode.BadRequest);
        Assert.Equal("validation_failed", warehouseErrors.Code);
        Assert.True(warehouseErrors.HasFieldError("Code") && warehouseErrors.HasFieldError("Name"));
        Assert.True((await emptyName.ErrorAsync(HttpStatusCode.BadRequest)).HasFieldError("Name"));
        Assert.True((await badOrigin.ErrorAsync(HttpStatusCode.BadRequest)).HasFieldError("Origin.XMm"));
        Assert.True((await badOrientation.ErrorAsync(HttpStatusCode.BadRequest)).HasFieldError("Orientation"));
        var shelfErrors = await shelf.ErrorAsync(HttpStatusCode.BadRequest);
        Assert.True(shelfErrors.HasFieldError("WidthMm") && shelfErrors.HasFieldError("DepthMm") && !shelfErrors.HasFieldError("HeightMm"));
        var binErrors = await binRequest.ErrorAsync(HttpStatusCode.BadRequest);
        Assert.True(binErrors.HasFieldError("DepthMm") && binErrors.HasFieldError("HeightMm") && binErrors.HasFieldError("MaxWeightGrams") && !binErrors.HasFieldError("WidthMm"));

        // Die Grenzen selbst sind erlaubt (100000 mm, Gewicht 0), und nichts Abgelehntes wurde gespeichert.
        var ok = await admin.PutAsJsonAsync($"/api/warehouse/storage-locations/{bin.Id}", new UpdateStorageLocationRequest("  " + bin.Code + "  ", 100_000, 1, 100_000, 0));
        var updated = await ok.ExpectAsync<StorageLocationDto>(HttpStatusCode.OK);
        Assert.Equal((bin.Code, 100_000, 1, 100_000, 0, bin.Position), (updated.Code, updated.WidthMm, updated.DepthMm, updated.HeightMm, updated.MaxWeightGrams, updated.Position));
        Assert.Single((await admin.FindWarehouseAsync(site.Warehouse.Id))!.Zones);
        Assert.Equal(site.Shelf.WidthMm, (await admin.FindWarehouseAsync(site.Warehouse.Id))!.Zones.Single().Aisles.Single().Shelves.Single().WidthMm);
    }

    [Fact]
    public async Task Names_codes_and_dimensions_can_be_changed_at_every_level()
    {
        var admin = await _factory.CreateClient().AsReadyAdminAsync();
        var site = await admin.BuildSiteAsync(bins: 2);
        var newCode = WorldBuilder.Unique("NEU");

        var warehouse = await (await admin.PutAsJsonAsync($"/api/warehouse/{site.Warehouse.Id}", new UpdateWarehouseRequest(newCode, "Umbenannt")))
            .ExpectAsync<WarehouseDto>(HttpStatusCode.OK);
        var zone = await (await admin.PutAsJsonAsync($"/api/warehouse/zones/{site.Zone.Id}", new UpdateZoneRequest("Z-NEU", "Zone neu", new PositionDto(1_000, 2_000, 0))))
            .ExpectAsync<ZoneDto>(HttpStatusCode.OK);
        var aisle = await (await admin.PutAsJsonAsync($"/api/warehouse/aisles/{site.Aisle.Id}", new UpdateAisleRequest("A-NEU", null, new PositionDto(0, 6_000, 0), "AlongY")))
            .ExpectAsync<AisleDto>(HttpStatusCode.OK);
        var shelf = await (await admin.PutAsJsonAsync($"/api/warehouse/shelves/{site.Shelf.Id}", new UpdateShelfRequest("R-" + newCode, 9_000, 700, 2_500)))
            .ExpectAsync<ShelfDto>(HttpStatusCode.OK);

        Assert.Equal((newCode, "Umbenannt"), (warehouse.Code, warehouse.Name));
        Assert.Equal(("Z-NEU", "Zone neu", new PositionDto(1_000, 2_000, 0)), (zone.Code, zone.Name, zone.Origin));
        // Nicht mitgeschickte Lage bleibt (Start), die geschickte ändert sich; der Baum der Antwort ist vollständig.
        Assert.Equal(("A-NEU", "AlongY", site.Aisle.StartPosition, new PositionDto(0, 6_000, 0)), (aisle.Code, aisle.Orientation, aisle.StartPosition, aisle.EndPosition));
        Assert.Single(aisle.Shelves);
        Assert.Equal(("R-" + newCode, 9_000, 700, 2_500, 2), (shelf.Code, shelf.WidthMm, shelf.DepthMm, shelf.HeightMm, shelf.Locations.Count));

        // Alles steht so auch im Baum (Lagerplätze und ihre Codes sind unverändert).
        var layout = (await admin.FindWarehouseAsync(site.Warehouse.Id))!;
        Assert.Equal((newCode, "Zone neu", "A-NEU", "R-" + newCode), (layout.Code, layout.Zones.Single().Name, layout.Zones.Single().Aisles.Single().Code, layout.Zones.Single().Aisles.Single().Shelves.Single().Code));
        Assert.Equal(site.Bins.Select(b => b.Code).Order(), layout.Zones.Single().Aisles.Single().Shelves.Single().Locations.Select(b => b.Code).Order());
    }
}
