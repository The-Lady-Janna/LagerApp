using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Articles;
using Lager.Contracts.Orders;
using Lager.Infrastructure.Persistence;
using Lager.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Lager.Tests.WP09;

/// <summary>
/// Startet die API gegen eine frische SQLite-Datei in einem EIGENEN Temp-Verzeichnis (Backups und Sicherheitskopien
/// landen daneben und werden mit aufgeräumt). Environment und Einstellungen sind wählbar; Admin-Zugangsdaten und
/// Signing-Key wie in <see cref="LagerApiFactory"/>, damit <c>AsReadyAdminAsync()</c> funktioniert.
/// </summary>
public sealed class Wp09Factory : WebApplicationFactory<Program>
{
    private readonly string _environment;
    private readonly Dictionary<string, string?> _settings;

    public Wp09Factory(string environment = "Testing", IDictionary<string, string?>? settings = null)
    {
        _environment = environment;
        _settings = settings is null ? new() : new Dictionary<string, string?>(settings);
        Directory = Path.Combine(Path.GetTempPath(), $"lager-wp09-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Directory);
        DbPath = Path.Combine(Directory, "lager.db");
    }

    /// <summary>Verzeichnis der Datenbank (Backups landen dort).</summary>
    public string Directory { get; }

    public string DbPath { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("Database:ConnectionString", $"Data Source={DbPath}");
        builder.UseSetting("Database:Seed", "false");
        builder.UseSetting("Jwt:SigningKey", LagerApiFactory.SigningKey);
        builder.UseSetting("Auth:BootstrapAdminUsername", LagerApiFactory.AdminUser);
        builder.UseSetting("Auth:BootstrapAdminPassword", LagerApiFactory.AdminPassword);
        foreach (var (key, value) in _settings)
            builder.UseSetting(key, value);
    }

    /// <summary>Führt Code mit einem DbContext der laufenden App aus (eigener Scope, mit Audit-Interceptor und Pragmas).</summary>
    public async Task<T> WithDbAsync<T>(Func<LagerDbContext, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<LagerDbContext>());
    }

    public Task WithDbAsync(Func<LagerDbContext, Task> action) =>
        WithDbAsync<object?>(async db => { await action(db); return null; });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        SqliteConnection.ClearAllPools();
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (IOException) { /* Temp-Verzeichnis, wird beim nächsten Cleanup entfernt */ }
        catch (UnauthorizedAccessException) { /* dito */ }
    }
}

/// <summary>Direkter SQL-Zugriff auf eine SQLite-Datei (eigene, nicht gepoolte Verbindung), unabhängig von der App.</summary>
internal static class Wp09Sql
{
    public static SqliteConnection Open(string path, bool readOnly = false)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
        }.ToString());
        conn.Open();
        return conn;
    }

    /// <summary>Legt eine neue SQLite-Datei an und führt die Anweisungen aus (für Dateien, die keine Lager-Datenbank sind).</summary>
    public static void CreateDatabase(string path, params string[] statements)
    {
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        conn.Open();
        foreach (var sql in statements)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }

    public static object? Scalar(string path, string sql, params (string Name, object? Value)[] parameters)
    {
        using var conn = Open(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        var result = cmd.ExecuteScalar();
        return result is DBNull ? null : result;
    }

    public static long Count(string path, string sql, params (string Name, object? Value)[] parameters) =>
        Convert.ToInt64(Scalar(path, sql, parameters) ?? 0L);

    public static int Execute(string path, string sql, params (string Name, object? Value)[] parameters)
    {
        using var conn = Open(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd.ExecuteNonQuery();
    }

    public static List<string> Strings(string path, string sql)
    {
        using var conn = Open(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(Convert.ToString(reader.GetValue(0)) ?? string.Empty);
        return values;
    }

    /// <summary>Ergebnis von <c>PRAGMA integrity_check</c> (bei einer heilen Datei genau "ok").</summary>
    public static List<string> IntegrityCheck(string path) => Strings(path, "PRAGMA integrity_check;");

    /// <summary>Ziele der Fremdschlüssel einer Tabelle (referenzierte Tabellennamen).</summary>
    public static List<string> ForeignKeyTargets(string path, string table) =>
        Strings(path, $"SELECT \"table\" FROM pragma_foreign_key_list('{table}')");
}

internal static class Wp09Api
{
    public static async Task<ArticleDto> CreateArticleAsync(this HttpClient admin, string sku)
    {
        var response = await admin.PostAsJsonAsync("/api/articles", new CreateArticleRequest(
            sku, $"Testartikel {sku}", null, new DimensionsDto(100, 100, 100), 100, new StackingInfoDto(false, "Z", 0, null)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ArticleDto>())!;
    }

    public static async Task<OrderDto> CreateOrderAsync(this HttpClient admin, string orderNumber, Guid articleId, int quantity = 1)
    {
        var response = await admin.PostAsJsonAsync("/api/orders/manual", new CreateOrderRequest(
            orderNumber, "Testkunde", new[] { new CreateOrderLineRequest(articleId, quantity) }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<OrderDto>())!;
    }
}

/// <summary>Sammelt Logeinträge, damit Tests prüfen können, dass Upgrader-Fehler geloggt statt verschluckt werden.</summary>
internal sealed class ListLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception) + (exception is null ? string.Empty : " | " + exception.Message)));
}

/// <summary>Eine SQLite-Datei mit dem aktuellen EF-Modell, ohne die App (für Upgrader-Tests an Legacy-Zuständen).</summary>
internal sealed class StandaloneDb : IDisposable
{
    public StandaloneDb()
    {
        Directory = Path.Combine(Path.GetTempPath(), $"lager-wp09-db-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Directory);
        DbPath = System.IO.Path.Combine(Directory, "standalone.db");
    }

    public string Directory { get; }
    public string DbPath { get; }

    public LagerDbContext CreateContext() => new(new DbContextOptionsBuilder<LagerDbContext>()
        .UseSqlite($"Data Source={DbPath};Pooling=False")
        .Options);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (IOException) { /* Temp-Verzeichnis */ }
        catch (UnauthorizedAccessException) { /* dito */ }
    }
}
