using System.Net.Http.Json;
using Lager.Contracts.Articles;
using Lager.Contracts.Orders;
using Lager.Contracts.PickLists;
using Lager.Contracts.Stock;
using Lager.Contracts.Warehouse;
using Lager.Domain.Stock;
using Lager.Infrastructure.Persistence;
using Lager.Tests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Lager.Tests.WP21;

/// <summary>
/// Start der kompletten App auf einer Datenbank in der Struktur einer alten Programmversion. Die Datei entsteht per Hand-SQL
/// (keine EF-Tabellen aus dem heutigen Modell): Artikel ohne Bestandsgrenzen, Lagerplätze ohne Typ, Bestellungen ohne Priorität
/// und Fälligkeit, Picklisten ohne Route, Wände mit Start-/End-Spalten, keine Benutzer-, Ledger- und Nummernkreis-Tabellen, keine
/// ConcurrencyToken-Spalten. Erwartet: die App startet, der SchemaUpgrader zieht die Struktur nach, die Altdaten sind über die API
/// lesbar und änderbar, und der Kern-Ablauf (Bestellung -> Pickliste -> Packen) läuft auf den Altdaten. Ein zweiter Start ändert nichts mehr.
/// </summary>
public class LegacyDatabaseStartTests
{
    private const string Warehouse = "A0000000-0000-4000-8000-000000000001";
    private const string Zone = "A0000000-0000-4000-8000-000000000002";
    private const string Aisle = "A0000000-0000-4000-8000-000000000003";
    private const string Shelf = "A0000000-0000-4000-8000-000000000004";
    private const string BinOne = "A0000000-0000-4000-8000-000000000011";
    private const string BinTwo = "A0000000-0000-4000-8000-000000000012";
    private const string ArticleOne = "A0000000-0000-4000-8000-000000000021";
    private const string ArticleTwo = "A0000000-0000-4000-8000-000000000022";
    private const string OrderNew = "A0000000-0000-4000-8000-000000000031";
    private const string OrderNewLine = "A0000000-0000-4000-8000-000000000032";
    private const string OrderStuck = "A0000000-0000-4000-8000-000000000033";
    private const string OrderStuckLine = "A0000000-0000-4000-8000-000000000034";
    private const string PickList = "A0000000-0000-4000-8000-000000000041";
    private const string PickItem = "A0000000-0000-4000-8000-000000000042";
    private const string Wall = "A0000000-0000-4000-8000-000000000051";

