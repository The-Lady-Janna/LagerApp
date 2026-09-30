using Lager.Infrastructure.Persistence.SchemaSteps;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lager.Infrastructure.Persistence;

/// <summary>
/// Der Datenbank-Start: EnsureCreated legt eine leere Datenbank aus dem EF-Modell an, danach bringt der
/// <see cref="SchemaUpgrader"/> jede Datenbank (frisch oder Bestand) auf den aktuellen Stand. Es gibt keinen zweiten
/// Weg (keine EF-Migrationen): Neue Schema-Änderungen kommen als Step-Datei in <c>SchemaSteps</c>.
/// </summary>
public static class DatabaseInitializer
{
    public static async Task InitializeAsync(LagerDbContext db, ILogger logger, CancellationToken ct = default)
    {
        var created = await db.Database.EnsureCreatedAsync(ct);
        logger.LogInformation(created
            ? "Neue Datenbank aus dem Modell angelegt."
            : "Bestehende Datenbank gefunden, das Schema wird geprüft.");

        await SchemaUpgrader.UpgradeAsync(db, logger, additionalSteps: null, ct);
    }
}

/// <summary>
/// Leert die Fachdaten der Datenbank (Reseed). Die Tabellenliste und die Löschreihenfolge werden aus dem EF-Modell
/// abgeleitet (Fremdschlüssel: Kinder vor Eltern), nicht von Hand gepflegt: Eine später ergänzte Tabelle wird automatisch
/// mitgelöscht und stört keinen Fremdschlüssel.
/// </summary>
public static class DatabaseReset
{
    /// <summary>Tabellen, die ein Reseed behält: Benutzer (man will sich danach noch anmelden) und der Audit-Trail.</summary>
    public static readonly IReadOnlyList<string> KeptTables = new[] { "Users", "AuditEntries" };

    /// <summary>
    /// Tabellennamen in einer Reihenfolge, in der jede Tabelle vor den Tabellen kommt, auf die sie per Fremdschlüssel
    /// verweist (Kinder vor Eltern). Ohne <paramref name="keepTables"/>.
    /// </summary>
    public static IReadOnlyList<string> GetDeleteOrder(LagerDbContext db, IEnumerable<string>? keepTables = null)
    {
        var keep = new HashSet<string>(keepTables ?? KeptTables, StringComparer.OrdinalIgnoreCase);
        var entityTypes = db.Model.GetEntityTypes()
            .Where(t => !t.IsOwned() && !string.IsNullOrEmpty(t.GetTableName()) && !keep.Contains(t.GetTableName()!))
            .ToList();

        var remaining = new HashSet<string>(entityTypes.Select(t => t.GetTableName()!), StringComparer.OrdinalIgnoreCase);
        // dependents[Eltern] = Tabellen, die per Fremdschlüssel auf die Eltern-Tabelle verweisen
        var dependents = remaining.ToDictionary(t => t, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        foreach (var type in entityTypes)
        foreach (var fk in type.GetForeignKeys())
        {
            var child = type.GetTableName()!;
            var parent = fk.PrincipalEntityType.GetTableName();
            if (parent is not null && !parent.Equals(child, StringComparison.OrdinalIgnoreCase) && dependents.TryGetValue(parent, out var set))
                set.Add(child);
        }

        var order = new List<string>();
        while (remaining.Count > 0)
        {
            // Bereit: keine noch nicht gelöschte Tabelle verweist auf sie.
            var ready = remaining.Where(t => !dependents[t].Any(remaining.Contains)).OrderBy(t => t, StringComparer.Ordinal).ToList();
            if (ready.Count == 0)
                ready = remaining.OrderBy(t => t, StringComparer.Ordinal).ToList(); // Zyklus: Rest in fester Reihenfolge (Fremdschlüssel werden aufgeschoben)
            order.AddRange(ready);
            foreach (var table in ready) remaining.Remove(table);
        }
        return order;
    }

    /// <summary>
    /// Löscht den Inhalt aller Fachtabellen. Aufrufer öffnen dafür eine Transaktion (scheitert etwas, bleibt alles wie
    /// vorher). Sind Fremdschlüssel-Zyklen im Modell, werden die Prüfungen bis zum Commit aufgeschoben (SQLite).
    /// </summary>
    public static async Task ClearBusinessDataAsync(LagerDbContext db, IEnumerable<string>? keepTables = null)
    {
        if (SchemaSql.IsSqlite(db))
            await SchemaSql.ExecuteAsync(db, "PRAGMA defer_foreign_keys = ON;");

        foreach (var table in GetDeleteOrder(db, keepTables))
        {
            if (!await SchemaSql.TableExistsAsync(db, table)) continue;
            await SchemaSql.ExecuteAsync(db, $"DELETE FROM {SchemaSql.Q(db, table)};");
        }
    }
}

/// <summary>
/// Dateioperationen an der SQLite-Datenbank für Backup und Restore (SQLite only; MySQL sichert man mit mysqldump).
/// </summary>
public static class SqliteDatabaseFile
{
    /// <summary>Tabellen, die eine Lager-Datenbank mindestens enthalten muss, damit ein Restore sie annimmt.</summary>
    public static readonly IReadOnlyList<string> RequiredTables = new[] { "Articles", "Users", "StockItems" };

