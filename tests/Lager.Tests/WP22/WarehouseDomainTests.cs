using Lager.Domain.Warehouse;
using WarehouseEntity = Lager.Domain.Warehouse.Warehouse;

namespace Lager.Tests.WP22;

/// <summary>Domain-Regeln der Lager-Stammdaten ohne Host: Umbenennen/Ändern der Ebenen und die Grenzen des Regals.</summary>
public class WarehouseDomainTests
{
    private static Shelf NewShelf() => new(Guid.NewGuid(), "S1", Position.Origin, 8_000, 600, 2_000);

    [Fact]
    public void Update_trims_the_values_and_marks_the_entity_as_changed()
    {
        var warehouse = new WarehouseEntity("WH1", "Hauptlager");
        var before = warehouse.ConcurrencyToken;

        warehouse.Update("  WH2 ", " Neues Lager ");

        Assert.Equal(("WH2", "Neues Lager"), (warehouse.Code, warehouse.Name));
        Assert.NotEqual(before, warehouse.ConcurrencyToken);

        var zone = new Zone(warehouse.Id, "Z1", "Zone", Position.Origin);
        zone.Update(" Z2 ", " Zone 2 ", new Position(10, 20, 0));
        Assert.Equal(("Z2", "Zone 2", new Position(10, 20, 0)), (zone.Code, zone.Name, zone.Origin));

        var aisle = new Aisle(zone.Id, "A1", Position.Origin, new Position(10_000, 0, 0), AisleOrientation.AlongX);
        aisle.Update(" A2 ", new Position(0, 500, 0), new Position(0, 9_000, 0), AisleOrientation.AlongY);
        Assert.Equal(("A2", AisleOrientation.AlongY, new Position(0, 9_000, 0)), (aisle.Code, aisle.Orientation, aisle.EndPosition));
    }

    [Fact]
    public void Update_requires_a_code_and_positive_dimensions()
    {
        Assert.Throws<ArgumentException>(() => new WarehouseEntity("WH1", "Lager").Update(" ", "Lager"));
        Assert.Throws<ArgumentException>(() => new WarehouseEntity("WH1", "Lager").Update("WH1", ""));
        Assert.Throws<ArgumentException>(() => new Aisle(Guid.NewGuid(), "A1", Position.Origin, Position.Origin, AisleOrientation.AlongX)
            .Update("", Position.Origin, Position.Origin, AisleOrientation.AlongX));

        var shelf = NewShelf();
        Assert.Throws<ArgumentOutOfRangeException>(() => shelf.Update("S1", 0, 600, 2_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => shelf.Update("S1", 8_000, -1, 2_000));
        Assert.Throws<ArgumentException>(() => shelf.Update(" ", 8_000, 600, 2_000));

        var bin = shelf.AddLocation("B1", 600, 600, 500, 1_000);
        Assert.Throws<ArgumentOutOfRangeException>(() => bin.Update("B1", 600, 600, 0, 1_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => bin.Update("B1", 600, 600, 500, -1));
        bin.Update(" B9 ", 700, 800, 900, 0);
        Assert.Equal(("B9", 700, 800, 900, 0, new Position(0, 0, 500)), (bin.Code, bin.WidthMm, bin.DepthMm, bin.HeightMm, bin.MaxWeightGrams, bin.Position));
    }

    [Fact]
    public void A_shelf_rejects_a_duplicate_bin_code_ignoring_case_and_carries_the_error_code()
    {
        var shelf = NewShelf();
        shelf.AddLocation("BIN-01", 600, 600, 500, 1_000);

        var ex = Assert.Throws<InvalidOperationException>(() => shelf.AddLocation(" bin-01 ", 600, 600, 500, 1_000));

        Assert.Equal("duplicate_code", ex.Data["code"]);
        Assert.Contains("BIN-01", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Single(shelf.Locations);
    }

    [Fact]
    public void A_shelf_holds_at_most_500_bins()
    {
        var shelf = NewShelf();
        for (var i = 1; i <= Shelf.MaxLocations; i++) shelf.AddLocation($"B-{i:D3}", 10, 600, 500, 1_000);

        var ex = Assert.Throws<InvalidOperationException>(() => shelf.AddLocation("B-501", 10, 600, 500, 1_000));

        Assert.Equal("shelf_bin_limit", ex.Data["code"]);
        Assert.Equal(500, shelf.Locations.Count);
    }
}
