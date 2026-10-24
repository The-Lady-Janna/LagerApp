using Lager.Application.PickLists;
using Lager.Application.Stock;
using Lager.Domain.Articles;
using Lager.Domain.Stock;
using Lager.Domain.Warehouse;
using Lager.Infrastructure.Persistence;
using Lager.Infrastructure.Persistence.SchemaSteps;
using Lager.Tests.WP09;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lager.Tests.WP35;

/// <summary>
/// Pufferplätze aus dem Raster (Packpuffer, Warenausgang, Retouren) halten keine frei verfügbare Ware: Picklisten und
/// Einlagerungsvorschläge übergehen sie. Dazu der Schema-Schritt, der die Tabelle GridLayouts in Bestandsdatenbanken anlegt.
/// </summary>
public class BufferBinAndSchemaTests
{
    private static StorageLocation Bin(string code, BinType type)
    {
        var bin = new StorageLocation(Guid.NewGuid(), code, Position.Origin, 600, 600, 500, 50_000);
        bin.SetBinType(type, 0);
        return bin;
    }

    [Theory]
    [InlineData(BinType.Packing)]
    [InlineData(BinType.Outgoing)]
    [InlineData(BinType.Returns)]
    public void Stock_in_buffer_bins_is_neither_picked_nor_suggested_for_putaway(BinType buffer)
    {
        var article = new Article("SKU-BUF", "Puffer", new Dimensions(100, 100, 100), 100, StackingInfo.NotStackable);
        var shelfBin = Bin("A01-01-1", BinType.Standard);
        var bufferBin = Bin("PK01-01-1", buffer);
        var stock = new[] { new StockItem(article.Id, bufferBin.Id, 50), new StockItem(article.Id, shelfBin.Id, 3) };

        var allocator = new StockAllocator(stock, new[] { shelfBin, bufferBin }.ToDictionary(b => b.Id));
        Assert.Equal(3, allocator.Available(article.Id));
        Assert.Throws<InvalidOperationException>(() => allocator.Allocate(new[] { new PickRequest(Guid.NewGuid(), Guid.NewGuid(), article.Id, 4) }));

        var suggestions = PutawayScorer.Rank(article, 1, new[] { shelfBin, bufferBin },
            new Dictionary<Guid, IReadOnlyList<StockItem>>(), new Dictionary<Guid, Article> { [article.Id] = article });
        Assert.DoesNotContain(suggestions, s => s.BinId == bufferBin.Id);
        Assert.Contains(suggestions, s => s.BinId == shelfBin.Id);
    }

    [Fact]
    public async Task The_schema_step_creates_the_grid_table_like_the_model_and_is_idempotent()
    {
        using var model = new StandaloneDb();
        using var legacy = new StandaloneDb();
        await using (var ctx = model.CreateContext())
            await ctx.Database.EnsureCreatedAsync();

        // Bestandsdatenbank vor dem Raster: alles aus dem Modell, nur ohne GridLayouts.
        await using (var ctx = legacy.CreateContext())
            await ctx.Database.EnsureCreatedAsync();
        Wp09Sql.Execute(legacy.DbPath, "DROP TABLE GridLayouts");

        var step = new AddGridLayoutStep();
        await using (var ctx = legacy.CreateContext())
        {
            await step.ApplyAsync(ctx, NullLogger.Instance);
            await step.ApplyAsync(ctx, NullLogger.Instance);
        }

        const string columns = "SELECT name FROM pragma_table_info('GridLayouts') ORDER BY name";
        Assert.Equal(Wp09Sql.Strings(model.DbPath, columns), Wp09Sql.Strings(legacy.DbPath, columns));
        Assert.Equal(1, Wp09Sql.Count(legacy.DbPath,
            "SELECT COUNT(*) FROM pragma_index_list('GridLayouts') WHERE name = 'IX_GridLayouts_WarehouseId' AND \"unique\" = 1"));
        Assert.Equal(new[] { "Warehouses" }, Wp09Sql.ForeignKeyTargets(legacy.DbPath, "GridLayouts"));

        // Das Raster lässt sich danach über EF speichern und lesen.
        var warehouseId = Guid.NewGuid();
        await using (var ctx = legacy.CreateContext())
        {
            ctx.Warehouses.Add(CreateWarehouse(warehouseId));
            var grid = new GridLayout(warehouseId);
            grid.Define(1_000, 4, 4, 600, 600, 400, 0, new[] { new GridCell(1, 1, GridCellType.Shelf, 2) });
            ctx.GridLayouts.Add(grid);
            await ctx.SaveChangesAsync();
        }
        await using (var ctx = legacy.CreateContext())
        {
            var stored = ctx.GridLayouts.Single(g => g.WarehouseId == warehouseId);
            Assert.Equal(new GridCell(1, 1, GridCellType.Shelf, 2), Assert.Single(stored.Cells));
        }
    }

    private static Lager.Domain.Warehouse.Warehouse CreateWarehouse(Guid id)
    {
        var warehouse = new Lager.Domain.Warehouse.Warehouse("WH-SCHEMA", "Schema");
        typeof(Lager.Domain.Common.Entity).GetProperty(nameof(Lager.Domain.Common.Entity.Id))!.SetValue(warehouse, id);
        return warehouse;
    }
}
