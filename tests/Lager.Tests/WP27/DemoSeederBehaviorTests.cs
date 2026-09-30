using Lager.Api.Seeding;
using Lager.Application.Abstractions;
using Lager.Domain.Auth;
using Lager.Domain.Suppliers;
using Lager.Infrastructure.Auth;
using Lager.Infrastructure.Persistence;
using Lager.Tests.Infrastructure;
using Lager.Tests.WP01;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lager.Tests.WP27;

/// <summary>
/// Verhalten des Seeders und des Starts: nur in eine leere Datenbank, nie Reseed, reproduzierbar, atomar, vorhandene Benutzer bleiben,
/// und der Weg von <c>Demo:Enabled</c> über Program.cs bis zu den Daten (samt Sperre in Production).
/// </summary>
public class DemoSeederBehaviorTests
{
    /// <summary>Der Dienstanbieter, den der Seeder braucht (nur den Passwort-Hasher).</summary>
    private static ServiceProvider Services(IPasswordHasher? hasher = null) =>
        new ServiceCollection().AddSingleton(hasher ?? new BCryptPasswordHasher()).BuildServiceProvider();

    private static DateTime ThirtyDaysAgo() => DateTime.UtcNow.Date.AddDays(-30).AddHours(12);

    private sealed class ThrowingHasher : IPasswordHasher
    {
        public string Hash(string plainPassword) => throw new InvalidOperationException("Hasher kaputt (Test)");
        public bool Verify(string plainPassword, string hash) => false;
        public bool VerifyDummy(string plainPassword) => false;
    }

    // ---- Nur in eine leere Datenbank ------------------------------------------------------------------------------------

    [Fact]
    public async Task Eine_Datenbank_mit_Fachdaten_bleibt_unberuehrt_und_bekommt_keine_Demo_Benutzer()
    {
        using var db = new DemoDb();
        await using var ctx = await db.CreateEmptyAsync();
        ctx.Suppliers.Add(new Supplier("EIGEN-1", "Eigener Lieferant"));
        await ctx.SaveChangesAsync();
        var logger = new ListLogger();
        using var services = Services();

        var seeded = await DemoDataSeeder.SeedAsync(ctx, services, logger, historyDays: 20);

        Assert.False(seeded);
        Assert.Equal(0, await ctx.Articles.CountAsync());
        Assert.Equal(0, await ctx.Users.CountAsync());
        Assert.Equal(1, await ctx.Suppliers.CountAsync());
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("Demo-Zugangsdaten"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Information && e.Message.Contains("schon Daten"));
    }

    [Fact]
    public async Task Vorhandene_Demo_Benutzer_bleiben_unveraendert_und_ihr_Passwort_wird_nicht_ausgegeben()
    {
        using var db = new DemoDb();
        await using var ctx = await db.CreateEmptyAsync();
        var hasher = new BCryptPasswordHasher();
        var existingHash = hasher.Hash("Vorhanden-Pw-2026!");
        ctx.Users.Add(new User("picker", existingHash, Role.Admin, displayName: "Bestehender Nutzer"));
        await ctx.SaveChangesAsync();
        var logger = new ListLogger();
        using var services = Services(hasher);

        Assert.True(await DemoDataSeeder.SeedAsync(ctx, services, logger, historyDays: 20));

        var picker = await ctx.Users.AsNoTracking().SingleAsync(u => u.Username == "picker");
        Assert.Equal(existingHash, picker.PasswordHash);
        Assert.Equal(Role.Admin, picker.Roles);
        Assert.Equal(DemoCatalog.Users.Count, await ctx.Users.CountAsync());   // ein bestehender + alle übrigen
        var credentials = Assert.Single(logger.Entries, e => e.Message.StartsWith("Demo-Zugangsdaten", StringComparison.Ordinal)).Message;
        Assert.DoesNotContain("picker ", credentials.Replace("picker2", "").Replace("picker3", ""));
        Assert.Contains("packer", credentials);
    }

    // ---- Reproduzierbar --------------------------------------------------------------------------------------------------

