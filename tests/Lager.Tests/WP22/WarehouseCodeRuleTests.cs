using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Warehouse;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP22;

/// <summary>
/// Codes sind je Elternteil eindeutig (Lager: im System, Zone: im Lager, Gang: in der Zone, Regal: im Gang; ohne Groß-/Kleinschreibung),
/// der Lagerplatz-Code im ganzen System. Ein Konflikt ist 409 mit dem Code <c>duplicate_code</c> und ändert nichts.
/// </summary>
public class WarehouseCodeRuleTests : IClassFixture<LagerApiFactory>
{
    private const string Duplicate = "duplicate_code";

    private readonly LagerApiFactory _factory;
    public WarehouseCodeRuleTests(LagerApiFactory factory) => _factory = factory;

    private async Task<HttpClient> AdminAsync() => await _factory.CreateClient().AsReadyAdminAsync();

    [Fact]
    public async Task Warehouse_zone_aisle_and_shelf_codes_must_be_unique_in_their_parent_ignoring_case()
    {
        var admin = await AdminAsync();
        var site = await admin.BuildSiteAsync(bins: 0);
        var otherZone = await admin.CreateZoneAsync(site.Warehouse.Id, "Z-B", "Zone B");
        var siblingAisle = await admin.CreateAisleAsync(site.Zone.Id, "A2");

        // Anlegen: dasselbe Lager, dieselbe Zone, derselbe Gang - jeweils mit anderer Schreibweise.
        var dupWarehouse = await admin.PostAsJsonAsync("/api/warehouse", new CreateWarehouseRequest(site.Warehouse.Code.ToLowerInvariant(), "Doppelt"));
        var dupZone = await admin.PostAsJsonAsync("/api/warehouse/zones", new CreateZoneRequest(site.Warehouse.Id, "z-a", "Doppelt"));
        var dupAisle = await admin.PostAsJsonAsync("/api/warehouse/aisles", new CreateAisleRequest(site.Zone.Id, "a1"));
        var dupShelf = await admin.PostAsJsonAsync("/api/warehouse/shelves", WarehouseAdminCalls.ShelfRequest(site.Aisle.Id, site.Shelf.Code.ToLowerInvariant(), 0));
        foreach (var response in new[] { dupWarehouse, dupZone, dupAisle, dupShelf })
            Assert.Equal(Duplicate, (await response.ErrorAsync(HttpStatusCode.Conflict)).Code);

        // Umbenennen auf den Code eines Geschwisters ist ebenfalls ein Konflikt ...
        var renameZone = await admin.PutAsJsonAsync($"/api/warehouse/zones/{otherZone.Id}", new UpdateZoneRequest("Z-A", "Zone B"));
        var renameAisle = await admin.PutAsJsonAsync($"/api/warehouse/aisles/{siblingAisle.Id}", new UpdateAisleRequest("a1"));
        Assert.Equal(Duplicate, (await renameZone.ErrorAsync(HttpStatusCode.Conflict)).Code);
        Assert.Equal(Duplicate, (await renameAisle.ErrorAsync(HttpStatusCode.Conflict)).Code);

        // ... aber nicht auf den eigenen Code in anderer Schreibweise, und derselbe Code in einem anderen Elternteil ist frei.
        var ownCase = await admin.PutAsJsonAsync($"/api/warehouse/zones/{site.Zone.Id}", new UpdateZoneRequest("z-a", "Zone A neu"));
        Assert.Equal("z-a", (await ownCase.ExpectAsync<ZoneDto>(HttpStatusCode.OK)).Code);
        var elsewhere = await admin.CreateWarehouseAsync();
        var sameZoneCodeElsewhere = await admin.CreateZoneAsync(elsewhere.Id, "Z-A", "Zone A");
        Assert.Equal("Z-A", sameZoneCodeElsewhere.Code);

        // Keiner der abgelehnten Aufrufe hat etwas angelegt.
        var warehouse = (await admin.FindWarehouseAsync(site.Warehouse.Id))!;
        Assert.Equal(2, warehouse.Zones.Count);
        var aisles = warehouse.Zones.Single(z => z.Id == site.Zone.Id).Aisles;
        Assert.Equal(new[] { "A1", "A2" }, aisles.Select(a => a.Code).Order().ToArray());
        Assert.Single(aisles.Single(a => a.Id == site.Aisle.Id).Shelves);
    }

