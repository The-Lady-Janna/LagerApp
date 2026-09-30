using Lager.Api.Seeding;
using Lager.Domain.Orders;
using Lager.Infrastructure.Persistence;
using Lager.Infrastructure.Persistence.SchemaSteps;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lager.Tests.WP09;

/// <summary>
/// Der Upgrade-Pfad für bestehende Datenbanken. Die Legacy-DB entsteht aus dem aktuellen Modell (EnsureCreated + Demo-Daten)
/// und wird per SQL in den Zustand gebracht, den ältere Versionen hinterließen: klein geschriebene ConcurrencyTokens,
/// doppelte Bestandszeilen und ein nicht eindeutiger Index auf StockItems. Danach läuft der SchemaUpgrader.
/// </summary>
public class LegacyUpgradeTests
{
    private const string StockIndex = DedupeStockItemsStep.IndexName;

    /// <summary>Baut die Legacy-DB und liefert die erwarteten Summen je Artikel (vor dem Upgrade).</summary>
    private static async Task<Dictionary<string, long>> BuildLegacyDbAsync(StandaloneDb db)
    {
        IReadOnlyList<string> entityTables;
        await using (var ctx = db.CreateContext())
        {
            await ctx.Database.EnsureCreatedAsync();
            await DemoDataSeeder.SeedAsync(ctx);
            entityTables = SchemaSql.EntityTables(ctx);
        }

        // Ältere Versionen: Index auf StockItems nicht eindeutig, Tokens klein geschrieben.
        Wp09Sql.Execute(db.DbPath, $"DROP INDEX {StockIndex}");
        Wp09Sql.Execute(db.DbPath, $"CREATE INDEX {StockIndex} ON StockItems (ArticleId, StorageLocationId, LotNumber)");
        foreach (var table in entityTables)
            Wp09Sql.Execute(db.DbPath, $"UPDATE {table} SET ConcurrencyToken = lower(ConcurrencyToken)");

        // Doppelte Bestandszeilen wie nach einem Race beim Wareneingang: gleiche (Artikel, Lagerplatz, Charge) mehrfach.
        var rows = Wp09Sql.Strings(db.DbPath, "SELECT Id FROM StockItems ORDER BY Id");
        var withoutLot = rows[0];
        var withLot = rows[1];
        Wp09Sql.Execute(db.DbPath, "UPDATE StockItems SET LotNumber = 'L1' WHERE Id = @id", ("@id", withLot));
        void Clone(string sourceId, int quantity, string? lot, bool overrideLot)
        {
            Wp09Sql.Execute(db.DbPath,
                "INSERT INTO StockItems (Id, ArticleId, StorageLocationId, Quantity, LotNumber, ExpiryDate, CreatedAt, UpdatedAt, ConcurrencyToken) " +
                "SELECT @newId, ArticleId, StorageLocationId, @q, CASE WHEN @override = 1 THEN @lot ELSE LotNumber END, ExpiryDate, CreatedAt, UpdatedAt, lower(ConcurrencyToken) " +
                "FROM StockItems WHERE Id = @src",
                ("@newId", Guid.NewGuid().ToString().ToUpperInvariant()), ("@q", quantity), ("@lot", lot),
                ("@override", overrideLot ? 1 : 0), ("@src", sourceId));
        }
        Clone(withoutLot, 7, null, false);   // gleiche Charge (keine) -> wird zusammengeführt
        Clone(withoutLot, 5, null, false);
        Clone(withLot, 11, null, false);     // gleiche Charge L1 -> wird zusammengeführt
        Clone(withoutLot, 100, "L2", true);  // andere Charge -> bleibt eine eigene Zeile

        // Movement-Ledger: zwei Buchungen, die der Upgrader nicht anfassen darf.
        foreach (var source in rows.Take(2))
            Wp09Sql.Execute(db.DbPath,
                "INSERT INTO StockMovements (Id, At, ArticleId, BinId, QuantityDelta, UnitCostCents, Reason, CreatedAt, UpdatedAt, ConcurrencyToken) " +
                "SELECT @id, CreatedAt, ArticleId, StorageLocationId, Quantity, 0, 1, CreatedAt, UpdatedAt, ConcurrencyToken FROM StockItems WHERE Id = @src",
                ("@id", Guid.NewGuid().ToString().ToUpperInvariant()), ("@src", source));

        var totals = new Dictionary<string, long>();
        using (var conn = Wp09Sql.Open(db.DbPath))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT ArticleId, SUM(Quantity) FROM StockItems GROUP BY ArticleId";
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) totals[reader.GetString(0)] = reader.GetInt64(1);
        }
        return totals;
    }

    [Fact]
    public async Task Legacy_DB_wird_hochgezogen_Duplikate_zusammengefuehrt_Summe_je_Artikel_bleibt()
    {
        using var db = new StandaloneDb();
        var totalsBefore = await BuildLegacyDbAsync(db);
        // Voraussetzung: zwei Gruppen mit doppelten Zeilen (ohne Charge und Charge L1).
        Assert.Equal(2, Wp09Sql.Count(db.DbPath,
            "SELECT COUNT(*) FROM (SELECT 1 FROM StockItems GROUP BY ArticleId, StorageLocationId, LotNumber HAVING COUNT(*) > 1)"));
        const string ledger = "SELECT COUNT(*) || ':' || SUM(QuantityDelta) || ':' || group_concat(Id) FROM (SELECT * FROM StockMovements ORDER BY Id)";
        var ledgerBefore = Wp09Sql.Scalar(db.DbPath, ledger);
        Assert.StartsWith("2:", (string)ledgerBefore!);

        var logger = new ListLogger();
        await using (var ctx = db.CreateContext())
            await SchemaUpgrader.UpgradeAsync(ctx, logger, additionalSteps: null);

        // Keine doppelte (Artikel, Lagerplatz, Charge) mehr; die Charge L2 blieb eine eigene Zeile.
        Assert.Equal(0, Wp09Sql.Count(db.DbPath,
            "SELECT COUNT(*) FROM (SELECT 1 FROM StockItems GROUP BY ArticleId, StorageLocationId, COALESCE(LotNumber, '') HAVING COUNT(*) > 1)"));
        Assert.Equal(1, Wp09Sql.Count(db.DbPath, "SELECT COUNT(*) FROM StockItems WHERE LotNumber = 'L2'"));

        // Summe je Artikel bleibt erhalten, der Movement-Ledger wurde nicht angefasst.
        using (var conn = Wp09Sql.Open(db.DbPath))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT ArticleId, SUM(Quantity) FROM StockItems GROUP BY ArticleId";
            using var reader = cmd.ExecuteReader();
            var totalsAfter = new Dictionary<string, long>();
            while (reader.Read()) totalsAfter[reader.GetString(0)] = reader.GetInt64(1);
            Assert.Equal(totalsBefore, totalsAfter);
        }
        Assert.Equal(ledgerBefore, Wp09Sql.Scalar(db.DbPath, ledger));

        // Der Unique-Index steht jetzt, und das Zusammenführen steht im Log.
        await using (var ctx = db.CreateContext())
            Assert.True(await SchemaSql.GetIndexUniqueAsync(ctx, "StockItems", StockIndex));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("zusammengeführt"));
    }

    [Fact]
    public async Task Legacy_Zeile_mit_klein_geschriebenem_Token_laesst_sich_nach_dem_Upgrade_aendern()
    {
        using var db = new StandaloneDb();
        await BuildLegacyDbAsync(db);

        // Voraussetzung (der Bug): Vor dem Upgrade scheitert jede Änderung an einer Legacy-Zeile mit einem Konflikt,
        // weil EF den Token als GROSS geschriebenen Text bindet, gespeichert ist er klein.
        await using (var before = db.CreateContext())
        {
            var order = await before.Orders.FirstAsync();
            order.Transition(OrderStatus.Picking);
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => before.SaveChangesAsync());
        }

        await using (var ctx = db.CreateContext())
            await SchemaUpgrader.UpgradeAsync(ctx);

        Assert.Equal(0, LowercaseTokenRows(db));

        await using (var after = db.CreateContext())
        {
            var order = await after.Orders.FirstAsync();
            order.Transition(OrderStatus.Picking);
            var stock = await after.StockItems.FirstAsync(s => s.Quantity > 0);
            stock.Remove(1);
            await after.SaveChangesAsync(); // kein DbUpdateConcurrencyException mehr
        }

        // Und ein zweiter Lauf am selben (jetzt geänderten) Datensatz: der Token ist rotiert, aber weiter gültig.
        await using (var again = db.CreateContext())
        {
            var order = await again.Orders.FirstAsync(o => o.Status == OrderStatus.Picking);
            order.ReleaseFromPicking(); // Picking -> New ist seit WP07 nur noch hierüber erlaubt
            await again.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task Upgrade_ist_idempotent_und_vermerkt_jeden_Schritt_einmal()
    {
        using var db = new StandaloneDb();
        await BuildLegacyDbAsync(db);

        await using (var ctx = db.CreateContext())
            await SchemaUpgrader.UpgradeAsync(ctx);

        var applied = Wp09Sql.Strings(db.DbPath, "SELECT Name FROM __LagerSchemaVersion ORDER BY StepOrder");
        Assert.Equal(SchemaUpgrader.GetKnownStepNames().OrderBy(n => n, StringComparer.Ordinal), applied.OrderBy(n => n, StringComparer.Ordinal));
        var stamps = Wp09Sql.Strings(db.DbPath, "SELECT AppliedAt FROM __LagerSchemaVersion ORDER BY Name");
        var stockRows = Wp09Sql.Count(db.DbPath, "SELECT COUNT(*) FROM StockItems");

        var logger = new ListLogger();
        await using (var ctx = db.CreateContext())
            await SchemaUpgrader.UpgradeAsync(ctx, logger, additionalSteps: null);

        Assert.Equal(stamps, Wp09Sql.Strings(db.DbPath, "SELECT AppliedAt FROM __LagerSchemaVersion ORDER BY Name"));
        Assert.Equal(stockRows, Wp09Sql.Count(db.DbPath, "SELECT COUNT(*) FROM StockItems"));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("wird angewendet"));
    }

    [Fact]
    public async Task Frische_DB_und_die_reine_Baseline_ohne_EnsureCreated_laufen_beide_durch()
    {
        // Frisch aus dem Modell: der Upgrader ändert nichts Nötiges, vermerkt aber alle Schritte.
        using (var fresh = new StandaloneDb())
        {
            await using (var ctx = fresh.CreateContext())
            {
                await ctx.Database.EnsureCreatedAsync();
                await SchemaUpgrader.UpgradeAsync(ctx);
                await SchemaUpgrader.UpgradeAsync(ctx);
                Assert.True(await SchemaSql.GetIndexUniqueAsync(ctx, "StockItems", StockIndex));
            }
            Assert.Equal(SchemaUpgrader.GetKnownStepNames().Count, Wp09Sql.Count(fresh.DbPath, "SELECT COUNT(*) FROM __LagerSchemaVersion"));
        }

        // Nur die Baseline auf einer leeren Datei: das Legacy-DDL (SQLite-Dialekt) muss durchlaufen und legt alle
        // Tabellen an, die nicht von EnsureCreated stammen. Fehlende Kerntabellen (Articles ...) werden übersprungen.
        using (var empty = new StandaloneDb())
        {
            await using (var ctx = empty.CreateContext())
            {
                await SchemaUpgrader.ApplyLegacyBaselineAsync(ctx, new ListLogger());
                await SchemaUpgrader.ApplyLegacyBaselineAsync(ctx, new ListLogger()); // idempotent
            }

            foreach (var table in new[] { "Walls", "PickPoints", "Suppliers", "PurchaseOrders", "ReturnShipments", "Customers", "Shipments",
                                          "StockMovements", "Users", "AuditEntries", "InboundShipments", "InventoryCounts", "PickWaves",
                                          "ReplenishmentTasks", "PickListSequence" })
                Assert.Equal(1, Wp09Sql.Count(empty.DbPath, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @t", ("@t", table)));
        }
    }

    [Fact]
    public async Task Legacy_Index_ohne_NOCASE_wird_neu_aufgebaut_und_Duplikate_werden_gemeldet_statt_verschluckt()
    {
        using var db = new StandaloneDb();
        await BuildLegacyDbAsync(db);

        // Legacy: Unique-Index mit binärer Collation, dadurch koexistieren 'sku-x' und 'SKU-X'.
        Wp09Sql.Execute(db.DbPath, "DROP INDEX IX_Articles_Sku");
        Wp09Sql.Execute(db.DbPath, "CREATE UNIQUE INDEX IX_Articles_Sku ON Articles (Sku COLLATE BINARY)");
        Wp09Sql.Execute(db.DbPath, "UPDATE Articles SET Sku = 'sku-x' WHERE Sku = 'SKU-001'");
        Wp09Sql.Execute(db.DbPath, "UPDATE Articles SET Sku = 'SKU-X' WHERE Sku = 'SKU-002'");

        var logger = new ListLogger();
        await using (var ctx = db.CreateContext())
            await SchemaUpgrader.UpgradeAsync(ctx, logger, additionalSteps: null); // wirft nicht: der Schritt ist nicht kritisch

        // Der Index bleibt (noch) binär, die Duplikate stehen im Log, der Schritt ist nicht als angewendet vermerkt.
        Assert.Equal("BINARY", Wp09Sql.Scalar(db.DbPath, "SELECT coll FROM pragma_index_xinfo('IX_Articles_Sku') WHERE \"key\" = 1"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Articles.Sku"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("0030_CaseInsensitiveUniqueIndexes"));
        Assert.Equal(0, Wp09Sql.Count(db.DbPath, "SELECT COUNT(*) FROM __LagerSchemaVersion WHERE Name = '0030_CaseInsensitiveUniqueIndexes'"));

        // Nach der Bereinigung baut der nächste Start den Index case-insensitiv auf.
        Wp09Sql.Execute(db.DbPath, "UPDATE Articles SET Sku = 'SKU-Y' WHERE Sku = 'SKU-X' COLLATE BINARY");
        await using (var ctx = db.CreateContext())
            await SchemaUpgrader.UpgradeAsync(ctx);

        Assert.Equal("NOCASE", Wp09Sql.Scalar(db.DbPath, "SELECT coll FROM pragma_index_xinfo('IX_Articles_Sku') WHERE \"key\" = 1"));
        Assert.Equal(1, Wp09Sql.Count(db.DbPath, "SELECT COUNT(*) FROM __LagerSchemaVersion WHERE Name = '0030_CaseInsensitiveUniqueIndexes'"));
        // Der Index erzwingt jetzt die Eindeutigkeit unabhängig von der Schreibweise.
        Assert.ThrowsAny<Exception>(() =>
            Wp09Sql.Execute(db.DbPath, "UPDATE Articles SET Sku = 'sku-y' WHERE Sku = 'sku-x'"));
    }

    [Fact]
    public async Task Vom_Upgrader_angelegte_Tabellen_haben_dieselben_Spalten_wie_das_Modell()
    {
        // Drift-Test: Das Legacy-DDL im SchemaUpgrader dupliziert das EF-Modell. Ändert sich das Modell (neue Spalte), ohne
        // dass das DDL nachzieht, bekommen Legacy-Datenbanken die Tabelle in einer anderen Form als neue Datenbanken.
        using var model = new StandaloneDb();
        using var upgraded = new StandaloneDb();
        await using (var ctx = model.CreateContext())
            await ctx.Database.EnsureCreatedAsync();
        await using (var ctx = upgraded.CreateContext())
            await SchemaUpgrader.ApplyLegacyBaselineAsync(ctx, new ListLogger()); // leere Datei: alles, was der Upgrader selbst anlegt

        var drift = new List<string>();
        foreach (var table in Wp09Sql.Strings(upgraded.DbPath,
                     "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> '__LagerSchemaVersion'"))
        {
            var expected = Wp09Sql.Strings(model.DbPath, $"SELECT name FROM pragma_table_info('{table}')").ToHashSet();
            var actual = Wp09Sql.Strings(upgraded.DbPath, $"SELECT name FROM pragma_table_info('{table}')").ToHashSet();
            Assert.NotEmpty(expected); // jede Upgrader-Tabelle gehört zum Modell
            var missing = expected.Except(actual).ToList();
            var extra = actual.Except(expected).ToList();
            if (missing.Count > 0 || extra.Count > 0)
                drift.Add($"{table}: fehlt [{string.Join(", ", missing)}], zu viel [{string.Join(", ", extra)}]");
        }

        Assert.Empty(drift);
    }

    private static long LowercaseTokenRows(StandaloneDb db)
    {
        IReadOnlyList<string> tables;
        using (var ctx = db.CreateContext()) tables = SchemaSql.EntityTables(ctx);
        return tables.Sum(t => Wp09Sql.Count(db.DbPath,
            $"SELECT COUNT(*) FROM {t} WHERE ConcurrencyToken IS NOT NULL AND ConcurrencyToken <> upper(ConcurrencyToken)"));
    }
}
