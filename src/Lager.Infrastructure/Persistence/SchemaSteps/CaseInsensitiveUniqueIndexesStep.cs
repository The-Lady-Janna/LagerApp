using Microsoft.Extensions.Logging;

namespace Lager.Infrastructure.Persistence.SchemaSteps;

/// <summary>
/// SKU, Auftragsnummer und Benutzername sind fachlich case-insensitiv eindeutig. MySQL (Standard-Collation *_ci)
/// verhält sich so; SQLite vergleicht Text standardmäßig binär, 'sku-1' und 'SKU-1' konnten dort nebeneinander
/// existieren, und eine Datenübernahme nach MySQL scheiterte am Unique-Index.
///
/// Das EF-Modell setzt deshalb bei SQLite die Collation NOCASE auf diese Spalten (LagerDbContext); neue Datenbanken
/// bekommen sie mit der Spalte. Dieser Schritt baut für Legacy-SQLite-Datenbanken die Unique-Indizes mit NOCASE neu auf.
///
/// Einschränkung (bewusst kein Tabellen-Rebuild): Die Spalte selbst behält in Legacy-Datenbanken ihre binäre
/// Collation. Eindeutigkeit ist dort case-insensitiv abgesichert (Index), Abfragen mit '=' vergleichen aber weiter
/// exakt. Die Services müssen SKU und Nummern deshalb vor dem Nachschlagen normalisieren (Trim, einheitliche Schreibweise).
///
/// Enthält eine Tabelle schon Werte, die sich nur in der Schreibweise unterscheiden, lässt sich der Index nicht
/// anlegen: Der Schritt lässt den bisherigen Index stehen, meldet die Werte im Log und schlägt (nicht kritisch)
/// fehl, damit er nach der Bereinigung beim nächsten Start erneut läuft. MySQL braucht nichts.
/// </summary>
public sealed class CaseInsensitiveUniqueIndexesStep : ISchemaUpgradeStep
{
    private static readonly (string Table, string Column, string Index)[] Targets =
    {
        ("Articles", "Sku", "IX_Articles_Sku"),
        ("Orders", "OrderNumber", "IX_Orders_OrderNumber"),
        ("Users", "Username", "IX_Users_Username"),
    };

    public int Order => 30;
    public string Name => "0030_CaseInsensitiveUniqueIndexes";
    public bool IsCritical => false;

    public async Task ApplyAsync(LagerDbContext db, ILogger logger)
    {
        if (!SchemaSql.IsSqlite(db))
        {
            logger.LogInformation("{Step}: MySQL vergleicht case-insensitiv, übersprungen.", Name);
            return;
        }

        var skipped = new List<string>();
        foreach (var (table, column, index) in Targets)
        {
            if (!await SchemaSql.ColumnExistsAsync(db, table, column)) continue; // Tabelle fehlt (wird aus dem Modell angelegt)

            var collation = await SchemaSql.ScalarAsync(db,
                "SELECT coll FROM pragma_index_xinfo(@i) WHERE name = @c AND \"key\" = 1",
                ("@i", index), ("@c", column));
            if (string.Equals(collation as string, "NOCASE", StringComparison.OrdinalIgnoreCase)) continue;

            var t = SchemaSql.Q(db, table);
            var c = SchemaSql.Q(db, column);
            var clashes = await SchemaSql.QueryAsync(db,
                $"SELECT lower({c}), COUNT(*) FROM {t} GROUP BY lower({c}) HAVING COUNT(*) > 1 LIMIT 5",
                r => SchemaSql.Str(r, 0) ?? string.Empty);
            if (clashes.Count > 0)
            {
                logger.LogWarning(
                    "{Step}: {Table}.{Column} enthält Werte, die sich nur in der Groß-/Kleinschreibung unterscheiden ({Examples}). " +
                    "Der case-insensitive Unique-Index wird erst nach der Bereinigung angelegt.",
                    Name, table, column, string.Join(", ", clashes));
                skipped.Add($"{table}.{column}");
                continue;
            }

            await SchemaSql.DropIndexAsync(db, table, index);
            await SchemaSql.ExecuteAsync(db,
                $"CREATE UNIQUE INDEX {SchemaSql.Q(db, index)} ON {t} ({c} COLLATE NOCASE);");
            logger.LogInformation("{Step}: Index {Index} auf {Table}.{Column} mit NOCASE neu aufgebaut.", Name, index, table, column);
        }

        if (skipped.Count > 0)
            throw new InvalidOperationException(
                $"Case-insensitive Unique-Indizes nicht angelegt wegen vorhandener Duplikate in: {string.Join(", ", skipped)}. Werte bereinigen, dann erneut starten.");
    }
}