    private static readonly string[] LegacyStatements =
    {
        "CREATE TABLE Warehouses (Id TEXT NOT NULL PRIMARY KEY, Code TEXT NOT NULL, Name TEXT NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL)",
        "CREATE TABLE Zones (Id TEXT NOT NULL PRIMARY KEY, WarehouseId TEXT NOT NULL, Code TEXT NOT NULL, Name TEXT NOT NULL, OriginXMm INTEGER NOT NULL, OriginYMm INTEGER NOT NULL, OriginZMm INTEGER NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL)",
        "CREATE TABLE Aisles (Id TEXT NOT NULL PRIMARY KEY, ZoneId TEXT NOT NULL, Code TEXT NOT NULL, StartXMm INTEGER NOT NULL, StartYMm INTEGER NOT NULL, StartZMm INTEGER NOT NULL, EndXMm INTEGER NOT NULL, EndYMm INTEGER NOT NULL, EndZMm INTEGER NOT NULL, Orientation INTEGER NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL)",
        "CREATE TABLE Shelves (Id TEXT NOT NULL PRIMARY KEY, AisleId TEXT NOT NULL, Code TEXT NOT NULL, PosXMm INTEGER NOT NULL, PosYMm INTEGER NOT NULL, PosZMm INTEGER NOT NULL, WidthMm INTEGER NOT NULL, DepthMm INTEGER NOT NULL, HeightMm INTEGER NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL)",
        "CREATE TABLE StorageLocations (Id TEXT NOT NULL PRIMARY KEY, ShelfId TEXT NOT NULL, Code TEXT NOT NULL, PosXMm INTEGER NOT NULL, PosYMm INTEGER NOT NULL, PosZMm INTEGER NOT NULL, WidthMm INTEGER NOT NULL, DepthMm INTEGER NOT NULL, HeightMm INTEGER NOT NULL, MaxWeightGrams INTEGER NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL)",
        "CREATE TABLE Articles (Id TEXT NOT NULL PRIMARY KEY, Sku TEXT NOT NULL, Name TEXT NOT NULL, Description TEXT NULL, LengthMm INTEGER NOT NULL, WidthMm INTEGER NOT NULL, HeightMm INTEGER NOT NULL, WeightGrams INTEGER NOT NULL, IsStackable INTEGER NOT NULL, StackingAxis INTEGER NOT NULL, StackingIncrementMm INTEGER NOT NULL, MaxStackCount INTEGER NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL)",
        "CREATE TABLE StockItems (Id TEXT NOT NULL PRIMARY KEY, ArticleId TEXT NOT NULL, StorageLocationId TEXT NOT NULL, Quantity INTEGER NOT NULL, LotNumber TEXT NULL, ExpiryDate TEXT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL)",
        "CREATE TABLE Orders (Id TEXT NOT NULL PRIMARY KEY, OrderNumber TEXT NOT NULL, CustomerReference TEXT NULL, Status INTEGER NOT NULL, Source INTEGER NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL)",
        "CREATE TABLE OrderLines (Id TEXT NOT NULL PRIMARY KEY, OrderId TEXT NOT NULL, ArticleId TEXT NOT NULL, Quantity INTEGER NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL)",
        "CREATE TABLE PickLists (Id TEXT NOT NULL PRIMARY KEY, PickListNumber TEXT NOT NULL, Status INTEGER NOT NULL, AssignedTo TEXT NULL, TotalDistanceMm INTEGER NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL)",
        "CREATE TABLE PickItems (Id TEXT NOT NULL PRIMARY KEY, PickListId TEXT NOT NULL, SequenceNumber INTEGER NOT NULL, OrderId TEXT NOT NULL, OrderLineId TEXT NOT NULL, ArticleId TEXT NOT NULL, StorageLocationId TEXT NOT NULL, Quantity INTEGER NOT NULL, Picked INTEGER NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL)",
        "CREATE TABLE Walls (Id TEXT NOT NULL PRIMARY KEY, WarehouseId TEXT NOT NULL, Label TEXT NULL, ThicknessMm INTEGER NOT NULL, StartXMm INTEGER NOT NULL, StartYMm INTEGER NOT NULL, StartZMm INTEGER NOT NULL, EndXMm INTEGER NOT NULL, EndYMm INTEGER NOT NULL, EndZMm INTEGER NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL)",

        $"INSERT INTO Warehouses VALUES ('{Warehouse}', 'WH-ALT', 'Altes Lager', '2020-01-01 08:00:00', '2020-01-01 08:00:00')",
        $"INSERT INTO Zones VALUES ('{Zone}', '{Warehouse}', 'Z1', 'Zone 1', 0, 0, 0, '2020-01-01 08:00:00', '2020-01-01 08:00:00')",
        $"INSERT INTO Aisles VALUES ('{Aisle}', '{Zone}', 'A1', 0, 0, 0, 10000, 0, 0, 0, '2020-01-01 08:00:00', '2020-01-01 08:00:00')",
        $"INSERT INTO Shelves VALUES ('{Shelf}', '{Aisle}', 'S1', 0, 200, 0, 8000, 600, 2000, '2020-01-01 08:00:00', '2020-01-01 08:00:00')",
        $"INSERT INTO StorageLocations VALUES ('{BinOne}', '{Shelf}', 'ALT-BIN-1', 1000, 200, 500, 600, 600, 500, 50000, '2020-01-01 08:00:00', '2020-01-01 08:00:00')",
        $"INSERT INTO StorageLocations VALUES ('{BinTwo}', '{Shelf}', 'ALT-BIN-2', 2500, 200, 500, 600, 600, 500, 50000, '2020-01-01 08:00:00', '2020-01-01 08:00:00')",
        $"INSERT INTO Articles VALUES ('{ArticleOne}', 'SKU-ALT-1', 'Alter Artikel 1', 'aus der Altdatenbank', 120, 80, 60, 450, 0, 0, 0, NULL, '2020-01-02 08:00:00', '2020-01-02 08:00:00')",
        $"INSERT INTO Articles VALUES ('{ArticleTwo}', 'SKU-ALT-2', 'Alter Artikel 2', NULL, 200, 100, 50, 900, 1, 2, 30, 4, '2020-01-02 08:00:00', '2020-01-02 08:00:00')",
        $"INSERT INTO StockItems VALUES ('A0000000-0000-4000-8000-000000000061', '{ArticleOne}', '{BinOne}', 40, 'ALT-1', '2027-06-30 00:00:00', '2020-01-03 08:00:00', '2020-01-03 08:00:00')",
        $"INSERT INTO StockItems VALUES ('A0000000-0000-4000-8000-000000000062', '{ArticleTwo}', '{BinTwo}', 15, NULL, NULL, '2020-01-03 08:00:00', '2020-01-03 08:00:00')",
        // eine neue Bestellung und eine, die auf Picking hängen geblieben ist, obwohl ihre Pickliste längst verpackt ist
        $"INSERT INTO Orders VALUES ('{OrderNew}', 'OLD-NEW-1', 'Altkunde', 0, 0, '2020-02-01 09:00:00', '2020-02-01 09:00:00')",
        $"INSERT INTO OrderLines VALUES ('{OrderNewLine}', '{OrderNew}', '{ArticleOne}', 5, '2020-02-01 09:00:00', '2020-02-01 09:00:00')",
        $"INSERT INTO Orders VALUES ('{OrderStuck}', 'OLD-STUCK-1', 'Altkunde', 1, 0, '2020-02-02 09:00:00', '2020-02-02 09:00:00')",
        $"INSERT INTO OrderLines VALUES ('{OrderStuckLine}', '{OrderStuck}', '{ArticleTwo}', 3, '2020-02-02 09:00:00', '2020-02-02 09:00:00')",
        $"INSERT INTO PickLists VALUES ('{PickList}', 'PL-20200202-00001', 2, 'lagerist', 1200, '2020-02-02 10:00:00', '2020-02-02 11:00:00')",
        $"INSERT INTO PickItems VALUES ('{PickItem}', '{PickList}', 1, '{OrderStuck}', '{OrderStuckLine}', '{ArticleTwo}', '{BinTwo}', 3, 1, '2020-02-02 10:00:00', '2020-02-02 11:00:00')",
        $"INSERT INTO Walls VALUES ('{Wall}', '{Warehouse}', 'Altwand', 100, 1000, 0, 0, 1000, 5000, 0, '2020-01-01 08:00:00', '2020-01-01 08:00:00')",
    };

