using System.Data.Common;
using System.Globalization;
using System.Text.RegularExpressions;
using Lager.Domain.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Lager.Infrastructure.Persistence.SchemaSteps;

/// <summary>
/// Gemeinsame Bausteine für den <see cref="SchemaUpgrader"/> und alle Schritte: provider-bewusste
/// Existenzprüfungen, ALTER/CREATE INDEX und Abfragen mit Parametern.
///
/// Sicherheitsregel: Werte gehören nie in den SQL-Text. Sie werden als Parameter übergeben. Bezeichner
/// (Tabellen, Spalten, Indizes) lassen sich nicht parametrisieren; sie werden deshalb gegen eine Whitelist
/// (<c>[A-Za-z_][A-Za-z0-9_]*</c>) geprüft und provider-spezifisch quotiert (<see cref="Q"/>).
/// Die Befehle laufen auf der Verbindung des DbContext (samt SQLite-Pragmas des Verbindungs-Interceptors) und in
/// dessen aktueller Transaktion.
/// </summary>
public static class SchemaSql
{
    private static readonly Regex IdentifierPattern = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsSqlite(LagerDbContext db) =>
        (db.Database.ProviderName ?? string.Empty).Contains("Sqlite", StringComparison.OrdinalIgnoreCase);

    public static bool IsMySql(LagerDbContext db)
    {
        var name = db.Database.ProviderName ?? string.Empty;
        return name.Contains("MySql", StringComparison.OrdinalIgnoreCase) || name.Contains("Pomelo", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Prüft einen Bezeichner gegen die Whitelist und gibt ihn unverändert zurück.</summary>
    public static string Ident(string name)
    {
        if (string.IsNullOrEmpty(name) || !IdentifierPattern.IsMatch(name))
            throw new ArgumentException($"Ungültiger SQL-Bezeichner: '{name}'.", nameof(name));
        return name;
    }

    /// <summary>Prüft und quotiert einen Bezeichner (SQLite: "Name", MySQL: `Name`).</summary>
    public static string Q(LagerDbContext db, string name)
    {
        Ident(name);
        return IsMySql(db) ? $"`{name}`" : $"\"{name}\"";
    }

    /// <summary>Tabellennamen aller Entity-Typen (Basisklasse <see cref="Entity"/>): die haben eine ConcurrencyToken-Spalte.</summary>
    public static IReadOnlyList<string> EntityTables(LagerDbContext db) =>
        db.Model.GetEntityTypes()
            .Where(t => typeof(Entity).IsAssignableFrom(t.ClrType) && !t.IsOwned())
            .Select(t => t.GetTableName())
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    // ---- Ausführen ------------------------------------------------------------------------------------------

    /// <summary>Führt eine Anweisung aus (auch mehrere, durch ';' getrennt, bei SQLite). Liefert die betroffenen Zeilen.</summary>
    public static async Task<int> ExecuteAsync(LagerDbContext db, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = await CreateCommandAsync(db, sql, parameters);
        return await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<object?> ScalarAsync(LagerDbContext db, string sql, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = await CreateCommandAsync(db, sql, parameters);
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    public static async Task<List<T>> QueryAsync<T>(LagerDbContext db, string sql, Func<DbDataReader, T> map, params (string Name, object? Value)[] parameters)
    {
        await using var cmd = await CreateCommandAsync(db, sql, parameters);
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<T>();
        while (await reader.ReadAsync())
            rows.Add(map(reader));
        return rows;
    }

    /// <summary>
    /// Text einer Spalte, unabhängig vom Provider: MySqlConnector liefert CHAR(36)-Spalten als Guid, SQLite als
    /// Text. NULL bleibt null.
    /// </summary>
    public static string? Str(DbDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Convert.ToString(reader.GetValue(ordinal), CultureInfo.InvariantCulture);

    /// <summary>Führt <paramref name="action"/> in einer Transaktion aus; läuft schon eine (Upgrader bei SQLite), wird sie genutzt.</summary>
    public static async Task InTransactionAsync(LagerDbContext db, Func<Task> action)
    {
        if (db.Database.CurrentTransaction is not null)
        {
            await action();
            return;
        }

        await using var tx = await db.Database.BeginTransactionAsync();
        await action();
        await tx.CommitAsync();
    }

    private static async Task<DbCommand> CreateCommandAsync(LagerDbContext db, string sql, (string Name, object? Value)[] parameters)
    {
        // Über EF öffnen (nicht direkt an der DbConnection): so läuft der SQLite-Pragma-Interceptor (busy_timeout, WAL).
        await db.Database.OpenConnectionAsync();
        var cmd = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText = sql;
        var tx = db.Database.CurrentTransaction?.GetDbTransaction();
        if (tx is not null) cmd.Transaction = tx;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value ?? DBNull.Value;
            cmd.Parameters.Add(p);
        }
        return cmd;
    }

    // ---- Existenzprüfungen ----------------------------------------------------------------------------------

    public static async Task<bool> TableExistsAsync(LagerDbContext db, string table)
    {
        Ident(table);
        var sql = IsMySql(db)
            ? "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t"
            : "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @t COLLATE NOCASE";
        return Convert.ToInt64(await ScalarAsync(db, sql, ("@t", table)) ?? 0L, CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>Spaltennamen der Tabelle (leer, wenn die Tabelle nicht existiert).</summary>
    public static async Task<HashSet<string>> GetColumnsAsync(LagerDbContext db, string table)
    {
        Ident(table);
        var sql = IsMySql(db)
            ? "SELECT COLUMN_NAME FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t"
            : "SELECT name FROM pragma_table_info(@t)";
        var names = await QueryAsync(db, sql, r => Str(r, 0)!, ("@t", table));
        return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
    }

    public static async Task<bool> ColumnExistsAsync(LagerDbContext db, string table, string column) =>
        (await GetColumnsAsync(db, table)).Contains(column);

    /// <summary>
    /// Ergänzt eine fehlende Spalte. <paramref name="typeDecl"/> ist die provider-spezifische Typdeklaration
    /// (z. B. "TEXT NULL" bzw. "CHAR(36) NULL") aus dem Quelltext des Schritts, nie ein Wert aus Daten.
    /// Existiert die Tabelle nicht, passiert nichts (sie legt EnsureCreated bzw. ein anderer Schritt an).
    /// </summary>
    public static async Task EnsureColumnAsync(LagerDbContext db, string table, string column, string typeDecl)
    {
        var columns = await GetColumnsAsync(db, table);
        if (columns.Count == 0 || columns.Contains(column)) return;
        await ExecuteAsync(db, $"ALTER TABLE {Q(db, table)} ADD COLUMN {Q(db, column)} {typeDecl};");
    }

    /// <summary>Wie <see cref="EnsureColumnAsync(LagerDbContext,string,string,string)"/> mit je einer Typdeklaration pro Provider.</summary>
    public static Task EnsureColumnAsync(LagerDbContext db, string table, string column, string sqliteTypeDecl, string mySqlTypeDecl) =>
        EnsureColumnAsync(db, table, column, IsMySql(db) ? mySqlTypeDecl : sqliteTypeDecl);

    /// <summary>
    /// null = der Index existiert nicht; sonst true bei einem Unique-Index. Funktioniert auf SQLite (pragma_index_list)
    /// und MySQL (INFORMATION_SCHEMA.STATISTICS).
    /// </summary>
    public static async Task<bool?> GetIndexUniqueAsync(LagerDbContext db, string table, string index)
    {
        Ident(table);
        Ident(index);
        var value = IsMySql(db)
            ? await ScalarAsync(db,
                "SELECT NON_UNIQUE FROM INFORMATION_SCHEMA.STATISTICS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = @t AND INDEX_NAME = @i LIMIT 1",
                ("@t", table), ("@i", index))
            : await ScalarAsync(db,
                "SELECT \"unique\" FROM pragma_index_list(@t) WHERE name = @i COLLATE NOCASE",
                ("@t", table), ("@i", index));
        if (value is null) return null;
        var flag = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        return IsMySql(db) ? flag == 0 : flag != 0;
    }

    public static async Task<bool> IndexExistsAsync(LagerDbContext db, string table, string index) =>
        await GetIndexUniqueAsync(db, table, index) is not null;

    /// <summary>
    /// Legt den Index an, wenn er fehlt. SQLite: CREATE INDEX IF NOT EXISTS. MySQL kennt IF NOT EXISTS bei
    /// CREATE INDEX nicht (nur MariaDB); dort wird vorher in INFORMATION_SCHEMA nachgesehen.
    /// </summary>
    public static async Task EnsureIndexAsync(LagerDbContext db, string table, string index, bool unique, params string[] columns)
    {
        if (columns.Length == 0) throw new ArgumentException("Mindestens eine Spalte nötig.", nameof(columns));
        if (!await TableExistsAsync(db, table)) return; // die Tabelle legt EnsureCreated bzw. ein anderer Schritt an
        if (IsMySql(db) && await IndexExistsAsync(db, table, index)) return;

        var kind = unique ? "UNIQUE INDEX" : "INDEX";
        var ifNotExists = IsMySql(db) ? string.Empty : " IF NOT EXISTS";
        var columnList = string.Join(", ", columns.Select(c => Q(db, c)));
        await ExecuteAsync(db, $"CREATE {kind}{ifNotExists} {Q(db, index)} ON {Q(db, table)} ({columnList});");
    }

    public static Task DropIndexAsync(LagerDbContext db, string table, string index) =>
        ExecuteAsync(db, IsMySql(db)
            ? $"DROP INDEX {Q(db, index)} ON {Q(db, table)};"
            : $"DROP INDEX IF EXISTS {Q(db, index)};");
}
