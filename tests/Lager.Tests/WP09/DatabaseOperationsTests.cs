using Lager.Domain.Articles;
using Lager.Domain.Inbound;
using Lager.Domain.Stock;
using Lager.Infrastructure;
using Lager.Infrastructure.Persistence;
using Lager.Api.Seeding;
using Lager.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Lager.Tests.WP09;

/// <summary>SQLite-Betrieb (WAL, Busy-Timeout, Pfade), MySQL-Einstellungen und die Regeln des Modells für neue Datenbanken.</summary>
public class DatabaseOperationsTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    // ---- SQLite-Pragmas ---------------------------------------------------------------------------------------

    [Fact]
    public async Task SQLite_laeuft_mit_WAL_Busy_Timeout_und_synchronous_NORMAL()
    {
        using var factory = new Wp09Factory();
        await factory.CreateClient().AsReadyAdminAsync();

        var pragmas = await factory.WithDbAsync(async db =>
        {
            async Task<string> Pragma(string name)
            {
                await using var cmd = await OpenCommandAsync(db, $"PRAGMA {name};");
                return Convert.ToString(await cmd.ExecuteScalarAsync())!;
            }
            return (Journal: await Pragma("journal_mode"), Busy: await Pragma("busy_timeout"), Sync: await Pragma("synchronous"));
        });

        Assert.Equal("wal", pragmas.Journal);
        Assert.Equal("5000", pragmas.Busy);
        Assert.Equal("1", pragmas.Sync); // 1 = NORMAL (Standard wäre 2 = FULL)
    }

    private static async Task<System.Data.Common.DbCommand> OpenCommandAsync(LagerDbContext db, string sql)
    {
        await db.Database.OpenConnectionAsync(); // über EF, damit der Pragma-Interceptor läuft
        var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        return cmd;
    }

    // ---- Pfade und MySQL --------------------------------------------------------------------------------------

    [Fact]
    public void Relativer_SQLite_Pfad_gilt_gegen_den_ContentRoot_absolute_bleiben_unveraendert()
    {
        var root = Path.Combine(Path.GetTempPath(), "lager-root");

        var relative = DatabaseSettings.Create(Config(("Database:ConnectionString", "Data Source=lager.db")), root);
        Assert.Equal(Path.Combine(root, "lager.db"), relative.SqliteFilePath);
        Assert.Contains(Path.Combine(root, "lager.db"), relative.ConnectionString);

        // Auch die Schreibweisen 'Filename=' und 'DataSource=' (früher nur per Regex 'Data Source=' erkannt).
        Assert.Equal(Path.Combine(root, "x.db"), DatabaseSettings.Create(Config(("Database:ConnectionString", "Filename=x.db")), root).SqliteFilePath);
        Assert.Equal(Path.Combine(root, "y.db"), DatabaseSettings.Create(Config(("Database:ConnectionString", "DataSource=y.db;Cache=Shared")), root).SqliteFilePath);

        var absolutePath = Path.Combine(Path.GetTempPath(), "elsewhere", "abs.db");
        var absolute = DatabaseSettings.Create(Config(("Database:ConnectionString", $"Data Source={absolutePath}")), root);
        Assert.Equal(absolutePath, absolute.SqliteFilePath);
        Assert.Equal($"Data Source={absolutePath}", absolute.ConnectionString);

        // In-Memory hat keine Datei.
        Assert.Null(DatabaseSettings.Create(Config(("Database:ConnectionString", "Data Source=:memory:")), root).SqliteFilePath);
    }

    [Fact]
    public void MySQL_Serverversion_kommt_aus_der_Konfiguration_ohne_Verbindung_und_ohne_Auto_Detect()
    {
        // Der Server "unreachable.invalid" existiert nicht: Ein AutoDetect würde scheitern. Die konfigurierte Version genügt.
        var settings = DatabaseSettings.Create(Config(
            ("Database:Provider", "MySql"),
            ("Database:ConnectionString", "Server=unreachable.invalid;Database=lager;User=x;Password=y"),
            ("Database:MySqlServerVersion", "8.0.36")), Path.GetTempPath());

        Assert.True(settings.IsMySql);
        Assert.Null(settings.SqliteFilePath);
        var first = settings.GetMySqlServerVersion();
        Assert.Equal(new Version(8, 0, 36), first.Version);
        Assert.Same(first, settings.GetMySqlServerVersion()); // einmal ermittelt, nicht pro Aufruf/Scope

        Assert.Throws<InvalidOperationException>(() =>
            DatabaseSettings.Create(Config(("Database:Provider", "Oracle"), ("Database:ConnectionString", "x")), Path.GetTempPath()));
    }

    // ---- Modell für neue Datenbanken ---------------------------------------------------------------------------

    [Fact]
    public async Task Neue_Datenbank_hat_Fremdschluessel_fuer_die_bisher_losen_Beziehungen()
    {
        using var factory = new Wp09Factory();
        await factory.CreateClient().AsReadyAdminAsync();

        void Expect(string table, params string[] targets)
        {
            var actual = Wp09Sql.ForeignKeyTargets(factory.DbPath, table);
            foreach (var target in targets)
                Assert.True(actual.Contains(target), $"{table} sollte einen Fremdschlüssel auf {target} haben (hat: {string.Join(", ", actual)})");
        }

        Expect("PurchaseOrders", "Suppliers");
        Expect("PurchaseOrderLines", "PurchaseOrders", "Articles");
        Expect("ReturnShipments", "Orders");
        Expect("ReturnLines", "ReturnShipments", "Articles", "StorageLocations");
        Expect("Shipments", "Orders", "PickLists");
        Expect("Orders", "Customers", "CustomerAddresses");
        Expect("Articles", "Suppliers");
        Expect("BundleComponents", "Articles");
        Expect("ReplenishmentTasks", "Articles", "StorageLocations");
        Expect("InventoryLines", "InventoryCounts", "Articles", "StorageLocations");
        Expect("Walls", "Warehouses");
        Expect("PickPoints", "Warehouses");
    }

    [Fact]
    public async Task Bestandszeile_ist_pro_Artikel_Lagerplatz_und_Charge_eindeutig()
    {
        using var factory = new Wp09Factory();
        await factory.CreateClient().AsReadyAdminAsync();
        await factory.WithDbAsync(db => DemoDataSeeder.SeedAsync(db));

        var (articleId, binId) = await factory.WithDbAsync(async db =>
            (await db.Articles.Select(a => a.Id).FirstAsync(), await db.StorageLocations.Select(b => b.Id).FirstAsync()));

        await factory.WithDbAsync(async db =>
        {
            db.StockItems.Add(new StockItem(articleId, binId, 4, "CHARGE-1"));
            await db.SaveChangesAsync();
        });

        // Zweite Zeile derselben Charge (der Race beim parallelen Wareneingang) scheitert am Unique-Index ...
        await Assert.ThrowsAsync<DbUpdateException>(() => factory.WithDbAsync(async db =>
        {
            db.StockItems.Add(new StockItem(articleId, binId, 9, "CHARGE-1"));
            await db.SaveChangesAsync();
        }));

        // ... eine andere Charge ist erlaubt.
        await factory.WithDbAsync(async db =>
        {
            db.StockItems.Add(new StockItem(articleId, binId, 9, "CHARGE-2"));
            await db.SaveChangesAsync();
        });
        Assert.Equal(2, Wp09Sql.Count(factory.DbPath, "SELECT COUNT(*) FROM StockItems WHERE LotNumber LIKE 'CHARGE-%'"));
    }

    [Fact]
    public async Task SKU_Auftragsnummer_und_Benutzername_sind_in_SQLite_case_insensitiv_eindeutig()
    {
        using var factory = new Wp09Factory();
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        var article = await admin.CreateArticleAsync("Case-Sku-1");

        // Nachschlagen mit '=' findet die Zeile unabhängig von der Schreibweise (Spalte mit COLLATE NOCASE).
        var found = await factory.WithDbAsync(db => db.Articles.FirstOrDefaultAsync(a => a.Sku == "CASE-SKU-1"));
        Assert.Equal(article.Id, found?.Id);

        // Ein zweiter Artikel, der sich nur in der Schreibweise unterscheidet, verletzt den Unique-Index.
        await Assert.ThrowsAsync<DbUpdateException>(() => factory.WithDbAsync(async db =>
        {
            db.Articles.Add(new Article("case-sku-1", "Doppelt", new Dimensions(1, 1, 1), 1, StackingInfo.NotStackable));
            await db.SaveChangesAsync();
        }));

        // Auch der Benutzername (der Bootstrap-Admin heißt 'admin').
        Assert.Equal(1, await factory.WithDbAsync(db => db.Users.CountAsync(u => u.Username == "ADMIN")));
    }

    // ---- Optimistische Sperre ------------------------------------------------------------------------------------

    [Fact]
    public async Task Parallele_Statuswechsel_am_Wareneingang_werden_erkannt_statt_doppelt_gebucht()
    {
        using var factory = new Wp09Factory();
        await factory.CreateClient().AsReadyAdminAsync();
        await factory.WithDbAsync(db => DemoDataSeeder.SeedAsync(db));
        var shipmentId = await factory.WithDbAsync(async db =>
        {
            var article = await db.Articles.FirstAsync();
            var bin = await db.StorageLocations.FirstAsync();
            var shipment = new InboundShipment("EIN-PAR-1", null, null);
            shipment.AddLine(article.Id, bin.Id, 5, null, null);
            db.InboundShipments.Add(shipment);
            await db.SaveChangesAsync();
            return shipment.Id;
        });

        // Zwei Requests lesen beide den Entwurf; der erste bucht, der zweite arbeitet auf dem alten Stand.
        using var scopeA = factory.Services.CreateScope();
        using var scopeB = factory.Services.CreateScope();
        var dbA = scopeA.ServiceProvider.GetRequiredService<LagerDbContext>();
        var dbB = scopeB.ServiceProvider.GetRequiredService<LagerDbContext>();
        var first = await dbA.InboundShipments.Include(s => s.Lines).SingleAsync(s => s.Id == shipmentId);
        var second = await dbB.InboundShipments.Include(s => s.Lines).SingleAsync(s => s.Id == shipmentId);

        first.MarkReceived();
        await dbA.SaveChangesAsync();

        second.MarkReceived();
        // Ohne Token am Beleg lief dieses UPDATE ohne Bedingung durch: die Lieferung wäre zweimal gebucht worden.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => dbB.SaveChangesAsync());
    }

    [Fact]
    public async Task Alle_Belege_mit_Statuswechsel_haben_ein_Concurrency_Token()
    {
        using var factory = new Wp09Factory();
        await factory.CreateClient().AsReadyAdminAsync();

        var withoutToken = await factory.WithDbAsync(db => Task.FromResult(
            new[]
            {
                typeof(StockItem), typeof(Lager.Domain.Orders.Order), typeof(Lager.Domain.PickLists.PickList),
                typeof(Lager.Domain.PickLists.PickWave), typeof(InboundShipment), typeof(Lager.Domain.Inventory.InventoryCount),
                typeof(Lager.Domain.Returns.ReturnShipment), typeof(Lager.Domain.Purchasing.PurchaseOrder),
                typeof(Lager.Domain.Shipping.Shipment), typeof(ReplenishmentTask),
            }
            .Where(t => db.Model.FindEntityType(t)!.FindProperty("ConcurrencyToken")!.IsConcurrencyToken == false)
            .Select(t => t.Name)
            .ToList()));

        Assert.Empty(withoutToken);
    }
}