    private static void Execute(string path, params string[] statements)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        connection.Open();
        foreach (var sql in statements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }

    private static List<string> Strings(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false, Mode = SqliteOpenMode.ReadWrite }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(Convert.ToString(reader.GetValue(0)) ?? string.Empty);
        return values;
    }

    [Fact]
    public async Task The_app_starts_on_a_legacy_database_upgrades_it_and_serves_and_changes_the_old_data()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lager-wp21-legacy-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var dbPath = Path.Combine(directory, "lager.db");
            Execute(dbPath, LegacyStatements);

            // Voraussetzung: das ist wirklich die alte Struktur
            Assert.DoesNotContain("MinStock", Strings(dbPath, "SELECT name FROM pragma_table_info('Articles')"));
            Assert.DoesNotContain("BinType", Strings(dbPath, "SELECT name FROM pragma_table_info('StorageLocations')"));
            Assert.DoesNotContain("Users", Strings(dbPath, "SELECT name FROM sqlite_master WHERE type = 'table'"));
            Assert.Contains("StartXMm", Strings(dbPath, "SELECT name FROM pragma_table_info('Walls')"));

            List<string> appliedSteps;
            using (var factory = new RestartableFactory(directory))
            {
                var w = new WorldBuilder(factory);
                var admin = await w.AdminAsync();   // startet den Host: EnsureCreated (nichts zu tun), SchemaUpgrader, Bootstrap-Admin

                // Die Struktur ist nachgezogen: neue Spalten und Tabellen, jeder Schritt vermerkt
                Assert.Contains("MinStock", Strings(dbPath, "SELECT name FROM pragma_table_info('Articles')"));
                Assert.Contains("BinType", Strings(dbPath, "SELECT name FROM pragma_table_info('StorageLocations')"));
                Assert.Contains("ConcurrencyToken", Strings(dbPath, "SELECT name FROM pragma_table_info('Orders')"));
                Assert.Contains("PointsJson", Strings(dbPath, "SELECT name FROM pragma_table_info('Walls')"));
                Assert.DoesNotContain("StartXMm", Strings(dbPath, "SELECT name FROM pragma_table_info('Walls')"));
                foreach (var table in new[] { "Users", "StockMovements", "PickListSequence", "Shipments", "Customers", "AuditEntries" })
                    Assert.Contains(table, Strings(dbPath, "SELECT name FROM sqlite_master WHERE type = 'table'"));
                appliedSteps = Strings(dbPath, "SELECT Name || '@' || AppliedAt FROM __LagerSchemaVersion ORDER BY Name");
                Assert.Equal(SchemaUpgrader.GetKnownStepNames().Order(StringComparer.Ordinal),
                    Strings(dbPath, "SELECT Name FROM __LagerSchemaVersion ORDER BY Name").Order(StringComparer.Ordinal));

                // Die Altdaten sind über die API lesbar - mit Standardwerten für die neuen Felder
                var articles = await (await admin.GetAsync("/api/articles")).ExpectAsync<List<ArticleDto>>();
                var one = Assert.Single(articles, a => a.Sku == "SKU-ALT-1");
                Assert.Equal((120, 80, 60, 450), (one.Dimensions.LengthMm, one.Dimensions.WidthMm, one.Dimensions.HeightMm, one.WeightGrams));
                Assert.Equal((0, 0, 0), (one.MinStock, one.ReorderPoint, one.MaxStock));
                Assert.Equal(2, articles.Count);

                var stock = await (await admin.GetAsync("/api/stock")).ExpectAsync<List<StockItemDto>>();
                var lotRow = Assert.Single(stock, s => s.ArticleSku == "SKU-ALT-1");
                Assert.Equal((40, "ALT-1", new DateTime(2027, 6, 30, 0, 0, 0, DateTimeKind.Utc)), (lotRow.Quantity, lotRow.LotNumber, lotRow.ExpiryDate));
                Assert.Equal(15, Assert.Single(stock, s => s.ArticleSku == "SKU-ALT-2").Quantity);

                var orders = await (await admin.GetAsync("/api/orders")).ExpectAsync<List<OrderDto>>();
                var fresh = Assert.Single(orders, o => o.OrderNumber == "OLD-NEW-1");
                Assert.Equal(("New", 0, 5), (fresh.Status, fresh.Priority, fresh.Lines.Single().Quantity));
                Assert.Equal("Packed", Assert.Single(orders, o => o.OrderNumber == "OLD-STUCK-1").Status);   // Altdaten-Korrektur: verpackte Liste, Status hing auf Picking

                var lists = await (await admin.GetAsync("/api/picklists")).ExpectAsync<List<PickListDto>>();
                var oldList = Assert.Single(lists);
                Assert.Equal(("PL-20200202-00001", "Completed", "lagerist"), (oldList.PickListNumber, oldList.Status, oldList.AssignedTo));

                var walls = await (await admin.GetAsync("/api/warehouse/walls")).ExpectAsync<List<WallDto>>();
                var wall = Assert.Single(walls);
                Assert.Equal(new[] { (1000, 0), (1000, 5000) }, wall.Points.Select(p => (p.XMm, p.YMm)));
                var layout = await (await admin.GetAsync("/api/warehouse/layout")).ExpectAsync<List<WarehouseDto>>();
                Assert.Equal("WH-ALT", Assert.Single(layout).Code);

                // Änderbar: die Zeilen haben einen gültigen Concurrency-Token bekommen, kein 409 beim ersten Schreiben
                var bin = Assert.Single(stock, s => s.ArticleSku == "SKU-ALT-1").StorageLocationId;
                await (await admin.PostAsJsonAsync("/api/stock/adjust", new AdjustStockRequest(one.Id, bin, 2, "ALT-1", null))).ExpectAsync<StockItemDto>();

                // Der Ablauf läuft auf den Altdaten: Pickliste, Packen (FEFO mit Charge und MHD), Bestellung Packed
                var list = await admin.GenerateAsync(fresh.Id);
                Assert.Equal(5, list.Items.Sum(i => i.Quantity));
                await admin.PackAsync(list);
                Assert.Equal("Packed", (await admin.GetOrderAsync(fresh.Id)).Status);
                Assert.Equal(37, await admin.TotalStockAsync(one.Id));   // 40 + 2 - 5
                var movements = await w.MovementsAsync(one.Id);
                var pick = Assert.Single(movements, m => m.Reason == StockMovementReason.Pick);
                Assert.Equal((-5, "ALT-1", new DateTime(2027, 6, 30, 0, 0, 0, DateTimeKind.Utc)), (pick.QuantityDelta, pick.LotNumber, pick.ExpiryDate));
                Assert.Equal(new[] { -5, 2 }, movements.Select(m => m.QuantityDelta).Order());
            }

            // Zweiter Start auf derselben Datei: nichts wird erneut angewendet, alles ist noch da
            using (var again = new RestartableFactory(directory))
            {
                var w = new WorldBuilder(again);
                var admin = await w.AdminAsync();

                Assert.Equal(appliedSteps, Strings(dbPath, "SELECT Name || '@' || AppliedAt FROM __LagerSchemaVersion ORDER BY Name"));
                Assert.Equal(2, (await (await admin.GetAsync("/api/articles")).ExpectAsync<List<ArticleDto>>()).Count);
                Assert.Equal(2, (await (await admin.GetAsync("/api/orders")).ExpectAsync<List<OrderDto>>()).Count(o => o.Status == "Packed"));
                Assert.Equal(37, await admin.TotalStockAsync(Guid.Parse(ArticleOne)));
                Assert.Equal(new[] { "ok" }, Strings(dbPath, "PRAGMA integrity_check;"));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* Temp-Verzeichnis, wird beim nächsten Cleanup entfernt */ }
            catch (UnauthorizedAccessException) { /* dito */ }
        }
    }
}
