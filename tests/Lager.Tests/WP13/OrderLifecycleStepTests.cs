using Lager.Api.Seeding;
using Lager.Domain.Orders;
using Lager.Domain.PickLists;
using Lager.Domain.Shipping;
using Lager.Infrastructure.Persistence;
using Lager.Infrastructure.Persistence.SchemaSteps;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lager.Tests.WP13;

/// <summary>
/// Der Schema-Schritt <c>1300_OrderLifecycle</c> gegen eine Bestandsdatenbank: neue Spalten und Index, und die Altdaten aus der Zeit
/// ohne Lebenszyklus (Bestellungen blieben nach dem Verpacken auf Picking stehen).
/// </summary>
public class OrderLifecycleStepTests
{
    private sealed class TempDb : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"lager-wp13-{Guid.NewGuid():N}");
        public string DbPath { get; }

        public TempDb()
        {
            System.IO.Directory.CreateDirectory(Directory);
            DbPath = Path.Combine(Directory, "legacy.db");
        }

        public LagerDbContext CreateContext() => new(new DbContextOptionsBuilder<LagerDbContext>()
            .UseSqlite($"Data Source={DbPath};Pooling=False")
            .Options);

        private SqliteConnection Open()
        {
            var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Pooling = false }.ToString());
            conn.Open();
            return conn;
        }

        public void Execute(string sql)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        public List<string> Strings(string sql)
        {
            using var conn = Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            using var reader = cmd.ExecuteReader();
            var values = new List<string>();
            while (reader.Read()) values.Add(Convert.ToString(reader.GetValue(0)) ?? string.Empty);
            return values;
        }

        public string Scalar(string sql) => Strings(sql).Single();

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            try { System.IO.Directory.Delete(Directory, recursive: true); }
            catch (IOException) { /* Temp-Verzeichnis */ }
            catch (UnauthorizedAccessException) { /* dito */ }
        }
    }

    private sealed record Legacy(Guid PackedByList, Guid StillPicking, Guid AllShipped, Guid OneStillOpen, Guid Untouched);

    /// <summary>
    /// Baut eine Datenbank im Zustand vor WP13: aktuelles Modell (EnsureCreated + Demo-Daten), Bestellungen auf Picking mit
    /// verpackten bzw. offenen Picklisten und Sendungen, dann ohne die neuen Spalten und ohne den Index.
    /// </summary>
    private static async Task<Legacy> BuildLegacyAsync(TempDb db)
    {
        Legacy legacy;
        await using (var ctx = db.CreateContext())
        {
            await ctx.Database.EnsureCreatedAsync();
            await DemoDataSeeder.SeedAsync(ctx);

            var orders = await ctx.Orders.Include(o => o.Lines).OrderBy(o => o.OrderNumber).Take(5).ToListAsync();
            var bin = (await ctx.StorageLocations.FirstAsync()).Id;

            PickList ListFor(Order order, string number, bool packed)
            {
                var line = order.Lines.First();
                var list = new PickList(number, new[] { new PickItem(1, order.Id, line.Id, line.ArticleId, bin, 1) }, 0);
                if (packed) list.MarkPacked();
                return list;
            }

            Shipment ShipmentFor(Order order, string number, bool shipped)
            {
                var shipment = new Shipment(number, order.Id, null, "MANUAL");
                shipment.SetDimensions(300, 200, 100, 1000);
                shipment.AssignTracking("T-" + number, null, 0);
                if (shipped) shipment.MarkShipped();
                return shipment;
            }

            var (packed, picking, allShipped, oneOpen, untouched) = (orders[0], orders[1], orders[2], orders[3], orders[4]);
            ctx.PickLists.AddRange(
                ListFor(packed, "PL-LEG-1", packed: true),
                ListFor(picking, "PL-LEG-2", packed: false),
                ListFor(allShipped, "PL-LEG-3", packed: true),
                ListFor(oneOpen, "PL-LEG-4", packed: true));
            ctx.Shipments.AddRange(
                ShipmentFor(allShipped, "SH-LEG-1", shipped: true),
                ShipmentFor(oneOpen, "SH-LEG-2", shipped: true),
                ShipmentFor(oneOpen, "SH-LEG-3", shipped: false));
            await ctx.SaveChangesAsync();

            // Vor dem Lebenszyklus blieb jede kommissionierte Bestellung auf Picking stehen.
            var stuck = new[] { packed.Id, picking.Id, allShipped.Id, oneOpen.Id };
            await ctx.Orders.Where(o => stuck.Contains(o.Id)).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Picking));
            legacy = new Legacy(packed.Id, picking.Id, allShipped.Id, oneOpen.Id, untouched.Id);
        }

        // Der Zustand einer Datenbank aus älteren Versionen: die Spalten und der Index von WP13 fehlen.
        db.Execute("DROP INDEX IF EXISTS IX_Orders_ExternalReference");
        foreach (var column in new[] { "Priority", "DueDate", "ExternalReference" })
            db.Execute($"ALTER TABLE Orders DROP COLUMN {column}");
        return legacy;
    }

    private static string StatusOf(TempDb db, Guid orderId) =>
        db.Scalar($"SELECT Status FROM Orders WHERE Id = '{orderId.ToString().ToUpperInvariant()}'");

    [Fact]
    public async Task A_legacy_database_gets_the_columns_the_index_and_promoted_orders()
    {
        using var db = new TempDb();
        var legacy = await BuildLegacyAsync(db);
        Assert.DoesNotContain("Priority", db.Strings("SELECT name FROM pragma_table_info('Orders')"));

        await using (var ctx = db.CreateContext())
            await SchemaUpgrader.UpgradeAsync(ctx);

        // Spalten und eindeutiger Index sind da, Priorität steht überall auf 0
        Assert.Superset(new HashSet<string> { "Priority", "DueDate", "ExternalReference" }, db.Strings("SELECT name FROM pragma_table_info('Orders')").ToHashSet());
        Assert.Equal("1", db.Scalar("SELECT \"unique\" FROM pragma_index_list('Orders') WHERE name = 'IX_Orders_ExternalReference'"));
        Assert.Equal("0", db.Scalar("SELECT MAX(Priority) FROM Orders"));

        // Altdaten: verpackte Liste -> Packed; alle Sendungen raus -> Shipped; noch offene Sendung -> bleibt Packed;
        // Liste noch offen bzw. Bestellung neu -> unverändert. Werte: New 0, Picking 1, Packed 3, Shipped 4.
        Assert.Equal("3", StatusOf(db, legacy.PackedByList));
        Assert.Equal("1", StatusOf(db, legacy.StillPicking));
        Assert.Equal("4", StatusOf(db, legacy.AllShipped));
        Assert.Equal("3", StatusOf(db, legacy.OneStillOpen));
        Assert.Equal("0", StatusOf(db, legacy.Untouched));

        // Das EF-Modell liest die Bestellungen jetzt wieder (es selektiert die neuen Spalten mit).
        await using (var ctx = db.CreateContext())
        {
            var reloaded = await ctx.Orders.AsNoTracking().SingleAsync(o => o.Id == legacy.AllShipped);
            Assert.Equal(OrderStatus.Shipped, reloaded.Status);
            Assert.Equal(0, reloaded.Priority);
            Assert.Null(reloaded.DueDate);
            Assert.Null(reloaded.ExternalReference);
        }
    }

    [Fact]
    public async Task The_external_reference_is_unique_but_many_orders_may_have_none()
    {
        using var db = new TempDb();
        var legacy = await BuildLegacyAsync(db);
        await using (var ctx = db.CreateContext())
            await SchemaUpgrader.UpgradeAsync(ctx);

        // viele Bestellungen ohne Referenz sind schon da (alle NULL) - eine Referenz darf es nur einmal geben
        db.Execute($"UPDATE Orders SET ExternalReference = 'SHOP-1' WHERE Id = '{legacy.Untouched.ToString().ToUpperInvariant()}'");
        var second = Assert.Throws<SqliteException>(() =>
            db.Execute($"UPDATE Orders SET ExternalReference = 'SHOP-1' WHERE Id = '{legacy.StillPicking.ToString().ToUpperInvariant()}'"));
        Assert.Equal(19, second.SqliteErrorCode);   // SQLITE_CONSTRAINT
    }

    [Fact]
    public async Task The_step_runs_once_and_a_second_upgrade_changes_nothing()
    {
        using var db = new TempDb();
        var legacy = await BuildLegacyAsync(db);
        await using (var ctx = db.CreateContext())
            await SchemaUpgrader.UpgradeAsync(ctx);

        // Nach dem Upgrade schreibt die App normal weiter: die verpackte Bestellung wird versendet, eine neue bleibt Picking.
        db.Execute($"UPDATE Orders SET Status = 1 WHERE Id = '{legacy.Untouched.ToString().ToUpperInvariant()}'");
        db.Execute($"UPDATE Orders SET Status = 3 WHERE Id = '{legacy.StillPicking.ToString().ToUpperInvariant()}'");
        await using (var ctx = db.CreateContext())
            await SchemaUpgrader.UpgradeAsync(ctx);

        Assert.Equal("1", db.Scalar("SELECT COUNT(*) FROM __LagerSchemaVersion WHERE Name = '1300_OrderLifecycle'"));
        Assert.Equal("1", StatusOf(db, legacy.Untouched));   // nicht erneut angefasst
        Assert.Equal("3", StatusOf(db, legacy.StillPicking));
        Assert.Contains("1300_OrderLifecycle", SchemaUpgrader.GetKnownStepNames());
    }

    [Fact]
    public async Task A_fresh_database_from_the_model_is_left_alone_by_the_step()
    {
        using var db = new TempDb();
        await using (var ctx = db.CreateContext())
        {
            await ctx.Database.EnsureCreatedAsync();
            await SchemaUpgrader.UpgradeAsync(ctx);
        }

        Assert.Equal("1", db.Scalar("SELECT \"unique\" FROM pragma_index_list('Orders') WHERE name = 'IX_Orders_ExternalReference'"));
        Assert.Single(db.Strings("SELECT name FROM pragma_table_info('Orders') WHERE name = 'Priority'"));
    }
}