    private static async Task<string> FingerprintAsync(LagerDbContext ctx)
    {
        var articles = await ctx.Articles.AsNoTracking().ToListAsync();
        var sku = articles.ToDictionary(a => a.Id, a => a.Sku);
        var stock = (await ctx.StockItems.AsNoTracking().ToListAsync())
            .GroupBy(s => sku[s.ArticleId]).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}={g.Sum(s => s.Quantity)}");
        var movements = (await ctx.StockMovements.AsNoTracking().ToListAsync())
            .GroupBy(m => (Sku: sku[m.ArticleId], m.Reason)).OrderBy(g => g.Key.Sku, StringComparer.Ordinal).ThenBy(g => g.Key.Reason)
            .Select(g => $"{g.Key.Sku}/{g.Key.Reason}={g.Count()}:{g.Sum(m => m.QuantityDelta)}");
        var orders = (await ctx.Orders.AsNoTracking().ToListAsync())
            .OrderBy(o => o.OrderNumber, StringComparer.Ordinal).Select(o => $"{o.OrderNumber}:{o.Status}:{o.Priority}:{o.CreatedAt:o}");
        var pickLists = (await ctx.PickLists.AsNoTracking().ToListAsync())
            .OrderBy(p => p.PickListNumber, StringComparer.Ordinal).Select(p => $"{p.PickListNumber}:{p.Status}:{p.AssignedTo}:{p.CreatedAt:o}:{p.UpdatedAt:o}");
        var purchaseOrders = (await ctx.PurchaseOrders.AsNoTracking().ToListAsync())
            .OrderBy(p => p.PoNumber, StringComparer.Ordinal).Select(p => $"{p.PoNumber}:{p.Status}");
        return string.Join("\n", new[]
        {
            $"articles={articles.Count}",
            $"stock={string.Join(";", stock)}",
            $"movements={string.Join(";", movements)}",
            $"orders={string.Join(";", orders)}",
            $"picklists={string.Join(";", pickLists)}",
            $"pos={string.Join(";", purchaseOrders)}",
            $"suppliers={await ctx.Suppliers.CountAsync()}",
            $"returns={await ctx.ReturnShipments.CountAsync()}",
            $"replenishment={await ctx.ReplenishmentTasks.CountAsync()}",
            $"shipments={await ctx.Shipments.CountAsync()}",
        });
    }

    [Fact]
    public async Task Zwei_Laeufe_mit_demselben_Bezugsdatum_ergeben_dieselben_Artikel_und_Bestandssummen()
    {
        var now = ThirtyDaysAgo();
        string first, second;
        using (var db = new DemoDb())
        {
            await using var ctx = await db.CreateEmptyAsync();
            await DemoHistoryGenerator.GenerateAsync(ctx, now, historyDays: 30);
            first = await FingerprintAsync(ctx);
        }
        using (var db = new DemoDb())
        {
            await using var ctx = await db.CreateEmptyAsync();
            await DemoHistoryGenerator.GenerateAsync(ctx, now, historyDays: 30);
            second = await FingerprintAsync(ctx);
        }

        Assert.Equal(first, second);
    }

    [Fact]
    public async Task Ein_anderer_Zufallsstartwert_ergibt_eine_andere_Historie_bei_gleichem_Katalog()
    {
        var now = ThirtyDaysAgo();
        using var db1 = new DemoDb();
        using var db2 = new DemoDb();
        await using var ctx1 = await db1.CreateEmptyAsync();
        await using var ctx2 = await db2.CreateEmptyAsync();
        await DemoHistoryGenerator.GenerateAsync(ctx1, now, seed: 1, historyDays: 30);
        await DemoHistoryGenerator.GenerateAsync(ctx2, now, seed: 2, historyDays: 30);

        Assert.Equal(await ctx1.Articles.CountAsync(), await ctx2.Articles.CountAsync());
        Assert.NotEqual(await FingerprintAsync(ctx1), await FingerprintAsync(ctx2));
    }

    [Fact]
    public async Task Die_Historie_braucht_mindestens_20_Tage()
    {
        using var db = new DemoDb();
        await using var ctx = await db.CreateEmptyAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => DemoHistoryGenerator.GenerateAsync(ctx, historyDays: 10));
    }

    [Fact]
    public async Task Eine_kurze_Historie_ist_in_sich_stimmig()
    {
        using var db = new DemoDb();
        await using var ctx = await db.CreateEmptyAsync();

        var summary = await DemoHistoryGenerator.GenerateAsync(ctx, ThirtyDaysAgo(), historyDays: 20);

        Assert.True(summary.Articles >= 40 && summary.Orders > 50 && summary.PickLists > 20);
        var stock = await ctx.StockItems.AsNoTracking().GroupBy(s => s.ArticleId).Select(g => new { g.Key, Q = g.Sum(s => s.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Q);
        var ledger = await ctx.StockMovements.AsNoTracking().GroupBy(m => m.ArticleId).Select(g => new { g.Key, Q = g.Sum(m => m.QuantityDelta) }).ToDictionaryAsync(x => x.Key, x => x.Q);
        foreach (var (articleId, quantity) in stock)
            Assert.Equal(quantity, ledger[articleId]);
    }

    // ---- Atomar ----------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Scheitert_das_Seeding_mittendrin_bleibt_die_Datenbank_leer()
    {
        using var db = new DemoDb();
        await using var ctx = await db.CreateEmptyAsync();
        using var services = Services(new ThrowingHasher());   // scheitert nach dem Generator, beim Anlegen der Benutzer

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DemoDataSeeder.SeedAsync(ctx, services, new ListLogger(), historyDays: 20));

        Assert.Contains("Hasher kaputt", ex.Message);
        Assert.Equal(0, await ctx.Articles.CountAsync());
        Assert.Equal(0, await ctx.StorageLocations.CountAsync());
        Assert.Equal(0, await ctx.StockItems.CountAsync());
        Assert.Equal(0, await ctx.StockMovements.CountAsync());
        Assert.Equal(0, await ctx.Orders.CountAsync());
        Assert.Equal(0, await ctx.Users.CountAsync());
        Assert.Equal(0, await ctx.PickListSequences.CountAsync());   // auch die Nummernkreise sind zurückgerollt
        Assert.True(await DemoDataSeeder.IsEmptyAsync(ctx), "danach lässt sich das Seeding wiederholen");
    }

    // ---- Start der Anwendung ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ohne_Demo_Enabled_bleibt_die_Datenbank_nach_dem_Start_leer()
    {
        using var factory = new LagerApiFactory();   // Seed=false, kein Demo-Schalter
        await factory.CreateClient().AsReadyAdminAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LagerDbContext>();
        Assert.Equal(0, await db.Articles.CountAsync());
        Assert.Equal(0, await db.Orders.CountAsync());
        Assert.Equal(0, await db.StockItems.CountAsync());
        Assert.Equal(1, await db.Users.CountAsync());   // nur der Bootstrap-Admin
    }

    [Fact]
    public async Task Mit_Demo_Enabled_startet_die_App_auf_leerer_Datenbank_mit_Daten_in_allen_Bereichen()
    {
        using var baseFactory = new LagerApiFactory();
        // Database:Seed steht zusätzlich an: der veraltete Schalter wird bei aktivem Demo-Modus ignoriert (kein Mischdatensatz).
        using var factory = baseFactory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Demo:Enabled", "true");
            b.UseSetting("Database:Seed", "true");
        });

        await factory.CreateClient().AsReadyAdminAsync();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LagerDbContext>();
        Assert.True(await db.Articles.CountAsync() >= 40);
        Assert.False(await db.Articles.AnyAsync(a => a.Sku == "SKU-001"), "der kleine Altbestand-Datensatz darf nicht mitgelegt werden");
        Assert.False(await db.Orders.AnyAsync(o => o.OrderNumber.StartsWith("ORD-DEMO")));
        Assert.True(await db.Suppliers.CountAsync() == 3 && await db.Customers.CountAsync() >= 5);
        Assert.True(await db.StockMovements.CountAsync() > 300);
        Assert.True(await db.PickLists.CountAsync() > 50);
        Assert.True(await db.PurchaseOrders.CountAsync() > 10);
        Assert.True(await db.ReturnShipments.CountAsync() > 5);
        Assert.Equal(1 + DemoCatalog.Users.Count, await db.Users.CountAsync());
        Assert.False(await db.Users.AnyAsync(u => u.Username != "admin" && u.MustChangePassword));
    }

    [Fact]
    public void Production_mit_Demo_Enabled_bricht_den_Start_mit_klarer_Meldung_ab()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = LagerApiFactory.SigningKey,
            ["Demo:Enabled"] = "true",
        };
        using var factory = new ConfigurableApiFactory("Production", settings);

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        var text = ex.ToString();
        Assert.Contains("Demo:Enabled", text);
        Assert.Contains("Production", text);
        Assert.Contains("Demo:AllowInProduction", text);
    }

    [Fact]
    public async Task Production_mit_ausdruecklicher_Freigabe_startet_und_legt_die_Demo_Daten_an()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = LagerApiFactory.SigningKey,
            ["Demo:Enabled"] = "true",
            ["Demo:AllowInProduction"] = "true",
        };
        using var factory = new ConfigurableApiFactory("Production", settings);

        factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LagerDbContext>();
        Assert.True(await db.Articles.CountAsync() >= 40);
    }

    [Fact]
    public async Task Production_ohne_Demo_Enabled_startet_normal_und_leer()
    {
        var settings = new Dictionary<string, string?> { ["Jwt:SigningKey"] = LagerApiFactory.SigningKey };
        using var factory = new ConfigurableApiFactory("Production", settings);

        factory.CreateClient();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LagerDbContext>();
        Assert.Equal(0, await db.Articles.CountAsync());
    }

    [Fact]
    public void Program_bildet_LAGER_DEMO_ab_und_prueft_die_Umgebung_vor_dem_Aufbau_der_Anwendung()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "Lager.Api", "Program.cs"));

        var shortcut = program.IndexOf("DemoSettings.ApplyEnvironmentShortcut(builder.Configuration, Environment.GetEnvironmentVariable)", StringComparison.Ordinal);
        var check = program.IndexOf("DemoSettings.From(builder.Configuration, builder.Environment)", StringComparison.Ordinal);
        var build = program.IndexOf("var app = builder.Build();", StringComparison.Ordinal);
        Assert.True(shortcut >= 0 && check > shortcut && build > check, "LAGER_DEMO abbilden, Umgebung prüfen, erst dann bauen");
    }
}
