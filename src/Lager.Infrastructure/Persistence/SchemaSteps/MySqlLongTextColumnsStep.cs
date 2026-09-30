using Microsoft.Extensions.Logging;

namespace Lager.Infrastructure.Persistence.SchemaSteps;

/// <summary>
/// MySQL: Die JSON-/CSV-Spalten (Audit-Diff, Wand-Punkte, Wegpunkte, Wellen-Zuordnungen) waren als TEXT (64 KB)
/// angelegt; SQLite kennt diese Grenze nicht. Große Wellen oder Audit-Diffs konnten SaveChanges im Strict-Mode mit
/// "Data too long" scheitern lassen. Neue Tabellen legen die Spalten jetzt als LONGTEXT an, dieser Schritt stellt
/// bestehende MySQL-Datenbanken um (die Nullbarkeit bleibt erhalten).
///
/// Nicht kritisch und nur MySQL: Scheitert das ALTER, läuft die App weiter (mit der alten Grenze) und der Schritt
/// wird beim nächsten Start erneut versucht.
/// </summary>
public sealed class MySqlLongTextColumnsStep : ISchemaUpgradeStep
{
    private static readonly (string Table, string Column)[] Targets =
    {
        ("AuditEntries", "ChangesJson"),
        ("Walls", "PointsJson"),
        ("PickLists", "WaypointsJson"),
        ("PickWaves", "OrderIdsCsv"),
        ("PickWaves", "PickListIdsCsv"),
    };

    public int Order => 40;
    public string Name => "0040_MySqlLongTextColumns";
    public bool IsCritical => false;

    public async Task ApplyAsync(LagerDbContext db, ILogger logger)
    {
        if (!SchemaSql.IsMySql(db))
        {
            logger.LogInformation("{Step}: nur für MySQL nötig, übersprungen.", Name);
            return;
        }

        foreach (var (table, column) in Targets)
        {
            var info = await SchemaSql.QueryAsync(db,
                "SELECT DATA_TYPE, IS_NULLABLE FROM INFORMATION_SCHEMA.COLUMNS " +
                "WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t AND COLUMN_NAME = @c",
                r => (Type: SchemaSql.Str(r, 0), Nullable: SchemaSql.Str(r, 1)),
                ("@t", table), ("@c", column));
            if (info.Count == 0 || !string.Equals(info[0].Type, "text", StringComparison.OrdinalIgnoreCase)) continue;

            var nullability = string.Equals(info[0].Nullable, "NO", StringComparison.OrdinalIgnoreCase) ? "NOT NULL" : "NULL";
            await SchemaSql.ExecuteAsync(db,
                $"ALTER TABLE {SchemaSql.Q(db, table)} MODIFY COLUMN {SchemaSql.Q(db, column)} LONGTEXT {nullability};");
            logger.LogInformation("{Step}: {Table}.{Column} auf LONGTEXT umgestellt.", Name, table, column);
        }
    }
}
