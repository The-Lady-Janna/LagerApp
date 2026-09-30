using System.Globalization;
using System.Text.RegularExpressions;
using Lager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Lager.Infrastructure.Backup;

/// <summary>
/// Backup und Restore der SQLite-Datenbank: Snapshot per <c>VACUUM INTO</c> (konsistent auch unter Schreiblast, inklusive
/// WAL-Inhalt), Liste, Löschen, Aufbewahrung (die neuesten <see cref="BackupOptions.RetentionCount"/> Backups bleiben) und
/// der geprüfte Austausch der Live-Datei. Die Datei-Logik (VACUUM INTO, Validierung, Austausch) steckt seit WP09 in
/// <see cref="SqliteDatabaseFile"/> und wird hier nur benutzt; dieser Dienst kümmert sich um Verzeichnis, Dateinamen und Ablauf.
///
/// Sicherungsdateien heißen <c>&lt;db-name&gt;-backup-&lt;UTC&gt;.db</c> bzw. <c>&lt;db-name&gt;-before-restore-&lt;UTC&gt;.db</c>
/// (UTC-Zeit auf die Millisekunde, bei einer Kollision mit Zähler). Nur Dateien mit genau diesem Namensmuster werden
/// aufgelistet, geliefert oder gelöscht: ein Dateiname aus einer Anfrage kann so nie auf eine andere Datei zeigen
/// (Pfad-Traversal ausgeschlossen), und die Live-Datenbank neben den Backups bleibt unberührt.
///
/// Ein einziger Zugriffsschutz serialisiert Backup, Löschen, Aufbewahrung und Restore (Zeitplan und Klick gleichzeitig,
/// Restore während eines Backups). Singleton. MySQL wird nicht unterstützt (<see cref="IsSupported"/> = false): dort sichert man
/// mit mysqldump.
/// </summary>
public sealed class BackupService
{
    private const string StampFormat = "yyyyMMdd-HHmmss-fff";

    private readonly DatabaseSettings _settings;
    private readonly Func<BackupOptions> _options;
    private readonly string _contentRoot;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Regex? _namePattern;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Für die Dependency Injection: die Einstellungen werden bei jedem Zugriff frisch gelesen (Änderungen in appsettings wirken sofort).</summary>
    public BackupService(
        DatabaseSettings settings, IOptionsMonitor<BackupOptions> options, IHostEnvironment environment,
        TimeProvider time, ILogger<BackupService> logger)
        : this(settings, () => options.CurrentValue, environment.ContentRootPath, time, logger)
    {
    }

    private BackupService(DatabaseSettings settings, Func<BackupOptions> options, string contentRoot, TimeProvider time, ILogger logger)
    {
        _settings = settings;
        _options = options;
        _contentRoot = contentRoot;
        _time = time;
        _logger = logger;

        if (settings.IsSqlite && settings.SqliteFilePath is { } live)
        {
            var stem = Regex.Escape(Path.GetFileNameWithoutExtension(live));
            var extension = Regex.Escape(Path.GetExtension(live));
            // [0-9] statt \d (kein Unicode) und \z statt $ ($ ließe einen angehängten Zeilenumbruch zu).
            _namePattern = new Regex(
                $"^{stem}-(?<kind>{BackupKinds.Backup}|{BackupKinds.BeforeRestore})-(?<stamp>[0-9]{{8}}-[0-9]{{6}}-[0-9]{{3}})(?:-(?<n>[0-9]{{1,9}}))?{extension}\\z",
                RegexOptions.CultureInvariant);
        }
    }

    /// <summary>Ein Dienst mit festen Einstellungen, ohne Dependency Injection (Aufruf des Controllers ohne Host, z. B. im Unit-Test).</summary>
    public static BackupService Create(
        DatabaseSettings settings, BackupOptions options, string contentRootPath, ILogger? logger = null, TimeProvider? time = null) =>
        new(settings, () => options, contentRootPath, time ?? TimeProvider.System, logger ?? NullLogger.Instance);

    /// <summary>Sicherung einer MySQL-Datenbank (Vorlage; Zeichensatz und Stolpersteine: docs/TROUBLESHOOTING.md).</summary>
    public const string MySqlDumpCommand =
        "mysqldump --single-transaction --routines --triggers -h <host> -u <benutzer> -p <datenbank> > lager-backup.sql";

    /// <summary>Einspielen eines MySQL-Dumps (Vorlage).</summary>
    public const string MySqlRestoreCommand = "mysql -h <host> -u <benutzer> -p <datenbank> < lager-backup.sql";

    /// <summary>Die aktuellen Einstellungen (Abschnitt <c>Backup</c>).</summary>
    public BackupOptions Options => _options();

