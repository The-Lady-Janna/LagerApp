using Lager.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Lager.Tests.WP21;

/// <summary>
/// Startet die API gegen eine SQLite-Datei in einem vorgegebenen Verzeichnis. Anders als <see cref="LagerApiFactory"/> (jede
/// Instanz eine frische Datenbank) lässt sich hier nacheinander mehr als ein Host auf dieselbe Datei richten: das ist der
/// "Neustart" nach einem Restore. Backups und Sicherheitskopien landen im selben Verzeichnis; aufräumen muss der Aufrufer
/// (erst wenn alle Factories entsorgt sind). Admin-Zugangsdaten und Signing-Key wie in <see cref="LagerApiFactory"/>, damit
/// <see cref="WorldBuilder"/> und <c>AsReadyAdminAsync()</c> funktionieren; der Restore ist freigegeben.
/// </summary>
public sealed class RestartableFactory : WebApplicationFactory<Program>
{
    public RestartableFactory(string dataDirectory) => DataDirectory = dataDirectory;

    public string DataDirectory { get; }

    public string DbPath => Path.Combine(DataDirectory, "lager.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("Database:ConnectionString", $"Data Source={DbPath}");
        builder.UseSetting("Database:Seed", "false");
        builder.UseSetting("Jwt:SigningKey", LagerApiFactory.SigningKey);
        builder.UseSetting("Auth:BootstrapAdminUsername", LagerApiFactory.AdminUser);
        builder.UseSetting("Auth:BootstrapAdminPassword", LagerApiFactory.AdminPassword);
        builder.UseSetting("Backup:AllowRestore", "true");
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) SqliteConnection.ClearAllPools();
    }
}
