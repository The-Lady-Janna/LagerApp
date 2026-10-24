using Microsoft.Extensions.Logging;

namespace Lager.Infrastructure.Persistence.SchemaSteps;

/// <summary>
/// Raster-Layout (einfacher Layout-Editor): Tabelle <c>GridLayouts</c> mit einer Zeile je Lager. Neue Datenbanken bekommen sie
/// aus dem EF-Modell (GridLayoutConfiguration); dieser Schritt legt sie in Bestandsdatenbanken an.
///
///  - Spalten wie das Modell: Rastergröße, Behältermaße, <c>CellsJson</c>/<c>ManagedJson</c> (SQLite TEXT, MySQL LONGTEXT),
///    dazu Zeitstempel und ConcurrencyToken der Entity-Basis;
///  - eindeutiger Index <c>IX_GridLayouts_WarehouseId</c> (höchstens ein Raster je Lager);
///  - Fremdschlüssel auf <c>Warehouses</c> mit ON DELETE CASCADE.
///
/// Die Tabelle ist neu, es gibt nichts zu übernehmen. Idempotent (IF NOT EXISTS). Fehlt <c>Warehouses</c>, legt EnsureCreated
/// alles aus dem Modell an und der Schritt tut nichts. "Rows" ist in MySQL 8 ein reserviertes Wort, deshalb wird jeder Name
/// über <see cref="SchemaSql.Q"/> gequotet.
/// </summary>
public sealed class AddGridLayoutStep : ISchemaUpgradeStep
{
    public const string TableName = "GridLayouts";
    public const string IndexName = "IX_GridLayouts_WarehouseId";

    public int Order => 3500;
    public string Name => "3500_AddGridLayout";

    public async Task ApplyAsync(LagerDbContext db, ILogger logger)
    {
        if (!await SchemaSql.TableExistsAsync(db, "Warehouses")) return;
        var existed = await SchemaSql.TableExistsAsync(db, TableName);

        var mySql = SchemaSql.IsMySql(db);
        string Q(string name) => SchemaSql.Q(db, name);
        var guid = mySql ? "CHAR(36)" : "TEXT";
        var integer = mySql ? "INT" : "INTEGER";
        var json = mySql ? "LONGTEXT" : "TEXT";
        var timestamp = mySql ? "DATETIME(6)" : "TEXT";
        var engine = mySql ? " ENGINE=InnoDB" : string.Empty;

        var columns = string.Join(", ",
            $"{Q("Id")} {guid} NOT NULL PRIMARY KEY",
            $"{Q("WarehouseId")} {guid} NOT NULL",
            $"{Q("CellSizeMm")} {integer} NOT NULL",
            $"{Q("Columns")} {integer} NOT NULL",
            $"{Q("Rows")} {integer} NOT NULL",
            $"{Q("ContainerWidthMm")} {integer} NOT NULL",
            $"{Q("ContainerDepthMm")} {integer} NOT NULL",
            $"{Q("ContainerHeightMm")} {integer} NOT NULL",
            $"{Q("ContainerMaxWeightGrams")} {integer} NOT NULL",
            $"{Q("CellsJson")} {json} NOT NULL",
            $"{Q("ManagedJson")} {json} NOT NULL",
            $"{Q("CreatedAt")} {timestamp} NOT NULL",
            $"{Q("UpdatedAt")} {timestamp} NOT NULL",
            $"{Q("ConcurrencyToken")} {guid} NOT NULL");

        await SchemaSql.ExecuteAsync(db,
            $"CREATE TABLE IF NOT EXISTS {Q(TableName)} ({columns}, " +
            $"FOREIGN KEY ({Q("WarehouseId")}) REFERENCES {Q("Warehouses")} ({Q("Id")}) ON DELETE CASCADE){engine};");
        await SchemaSql.EnsureIndexAsync(db, TableName, IndexName, unique: true, "WarehouseId");

        if (!existed)
            logger.LogInformation("{Step}: Tabelle {Table} angelegt.", Name, TableName);
    }
}