    /// <summary>Der nächste zeitgesteuerte Lauf (UTC) oder null, wenn es keinen gibt (kein/ungültiger Zeitplan, MySQL).</summary>
    public DateTimeOffset? NextScheduledRun() =>
        IsSupported && BackupSchedule.TryParse(Options.Schedule, out var at) ? BackupSchedule.NextRun(_time.GetUtcNow(), at) : null;

    /// <summary>true bei einer SQLite-Datei; bei MySQL und bei einer In-Memory-Datenbank gibt es kein Backup über die Anwendung.</summary>
    public bool IsSupported => _settings.IsSqlite && _settings.SqliteFilePath is not null;

    /// <summary>Die Datenbank ist MySQL (Sicherung mit mysqldump).</summary>
    public bool IsMySql => _settings.IsMySql;

    /// <summary>Absoluter Pfad der Live-Datenbank; nur intern (nie in Antworten). Wirft, wenn <see cref="IsSupported"/> false ist.</summary>
    public string LiveDatabasePath =>
        _settings.SqliteFilePath ?? throw new InvalidOperationException("Backup ist nur für eine SQLite-Datei verfügbar.");

    /// <summary>Zielverzeichnis für Backups: <c>Backup:Directory</c> (relativ zum Content-Root) oder das Verzeichnis der Live-DB.</summary>
    public string GetDirectory()
    {
        var live = LiveDatabasePath;
        var configured = Options.Directory;
        return string.IsNullOrWhiteSpace(configured)
            ? Path.GetDirectoryName(live) ?? _contentRoot
            : Path.GetFullPath(configured.Trim(), _contentRoot);
    }

    // ---- Liste, Namen ------------------------------------------------------------------------------------------------

    /// <summary>true, wenn <paramref name="name"/> genau dem Namensmuster einer Sicherungsdatei entspricht (kein Pfad, keine Sonderzeichen).</summary>
    public bool IsValidName(string? name) => TryParseName(name, out _, out _, out _);

    /// <summary>Vollständiger Pfad einer Sicherungsdatei zum Namen; false bei einem ungültigen Namen (die Datei muss deshalb noch nicht existieren).</summary>
    public bool TryResolvePath(string? name, out string path)
    {
        path = string.Empty;
        if (!IsValidName(name)) return false;
        path = Path.Combine(GetDirectory(), name!);
        return true;
    }

    /// <summary>Alle Sicherungsdateien im Backup-Verzeichnis, die neuesten zuerst.</summary>
    public async Task<IReadOnlyList<BackupFileInfo>> ListAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return ListCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    private List<BackupFileInfo> ListCore()
    {
        var directory = GetDirectory();
        if (!Directory.Exists(directory)) return new List<BackupFileInfo>();

        var found = new List<(BackupFileInfo Info, int Counter)>();
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            if (!TryParseName(Path.GetFileName(path), out var kind, out var created, out var counter)) continue;
            try
            {
                found.Add((new BackupFileInfo(Path.GetFileName(path), new FileInfo(path).Length, created, kind), counter));
            }
            catch (IOException)
            {
                // Die Datei wurde zwischen Auflisten und Lesen entfernt (z. B. von einem anderen Prozess): sie gehört nicht mehr zur Liste.
            }
        }

