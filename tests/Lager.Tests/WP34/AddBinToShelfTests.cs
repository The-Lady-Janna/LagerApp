using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Warehouse;
using Lager.Tests.Infrastructure;
using Lager.Tests.WP22;

namespace Lager.Tests.WP34;

/// <summary>
/// Regression: POST /api/warehouse/shelves/{id}/bins antwortete bei JEDEM Regal mit 409 concurrency_conflict, weil das
/// neue Fach (Guid schon im Konstruktor) am getrackten Regal als "bestehend" erkannt wurde. Einen Erfolgsfall-Test gab es nicht.
/// </summary>
public class AddBinToShelfTests : IClassFixture<LagerApiFactory>
{
    private readonly LagerApiFactory _factory;
    public AddBinToShelfTests(LagerApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Adding_a_bin_to_an_existing_shelf_creates_it_and_shows_it_in_the_shelf_and_the_bin_list()
    {
        var admin = await _factory.CreateClient().AsReadyAdminAsync();
        var site = await admin.BuildSiteAsync(bins: 2);
        var code = WorldBuilder.Unique("BIN");

        var response = await admin.PostAsJsonAsync($"/api/warehouse/shelves/{site.Shelf.Id}/bins",
            new AddBinToShelfRequest(code, 500, 600, 400, 25_000));
        var created = await response.ExpectAsync<StorageLocationDto>(HttpStatusCode.Created);

        Assert.Equal(code, created.Code);
        Assert.Equal(site.Shelf.Id, created.ShelfId);

        var bins = await admin.BinsAsync();
        Assert.Contains(bins, b => b.Id == created.Id && b.Code == code);

        var warehouse = await admin.FindWarehouseAsync(site.Warehouse.Id);
        var shelf = warehouse!.Zones.SelectMany(z => z.Aisles).SelectMany(a => a.Shelves).Single(s => s.Id == site.Shelf.Id);
        Assert.Equal(3, shelf.Locations.Count);
    }

    [Fact]
    public async Task Adding_two_bins_one_after_another_works_and_a_duplicate_code_is_a_duplicate_code_conflict()
    {
        var admin = await _factory.CreateClient().AsReadyAdminAsync();
        var site = await admin.BuildSiteAsync(bins: 0);
        var first = WorldBuilder.Unique("BIN");
        var second = WorldBuilder.Unique("BIN");

        await (await admin.PostAsJsonAsync($"/api/warehouse/shelves/{site.Shelf.Id}/bins", new AddBinToShelfRequest(first, 500, 600, 400, 25_000)))
            .ExpectAsync<StorageLocationDto>(HttpStatusCode.Created);
        await (await admin.PostAsJsonAsync($"/api/warehouse/shelves/{site.Shelf.Id}/bins", new AddBinToShelfRequest(second, 500, 600, 400, 25_000)))
            .ExpectAsync<StorageLocationDto>(HttpStatusCode.Created);

        var duplicate = await admin.PostAsJsonAsync($"/api/warehouse/shelves/{site.Shelf.Id}/bins", new AddBinToShelfRequest(first, 500, 600, 400, 25_000));
        var error = await duplicate.ErrorAsync(HttpStatusCode.Conflict);
        Assert.Equal("duplicate_code", error.Code);
    }

    [Fact]
    public async Task Adding_a_bin_to_an_unknown_shelf_is_a_404()
    {
        var admin = await _factory.CreateClient().AsReadyAdminAsync();

        var response = await admin.PostAsJsonAsync($"/api/warehouse/shelves/{Guid.NewGuid()}/bins",
            new AddBinToShelfRequest(WorldBuilder.Unique("BIN"), 500, 600, 400, 25_000));

        await response.ExpectStatusAsync(HttpStatusCode.NotFound);
    }
}
