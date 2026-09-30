using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lager.Tests.Infrastructure;

/// <summary>
/// Startet die komplette API im Speicher (TestServer) gegen eine frische SQLite-Datei
/// im Temp-Verzeichnis. Jede Factory-Instanz hat ihre eigene Datenbank, Tests
/// können also parallel laufen und beeinflussen sich nicht.
///
/// Konfiguration wird über <c>UseSetting</c> gesetzt — nur so sieht <c>Program.cs</c> die Werte
/// schon beim Lesen von <c>builder.Configuration</c> (ConfigureAppConfiguration kommt dafür zu spät).
/// </summary>
public sealed class LagerApiFactory : WebApplicationFactory<Program>
{
    public const string AdminUser = "admin";
    public const string AdminPassword = "Test-Admin-Pw-2025!";
    /// <summary>Passwort, auf das <c>AsAdminAsync()</c> den Bootstrap-Admin beim ersten Aufruf umstellt.</summary>
    public const string ChangedAdminPassword = "Test-Admin-Geaendert-2025!";
    public const string SigningKey = "test-only-signing-key-0123456789-0123456789-0123456789";

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"lager-test-{Guid.NewGuid():N}.db");

    public bool Seed { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath}");
        builder.UseSetting("Database:Seed", Seed ? "true" : "false");
        builder.UseSetting("Jwt:SigningKey", SigningKey);
        builder.UseSetting("Auth:BootstrapAdminUsername", AdminUser);
        builder.UseSetting("Auth:BootstrapAdminPassword", AdminPassword);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            try { File.Delete(_dbPath + suffix); } catch (IOException) { /* Temp-Datei, wird beim nächsten Cleanup entfernt */ }
        }
    }
}
