using Lager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Lager.Api.Health;

/// <summary>
/// Prüft, ob die Datenbank erreichbar ist (SQLite: Datei vorhanden und lesbar, MySQL: Verbindung möglich).
/// Bewusst mit kurzem Timeout: Ein hängender Datenbankserver soll die Bereitschaftsprüfung nicht blockieren,
/// sondern schnell "nicht bereit" melden. Die Ursache (Exception) landet nur im Log, nie in der Antwort.
/// </summary>
public sealed class DbHealthCheck : IHealthCheck
{
    /// <summary>Wie lange die Verbindungsprüfung höchstens dauern darf.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    private readonly LagerDbContext _db;
    private readonly TimeSpan _timeout;

    [ActivatorUtilitiesConstructor]
    public DbHealthCheck(LagerDbContext db) : this(db, DefaultTimeout) { }

    public DbHealthCheck(LagerDbContext db, TimeSpan timeout)
    {
        _db = db;
        _timeout = timeout;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);

        try
        {
            if (!await _db.Database.CanConnectAsync(timeout.Token))
                return HealthCheckResult.Unhealthy("Datenbank nicht erreichbar.");

            // Bei SQLite prüft CanConnect nur, ob die Datei existiert; die Abfrage stellt sicher, dass sie sich auch öffnen lässt
            // (Berechtigungen, gesperrter Datenträger). Bei MySQL ist es eine zweite, billige Runde über dieselbe Verbindung.
            await _db.Database.ExecuteSqlRawAsync("SELECT 1", timeout.Token);
            return HealthCheckResult.Healthy("Datenbank erreichbar.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy($"Datenbank antwortet nicht innerhalb von {_timeout.TotalSeconds:0.#} s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("Datenbank nicht erreichbar.", ex);
        }
    }
}
