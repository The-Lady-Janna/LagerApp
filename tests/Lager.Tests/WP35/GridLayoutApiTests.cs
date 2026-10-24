using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Warehouse;
using Lager.Domain.Warehouse;
using Lager.Tests.Infrastructure;
using Lager.Tests.WP22;

namespace Lager.Tests.WP35;

/// <summary>
/// Einfaches Raster-Layout über die API: Speichern erzeugt Regale mit X Behältern (gangbasierte Codes), Wände und Pickpunkte;
/// erneutes Speichern behält Ids und Codes; weniger Behälter oder weggefallene Felder entfernen nur leere Behälter, sonst lehnt
/// das Speichern als Ganzes ab (409 <c>grid_cells_in_use</c> mit den Feldern). Objekte aus dem erweiterten Editor bleiben.
/// </summary>
public class GridLayoutApiTests : IClassFixture<LagerApiFactory>
{
    private readonly LagerApiFactory _factory;
    private readonly WorldBuilder _world;

    public GridLayoutApiTests(LagerApiFactory factory)
    {
        _factory = factory;
        _world = new WorldBuilder(factory);
    }

    private Task<HttpClient> AdminAsync() => _world.AdminAsync();

    /// <summary>
    /// Code ohne Lager-Präfix: Behälter-Codes sind systemweit eindeutig, und die Tests dieser Klasse teilen sich eine Datenbank.
    /// Hat ein anderes Lager "A01-01-1" schon, beginnen die Codes mit dem Lager-Code ("WH-1A2B-A01-01").
    /// </summary>
    private static string Plain(string? code, WarehouseDto warehouse) =>
        code is not null && code.StartsWith(warehouse.Code + "-", StringComparison.OrdinalIgnoreCase) ? code[(warehouse.Code.Length + 1)..] : code ?? "";

    private static GridCellInput Cell(string type, int col, int row, int containers = 0) => new(col, row, type, containers);

    private static SaveGridLayoutRequest Request(IEnumerable<GridCellInput> cells, Guid? version, int cellSize = 1_000, int columns = 12, int rows = 8) =>
        new(cellSize, columns, rows, 600, 600, 400, 50_000, cells.ToList(), version);

    private static async Task<SaveGridLayoutResult> SaveAsync(HttpClient client, Guid warehouseId, SaveGridLayoutRequest request) =>
        await (await client.PutAsJsonAsync($"/api/warehouse/{warehouseId}/grid", request)).ExpectAsync<SaveGridLayoutResult>(HttpStatusCode.OK);

    private static async Task<GridLayoutDto> GetAsync(HttpClient client, Guid warehouseId) =>
        await (await client.GetAsync($"/api/warehouse/{warehouseId}/grid")).ExpectAsync<GridLayoutDto>(HttpStatusCode.OK);

    /// <summary>Kleines Lager: Regalreihe B2:D2 (2 Behälter), Wand A4:E4, Start in A1, Verpackung F1:F2, Packpuffer G1.</summary>
    private static List<GridCellInput> SmallWarehouse(int containers = 2) => new()
    {
        Cell("Shelf", 1, 1, containers), Cell("Shelf", 2, 1, containers), Cell("Shelf", 3, 1, containers),
        Cell("Wall", 0, 3), Cell("Wall", 1, 3), Cell("Wall", 2, 3), Cell("Wall", 3, 3), Cell("Wall", 4, 3),
        Cell("Door", 5, 3),
        Cell("Start", 0, 0),
        Cell("Packing", 5, 0), Cell("Packing", 5, 1),
        Cell("PackingStorage", 6, 0, 3),
        Cell("GoodsReceipt", 8, 0),
    };

