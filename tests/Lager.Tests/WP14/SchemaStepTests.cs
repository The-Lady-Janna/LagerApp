using Lager.Domain.Inbound;
using Lager.Domain.Inventory;
using Lager.Infrastructure.Persistence;
using Lager.Infrastructure.Persistence.SchemaSteps;
using Lager.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lager.Tests.WP14;

/// <summary>
/// Der Schema-Schritt für die Detailtabellen (Bestellbezug, Einkaufspreis, Charge der Zählzeile): Legacy-Datenbanken
/// bekommen sie nachgerüstet (idempotent) und bleiben mit EF les- und beschreibbar; neue Datenbanken (aus dem Modell
/// angelegt) haben sie schon. Die Baseline-Tabellen behalten ihre Spalten.
/// </summary>
public class SchemaStepTests
{
    private static readonly string[] DetailTables = { "InboundShipmentLinks", "InboundLineLinks", "InventoryLineLots" };

    [Fact]
    public void The_step_is_found_by_the_catalog_with_a_unique_order_in_the_WP14_range()
    {
        var steps = SchemaStepCatalog.Discover();

        var step = Assert.Single(steps, s => s.Name == "1400_PurchaseOrderInboundLink");
        Assert.Equal(1400, step.Order);
        Assert.True(step.IsCritical);
        SchemaStepCatalog.Validate(steps);
    }

    [Fact]
    public async Task A_database_created_from_the_model_already_has_the_detail_tables_and_the_upgrade_runs_through()
    {
        using var factory = new LagerApiFactory();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LagerDbContext>();

        foreach (var table in DetailTables)
            Assert.True(await SchemaSql.TableExistsAsync(db, table), $"{table} fehlt im Modell");
        // die Baseline-Tabellen behalten ihre Spalten (der Drift-Test des SchemaUpgraders verlangt Deckung mit dem DDL)
        Assert.DoesNotContain("PurchaseOrderId", await SchemaSql.GetColumnsAsync(db, "InboundShipments"));
        Assert.DoesNotContain("UnitCostCents", await SchemaSql.GetColumnsAsync(db, "InboundLines"));
        Assert.DoesNotContain("LotNumber", await SchemaSql.GetColumnsAsync(db, "InventoryLines"));

        await SchemaUpgrader.UpgradeAsync(db);   // Baseline + alle Schritte (auch WP14) auf der frischen Datenbank: kein Fehler
        Assert.Contains("1400_PurchaseOrderInboundLink", SchemaUpgrader.GetKnownStepNames());
    }

    [Fact]
    public async Task A_legacy_database_gets_the_detail_tables_and_stays_usable_with_EF()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"lager-wp14-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var options = new DbContextOptionsBuilder<LagerDbContext>().UseSqlite($"Data Source={Path.Combine(dir, "legacy.db")};Pooling=False").Options;

            // Zustand einer Datenbank vor WP14: nur die Tabellen des Legacy-DDL (ohne Erweiterungen), mit Altdaten.
            Guid shipmentId = Guid.NewGuid(), lineId = Guid.NewGuid(), countId = Guid.NewGuid(), countLineId = Guid.NewGuid();
            await using (var db = new LagerDbContext(options))
            {
                await SchemaUpgrader.ApplyLegacyBaselineAsync(db, NullLogger.Instance);
                foreach (var table in DetailTables)
                    Assert.False(await SchemaSql.TableExistsAsync(db, table));

                await SchemaSql.ExecuteAsync(db,
                    "INSERT INTO InboundShipments (Id, ShipmentNumber, Status, CreatedAt, UpdatedAt, ConcurrencyToken) VALUES (@id, 'ALT-1', 0, @now, @now, @tok)",
                    ("@id", shipmentId.ToString().ToUpperInvariant()), ("@now", DateTime.UtcNow), ("@tok", Guid.NewGuid().ToString().ToUpperInvariant()));
                await SchemaSql.ExecuteAsync(db,
                    "INSERT INTO InboundLines (Id, InboundShipmentId, ArticleId, TargetBinId, Quantity, LotNumber, CreatedAt, UpdatedAt, ConcurrencyToken) " +
                    "VALUES (@id, @ship, @art, @bin, 7, 'LOT-ALT', @now, @now, @tok)",
                    ("@id", lineId.ToString().ToUpperInvariant()), ("@ship", shipmentId.ToString().ToUpperInvariant()),
                    ("@art", Guid.NewGuid().ToString().ToUpperInvariant()), ("@bin", Guid.NewGuid().ToString().ToUpperInvariant()),
                    ("@now", DateTime.UtcNow), ("@tok", Guid.NewGuid().ToString().ToUpperInvariant()));
                await SchemaSql.ExecuteAsync(db,
                    "INSERT INTO InventoryCounts (Id, Name, Status, CreatedAt, UpdatedAt, ConcurrencyToken) VALUES (@id, 'ALT', 0, @now, @now, @tok)",
                    ("@id", countId.ToString().ToUpperInvariant()), ("@now", DateTime.UtcNow), ("@tok", Guid.NewGuid().ToString().ToUpperInvariant()));
                await SchemaSql.ExecuteAsync(db,
                    "INSERT INTO InventoryLines (Id, InventoryCountId, BinId, BinCode, ArticleId, ArticleSku, ExpectedQty, CreatedAt, UpdatedAt, ConcurrencyToken) " +
                    "VALUES (@id, @count, @bin, 'A-01', @art, 'SKU-ALT', 5, @now, @now, @tok)",
                    ("@id", countLineId.ToString().ToUpperInvariant()), ("@count", countId.ToString().ToUpperInvariant()),
                    ("@bin", Guid.NewGuid().ToString().ToUpperInvariant()), ("@art", Guid.NewGuid().ToString().ToUpperInvariant()),
                    ("@now", DateTime.UtcNow), ("@tok", Guid.NewGuid().ToString().ToUpperInvariant()));
            }