        return found
            .OrderByDescending(f => f.Info.CreatedUtc).ThenByDescending(f => f.Counter).ThenByDescending(f => f.Info.Name, StringComparer.Ordinal)
            .Select(f => f.Info)
            .ToList();
    }

    private bool TryParseName(string? name, out string kind, out DateTime createdUtc, out int counter)
    {
        kind = string.Empty;
        createdUtc = default;
        counter = 1;
        if (_namePattern is null || string.IsNullOrEmpty(name) || name.Length > 255) return false;

        var match = _namePattern.Match(name);
        if (!match.Success) return false;
        if (!DateTime.TryParseExact(match.Groups["stamp"].Value, StampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out createdUtc))
            return false;

        kind = match.Groups["kind"].Value;
        if (match.Groups["n"].Success) counter = int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture);
        return true;
    }

    // ---- Backup erstellen, Aufbewahrung ----------------------------------------------------------------------------------

    /// <summary>
    /// Erstellt ein konsistentes Backup der laufenden Datenbank und wendet danach die Aufbewahrung an. Die Datei entsteht unter
    /// einem Zwischennamen und wird erst fertig umbenannt: eine halb geschriebene Datei taucht nie in der Liste auf und wird
    /// nie für ein Backup gehalten.
    /// </summary>
    /// <param name="db">Der DbContext der laufenden App (Verbindung mit WAL-Pragmas); für den Hintergrunddienst ein eigener Scope.</param>
    /// <param name="requestedBy">Wer es angefordert hat (nur fürs Log), z. B. der Benutzername oder "Zeitplan".</param>
    public async Task<BackupFileInfo> CreateAsync(LagerDbContext db, string? requestedBy, CancellationToken ct = default)
    {
        var live = LiveDatabasePath;
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(live))
                throw new FileNotFoundException("Die Datenbankdatei wurde nicht gefunden.", Path.GetFileName(live));

            var directory = GetDirectory();
            Directory.CreateDirectory(directory);
            var target = await SnapshotAsync(db, directory, BackupKinds.Backup, ct);
            var info = ToInfo(target);
            _logger.LogInformation("Backup erstellt: {FileName} ({Size} Bytes), angefordert von {User}.",
                info.Name, info.SizeBytes, requestedBy ?? "unbekannt");

            ApplyRetentionCore();
            return info;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Löscht die ältesten Backups über <see cref="BackupOptions.RetentionCount"/> hinaus (nur <see cref="BackupKinds.Backup"/>,
    /// nie die Sicherheitskopien vor einem Restore). Ein Wert unter 1 schaltet das Löschen ab. Gibt die Zahl der gelöschten Dateien zurück.
    /// </summary>
    public async Task<int> ApplyRetentionAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return ApplyRetentionCore();
        }
        finally
        {
            _gate.Release();
        }
    }

    private int ApplyRetentionCore()
    {
        var keep = Options.RetentionCount;
        if (keep < 1) return 0;

        var directory = GetDirectory();
        var removed = 0;
        foreach (var old in ListCore().Where(f => f.Kind == BackupKinds.Backup).Skip(keep))
        {
            try
            {
                File.Delete(Path.Combine(directory, old.Name));
                removed++;
                _logger.LogInformation("Aufbewahrung: altes Backup {FileName} gelöscht (behalten werden die neuesten {Keep}).", old.Name, keep);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Aufbewahrung: {FileName} konnte nicht gelöscht werden; der nächste Lauf versucht es erneut.", old.Name);
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.LogWarning(ex, "Aufbewahrung: {FileName} konnte nicht gelöscht werden; der nächste Lauf versucht es erneut.", old.Name);
            }
        }
        return removed;
    }

    /// <summary>
    /// Löscht eine Sicherungsdatei. false, wenn es sie nicht gibt (oder der Name kein gültiger Sicherungsname ist).
    /// Ist die Datei gerade in Benutzung (Windows: laufender Download), ein Konflikt (409, Code <c>backup_in_use</c>).
    /// </summary>
    public async Task<bool> DeleteAsync(string name, string? requestedBy, CancellationToken ct = default)
    {
        if (!TryResolvePath(name, out var path)) return false;
        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(path)) return false;
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Übersetzung in einen Konflikt (begründeter catch): die Antwort baut die zentrale Fehlerabbildung (409 mit Code).
                throw new InvalidOperationException(
                    "Das Backup ist gerade in Benutzung (z. B. ein laufender Download) und wurde nicht gelöscht. Bitte später erneut versuchen.", ex)
                {
                    Data = { ["code"] = "backup_in_use" },
                };
            }
            _logger.LogInformation("Backup gelöscht: {FileName}, angefordert von {User}.", name, requestedBy ?? "unbekannt");
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- Restore ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Ersetzt die Live-Datenbank durch ein vorhandenes Backup. Das Backup selbst bleibt unverändert (es wird kopiert und die Kopie
    /// geprüft). Sonst wie <see cref="RestoreAsync"/>. Das Backup muss existieren (<see cref="FileNotFoundException"/> sonst).
    /// </summary>
    public async Task<RestoreOutcome> RestoreFromBackupAsync(LagerDbContext db, string name, string? requestedBy, CancellationToken ct = default)
    {
        if (!TryResolvePath(name, out var path)) throw new ArgumentException("Kein gültiger Backup-Name.", nameof(name));
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return await RestoreAsync(db, stream, requestedBy, ct);
    }

    /// <summary>
    /// Ersetzt die Live-Datenbank durch den Inhalt von <paramref name="source"/> (SQLite only). Ablauf: den Inhalt in eine
    /// Temp-Datei NEBEN der Live-DB schreiben (gleiches Volume: der Austausch ist ein atomares Umbenennen) und prüfen (SQLite-Header,
    /// PRAGMA integrity_check, Pflichttabellen, Schemastand nicht neuer als die App) - erst dann die Sicherheitskopie der Live-DB
    /// (<c>*-before-restore-*</c>), alle gepoolten Verbindungen schließen, alte -wal/-shm entfernen und die Datei austauschen. Eine
    /// ungültige Datei berührt die Live-DB nie und ergibt ein <see cref="RestoreOutcome"/> mit Grund. Danach muss der API-Server
    /// neu gestartet werden - die EF-Connection lässt sich zur Laufzeit nicht auswechseln.
    /// Ist die Live-Datei noch in Benutzung, wirft der Austausch eine <see cref="DatabaseFileInUseException"/>.
    /// </summary>
    public async Task<RestoreOutcome> RestoreAsync(LagerDbContext db, Stream source, string? requestedBy, CancellationToken ct = default)
    {
        var live = LiveDatabasePath;
        var user = requestedBy ?? "unbekannt";
        var temp = Path.Combine(Path.GetDirectoryName(live)!, $".{Path.GetFileName(live)}.restore-{Guid.NewGuid():N}.tmp");
        try
        {
            // 1. Inhalt neben die Live-DB schreiben (ohne den Zugriffsschutz: ein großer Upload soll Liste und Backup nicht blockieren).
            await using (var destination = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await source.CopyToAsync(destination, ct);

            await _gate.WaitAsync(ct);
            try
            {
                // 2. Prüfen, bevor die Live-DB auch nur angefasst wird.
                var validation = await SqliteDatabaseFile.ValidateAsync(temp, SchemaUpgrader.GetKnownStepNames(), ct);
                if (!validation.IsValid)
                {
                    _logger.LogWarning("Restore abgelehnt (angefordert von {User}): {Reason}", user, validation.Error);
                    return RestoreOutcome.Rejected("invalid_backup_file", validation.Error ?? "Die Datei ist keine gültige Sicherung.");
                }

                // 3. Sicherheitskopie der Live-DB: VACUUM INTO ist konsistent und enthält, was noch im WAL steht.
                var directory = GetDirectory();
                Directory.CreateDirectory(directory);
                var safety = await SnapshotAsync(db, directory, BackupKinds.BeforeRestore, ct);

                // 4. WAL in die Hauptdatei schreiben und alle Verbindungen dieses Requests freigeben.
                await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE);", ct);
                await db.Database.CloseConnectionAsync();

                // 5. Austausch: Pools leeren, -wal/-shm/-journal der alten DB entfernen, Datei ersetzen.
                try
                {
                    await SqliteDatabaseFile.ReplaceLiveFileAsync(live, temp, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogError(ex, "Restore fehlgeschlagen: Die Datenbankdatei ist in Benutzung (angefordert von {User}).", user);
                    throw new DatabaseFileInUseException("Die Datenbankdatei ist noch in Benutzung.", ex);
                }

                _logger.LogWarning("Restore durchgeführt (angefordert von {User}); Sicherheitskopie {Safety}. Neustart erforderlich.",
                    user, Path.GetFileName(safety));
                return new RestoreOutcome(true, null, null, Path.GetFileName(safety), new FileInfo(live).Length);
            }
            finally
            {
                _gate.Release();
            }
        }
        finally
        {
            // Nach Erfolg existiert die Temp-Datei nicht mehr; nach einer Ablehnung oder einem Fehler wird sie samt Resten entfernt.
            foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            {
                try { File.Delete(temp + suffix); }
                catch (IOException) { /* Aufräumen ist best effort */ }
                catch (UnauthorizedAccessException) { /* dito */ }
            }
        }
    }

    // ---- Hilfen ----------------------------------------------------------------------------------------------------------

    /// <summary>Snapshot der Live-DB als neue Datei der Art <paramref name="kind"/>; unter Zwischennamen geschrieben, dann umbenannt.</summary>
    private async Task<string> SnapshotAsync(LagerDbContext db, string directory, string kind, CancellationToken ct)
    {
        var live = LiveDatabasePath;
        var target = UniquePath(directory, $"{Path.GetFileNameWithoutExtension(live)}-{kind}", Path.GetExtension(live));
        var temp = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await SqliteDatabaseFile.BackupToAsync(db, temp, ct);
            File.Move(temp, target);
            return target;
        }
        catch
        {
            // Aufräumen und die ursprüngliche Ausnahme unverändert weiterreichen (best effort).
            try { File.Delete(temp); }
            catch (IOException) { /* nichts zu tun */ }
            catch (UnauthorizedAccessException) { /* nichts zu tun */ }
            throw;
        }
    }

    /// <summary>Dateiname mit UTC-Zeit (Millisekunden); bei einer Kollision kommt ein Zähler dazu, nichts wird überschrieben.</summary>
    private string UniquePath(string directory, string stem, string extension)
    {
        var stamp = _time.GetUtcNow().UtcDateTime.ToString(StampFormat, CultureInfo.InvariantCulture);
        var path = Path.Combine(directory, $"{stem}-{stamp}{extension}");
        for (var i = 2; File.Exists(path); i++)
            path = Path.Combine(directory, $"{stem}-{stamp}-{i}{extension}");
        return path;
    }

    private BackupFileInfo ToInfo(string path)
    {
        var name = Path.GetFileName(path);
        if (!TryParseName(name, out var kind, out var created, out _))
            throw new InvalidOperationException($"Unerwarteter Dateiname einer Sicherung: {name}");
        return new BackupFileInfo(name, new FileInfo(path).Length, created, kind);
    }
}
