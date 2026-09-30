using System.Text.Json.Serialization;
using Lager.Api.Errors;
using Lager.Api.Seeding;
using Lager.Infrastructure;
using Lager.Infrastructure.Backup;
using Lager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Lager.Api.Controllers;

/// <summary>Zeilenzahlen nach dem Reseed.</summary>
/// <param name="Articles">Artikel.</param>
/// <param name="Shelves">Regale.</param>
/// <param name="Bins">Lagerplätze.</param>
/// <param name="StockItems">Bestandszeilen.</param>
/// <param name="Orders">Bestellungen.</param>
/// <param name="Walls">Wände.</param>
/// <param name="PickPoints">Pickpunkte.</param>
public sealed record ReseedCounts(
    [property: JsonPropertyName("articles")] int Articles,
    [property: JsonPropertyName("shelves")] int Shelves,
    [property: JsonPropertyName("bins")] int Bins,
    [property: JsonPropertyName("stockItems")] int StockItems,
    [property: JsonPropertyName("orders")] int Orders,
    [property: JsonPropertyName("walls")] int Walls,
    [property: JsonPropertyName("pickPoints")] int PickPoints);

/// <summary>Antwort von <c>POST /api/admin/reseed</c>.</summary>
/// <param name="Message">Meldung für Menschen.</param>
/// <param name="Counts">Anzahl der angelegten Demo-Datensätze je Art.</param>
public sealed record ReseedResponse(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("counts")] ReseedCounts Counts);

/// <summary>Antwort von <c>POST /api/admin/seed-bulk</c>.</summary>
/// <param name="Message">Meldung für Menschen.</param>
/// <param name="Articles">Neu angelegte Artikel.</param>
/// <param name="StockItems">Neu angelegte Bestandszeilen.</param>
/// <param name="Orders">Neu angelegte Bestellungen.</param>
public sealed record SeedBulkResponse(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("articles")] int Articles,
    [property: JsonPropertyName("stockItems")] int StockItems,
    [property: JsonPropertyName("orders")] int Orders);

/// <summary>Backup-Einstellungen und Betriebszustand (<c>GET /api/admin/backup-settings</c>); enthält keine Serverpfade.</summary>
/// <param name="Provider">Datenbank-Art: <c>Sqlite</c> oder <c>MySql</c>.</param>
/// <param name="Supported">Ob Backups hier möglich sind (nur SQLite in einer Datei).</param>
/// <param name="UnsupportedReason">Wenn nicht unterstützt: <c>mysql</c> oder <c>not_file_based</c>; sonst null.</param>
/// <param name="MysqlDumpCommand">Bei MySQL der Befehl für <c>mysqldump</c>; sonst null.</param>
/// <param name="MysqlRestoreCommand">Bei MySQL der Befehl zum Einspielen; sonst null.</param>
/// <param name="Schedule">Tägliche Startzeit des Zeitplans als <c>HH:mm</c> (UTC); null ohne Zeitplan. Ein ungültiger Wert kommt als Rohtext zurück.</param>
/// <param name="ScheduleValid">false, wenn der konfigurierte Zeitplan nicht lesbar ist.</param>
/// <param name="ScheduleTimeZone">Zeitzone des Zeitplans (immer <c>UTC</c>).</param>
/// <param name="NextRunUtc">Nächster geplanter Lauf (UTC); null ohne Zeitplan.</param>
/// <param name="RetentionCount">Wie viele Backups erhalten bleiben (0 oder weniger = nie automatisch löschen).</param>
/// <param name="AllowRestore">Konfiguration <c>Backup:AllowRestore</c>.</param>
/// <param name="RestoreAllowed">Ob der Restore in dieser Umgebung erlaubt ist (Development oder <c>AllowRestore</c>).</param>
/// <param name="IsDevelopment">Ob die Umgebung Development ist.</param>
/// <param name="CustomDirectory">Ob ein eigenes Backup-Verzeichnis konfiguriert ist (der Pfad selbst wird nicht genannt).</param>
/// <param name="MaxRestoreBytes">Obergrenze des Restore-Uploads in Bytes.</param>
public sealed record BackupSettingsResponse(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("supported")] bool Supported,
    [property: JsonPropertyName("unsupportedReason")] string? UnsupportedReason,
    [property: JsonPropertyName("mysqlDumpCommand")] string? MysqlDumpCommand,
    [property: JsonPropertyName("mysqlRestoreCommand")] string? MysqlRestoreCommand,
    [property: JsonPropertyName("schedule")] string? Schedule,
    [property: JsonPropertyName("scheduleValid")] bool ScheduleValid,
    [property: JsonPropertyName("scheduleTimeZone")] string ScheduleTimeZone,
    [property: JsonPropertyName("nextRunUtc")] DateTime? NextRunUtc,
    [property: JsonPropertyName("retentionCount")] int RetentionCount,
    [property: JsonPropertyName("allowRestore")] bool AllowRestore,
    [property: JsonPropertyName("restoreAllowed")] bool RestoreAllowed,
    [property: JsonPropertyName("isDevelopment")] bool IsDevelopment,
    [property: JsonPropertyName("customDirectory")] bool CustomDirectory,
    [property: JsonPropertyName("maxRestoreBytes")] long MaxRestoreBytes);