    [Fact]
    public async Task A_warehouse_without_grid_returns_defaults_and_an_unknown_warehouse_is_404()
    {
        var admin = await AdminAsync();
        var warehouse = await admin.CreateWarehouseAsync();

        var grid = await GetAsync(admin, warehouse.Id);
        Assert.False(grid.Exists);
        Assert.Null(grid.Version);
        Assert.Equal((GridLayout.DefaultCellSizeMm, GridLayout.DefaultColumns, GridLayout.DefaultRows), (grid.CellSizeMm, grid.Columns, grid.Rows));
        Assert.Empty(grid.Cells);

        await (await admin.GetAsync($"/api/warehouse/{Guid.NewGuid()}/grid")).ExpectStatusAsync(HttpStatusCode.NotFound);
        await (await admin.PutAsJsonAsync($"/api/warehouse/{Guid.NewGuid()}/grid", Request(SmallWarehouse(), null)))
            .ExpectStatusAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Saving_creates_shelves_with_containers_walls_and_pick_points_with_aisle_based_codes()
    {
        var admin = await AdminAsync();
        var warehouse = await admin.CreateWarehouseAsync();

        var result = await SaveAsync(admin, warehouse.Id, Request(SmallWarehouse(), null));

        Assert.Equal(new GridApplySummary(ShelvesCreated: 4, ShelvesRemoved: 0, ContainersCreated: 9, ContainersRemoved: 0, Walls: 1, PickPoints: 2),
            result.Summary);
        Assert.True(result.Layout.Exists);
        Assert.Equal(new[] { "A01-01", "A01-02", "A01-03" },
            result.Layout.Cells.Where(c => c.Type == "Shelf").OrderBy(c => c.Col).Select(c => Plain(c.ShelfCode, warehouse)));
        Assert.Equal("PK01-01", Plain(result.Layout.Cells.Single(c => c.Type == "PackingStorage").ShelfCode, warehouse));
        Assert.Equal("B2", result.Layout.Cells.Single(c => Plain(c.ShelfCode, warehouse) == "A01-01").Ref);

        // Struktur: Zonen je Lagerfeld-Art, ein Gang je Reihe, Behälter übereinander in der Feldmitte.
        var tree = (await admin.FindWarehouseAsync(warehouse.Id))!;
        Assert.Equal(new[] { "LAGER", "PACKPUFFER" }, tree.Zones.Select(z => z.Code).OrderBy(c => c));
        var aisle = tree.Zones.Single(z => z.Code == "LAGER").Aisles.Single();
        Assert.Equal(("A01", "AlongX"), (aisle.Code, aisle.Orientation));
        var shelf = aisle.Shelves.Single(s => Plain(s.Code, warehouse) == "A01-02");
        Assert.Equal(new PositionDto(2_000, 1_000, 0), shelf.Position);
        Assert.Equal(new[] { "A01-02-1", "A01-02-2" }, shelf.Locations.OrderBy(l => l.Position.ZMm).Select(l => Plain(l.Code, warehouse)));
        Assert.Equal(new[] { 0, 400 }, shelf.Locations.Select(l => l.Position.ZMm).OrderBy(z => z));
        Assert.All(shelf.Locations, l => Assert.Equal((2_200, 1_200, "Standard"), (l.Position.XMm, l.Position.YMm, l.BinType)));
        var buffer = tree.Zones.Single(z => z.Code == "PACKPUFFER").Aisles.Single().Shelves.Single();
        Assert.Equal(3, buffer.Locations.Count);
        Assert.All(buffer.Locations, l => Assert.Equal("Packing", l.BinType));

        // Wand A4:E4 als eine Wand mit Feldgröße als Dicke; Pickpunkte für Start und Verpackung (Wareneingang hat keinen).
        var walls = (await (await admin.GetAsync("/api/warehouse/walls")).ExpectAsync<List<WallDto>>(HttpStatusCode.OK))
            .Where(w => w.WarehouseId == warehouse.Id).ToList();
        var wall = Assert.Single(walls);
        Assert.Equal(1_000, wall.ThicknessMm);
        Assert.Equal(new[] { new PositionDto(0, 3_500, 0), new PositionDto(5_000, 3_500, 0) }, wall.Points);
        var points = (await (await admin.GetAsync("/api/warehouse/pick-points")).ExpectAsync<List<PickPointDto>>(HttpStatusCode.OK))
            .Where(p => p.WarehouseId == warehouse.Id).OrderBy(p => p.Label).ToList();
        Assert.Equal(new[] { ("Start", "Start"), ("Verpackung", "Both") }, points.Select(p => (p.Label, p.Type)));
        Assert.Equal(new PositionDto(500, 500, 0), points[0].Position);

        Assert.Equal(walls.Select(w => w.Id), result.Layout.ManagedWallIds);
        Assert.Equal(points.Select(p => p.Id).OrderBy(i => i), result.Layout.ManagedPickPointIds.OrderBy(i => i));
    }

    [Fact]
    public async Task Saving_again_keeps_ids_and_codes_and_changes_only_what_changed()
    {
        var admin = await AdminAsync();
        var warehouse = await admin.CreateWarehouseAsync();
        var first = await SaveAsync(admin, warehouse.Id, Request(SmallWarehouse(), null));

        var same = await SaveAsync(admin, warehouse.Id, Request(SmallWarehouse(), first.Layout.Version));
        Assert.Equal(new GridApplySummary(0, 0, 0, 0, 1, 2), same.Summary);
        Assert.Equal(first.Layout.Cells.Select(c => (c.Ref, c.ShelfId, c.ShelfCode)), same.Layout.Cells.Select(c => (c.Ref, c.ShelfId, c.ShelfCode)));
        Assert.Equal(first.Layout.ManagedPickPointIds.OrderBy(i => i), same.Layout.ManagedPickPointIds.OrderBy(i => i));

        // Mehr Behälter, ein Regal mehr am Anfang der Reihe (A2) und C2 weg: alte Codes bleiben, neue bekommen freie Nummern.
        var cells = SmallWarehouse(containers: 4).Where(c => !(c.Col == 2 && c.Row == 1)).ToList();
        cells.Add(Cell("Shelf", 0, 1, 1));
        var changed = await SaveAsync(admin, warehouse.Id, Request(cells, same.Layout.Version));

        Assert.Equal((1, 1, 1 + 2 * 2, 2), (changed.Summary.ShelvesCreated, changed.Summary.ShelvesRemoved, changed.Summary.ContainersCreated, changed.Summary.ContainersRemoved));
        var codes = changed.Layout.Cells.Where(c => c.Type == "Shelf").ToDictionary(c => c.Ref, c => Plain(c.ShelfCode, warehouse));
        Assert.Equal("A01-01", codes["B2"]);
        Assert.Equal("A01-03", codes["D2"]);
        Assert.Equal("A01-04", codes["A2"]);
        var shelf = (await admin.FindWarehouseAsync(warehouse.Id))!.Zones.SelectMany(z => z.Aisles).SelectMany(a => a.Shelves).Single(s => Plain(s.Code, warehouse) == "A01-01");
        Assert.Equal(new[] { "A01-01-1", "A01-01-2", "A01-01-3", "A01-01-4" }, shelf.Locations.Select(l => Plain(l.Code, warehouse)).OrderBy(c => c));

        // Weniger Behälter: die obersten verschwinden.
        var fewer = await SaveAsync(admin, warehouse.Id, Request(cells.Select(c => c.Type == "Shelf" ? c with { Containers = 1 } : c), changed.Layout.Version));
        Assert.Equal(3 + 3, fewer.Summary.ContainersRemoved);
        shelf = (await admin.FindWarehouseAsync(warehouse.Id))!.Zones.SelectMany(z => z.Aisles).SelectMany(a => a.Shelves).Single(s => Plain(s.Code, warehouse) == "A01-01");
        Assert.Equal("A01-01-1", Plain(Assert.Single(shelf.Locations).Code, warehouse));
    }

    [Fact]
    public async Task Cells_with_stock_cannot_be_removed_and_nothing_changes()
    {
        var admin = await AdminAsync();
        var warehouse = await admin.CreateWarehouseAsync();
        var saved = await SaveAsync(admin, warehouse.Id, Request(SmallWarehouse(), null));
        var d2 = saved.Layout.Cells.Single(c => c.Ref == "D2");
        var bin = (await admin.BinsAsync()).Where(b => b.ShelfId == d2.ShelfId).OrderBy(b => b.Position.ZMm).Last();
        Assert.Equal("A01-03-2", Plain(bin.Code, warehouse));
        var article = await _world.AddArticleAsync();
        await _world.AddLegacyStockAsync(article.Id, new WorldBuilder.Bin(bin.Id, bin.Code, BinType.Standard), 5);

        var occupied = await GetAsync(admin, warehouse.Id);
        Assert.Equal(1, occupied.Cells.Single(c => c.Ref == "D2").OccupiedContainers);

        // D2 wird Laufweg und die übrigen Regale bekommen nur noch einen Behälter: beides trifft den Behälter mit Bestand.
        var cells = SmallWarehouse(containers: 1).Where(c => !(c.Col == 3 && c.Row == 1)).ToList();
        var error = await (await admin.PutAsJsonAsync($"/api/warehouse/{warehouse.Id}/grid", Request(cells, saved.Layout.Version)))
            .ErrorAsync(HttpStatusCode.Conflict);
        Assert.Equal("grid_cells_in_use", error.Code);
        Assert.Contains("D2", error.Detail);
        var blocked = Assert.Single(error.Body.GetProperty("cells").EnumerateArray());
        Assert.Equal("D2", blocked.GetProperty("ref").GetString());
        Assert.Equal(bin.Code, Assert.Single(blocked.GetProperty("containerCodes").EnumerateArray()).GetString());

        var after = await GetAsync(admin, warehouse.Id);
        Assert.Equal(saved.Layout.Version, after.Version);
        Assert.Equal(9, (await admin.BinsAsync()).Count(b => saved.Layout.ManagedShelfIds.Contains(b.ShelfId)));
    }

    [Fact]
    public async Task A_stale_version_is_rejected_with_grid_conflict()
    {
        var admin = await AdminAsync();
        var warehouse = await admin.CreateWarehouseAsync();
        var first = await SaveAsync(admin, warehouse.Id, Request(SmallWarehouse(), null));
        await SaveAsync(admin, warehouse.Id, Request(SmallWarehouse(3), first.Layout.Version));

        var error = await (await admin.PutAsJsonAsync($"/api/warehouse/{warehouse.Id}/grid", Request(SmallWarehouse(), first.Layout.Version)))
            .ErrorAsync(HttpStatusCode.Conflict);
        Assert.Equal("grid_conflict", error.Code);
        var missing = await (await admin.PutAsJsonAsync($"/api/warehouse/{warehouse.Id}/grid", Request(SmallWarehouse(), null)))
            .ErrorAsync(HttpStatusCode.Conflict);
        Assert.Equal("grid_conflict", missing.Code);
    }

    [Fact]
    public async Task Objects_of_the_advanced_editor_stay_and_bin_codes_of_other_warehouses_get_a_prefix()
    {
        var admin = await AdminAsync();
        // Ein anderes Lager hat schon Behälter "A01-01-1" ... (etwa aus einem Raster): das neue Raster weicht auf den Lager-Code aus.
        var other = await admin.CreateWarehouseAsync();
        await SaveAsync(admin, other.Id, Request(new[] { Cell("Shelf", 0, 0, 2) }, null));

        var site = await admin.BuildSiteAsync(bins: 2);
        var result = await SaveAsync(admin, site.Warehouse.Id, Request(new[] { Cell("Shelf", 0, 0, 2), Cell("Shelf", 1, 0, 2) }, null));

        Assert.Equal(new[] { $"{site.Warehouse.Code}-A01-01", $"{site.Warehouse.Code}-A01-02" },
            result.Layout.Cells.OrderBy(c => c.Col).Select(c => c.ShelfCode));
        var tree = (await admin.FindWarehouseAsync(site.Warehouse.Id))!;
        Assert.Contains(tree.Zones.SelectMany(z => z.Aisles).SelectMany(a => a.Shelves), s => s.Id == site.Shelf.Id && s.Locations.Count == 2);
        Assert.DoesNotContain(site.Shelf.Id, result.Layout.ManagedShelfIds);

        // Leeres Raster: die Raster-Objekte verschwinden samt Zone, das Regal aus dem erweiterten Editor bleibt.
        var cleared = await SaveAsync(admin, site.Warehouse.Id, Request(Array.Empty<GridCellInput>(), result.Layout.Version));
        Assert.Equal((2, 4), (cleared.Summary.ShelvesRemoved, cleared.Summary.ContainersRemoved));
        tree = (await admin.FindWarehouseAsync(site.Warehouse.Id))!;
        Assert.Equal(new[] { site.Zone.Id }, tree.Zones.Select(z => z.Id));
        Assert.Single(tree.Zones.Single().Aisles.Single().Shelves);

        // Ein Lager mit Raster lässt sich löschen (das Raster geht mit).
        await (await admin.DeleteAsync($"/api/warehouse/{other.Id}")).ExpectStatusAsync(HttpStatusCode.NoContent);
        await (await admin.GetAsync($"/api/warehouse/{other.Id}/grid")).ExpectStatusAsync(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Invalid_grids_are_400_and_only_managers_may_save()
    {
        var admin = await AdminAsync();
        var warehouse = await admin.CreateWarehouseAsync();

        async Task<string?> Rejected(SaveGridLayoutRequest request) =>
            (await (await admin.PutAsJsonAsync($"/api/warehouse/{warehouse.Id}/grid", request)).ErrorAsync(HttpStatusCode.BadRequest)).Code;

        Assert.Equal("validation_failed", await Rejected(Request(new[] { Cell("Lava", 0, 0) }, null)));
        Assert.Equal("validation_failed", await Rejected(Request(new[] { Cell("Shelf", 0, 0, 0) }, null)));
        Assert.Equal("validation_failed", await Rejected(Request(new[] { Cell("Shelf", 20, 0, 1) }, null)));
        Assert.Equal("validation_failed", await Rejected(Request(new[] { Cell("Wall", 0, 0), Cell("Door", 0, 0) }, null)));
        Assert.Equal("validation_failed", await Rejected(Request(Array.Empty<GridCellInput>(), null) with { ContainerWidthMm = 1_500 }));
        Assert.Equal("validation_failed", await Rejected(Request(Array.Empty<GridCellInput>(), null) with { Columns = GridLayout.MaxColumns + 1 }));

        var picker = await _world.ClientAsync("Picker");
        await (await picker.GetAsync($"/api/warehouse/{warehouse.Id}/grid")).ExpectStatusAsync(HttpStatusCode.OK);
        await (await picker.PutAsJsonAsync($"/api/warehouse/{warehouse.Id}/grid", Request(SmallWarehouse(), null)))
            .ExpectStatusAsync(HttpStatusCode.Forbidden);
    }
}
