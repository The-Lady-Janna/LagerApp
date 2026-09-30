using Lager.Api.Seeding;
using Lager.Domain.Warehouse;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using WarehouseEntity = Lager.Domain.Warehouse.Warehouse;

namespace Lager.Tests.WP09;

/// <summary>Der Seeder legt nur in eine leere Datenbank Demo-Daten an, adressiert nichts über Codes und respektiert Production.</summary>
public class DemoDataSeederTests
{
    private static IHostEnvironment Env(string name) => new HostingEnvironment { EnvironmentName = name };

    private static IConfiguration Config(bool allowInProduction = false) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Demo:AllowInProduction"] = allowInProduction ? "true" : null,
        }).Build();

    private static async Task<(int Articles, int Bins, int Stock, int Orders)> CountsAsync(Lager.Infrastructure.Persistence.LagerDbContext db) =>
        (await db.Articles.CountAsync(), await db.StorageLocations.CountAsync(), await db.StockItems.CountAsync(), await db.Orders.CountAsync());

    [Fact]
    public async Task Seeder_legt_Demo_Daten_nur_in_eine_leere_Datenbank_an_und_ist_wiederholbar()
    {
        using var db = new StandaloneDb();
        await using var ctx = db.CreateContext();
        await ctx.Database.EnsureCreatedAsync();

        Assert.True(await DemoDataSeeder.SeedAsync(ctx));
        var afterFirst = await CountsAsync(ctx);
        Assert.Equal((20, 36, 25, 8), afterFirst);

        // Zweiter Start (Database:Seed bleibt an): nichts Neues, nichts doppelt, gelöschte Demo-Daten kommen nicht zurück.
        Assert.False(await DemoDataSeeder.SeedAsync(ctx));
        Assert.Equal(afterFirst, await CountsAsync(ctx));
        ctx.Orders.RemoveRange(await ctx.Orders.Include(o => o.Lines).ToListAsync());
        await ctx.SaveChangesAsync();
        Assert.False(await DemoDataSeeder.SeedAsync(ctx));
        Assert.Equal(0, await ctx.Orders.CountAsync());
    }

    [Fact]
    public async Task Seeder_stuerzt_bei_doppelten_Codes_nicht_ab_und_fasst_Nutzerdaten_nicht_an()
    {
        using var db = new StandaloneDb();
        await using var ctx = db.CreateContext();
        await ctx.Database.EnsureCreatedAsync();

        // Nutzerdaten: zwei Gänge mit demselben Code 'A1', zwei Regale 'A1-01' und ein Lagerplatz namens wie der Demo-Bin.
        // Früher lud der Seeder Gänge/Regale per ToDictionary(Code) und warf beim Start eine ArgumentException;
        // außerdem buchte er Demo-Bestand in einen Nutzer-Bin mit dem Demo-Code.
        var warehouse = new WarehouseEntity("USR", "Nutzerlager");
        var zone = new Zone(warehouse.Id, "Z-U", "Zone U", new Position(0, 0, 0));
        var aisle1 = new Aisle(zone.Id, "A1", new Position(0, 0, 0), new Position(1000, 0, 0), AisleOrientation.AlongX);
        var aisle2 = new Aisle(zone.Id, "A1", new Position(0, 2000, 0), new Position(1000, 2000, 0), AisleOrientation.AlongX);
        var shelf1 = new Shelf(aisle1.Id, "A1-01", new Position(0, 0, 0), 1000, 500, 1000);
        var shelf2 = new Shelf(aisle2.Id, "A1-01", new Position(0, 2000, 0), 1000, 500, 1000);
        var userBin = new StorageLocation(shelf1.Id, "A1-01-01", new Position(0, 0, 0), 500, 500, 500, 10_000);
        ctx.Warehouses.Add(warehouse);
        ctx.Zones.Add(zone);
        ctx.Aisles.AddRange(aisle1, aisle2);
        ctx.Shelves.AddRange(shelf1, shelf2);
        ctx.StorageLocations.Add(userBin);
        await ctx.SaveChangesAsync();

        var seeded = await DemoDataSeeder.SeedAsync(ctx);

        Assert.False(seeded);
        Assert.Equal(0, await ctx.Articles.CountAsync());   // keine Demo-Artikel
        Assert.Equal(0, await ctx.StockItems.CountAsync()); // kein Phantom-Bestand im Nutzer-Bin
        Assert.Equal(2, await ctx.Aisles.CountAsync());
        Assert.Equal(1, await ctx.StorageLocations.CountAsync());
    }

    [Fact]
    public async Task Seeder_verweigert_in_Production_ohne_Demo_AllowInProduction()
    {
        using var db = new StandaloneDb();
        await using var ctx = db.CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        var logger = new ListLogger();

        Assert.False(await DemoDataSeeder.SeedAsync(ctx, Env(Environments.Production), Config(), logger));
        Assert.Equal(0, await ctx.Articles.CountAsync());
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Production"));

        // Ausdrücklich freigegeben: Demo-Daten entstehen.
        Assert.True(await DemoDataSeeder.SeedAsync(ctx, Env(Environments.Production), Config(allowInProduction: true), new ListLogger()));
        Assert.Equal(20, await ctx.Articles.CountAsync());
    }

    [Fact]
    public async Task Seeder_in_Development_und_Staging_legt_an_Staging_warnt_zusaetzlich()
    {
        using var dev = new StandaloneDb();
        await using (var ctx = dev.CreateContext())
        {
            await ctx.Database.EnsureCreatedAsync();
            var logger = new ListLogger();
            Assert.True(await DemoDataSeeder.SeedAsync(ctx, Env(Environments.Development), Config(), logger));
            Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
        }

        using var staging = new StandaloneDb();
        await using (var ctx = staging.CreateContext())
        {
            await ctx.Database.EnsureCreatedAsync();
            var logger = new ListLogger();
            Assert.True(await DemoDataSeeder.SeedAsync(ctx, Env(Environments.Staging), Config(), logger));
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Staging"));
        }
    }
}