/// <summary>Antwort des älteren <c>POST /api/admin/backup</c> (Form aus WP09).</summary>
/// <param name="Message">Meldung für Menschen.</param>
/// <param name="FileName">Dateiname der Sicherung.</param>
/// <param name="SizeBytes">Größe in Bytes.</param>
public sealed record LegacyBackupResponse(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes);

/// <summary>Antwort von <c>POST /api/admin/restore</c>.</summary>
/// <param name="Message">Meldung für Menschen.</param>
/// <param name="RestartRequired">Immer true: der API-Server muss neu gestartet werden, damit die Datenbankverbindung neu aufgebaut wird.</param>
/// <param name="SizeBytes">Größe der neuen Live-Datenbank.</param>
/// <param name="SafetyBackup">Dateiname der Sicherheitskopie der bisherigen Live-Datenbank.</param>
public sealed record RestoreResponse(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("restartRequired")] bool RestartRequired,
    [property: JsonPropertyName("sizeBytes")] long SizeBytes,
    [property: JsonPropertyName("safetyBackup")] string? SafetyBackup);

/// <summary>
/// Administration (Reseed, Backup-Verwaltung, Restore). Antworten mit festem Status, die aus Umgebung, Konfiguration oder Upload
/// folgen (kein Fachfehler aus einem Service), baut <see cref="Fail"/> im einheitlichen Fehlerformat (ProblemDetails
/// mit code, correlationId und error). Alles andere - auch die belegte Datenbankdatei beim Restore - läuft als Exception
/// über die zentrale Fehlerabbildung der API.
/// </summary>
[ApiController]
[Route("api/admin")]
[Authorize(Policy = "Admin")]
public class AdminController : ControllerBase
{
    /// <summary>Obergrenze für den Restore-Upload (bewusst gesetzt statt Kestrel-Standard von rund 30 MB).</summary>
    private const long MaxRestoreBytes = 1024L * 1024 * 1024;

    /// <summary>Text, den man zur Bestätigung eines Restores eintippen muss (Formularfeld <c>confirm</c>).</summary>
    public const string RestoreConfirmation = "RESTORE";

    // Codes der Antworten aus Umgebung/Konfiguration (snake_case; die allgemeinen stehen in ProblemCodes).
    private const string SqliteOnly = "sqlite_only";
    private const string NotFileBased = "database_not_file_based";
    private const string InvalidBackupName = "invalid_backup_name";
    private const string ConfirmationRequired = "confirmation_required";

    private readonly LagerDbContext _db;
    private readonly IWebHostEnvironment _env;
    private readonly DatabaseSettings _settings;
    private readonly BackupService _backups;
    private readonly ILogger<AdminController> _logger;

