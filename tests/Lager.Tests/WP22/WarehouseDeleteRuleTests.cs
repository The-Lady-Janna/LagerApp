using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Inventory;
using Lager.Contracts.Warehouse;
using Lager.Domain.Stock;
using Lager.Domain.Warehouse;
using Lager.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Lager.Tests.WP22;

/// <summary>
/// Löschregeln: Lager, Zone, Gang und Regal sind nur löschbar, wenn darunter kein Lagerplatz mit Bestand (Menge über 0) oder offener
/// Aufgabe liegt (409 <c>warehouse_not_empty</c>); Belege, die auf einen Lagerplatz verweisen, blockieren mit <c>in_use</c>.
/// Leere Unterstrukturen werden kaskadierend mitgelöscht.
/// </summary>
public class WarehouseDeleteRuleTests : IClassFixture<LagerApiFactory>
{
    private readonly LagerApiFactory _factory;
    private readonly WorldBuilder _world;

    public WarehouseDeleteRuleTests(LagerApiFactory factory)
    {
        _factory = factory;
        _world = new WorldBuilder(factory);
    }

    private async Task<HttpClient> AdminAsync() => await _factory.CreateClient().AsReadyAdminAsync();

    private static WorldBuilder.Bin AsBin(StorageLocationDto bin) => new(bin.Id, bin.Code, BinType.Standard);

    [Fact]
    public async Task A_shelf_with_stock_cannot_be_deleted_but_an_emptied_shelf_is_removed_with_its_bins()
    {
        var admin = await AdminAsync();
        var site = await admin.BuildSiteAsync(bins: 3);
        var stocked = site.Bins.First();
        var article = await _world.AddArticleAsync();
        var stockRow = await _world.AddLegacyStockAsync(article.Id, AsBin(stocked), 5);

        // Bestand > 0: Regal, Gang, Zone und Lager lassen sich nicht löschen; die Meldung nennt den Lagerplatz.
        var shelfError = await (await admin.DeleteAsync($"/api/warehouse/shelves/{site.Shelf.Id}")).ErrorAsync(HttpStatusCode.Conflict);
        Assert.Equal("warehouse_not_empty", shelfError.Code);
        Assert.Contains(stocked.Code, shelfError.Detail);
        foreach (var url in new[] { $"aisles/{site.Aisle.Id}", $"zones/{site.Zone.Id}", site.Warehouse.Id.ToString() })
            Assert.Equal("warehouse_not_empty", (await (await admin.DeleteAsync($"/api/warehouse/{url}")).ErrorAsync(HttpStatusCode.Conflict)).Code);

        // Ein einzelner Lagerplatz mit Bestand bleibt wie bisher "in Benutzung" (409 in_use); ein leerer lässt sich löschen.
        Assert.Equal("in_use", (await (await admin.DeleteAsync($"/api/warehouse/storage-locations/{stocked.Id}")).ErrorAsync(HttpStatusCode.Conflict)).Code);
        await (await admin.DeleteAsync($"/api/warehouse/storage-locations/{site.Bins.Last().Id}")).ExpectStatusAsync(HttpStatusCode.NoContent);

        // Nichts wurde teilweise gelöscht.
        var still = (await admin.FindWarehouseAsync(site.Warehouse.Id))!.Zones.Single().Aisles.Single().Shelves.Single();
        Assert.Equal(2, still.Locations.Count);

        // Bestand aufgebraucht (die Bestandszeile bleibt mit Menge 0 stehen): jetzt geht das Löschen samt Lagerplätzen.
        await _world.DbAsync(async db =>
        {
            (await db.StockItems.SingleAsync(s => s.Id == stockRow)).Remove(5);
            await db.SaveChangesAsync();
        });
        await (await admin.DeleteAsync($"/api/warehouse/shelves/{site.Shelf.Id}")).ExpectStatusAsync(HttpStatusCode.NoContent);

        var aisle = (await admin.FindWarehouseAsync(site.Warehouse.Id))!.Zones.Single().Aisles.Single();
        Assert.Empty(aisle.Shelves);
        var remainingBins = await admin.BinsAsync();
        Assert.DoesNotContain(remainingBins, b => site.Bins.Any(x => x.Id == b.Id));
        // Die leere Bestandszeile ist mit dem Lagerplatz verschwunden (sonst hätte der Fremdschlüssel das Löschen verhindert).
        Assert.Empty(await _world.StockRowsAsync(article.Id));
    }