    private static readonly byte[] Magic = "SQLite format 3\0"u8.ToArray();

    public sealed record ValidationResult(bool IsValid, string? Error)
    {
        public static ValidationResult Ok { get; } = new(true, null);
        public static ValidationResult Fail(string error) => new(false, error);
    }

    /// <summary>
    /// Konsistenter Snapshot der laufenden Datenbank per <c>VACUUM INTO</c>: liest in einer Lesetransaktion, ist unter
    /// paralleler Schreiblast konsistent (im Gegensatz zu einer Dateikopie) und enthält auch, was noch im WAL steht.
    /// Das Ziel darf nicht existieren; der Pfad geht als Parameter in die Anweisung.
    /// </summary>
    public static async Task BackupToAsync(LagerDbContext db, string targetPath, CancellationToken ct = default)
    {
        await db.Database.ExecuteSqlRawAsync("VACUUM INTO {0}", new object[] { targetPath }, ct);
    }

    /// <summary>
    /// Prüft eine hochgeladene Datei, bevor sie die Live-Datenbank ersetzen darf: SQLite-Header, <c>PRAGMA integrity_check</c>,
    /// Pflichttabellen und Schemastand (<c>__LagerSchemaVersion</c> nicht neuer als die App: nur Schritte, die
    /// <paramref name="knownSchemaSteps"/> kennt). Die Datei wird auf einer eigenen, nicht gepoolten Verbindung geöffnet.
    /// </summary>
    public static async Task<ValidationResult> ValidateAsync(string path, IReadOnlyCollection<string> knownSchemaSteps, CancellationToken ct = default)
    {
        try
        {
            var header = new byte[Magic.Length];
            await using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true))
            {
                var read = await fs.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
                if (read < header.Length || !header.AsSpan().SequenceEqual(Magic))
                    return ValidationResult.Fail("Die Datei ist keine SQLite-Datenbank (Header 'SQLite format 3' fehlt).");
            }

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString();

            await using var conn = new SqliteConnection(connectionString);
            await conn.OpenAsync(ct);

            var integrity = await ReadStringsAsync(conn, "PRAGMA integrity_check;", ct);
            if (integrity.Count != 1 || !string.Equals(integrity[0], "ok", StringComparison.OrdinalIgnoreCase))
                return ValidationResult.Fail("Die Integritätsprüfung der Datenbank (PRAGMA integrity_check) ist fehlgeschlagen.");

            var tables = new HashSet<string>(
                await ReadStringsAsync(conn, "SELECT name FROM sqlite_master WHERE type = 'table';", ct),
                StringComparer.OrdinalIgnoreCase);
            var missing = RequiredTables.Where(t => !tables.Contains(t)).ToList();
            if (missing.Count > 0)
                return ValidationResult.Fail($"Die Datenbank enthält nicht die erwarteten Tabellen (es fehlen: {string.Join(", ", missing)}).");

            if (tables.Contains(SchemaUpgrader.VersionTable))
            {
                var known = new HashSet<string>(knownSchemaSteps, StringComparer.OrdinalIgnoreCase);
                var unknown = (await ReadStringsAsync(conn, $"SELECT Name FROM {SchemaUpgrader.VersionTable};", ct))
                    .Where(n => !known.Contains(n))
                    .ToList();
                if (unknown.Count > 0)
                    return ValidationResult.Fail(
                        "Die Datenbank stammt von einer neueren Programmversion (unbekannte Schema-Schritte: " +
                        $"{string.Join(", ", unknown)}). Bitte zuerst die Anwendung aktualisieren.");
            }

            return ValidationResult.Ok;
        }
        catch (SqliteException ex)
        {
            return ValidationResult.Fail($"Die Datei ist keine gültige SQLite-Datenbank ({ex.Message}).");
        }
    }

    /// <summary>
    /// Ersetzt die Live-Datenbankdatei durch <paramref name="sourcePath"/> (liegt im selben Verzeichnis: der Austausch ist
    /// ein atomares Umbenennen). Vorher werden alle gepoolten Verbindungen geschlossen und die alten
    /// <c>-wal</c>/<c>-shm</c>/<c>-journal</c>-Dateien entfernt, damit ein veraltetes WAL nie auf die neue Datei trifft.
    /// Ist die Datei noch in Benutzung (Windows), gibt es einige Wiederholungsversuche; danach eine <see cref="IOException"/>.
    /// </summary>
    public static async Task ReplaceLiveFileAsync(string livePath, string sourcePath, CancellationToken ct = default)
    {
        const int attempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            SqliteConnection.ClearAllPools();
            try
            {
                foreach (var suffix in new[] { "-wal", "-shm", "-journal" })
                {
                    var sidecar = livePath + suffix;
                    if (File.Exists(sidecar)) File.Delete(sidecar);
                }
                File.Move(sourcePath, livePath, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException || ex is UnauthorizedAccessException) && attempt < attempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
            }
        }
    }

    private static async Task<List<string>> ReadStringsAsync(SqliteConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var values = new List<string>();
        while (await reader.ReadAsync(ct))
            values.Add(reader.IsDBNull(0) ? string.Empty : Convert.ToString(reader.GetValue(0)) ?? string.Empty);
        return values;
    }
}