            await using (var db = new LagerDbContext(options))
            {
                await SchemaUpgrader.UpgradeAsync(db);
                await SchemaUpgrader.UpgradeAsync(db);   // idempotent: ein zweiter Lauf ändert nichts

                foreach (var table in DetailTables)
                    Assert.True(await SchemaSql.TableExistsAsync(db, table), $"{table} fehlt");
                Assert.True(await SchemaSql.IndexExistsAsync(db, "InboundShipmentLinks", "IX_InboundShipmentLinks_PurchaseOrderId"));
                // Altzeilen brauchen keine Detailzeile
                Assert.Equal(0L, await SchemaSql.ScalarAsync(db, "SELECT COUNT(*) FROM InboundShipmentLinks"));
                Assert.Equal(0L, await SchemaSql.ScalarAsync(db, "SELECT COUNT(*) FROM InboundLineLinks"));
                Assert.Equal(0L, await SchemaSql.ScalarAsync(db, "SELECT COUNT(*) FROM InventoryLineLots"));
            }

            // Mit EF: Altdaten lesbar (samt Zeilen), an der Alt-Lieferung lassen sich Zeilen ergänzen, neue Lieferungen
            // tragen den Bestellbezug und den Preis, die Alt-Inventur lässt sich zählen.
            var purchaseOrder = Guid.NewGuid();
            var poLine = Guid.NewGuid();
            await using (var db = new LagerDbContext(options))
            {
                var legacy = await db.InboundShipments.Include(s => s.Lines).SingleAsync(s => s.Id == shipmentId);
                var oldLine = Assert.Single(legacy.Lines);
                Assert.Equal((7, "LOT-ALT", null, null), (oldLine.Quantity, oldLine.LotNumber, oldLine.PurchaseOrderLineId, oldLine.UnitCostCents));
                Assert.Null(legacy.PurchaseOrderId);

                legacy.AddLine(Guid.NewGuid(), Guid.NewGuid(), 3, "LOT-NEU", null);
                var linked = new InboundShipment("NEU-1", "PO-1", null, purchaseOrder);
                linked.AddLine(Guid.NewGuid(), Guid.NewGuid(), 5, null, null, poLine, 250);
                db.InboundShipments.Add(linked);

                var count = await db.InventoryCounts.Include(c => c.Lines).SingleAsync(c => c.Id == countId);
                Assert.Null(Assert.Single(count.Lines).LotNumber);
                count.SetCount(countLineId, 4, "gezählt");
                await db.SaveChangesAsync();
            }

            await using (var db = new LagerDbContext(options))
            {
                var legacy = await db.InboundShipments.Include(s => s.Lines).SingleAsync(s => s.Id == shipmentId);
                Assert.Equal(new[] { 3, 7 }, legacy.Lines.Select(l => l.Quantity).Order());

                var linked = await db.InboundShipments.Include(s => s.Lines).SingleAsync(s => s.ShipmentNumber == "NEU-1");
                Assert.Equal(purchaseOrder, linked.PurchaseOrderId);
                var line = Assert.Single(linked.Lines);
                Assert.Equal((poLine, 250), (line.PurchaseOrderLineId, line.UnitCostCents));
                // die Detailzeilen gibt es nur für die neue Lieferung; die Alt-Zeile hat keine
                Assert.Equal(1L, await SchemaSql.ScalarAsync(db, "SELECT COUNT(*) FROM InboundShipmentLinks"));
                Assert.Equal(1L, await SchemaSql.ScalarAsync(db, "SELECT COUNT(*) FROM InboundLineLinks"));
                Assert.Equal(linked.Id, (await db.InboundShipments.SingleAsync(s => s.PurchaseOrderLink!.PurchaseOrderId == purchaseOrder)).Id);

                var count = await db.InventoryCounts.Include(c => c.Lines).SingleAsync(c => c.Id == countId);
                Assert.Equal(4, Assert.Single(count.Lines).CountedQty);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* Temp-Verzeichnis */ }
        }
    }
}
