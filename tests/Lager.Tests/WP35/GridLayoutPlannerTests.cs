using Lager.Application.PickLists;
using Lager.Application.Warehouse;
using Lager.Domain.Warehouse;

namespace Lager.Tests.WP35;

/// <summary>
/// Reine Rasterlogik des einfachen Layout-Editors: Feldbezeichnungen, Prüfung der Felder, Reihen für die Gang-Codes, Wände aus
/// Wand- und Hindernisfeldern (decken die Felder genau ab) und Pickpunkte je zusammenhängendem Bereich.
/// </summary>
public class GridLayoutPlannerTests
{
    private static GridCell C(int col, int row, GridCellType type = GridCellType.Shelf, int containers = 1) =>
        new(col, row, type, type.IsStorage() ? containers : 0);

    [Theory]
    [InlineData(0, 0, "A1")]
    [InlineData(2, 4, "C5")]
    [InlineData(25, 0, "Z1")]
    [InlineData(26, 9, "AA10")]
    [InlineData(99, 99, "CV100")]
    public void Cell_references_read_like_a_spreadsheet(int col, int row, string expected) =>
        Assert.Equal(expected, GridLayout.CellRef(col, row));

    [Fact]
    public void Define_drops_walkways_and_clears_containers_of_non_storage_cells()
    {
        var grid = new GridLayout(Guid.NewGuid());
        grid.Define(1_000, 5, 4, 600, 600, 400, 50_000, new[]
        {
            C(0, 0, GridCellType.Walkway),
            C(1, 0, GridCellType.Shelf, 3),
            new GridCell(2, 0, GridCellType.Wall, Containers: 7),
        });

        Assert.Equal(2, grid.Cells.Count);
        Assert.Equal(3, grid.Cells.Single(c => c.Type == GridCellType.Shelf).Containers);
        Assert.Equal(0, grid.Cells.Single(c => c.Type == GridCellType.Wall).Containers);

        // JSON-Spalte übersteht den Weg in die Datenbank und zurück.
        var json = grid.CellsJson;
        var copy = new GridLayout(Guid.NewGuid());
        typeof(GridLayout).GetProperty(nameof(GridLayout.CellsJson))!.SetValue(copy, json);
        Assert.Equal(grid.Cells, copy.Cells);
    }

