using Microsoft.Extensions.Logging;

namespace Lager.Infrastructure.Persistence.SchemaSteps;

/// <summary>
/// Legacy-SQLite-Datenbanken: Der frühere Backfill der ConcurrencyToken-Spalte schrieb die Guids klein
/// (<c>lower(hex(randomblob(..)))</c>). Microsoft.Data.Sqlite schreibt und bindet Guids aber als GROSS
/// geschriebenen Text, und die Prüfung <c>WHERE ConcurrencyToken = @original</c> vergleicht in SQLite
/// case-sensitiv (BINARY). Jede solche Zeile war deshalb nie mehr änderbar (dauerhaft
/// DbUpdateConcurrencyException / 409).
///
/// Dieser Schritt schreibt einmalig alle Tokens in Großschreibung. Idempotent: eine zweite Ausführung findet
/// nichts mehr. MySQL vergleicht CHAR(36) case-insensitiv, dort ist nichts zu tun.
/// </summary>
public sealed class NormalizeConcurrencyTokenCaseStep : ISchemaUpgradeStep
{
    public int Order => 10;
    public string Name => "0010_NormalizeConcurrencyTokenCase";

    public async Task ApplyAsync(LagerDbContext db, ILogger logger)
    {
        if (!SchemaSql.IsSqlite(db))
        {
            logger.LogInformation("{Step}: nur für SQLite nötig, übersprungen.", Name);
            return;
        }

        var total = 0;
        foreach (var table in SchemaSql.EntityTables(db))
        {
            if (!await SchemaSql.ColumnExistsAsync(db, table, "ConcurrencyToken")) continue;

            var t = SchemaSql.Q(db, table);
            var fixedRows = await SchemaSql.ExecuteAsync(db,
                $"UPDATE {t} SET \"ConcurrencyToken\" = upper(\"ConcurrencyToken\") " +
                "WHERE \"ConcurrencyToken\" IS NOT NULL AND \"ConcurrencyToken\" <> upper(\"ConcurrencyToken\");");
            if (fixedRows > 0)
            {
                logger.LogInformation("{Step}: {Table}: {Count} ConcurrencyToken-Werte auf Großschreibung gesetzt.", Name, table, fixedRows);
                total += fixedRows;
            }
        }

        logger.LogInformation("{Step}: {Total} Zeilen korrigiert.", Name, total);
    }
}
