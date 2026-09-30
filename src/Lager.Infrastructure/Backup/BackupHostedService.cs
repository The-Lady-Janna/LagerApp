using Lager.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lager.Infrastructure.Backup;

/// <summary>
/// Zeitgesteuerter Hintergrundjob: legt täglich zur Uhrzeit aus <c>Backup:Schedule</c> ("HH:mm", <b>UTC</b>) ein Backup an;
/// die Aufbewahrung (<c>Backup:RetentionCount</c>) löscht danach die ältesten (siehe <see cref="BackupService.CreateAsync"/>).
/// Ohne Zeitplan (Standard) macht der Job nichts. Die Einstellungen werden in jedem Durchlauf frisch gelesen (spätestens nach
/// <see cref="MaxWait"/>): ein geänderter Zeitplan gilt ohne Neustart.
///
/// Ein Fehler beim Backup (Platte voll, Datei gesperrt) wird nur geloggt und stoppt den Dienst nicht; der nächste Lauf versucht
/// es wieder. Ein verpasster Lauf (Server war zur Startzeit aus) wird nicht nachgeholt. Läuft nur bei SQLite mit Datei
/// (bei MySQL sichert man mit mysqldump). Die Uhr kommt aus dem <see cref="TimeProvider"/>, damit ein Test sie vorstellen kann.
/// </summary>
public sealed class BackupHostedService : BackgroundService
{
    /// <summary>
    /// Längste einzelne Wartezeit. Der Job schaut spätestens dann wieder auf die Uhr (Zeitsprünge, Ruhezustand,
    /// geänderte Einstellungen) statt einmal bis zum nächsten Tag zu schlafen.
    /// </summary>
    internal static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(30);

    private readonly IServiceScopeFactory _scopes;
    private readonly BackupService _backups;
    private readonly TimeProvider _time;
    private readonly ILogger<BackupHostedService> _logger;

    public BackupHostedService(IServiceScopeFactory scopes, BackupService backups, TimeProvider time, ILogger<BackupHostedService> logger)
    {
        _scopes = scopes;
        _backups = backups;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_backups.IsSupported)
        {
            _logger.LogInformation("Backup-Zeitplan: nur für eine SQLite-Datei verfügbar (bei MySQL mysqldump nutzen), der Job ruht.");
            return;
        }

        try
        {
            await RunLoopAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Herunterfahren: kein Fehler.
        }
    }

    // Zustand der Schleife (nur vom einen Schleifen-Task benutzt).
    private DateTimeOffset? _nextRun;
    private TimeOnly _plannedAt;
    private string? _warnedAbout;

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Begründeter Sammel-catch: eine unlesbare Einstellung (z. B. Backup:RetentionCount=abc) oder ein anderer Fehler darf
                // den Host nicht beenden (ein Hintergrunddienst, der wirft, stoppt standardmäßig die ganze Anwendung).
                _logger.LogError(ex, "Backup-Zeitplan: Die Einstellungen ließen sich nicht auswerten; neuer Versuch in {Minutes} Minuten.", MaxWait.TotalMinutes);
                _nextRun = null;
                await Task.Delay(MaxWait, _time, ct);
            }
        }
    }

    /// <summary>Ein Schritt der Schleife: entweder warten (höchstens <see cref="MaxWait"/>) oder, wenn der Lauf fällig ist, das Backup anlegen.</summary>
    private async Task TickAsync(CancellationToken ct)
    {
        var schedule = _backups.Options.Schedule;
        if (!BackupSchedule.TryParse(schedule, out var at))
        {
            // Kein (oder ein ungültiger) Zeitplan: ein ungültiger Wert wird einmal gemeldet; später erneut nachsehen.
            if (!string.IsNullOrWhiteSpace(schedule) && schedule != _warnedAbout)
            {
                _warnedAbout = schedule;
                _logger.LogWarning("{Key} '{Schedule}' ist ungültig (erwartet: HH:mm, UTC, z. B. 02:00); es gibt keine zeitgesteuerten Backups.", BackupOptions.ScheduleKey, schedule);
            }
            _nextRun = null;
            await Task.Delay(MaxWait, _time, ct);
            return;
        }

        var now = _time.GetUtcNow();
        if (_nextRun is null || _plannedAt != at)
        {
            _plannedAt = at;
            _nextRun = BackupSchedule.NextRun(now, at);
            _logger.LogInformation("Backup-Zeitplan: täglich {Time} UTC, nächster Lauf {Next:u}.", at.ToString("HH:mm"), _nextRun);
        }

        var due = _nextRun.Value;
        if (now < due)
        {
            await Task.Delay(Min(due - now, MaxWait), _time, ct);
            return;
        }

        await RunBackupAsync(ct);
        _nextRun = BackupSchedule.NextRun(_time.GetUtcNow(), at);
    }

    /// <summary>
    /// Ein Lauf: Backup erstellen (samt Aufbewahrung). Ein Fehler wird nur geloggt (false), der Dienst läuft weiter. Öffentlich,
    /// damit sich ein Lauf ohne Warten auf die Uhr auslösen lässt (Test).
    /// </summary>
    public async Task<bool> RunBackupAsync(CancellationToken ct = default)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LagerDbContext>();
            var info = await _backups.CreateAsync(db, "Zeitplan", ct);
            _logger.LogInformation("Zeitgesteuertes Backup erstellt: {FileName} ({Size} Bytes).", info.Name, info.SizeBytes);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Begründeter Sammel-catch: ein fehlgeschlagener Lauf darf den Dienst (und den Host) nicht beenden; der Grund steht im Log.
            _logger.LogError(ex, "Zeitgesteuertes Backup fehlgeschlagen; der nächste Lauf versucht es erneut.");
            return false;
        }
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
