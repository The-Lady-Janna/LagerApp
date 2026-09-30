using System.Data.Common;
using Lager.Api.Health;
using Lager.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Lager.Tests.WP16;

/// <summary>Die Datenbankprüfung selbst (ohne Host): gesund, nicht erreichbar, hängend.</summary>
public sealed class DbHealthCheckTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lager-wp16-db-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private LagerDbContext CreateContext(string file, params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<LagerDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dir, file)};Mode=ReadWrite")
            .AddInterceptors(interceptors)
            .Options;
        return new LagerDbContext(options);
    }

    private static Task<HealthCheckResult> RunAsync(DbHealthCheck check) =>
        check.CheckHealthAsync(new HealthCheckContext { Registration = new HealthCheckRegistration("database", check, null, null) });

    [Fact]
    public async Task Existing_database_is_healthy()
    {
        using (var setup = new LagerDbContext(new DbContextOptionsBuilder<LagerDbContext>()
                   .UseSqlite($"Data Source={Path.Combine(_dir, "ok.db")}").Options))
            await setup.Database.EnsureCreatedAsync();

        using var db = CreateContext("ok.db");
        // Großzügiges Timeout statt der 3 s der Vorgabe: Der Test prüft den gesunden Pfad, nicht das Timeout; auf einem
        // ausgelasteten Rechner (erster Zugriff, JIT) sollen ihn die 3 s nicht zu "nicht erreichbar" kippen.
        var result = await RunAsync(new DbHealthCheck(db, TimeSpan.FromSeconds(30)));

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task Missing_database_is_unhealthy_instead_of_throwing()
    {
        using var db = CreateContext("gibt-es-nicht.db");

        var result = await RunAsync(new DbHealthCheck(db));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task A_hanging_connection_ends_in_unhealthy_after_the_timeout_not_in_a_hanging_probe()
    {
        using (var setup = new LagerDbContext(new DbContextOptionsBuilder<LagerDbContext>()
                   .UseSqlite($"Data Source={Path.Combine(_dir, "haengt.db")}").Options))
            await setup.Database.EnsureCreatedAsync();

        using var db = CreateContext("haengt.db", new HangingConnectionInterceptor());
        // Ohne Timeout in der Prüfung würde der Interceptor ewig warten. Statt einer knappen Wanduhr-Grenze (kippt auf einem
        // ausgelasteten Rechner, ohne dass im Code etwas kaputt ist) fängt WaitAsync nur das Hängen ab: 60 s liegen weit über
        // den 200 ms der Prüfung, und ein Fehlschlag steht dann als TimeoutException im Bericht statt als Dauerläufer.
        var result = await RunAsync(new DbHealthCheck(db, TimeSpan.FromMilliseconds(200))).WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("antwortet nicht", result.Description);
    }

    /// <summary>Simuliert einen Datenbankserver, der die Verbindung nie aufbaut: wartet, bis abgebrochen wird.</summary>
    private sealed class HangingConnectionInterceptor : DbConnectionInterceptor
    {
        public override async ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return result;
        }
    }
}
