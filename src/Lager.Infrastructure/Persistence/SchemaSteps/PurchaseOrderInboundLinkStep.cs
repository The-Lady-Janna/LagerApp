using Microsoft.Extensions.Logging;

namespace Lager.Infrastructure.Persistence.SchemaSteps;

/// <summary>
/// WP14 (Wareneingang, Inventur, Retouren, Nachschub, Einkauf): legt für bestehende Datenbanken die drei Detailtabellen an,
/// in denen die neuen, nullbaren Angaben liegen. Neue Datenbanken bekommen sie mit den Tabellen aus dem Modell
/// (EnsureCreated), dort tut dieser Schritt nichts.
///
///  - InboundShipmentLinks (InboundShipmentId, PurchaseOrderId): Bestellbezug einer Lieferung, mit Index auf
///    PurchaseOrderId (Suche nach dem offenen Wareneingang einer Bestellung);
///  - InboundLineLinks (InboundLineId, PurchaseOrderLineId, UnitCostCents): Bestellzeile und Einkaufspreis einer
///    Wareneingangszeile;
///  - InventoryLineLots (InventoryLineId, LotNumber, ExpiryDate): Charge und MHD einer Inventur-Zählzeile
///    (lot-bewusste Inventur).
///
/// Warum eigene Tabellen statt neuer Spalten: InboundShipments, InboundLines und InventoryLines legt die Baseline des
/// SchemaUpgraders (WP09) mit festem DDL an, und der Drift-Test verlangt, dass dieses DDL genau dem EF-Modell entspricht.
/// Jede Detailtabelle hat eine Zeile nur für die Datensätze, die die Angaben brauchen; Altzeilen brauchen nichts
/// (kein Backfill). Der Fremdschlüssel auf die Haupttabelle löscht die Detailzeile mit.
///
/// Idempotent (IF NOT EXISTS). Existiert die Haupttabelle nicht, passiert nichts (sie legt EnsureCreated bzw. die
/// Baseline an).
/// </summary>
public sealed class PurchaseOrderInboundLinkStep : ISchemaUpgradeStep
{
    public int Order => 1400;
    public string Name => "1400_PurchaseOrderInboundLink";

    public async Task ApplyAsync(LagerDbContext db, ILogger logger)
    {
        var mySql = SchemaSql.IsMySql(db);

        await EnsureDetailTableAsync(db, logger, "InboundShipments", "InboundShipmentLinks", "InboundShipmentId",
            mySql ? "InboundShipmentId CHAR(36) NOT NULL PRIMARY KEY, PurchaseOrderId CHAR(36) NOT NULL"
                  : "InboundShipmentId TEXT NOT NULL PRIMARY KEY, PurchaseOrderId TEXT NOT NULL");
        await SchemaSql.EnsureIndexAsync(db, "InboundShipmentLinks", "IX_InboundShipmentLinks_PurchaseOrderId", unique: false, "PurchaseOrderId");

        await EnsureDetailTableAsync(db, logger, "InboundLines", "InboundLineLinks", "InboundLineId",
            mySql ? "InboundLineId CHAR(36) NOT NULL PRIMARY KEY, PurchaseOrderLineId CHAR(36) NULL, UnitCostCents INT NULL"
                  : "InboundLineId TEXT NOT NULL PRIMARY KEY, PurchaseOrderLineId TEXT NULL, UnitCostCents INTEGER NULL");

        await EnsureDetailTableAsync(db, logger, "InventoryLines", "InventoryLineLots", "InventoryLineId",
            mySql ? "InventoryLineId CHAR(36) NOT NULL PRIMARY KEY, LotNumber VARCHAR(64) NULL, ExpiryDate DATETIME(6) NULL"
                  : "InventoryLineId TEXT NOT NULL PRIMARY KEY, LotNumber TEXT NULL, ExpiryDate TEXT NULL");
    }

    private async Task EnsureDetailTableAsync(LagerDbContext db, ILogger logger, string mainTable, string detailTable, string keyColumn, string columns)
    {
        if (!await SchemaSql.TableExistsAsync(db, mainTable)) return;
        var existed = await SchemaSql.TableExistsAsync(db, detailTable);

        var engine = SchemaSql.IsMySql(db) ? " ENGINE=InnoDB" : string.Empty;
        await SchemaSql.ExecuteAsync(db,
            $"CREATE TABLE IF NOT EXISTS {SchemaSql.Q(db, detailTable)} ({columns}, " +
            $"FOREIGN KEY ({SchemaSql.Q(db, keyColumn)}) REFERENCES {SchemaSql.Q(db, mainTable)} ({SchemaSql.Q(db, "Id")}) ON DELETE CASCADE){engine};");

        if (!existed)
            logger.LogInformation("{Step}: Tabelle {Table} angelegt.", Name, detailTable);
    }
}