    [Fact]
    public async Task A_duplicate_bin_code_is_rejected_everywhere_a_bin_can_be_created_or_renamed()
    {
        var admin = await AdminAsync();
        var site = await admin.BuildSiteAsync(bins: 2);
        var existing = site.Bins.First();
        var otherAisle = await admin.CreateAisleAsync(site.Zone.Id, "A2");
        var otherShelf = await admin.CreateShelfAsync(otherAisle.Id, "S2-" + site.Warehouse.Code, bins: 1);

        // Ein weiteres Fach im selben Regal (andere Schreibweise) und in einem ANDEREN Regal: der Code ist global eindeutig.
        var sameShelf = await admin.PostAsJsonAsync($"/api/warehouse/shelves/{site.Shelf.Id}/bins",
            new AddBinToShelfRequest(existing.Code.ToLowerInvariant(), 600, 600, 500, 50_000));
        var otherShelfSameCode = await admin.PostAsJsonAsync($"/api/warehouse/shelves/{otherShelf.Id}/bins",
            new AddBinToShelfRequest(existing.Code, 600, 600, 500, 50_000));
        var direct = await admin.PostAsJsonAsync("/api/warehouse/storage-locations",
            new CreateStorageLocationRequest(otherShelf.Id, existing.Code, new PositionDto(0, 0, 0), 600, 600, 500, 50_000));
        var error = await sameShelf.ErrorAsync(HttpStatusCode.Conflict);
        Assert.Equal(Duplicate, error.Code);
        Assert.Contains(existing.Code, error.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Duplicate, (await otherShelfSameCode.ErrorAsync(HttpStatusCode.Conflict)).Code);
        Assert.Equal(Duplicate, (await direct.ErrorAsync(HttpStatusCode.Conflict)).Code);

        // Umbenennen eines Lagerplatzes auf den Code eines anderen Regals.
        var rename = await admin.PutAsJsonAsync($"/api/warehouse/storage-locations/{otherShelf.Locations.Single().Id}",
            new UpdateStorageLocationRequest(existing.Code, 600, 600, 500, 50_000));
        Assert.Equal(Duplicate, (await rename.ErrorAsync(HttpStatusCode.Conflict)).Code);

        // Nichts davon hat einen Lagerplatz angelegt oder umbenannt.
        var bins = await admin.BinsAsync();
        Assert.Equal(1, bins.Count(b => string.Equals(b.Code, existing.Code, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(3, bins.Count(b => site.Bins.Concat(otherShelf.Locations).Any(x => x.Id == b.Id)));
    }

    [Fact]
    public async Task A_shelf_whose_generated_bin_codes_collide_is_rejected_as_a_whole()
    {
        var admin = await AdminAsync();
        var first = await admin.BuildSiteAsync(bins: 3);
        var otherAisle = await admin.CreateAisleAsync(first.Zone.Id, "A2");

        // Gleicher Regalcode in einem anderen Gang: erlaubt, aber die erzeugten Codes "<Regal>-01..03" sind schon vergeben.
        var response = await admin.PostAsJsonAsync("/api/warehouse/shelves", WarehouseAdminCalls.ShelfRequest(otherAisle.Id, first.Shelf.Code, 3));
        var error = await response.ErrorAsync(HttpStatusCode.Conflict);

        Assert.Equal(Duplicate, error.Code);
        // Atomar: weder das Regal noch ein Fach ist entstanden.
        var aisle = (await admin.FindWarehouseAsync(first.Warehouse.Id))!.Zones.Single().Aisles.Single(a => a.Id == otherAisle.Id);
        Assert.Empty(aisle.Shelves);

        // Ohne Fächer ist derselbe Regalcode in einem anderen Gang dagegen frei.
        var withoutBins = await admin.CreateShelfAsync(otherAisle.Id, first.Shelf.Code, bins: 0);
        Assert.Equal(first.Shelf.Code, withoutBins.Code);
    }
}