    [Fact]
    public void Define_rejects_cells_outside_the_grid_duplicates_and_bad_container_counts()
    {
        var grid = new GridLayout(Guid.NewGuid());
        void Define(params GridCell[] cells) => grid.Define(1_000, 3, 3, 600, 600, 400, 0, cells);

        Assert.Contains("D1", Assert.Throws<ArgumentException>(() => Define(C(3, 0))).Message);
        Assert.Contains("doppelt", Assert.Throws<ArgumentException>(() => Define(C(1, 1), C(1, 1, GridCellType.Wall))).Message);
        Assert.Throws<ArgumentException>(() => Define(C(0, 0, GridCellType.Shelf, 0)));
        Assert.Throws<ArgumentException>(() => Define(C(0, 0, GridCellType.Shelf, GridLayout.MaxContainersPerCell + 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Define(1_000, 3, 3, 1_200, 600, 400, 0, Array.Empty<GridCell>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Define(100, 3, 3, 50, 50, 50, 0, Array.Empty<GridCell>()));
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.Define(1_000, GridLayout.MaxColumns + 1, 3, 600, 600, 400, 0, Array.Empty<GridCell>()));
    }

    [Fact]
    public void Runs_follow_the_longer_direction_and_are_numbered_top_to_bottom_then_left_to_right()
    {
        var cells = new List<GridCell>();
        // Doppelregal: zwei Zeilen mit je vier Feldern (B2:E3) -> zwei waagerechte Reihen.
        for (var col = 1; col <= 4; col++) { cells.Add(C(col, 1)); cells.Add(C(col, 2)); }
        // Senkrechte Reihe in Spalte H (H2:H5).
        for (var row = 1; row <= 4; row++) cells.Add(C(7, row));
        // Einzelnes Feld A8.
        cells.Add(C(0, 7));

        var runs = GridLayoutPlanner.DetectRuns(cells);

        Assert.Equal(4, runs.Count);
        Assert.Equal(AisleOrientation.AlongX, runs[0].Orientation);
        Assert.Equal(new[] { "B2", "C2", "D2", "E2" }, runs[0].Cells.Select(c => GridLayout.CellRef(c.Col, c.Row)));
        Assert.Equal(AisleOrientation.AlongY, runs[1].Orientation);
        Assert.Equal(new[] { "H2", "H3", "H4", "H5" }, runs[1].Cells.Select(c => GridLayout.CellRef(c.Col, c.Row)));
        Assert.Equal(new[] { "B3", "C3", "D3", "E3" }, runs[2].Cells.Select(c => GridLayout.CellRef(c.Col, c.Row)));
        Assert.Equal(new[] { "A8" }, runs[3].Cells.Select(c => GridLayout.CellRef(c.Col, c.Row)));
    }

    [Fact]
    public void Wall_cells_become_wall_segments_that_cover_exactly_those_cells()
    {
        const int size = 1_000;
        // L-förmige Wand: A1:D1 waagerecht, A2:A3 senkrecht darunter; dazu ein einzelnes Hindernis in C3.
        var cells = new List<GridCell>
        {
            C(0, 0, GridCellType.Wall), C(1, 0, GridCellType.Wall), C(2, 0, GridCellType.Wall), C(3, 0, GridCellType.Wall),
            C(0, 1, GridCellType.Wall), C(0, 2, GridCellType.Wall),
            C(2, 2, GridCellType.Obstacle),
            C(1, 1, GridCellType.Door),
        };

        var segments = GridLayoutPlanner.PlanWalls(cells, size);

        Assert.Equal(3, segments.Count);
        Assert.Contains(new GridWallSegment(GridCellType.Wall, new Position(0, 500, 0), new Position(4_000, 500, 0)), segments);
        Assert.Contains(new GridWallSegment(GridCellType.Wall, new Position(500, 1_000, 0), new Position(500, 3_000, 0)), segments);
        Assert.Contains(new GridWallSegment(GridCellType.Obstacle, new Position(2_500, 2_000, 0), new Position(2_500, 3_000, 0)), segments);

        // Als Hindernis der Wegberechnung: Wandfelder sind gesperrt, die Tür und der Laufweg daneben nicht.
        var rects = segments.Select(s => new WallRect(s.From.XMm, s.From.YMm, s.To.XMm, s.To.YMm, size)).ToList();
        bool Blocked(int col, int row) => rects.Any(r => r.Depth(new Vec2(col * size + 500, row * size + 500)) > WallRect.DepthTolerance);
        Assert.True(Blocked(0, 0));
        Assert.True(Blocked(3, 0));
        Assert.True(Blocked(0, 2));
        Assert.True(Blocked(2, 2));
        Assert.False(Blocked(1, 1));
        Assert.False(Blocked(4, 0));
        Assert.False(Blocked(1, 2));
    }

    [Fact]
    public void Each_connected_start_end_or_packing_area_becomes_one_pick_point_near_its_middle()
    {
        var cells = new List<GridCell>
        {
            // Verpackung als 3x1-Block (B2:D2) -> Pickpunkt in der Mitte (C2).
            C(1, 1, GridCellType.Packing), C(2, 1, GridCellType.Packing), C(3, 1, GridCellType.Packing),
            // Zwei getrennte Startfelder.
            C(0, 0, GridCellType.Start), C(5, 5, GridCellType.Start),
            C(9, 0, GridCellType.End),
            // Wareneingang ist nur eine markierte Fläche ohne Pickpunkt.
            C(9, 9, GridCellType.GoodsReceipt),
        };

        var areas = GridLayoutPlanner.PlanPickAreas(cells);

        Assert.Equal(4, areas.Count);
        Assert.Equal(new[] { "A1", "F6" }, areas.Where(a => a.Kind == GridCellType.Start).Select(a => GridLayout.CellRef(a.Col, a.Row)));
        var packing = Assert.Single(areas, a => a.Kind == GridCellType.Packing);
        Assert.Equal(("C2", 3), (GridLayout.CellRef(packing.Col, packing.Row), packing.CellCount));
        Assert.Single(areas, a => a.Kind == GridCellType.End);
    }
}