    /// <param name="db">Datenbankkontext der Anfrage.</param>
    /// <param name="env">Hosting-Umgebung (Development-Prüfung).</param>
    /// <param name="settings">Datenbank-Einstellungen (Provider).</param>
    /// <param name="config">Konfiguration, nur für den Fallback des Backup-Dienstes.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="backups">
    /// Der Backup-Dienst aus der Dependency Injection. Fehlt er (Aufruf ohne Host, z. B. im Unit-Test), baut der Controller einen
    /// mit den Einstellungen aus <paramref name="config"/> (Abschnitt <c>Backup</c>).
    /// </param>
    public AdminController(
        LagerDbContext db, IWebHostEnvironment env, DatabaseSettings settings, IConfiguration config, ILogger<AdminController> logger,
        BackupService? backups = null)
    {
        _db = db;
        _env = env;
        _settings = settings;
        _logger = logger;
        _backups = backups ?? BackupService.Create(
            settings, config.GetSection(BackupOptions.SectionName).Get<BackupOptions>() ?? new BackupOptions(), env.ContentRootPath);
    }

    /// <summary>Löscht alle Fachdaten und setzt die Demo-Daten neu auf (nur Development).</summary>
    /// <remarks>
    /// Wipes all data and reseeds from <see cref="DemoDataSeeder"/>. Development-only
    /// — refuses to run outside <c>Development</c> environment to avoid accidents (403, Code <c>development_only</c>).
    /// Löscht vollständig: alle Fachtabellen in Fremdschlüssel-Reihenfolge (Kinder vor Eltern), in einer Transaktion.
    /// Benutzer bleiben (man will sich danach noch anmelden können), ebenso der Audit-Trail (siehe <see cref="DatabaseReset"/>).
    /// </remarks>
    [HttpPost("reseed")]
    [ProducesResponseType<ReseedResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Reseed(CancellationToken ct)
    {
        if (!_env.IsDevelopment())
            return Fail(StatusCodes.Status403Forbidden, "development_only", "Reseed ist nur in der Development-Umgebung verfügbar.");

        _logger.LogWarning("Reseed: Fachdaten werden gelöscht und neu aufgesetzt (angefordert von {User}).", User.Identity?.Name ?? "unbekannt");

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        // Alle Fachtabellen leeren, Kinder vor Eltern: Liste und Reihenfolge kommen aus dem EF-Modell (Fremdschlüssel), damit
        // später ergänzte Tabellen automatisch mitgelöscht werden. Direkt in der Datenbank (kein Laden in den Speicher,
        // keine Audit-Einträge pro Zeile); Benutzer und Audit-Trail bleiben.
        await DatabaseReset.ClearBusinessDataAsync(_db);

        // Die Datenbank ist jetzt leer (bis auf Benutzer): der Seeder legt die Demo-Daten an. Scheitert etwas, rollt alles zurück.
        if (!await DemoDataSeeder.SeedAsync(_db, ct))
            return Fail(StatusCodes.Status500InternalServerError, "reseed_failed",
                "Nach dem Löschen war die Datenbank nicht leer; Reseed abgebrochen und zurückgerollt.");
        await tx.CommitAsync(ct);

        var counts = new ReseedCounts(
            Articles: await _db.Articles.CountAsync(ct),
            Shelves: await _db.Shelves.CountAsync(ct),
            Bins: await _db.StorageLocations.CountAsync(ct),
            StockItems: await _db.StockItems.CountAsync(ct),
            Orders: await _db.Orders.CountAsync(ct),
            Walls: await _db.Walls.CountAsync(ct),
            PickPoints: await _db.PickPoints.CountAsync(ct));
        return Ok(new ReseedResponse("Datenbank zurückgesetzt und neu aufgesetzt.", counts));
    }

    /// <summary>Legt zusätzlich zufällige Demo-Datensätze an (nur Development).</summary>
    /// <remarks>
    /// Additively generate <paramref name="count"/> random demo rows (default 1000):
    /// ~5% articles, ~10% stock items, ~85% orders. Each call uses a fresh batch
    /// tag so repeated calls accumulate. Außerhalb von Development 403 (Code <c>development_only</c>).
    /// </remarks>
    /// <param name="count">Anzahl der Datensätze (1 bis 100000, Standard 1000).</param>
    [HttpPost("seed-bulk")]
    [ProducesResponseType<SeedBulkResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> SeedBulk([FromQuery] int count = 1000, CancellationToken ct = default)
    {
        if (!_env.IsDevelopment())
            return Fail(StatusCodes.Status403Forbidden, "development_only", "Bulk-Seed ist nur in der Development-Umgebung verfügbar.");
        if (count < 1 || count > 100_000)
            return Fail(StatusCodes.Status400BadRequest, ProblemCodes.ValidationFailed, "count muss zwischen 1 und 100000 liegen.");

        var result = await DemoDataSeeder.SeedBulkAsync(_db, count, ct);
        return Ok(new SeedBulkResponse(
            $"{result.Articles + result.StockItems + result.Orders} Demo-Datensätze hinzugefügt.",
            result.Articles, result.StockItems, result.Orders));
    }

    /// <summary>Liefert die Backup-Einstellungen und den Betriebszustand (nur lesen).</summary>
    /// <remarks>
    /// Für die Oberfläche: Zeitplan samt nächstem Lauf, Aufbewahrung,
    /// ob der Restore hier erlaubt ist, und - bei MySQL - die Befehle für mysqldump statt eines Fehlers. Keine Serverpfade.
    /// </remarks>
    [HttpGet("backup-settings")]
    [ProducesResponseType<BackupSettingsResponse>(StatusCodes.Status200OK)]
    public IActionResult GetBackupSettings()
    {
        var options = _backups.Options;
        var supported = _backups.IsSupported;
        var scheduleSet = !string.IsNullOrWhiteSpace(options.Schedule);
        var scheduleValid = BackupSchedule.TryParse(options.Schedule, out var at);
        // Die Startzeit einheitlich als "HH:mm"; ein ungültiger Wert kommt als Rohtext zurück, damit die Oberfläche ihn nennen kann.
        var schedule = !supported || !scheduleSet
            ? null
            : scheduleValid ? at.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture) : options.Schedule!.Trim();

        return Ok(new BackupSettingsResponse(
            Provider: _settings.IsMySql ? "MySql" : "Sqlite",
            Supported: supported,
            UnsupportedReason: supported ? null : _settings.IsMySql ? "mysql" : "not_file_based",
            MysqlDumpCommand: _settings.IsMySql ? BackupService.MySqlDumpCommand : null,
            MysqlRestoreCommand: _settings.IsMySql ? BackupService.MySqlRestoreCommand : null,
            Schedule: schedule,
            ScheduleValid: !scheduleSet || scheduleValid,
            ScheduleTimeZone: "UTC",
            NextRunUtc: _backups.NextScheduledRun()?.UtcDateTime,
            RetentionCount: options.RetentionCount,
            AllowRestore: options.AllowRestore,
            RestoreAllowed: _env.IsDevelopment() || options.AllowRestore,
            IsDevelopment: _env.IsDevelopment(),
            CustomDirectory: !string.IsNullOrWhiteSpace(options.Directory),
            MaxRestoreBytes: MaxRestoreBytes));
    }

    /// <summary>Listet die Sicherungsdateien (nur SQLite).</summary>
    /// <remarks>
    /// Die Sicherungsdateien im Backup-Verzeichnis (Backups und Sicherheitskopien vor einem Restore), die neuesten
    /// zuerst - Name, Größe, Zeit (UTC) und Art. Nur der Dateiname, kein Serverpfad. Bei MySQL oder einer In-Memory-Datenbank 400
    /// (Codes <c>sqlite_only</c> bzw. <c>database_not_file_based</c>).
    /// </remarks>
    [HttpGet("backups")]
    [ProducesResponseType<IReadOnlyList<BackupFileInfo>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListBackups(CancellationToken ct)
    {
        if (Unsupported(restore: false) is { } unsupported) return unsupported;
        return Ok(await _backups.ListAsync(ct));
    }

    /// <summary>Erzeugt jetzt ein Backup der laufenden Datenbank (nur SQLite).</summary>
    /// <remarks>
    /// Erzeugt einen konsistenten Snapshot der laufenden Datenbank per <c>VACUUM INTO</c> (auch unter
    /// paralleler Schreiblast, inklusive WAL-Inhalt) und wendet danach die Aufbewahrung an (<c>Backup:RetentionCount</c>). Die
    /// Datei liegt im Verzeichnis der Live-DB oder in <c>Backup:Directory</c>; der Dateiname trägt die UTC-Zeit. Die Antwort
    /// (201, <c>Location</c> = Download der Datei) nennt nur Dateiname, Größe, Zeit und Art, keinen Serverpfad. Achtung: Backups
    /// enthalten alle Daten samt Passwort-Hashes und sind unverschlüsselt; sie gehören auf ein anderes Medium. Für MySQL
    /// mysqldump nutzen - das ist nicht Aufgabe eines API-Endpunkts (400).
    /// </remarks>
    [HttpPost("backups")]
    [ProducesResponseType<BackupFileInfo>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> CreateBackup(CancellationToken ct)
    {
        if (Unsupported(restore: false) is { } unsupported) return unsupported;
        if (!System.IO.File.Exists(_backups.LiveDatabasePath))
            return Fail(StatusCodes.Status500InternalServerError, "database_file_missing", "Die Datenbankdatei wurde nicht gefunden.");

        var info = await _backups.CreateAsync(_db, User.Identity?.Name, ct);
        return CreatedAtAction(nameof(DownloadBackup), new { name = info.Name }, info);
    }

    /// <summary>Erzeugt ein Backup wie <c>POST /api/admin/backups</c>, mit der älteren Antwortform.</summary>
    /// <remarks>
    /// Wie <see cref="CreateBackup"/>, mit der Antwortform aus WP09 (<c>message</c>, <c>fileName</c>, <c>sizeBytes</c>;
    /// Status 200). Bleibt für bestehende Skripte erhalten; neue Clients nutzen <c>POST /api/admin/backups</c>.
    /// </remarks>
    [HttpPost("backup")]
    [ProducesResponseType<LegacyBackupResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> Backup(CancellationToken ct)
    {
        var created = await CreateBackup(ct);
        return created is CreatedAtActionResult { Value: BackupFileInfo info }
            ? Ok(new LegacyBackupResponse("Backup erstellt.", info.Name, info.SizeBytes))
            : created;
    }

    /// <summary>Lädt eine Sicherungsdatei herunter.</summary>
    /// <remarks>
    /// Liefert die Datei als Datei-Stream mit Content-Disposition, nicht zwischenspeicherbar. Der Name muss
    /// genau dem Namensmuster einer Sicherung entsprechen (kein Pfad, kein "..", nur Dateien aus dem Backup-Verzeichnis): sonst
    /// 400, eine unbekannte Sicherung 404.
    /// </remarks>
    /// <param name="name">Dateiname der Sicherung (aus <c>GET /api/admin/backups</c>).</param>
    [HttpGet("backups/{name}")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK, "application/octet-stream")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public IActionResult DownloadBackup(string name)
    {
        if (Unsupported(restore: false) is { } unsupported) return unsupported;
        if (!_backups.TryResolvePath(name, out var path))
            return Fail(StatusCodes.Status400BadRequest, InvalidBackupName, "Ungültiger Backup-Name.");
        if (!System.IO.File.Exists(path))
            return Fail(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "Das Backup wurde nicht gefunden.");

        // Enthält alle Daten samt Passwort-Hashes: nie in einem Zwischenspeicher ablegen.
        Response.Headers.CacheControl = "no-store";
        return PhysicalFile(path, "application/octet-stream", name, enableRangeProcessing: true);
    }

    /// <summary>Löscht eine Sicherungsdatei.</summary>
    /// <remarks>
    /// Backup oder Sicherheitskopie vor einem Restore; Namensregeln wie beim Download.
    /// 204 bei Erfolg, 404 wenn es sie nicht gibt.
    /// </remarks>
    /// <param name="name">Dateiname der Sicherung.</param>
    [HttpDelete("backups/{name}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteBackup(string name, CancellationToken ct)
    {
        if (Unsupported(restore: false) is { } unsupported) return unsupported;
        if (!_backups.IsValidName(name))
            return Fail(StatusCodes.Status400BadRequest, InvalidBackupName, "Ungültiger Backup-Name.");
        if (!await _backups.DeleteAsync(name, User.Identity?.Name, ct))
            return Fail(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "Das Backup wurde nicht gefunden.");
        return NoContent();
    }

    /// <summary>Ersetzt die laufende Datenbank durch einen Snapshot (nur SQLite).</summary>
    /// <remarks>
    /// Ersetzt die laufende DB durch einen hochgeladenen Snapshot (Formularfeld <c>file</c>) ODER durch ein
    /// vorhandenes Backup (Feld <c>backupName</c>, ein Name aus der Liste) - als <c>multipart/form-data</c>. Erlaubt in
    /// Development und sonst nur mit <c>Backup:AllowRestore=true</c>. Das Feld <c>confirm</c> muss <c>RESTORE</c> enthalten;
    /// für ein vorhandenes Backup ist es Pflicht, beim Upload wird es geprüft, sobald es gesendet wird (die Oberfläche
    /// sendet es immer; ein Upload ohne das Feld bleibt aus Gründen der Rückwärtskompatibilität zu WP09 möglich).
    /// Ablauf: Datei in eine Temp-Datei neben der DB schreiben und prüfen (SQLite-Header, PRAGMA integrity_check, Pflichttabellen,
    /// Schemastand nicht neuer als die App) — erst dann Sicherheitskopie der Live-DB (<c>*-before-restore-*</c>), alle gepoolten
    /// Verbindungen schließen, alte -wal/-shm entfernen und die Datei atomar austauschen. Eine ungültige Datei berührt die
    /// Live-DB nie. Der API-Server <b>muss anschließend neu gestartet werden</b> — die EF-Connection lässt sich zur Laufzeit
    /// nicht auswechseln (Antwort: <c>restartRequired</c>). Gesperrt (403, <c>restore_disabled</c>), wenn der Restore in dieser
    /// Umgebung nicht erlaubt ist; 409 (<c>database_in_use</c>), wenn die Datenbankdatei noch in Benutzung ist.
    /// </remarks>
    /// <param name="file">Hochgeladener Snapshot (Formularfeld <c>file</c>).</param>
    /// <param name="backupName">Name eines vorhandenen Backups (Formularfeld <c>backupName</c>) statt eines Uploads.</param>
    /// <param name="confirm">Bestätigung: der Text <c>RESTORE</c>; Pflicht bei <c>backupName</c>.</param>
    [HttpPost("restore")]
    [RequestSizeLimit(MaxRestoreBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRestoreBytes)]
    [ProducesResponseType<RestoreResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    public async Task<IActionResult> Restore(
        IFormFile? file, CancellationToken ct, [FromForm] string? backupName = null, [FromForm] string? confirm = null)
    {
        if (!_env.IsDevelopment() && !_backups.Options.AllowRestore)
            return Fail(StatusCodes.Status403Forbidden, "restore_disabled",
                $"Restore ist in dieser Umgebung gesperrt: nur in Development oder mit {BackupOptions.AllowRestoreKey}=true. " +
                "Alternativ offline: Dienst stoppen, DB-Datei samt -wal/-shm ersetzen, Dienst starten.");
        if (Unsupported(restore: true) is { } unsupported) return unsupported;

        var hasFile = file is { Length: > 0 };
        var name = backupName?.Trim();
        var fromBackup = !string.IsNullOrEmpty(name);
        if (hasFile && fromBackup)
            return Fail(StatusCodes.Status400BadRequest, ProblemCodes.ValidationFailed,
                "Entweder eine Datei hochladen oder ein vorhandenes Backup auswählen, nicht beides.");
        if (!hasFile && !fromBackup)
            return Fail(StatusCodes.Status400BadRequest, ProblemCodes.ValidationFailed, "Keine Datei hochgeladen und kein Backup ausgewählt.");
        if ((fromBackup || confirm is not null) && !string.Equals(confirm?.Trim(), RestoreConfirmation, StringComparison.Ordinal))
            return Fail(StatusCodes.Status400BadRequest, ConfirmationRequired,
                $"Zur Bestätigung muss das Feld confirm den Wert {RestoreConfirmation} enthalten.");
        if (hasFile && file!.Length > MaxRestoreBytes)
            return Fail(StatusCodes.Status413PayloadTooLarge, ProblemCodes.PayloadTooLarge,
                $"Die Datei ist zu groß (Maximum {MaxRestoreBytes / (1024 * 1024)} MB).");
        if (fromBackup)
        {
            if (!_backups.TryResolvePath(name, out var path))
                return Fail(StatusCodes.Status400BadRequest, InvalidBackupName, "Ungültiger Backup-Name.");
            if (!System.IO.File.Exists(path))
                return Fail(StatusCodes.Status404NotFound, ProblemCodes.NotFound, "Das Backup wurde nicht gefunden.");
        }

        var user = User.Identity?.Name ?? "unbekannt";
        RestoreOutcome outcome;
        try
        {
            if (fromBackup)
            {
                outcome = await _backups.RestoreFromBackupAsync(_db, name!, user, ct);
            }
            else
            {
                await using var upload = file!.OpenReadStream();
                outcome = await _backups.RestoreAsync(_db, upload, user, ct);
            }
        }
        catch (DatabaseFileInUseException ex)
        {
            _logger.LogWarning(ex, "Restore abgebrochen: Die Datenbankdatei ist in Benutzung (angefordert von {User}).", user);
            // Übersetzung der IO-Ausnahme in einen Konflikt (begründeter catch): die Antwort baut die zentrale Fehlerabbildung
            // (409 mit Code database_in_use), nicht dieser Controller.
            throw new InvalidOperationException(
                "Die Datenbankdatei ist noch in Benutzung; der Restore wurde nicht durchgeführt. " +
                "Bitte in einem ruhigen Moment wiederholen oder offline ersetzen (Dienst stoppen, Datei tauschen, starten).", ex)
            {
                Data = { ["code"] = "database_in_use" },
            };
        }

        if (!outcome.Success)
            return Fail(StatusCodes.Status400BadRequest, outcome.ErrorCode ?? "invalid_backup_file", outcome.Error ?? "Die Datei ist keine gültige Sicherung.");

        return Ok(new RestoreResponse(
            "Restore erfolgreich. Bitte den API-Server jetzt neu starten — die DB-Connection muss neu aufgebaut werden.",
            RestartRequired: true,
            SizeBytes: outcome.SizeBytes,
            SafetyBackup: outcome.SafetyBackupName));
    }

    /// <summary>
    /// Antwort, wenn Backup/Restore hier nicht möglich ist (MySQL: mysqldump; In-Memory-Datenbank: keine Datei), sonst null.
    /// </summary>
    private ObjectResult? Unsupported(bool restore)
    {
        if (!_settings.IsSqlite)
            return Fail(StatusCodes.Status400BadRequest, SqliteOnly, restore
                ? "Restore-Endpoint ist aktuell nur für SQLite implementiert. Für MySQL den Dump mit dem mysql-Client einspielen."
                : "Backup-Endpoint ist aktuell nur für SQLite implementiert. Nutze mysqldump für MySQL.");
        if (!_backups.IsSupported)
            return Fail(StatusCodes.Status400BadRequest, NotFileBased, restore
                ? "Die Datenbank liegt nicht in einer Datei (In-Memory), ein Restore ist nicht möglich."
                : "Die Datenbank liegt nicht in einer Datei (In-Memory), ein Backup ist nicht möglich.");
        return null;
    }

    /// <summary>
    /// Fehlerantwort mit festem Status im einheitlichen Format (ProblemDetails samt code, correlationId und error - wie beim
    /// globalen Fehler-Handler). Ohne HttpContext (Action direkt aufgerufen, etwa im Unit-Test) gibt es keine
    /// Korrelations-ID: dann nur die Grundfelder.
    /// </summary>
    private ObjectResult Fail(int status, string code, string detail)
    {
        var problem = HttpContext is null
            ? new ProblemDetails
            {
                Status = status,
                Title = ProblemCatalog.Title(status),
                Detail = detail,
                Extensions = { ["code"] = code, ["error"] = detail },
            }
            : Problems.Create(HttpContext, status, code, detail);
        ObjectResult result = status == StatusCodes.Status400BadRequest
            ? new BadRequestObjectResult(problem)
            : new ObjectResult(problem) { StatusCode = status };
        result.ContentTypes.Add(Problems.ContentType);
        return result;
    }
}