    [Fact]
    public async Task Deleting_an_empty_warehouse_removes_the_whole_structure_including_walls_and_pick_points()
    {
        var admin = await AdminAsync();
        var site = await admin.BuildSiteAsync(bins: 4);
        var other = await admin.BuildSiteAsync(bins: 1);
        var wall = await (await admin.PostAsJsonAsync("/api/warehouse/walls",
                new CreateWallRequest(site.Warehouse.Id, "Wand", new[] { new PositionDto(0, 0, 0), new PositionDto(5_000, 0, 0) }, 200)))
            .ExpectAsync<WallDto>(HttpStatusCode.Created);
        var pickPoint = await (await admin.PostAsJsonAsync("/api/warehouse/pick-points",
                new CreatePickPointRequest(site.Warehouse.Id, "Start", "Start", new PositionDto(0, 0, 0))))
            .ExpectAsync<PickPointDto>(HttpStatusCode.Created);
        var otherWall = await (await admin.PostAsJsonAsync("/api/warehouse/walls",
                new CreateWallRequest(other.Warehouse.Id, "Fremde Wand", new[] { new PositionDto(0, 0, 0), new PositionDto(1_000, 0, 0) }, 200)))
            .ExpectAsync<WallDto>(HttpStatusCode.Created);

        await (await admin.DeleteAsync($"/api/warehouse/{site.Warehouse.Id}")).ExpectStatusAsync(HttpStatusCode.NoContent);

        Assert.Null(await admin.FindWarehouseAsync(site.Warehouse.Id));
        Assert.DoesNotContain(await admin.BinsAsync(), b => site.Bins.Any(x => x.Id == b.Id));
        var walls = (await (await admin.GetAsync("/api/warehouse/walls")).ExpectAsync<List<WallDto>>(HttpStatusCode.OK));
        Assert.DoesNotContain(walls, w => w.Id == wall.Id);
        var pickPoints = (await (await admin.GetAsync("/api/warehouse/pick-points")).ExpectAsync<List<PickPointDto>>(HttpStatusCode.OK));
        Assert.DoesNotContain(pickPoints, p => p.Id == pickPoint.Id);

        // Das andere Lager samt Wand und Lagerplatz ist unberührt.
        Assert.NotNull(await admin.FindWarehouseAsync(other.Warehouse.Id));
        Assert.Contains(await admin.BinsAsync(), b => b.Id == other.Bins.Single().Id);
        Assert.Contains((await (await admin.GetAsync("/api/warehouse/walls")).ExpectAsync<List<WallDto>>(HttpStatusCode.OK)), w => w.Id == otherWall.Id);

        // Zonen und Gänge lassen sich ebenso löschen (samt Regalen), unbekannte Ids sind 404.
        var another = await admin.BuildSiteAsync(bins: 2);
        await (await admin.DeleteAsync($"/api/warehouse/aisles/{another.Aisle.Id}")).ExpectStatusAsync(HttpStatusCode.NoContent);
        Assert.Empty((await admin.FindWarehouseAsync(another.Warehouse.Id))!.Zones.Single().Aisles);
        await (await admin.DeleteAsync($"/api/warehouse/zones/{another.Zone.Id}")).ExpectStatusAsync(HttpStatusCode.NoContent);
        Assert.Empty((await admin.FindWarehouseAsync(another.Warehouse.Id))!.Zones);
        await (await admin.DeleteAsync($"/api/warehouse/zones/{another.Zone.Id}")).ExpectStatusAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_open_replenishment_task_blocks_the_delete_and_a_finished_one_still_counts_as_in_use()
    {
        var admin = await AdminAsync();
        var site = await admin.BuildSiteAsync(bins: 2);
        var (source, target) = (site.Bins[0], site.Bins[1]);
        var article = await _world.AddArticleAsync();
        var taskId = await _world.DbAsync(async db =>
        {
            var task = new ReplenishmentTask(article.Id, article.Sku, source.Id, source.Code, target.Id, target.Code, 5);
            db.ReplenishmentTasks.Add(task);
            await db.SaveChangesAsync();
            return task.Id;
        });

        // Offene Nachschub-Aufgabe (kein Bestand nötig): das Regal ist nicht löschbar.
        var open = await (await admin.DeleteAsync($"/api/warehouse/shelves/{site.Shelf.Id}")).ErrorAsync(HttpStatusCode.Conflict);
        Assert.Equal("warehouse_not_empty", open.Code);

        // Ist die Aufgabe storniert, verweist sie als Beleg weiter auf die Lagerplätze: in_use statt eines Datenbankfehlers (500).
        await _world.DbAsync(async db =>
        {
            (await db.ReplenishmentTasks.SingleAsync(t => t.Id == taskId)).Cancel();
            await db.SaveChangesAsync();
        });
        var referenced = await (await admin.DeleteAsync($"/api/warehouse/shelves/{site.Shelf.Id}")).ErrorAsync(HttpStatusCode.Conflict);
        Assert.Equal("in_use", referenced.Code);
        Assert.Equal(2, (await admin.FindWarehouseAsync(site.Warehouse.Id))!.Zones.Single().Aisles.Single().Shelves.Single().Locations.Count);
    }

    [Fact]
    public async Task A_running_inventory_blocks_the_delete_even_when_the_stock_is_gone()
    {
        var admin = await AdminAsync();
        var site = await admin.BuildSiteAsync(bins: 1);
        var bin = site.Bins.Single();
        var article = await _world.AddArticleAsync();
        var stockRow = await _world.AddLegacyStockAsync(article.Id, AsBin(bin), 5);
        var count = await (await admin.PostAsJsonAsync("/api/inventory/start", new StartInventoryRequest(WorldBuilder.Unique("INV"), bin.Id)))
            .ExpectAsync<InventoryCountDto>(HttpStatusCode.Created);
        Assert.Single(count.Lines);
        await _world.DbAsync(async db =>
        {
            (await db.StockItems.SingleAsync(x => x.Id == stockRow)).Remove(5);
            await db.SaveChangesAsync();
        });

        // Kein Bestand mehr, aber die offene Inventur zählt den Lagerplatz noch.
        var running = await (await admin.DeleteAsync($"/api/warehouse/aisles/{site.Aisle.Id}")).ErrorAsync(HttpStatusCode.Conflict);
        Assert.Equal("warehouse_not_empty", running.Code);
        Assert.Contains(bin.Code, running.Detail);

        // Nach dem Abbruch der Inventur bleibt ihre Zeile als Beleg: in_use.
        await (await admin.PostAsync($"/api/inventory/{count.Id}/cancel", null)).ExpectStatusAsync(HttpStatusCode.NoContent);
        var finished = await (await admin.DeleteAsync($"/api/warehouse/aisles/{site.Aisle.Id}")).ErrorAsync(HttpStatusCode.Conflict);
        Assert.Equal("in_use", finished.Code);
    }
}
