namespace Lager.Infrastructure.Backup;

/// <summary>Art einer Sicherungsdatei (Wert im Dateinamen und im API-Feld <c>kind</c>).</summary>
public static class BackupKinds
{
    /// <summary>Ein Backup (manuell oder per Zeitplan): <c>lager-backup-…</c>. Nur diese Dateien unterliegen der Aufbewahrung.</summary>
    public const string Backup = "backup";

    /// <summary>Die Sicherheitskopie der Live-DB vor einem Restore: <c>lager-before-restore-…</c>. Wird nie automatisch gelöscht.</summary>
    public const string BeforeRestore = "before-restore";
}

/// <summary>Eine Sicherungsdatei im Backup-Verzeichnis. Nur der Dateiname, nie ein Serverpfad.</summary>
/// <param name="Name">Dateiname, z. B. <c>lager-backup-20260930-020000-000.db</c>.</param>
/// <param name="SizeBytes">Größe in Bytes.</param>
/// <param name="CreatedUtc">Zeitpunkt der Sicherung (UTC, aus dem Dateinamen; sonst die Änderungszeit der Datei).</param>
/// <param name="Kind">Siehe <see cref="BackupKinds"/>.</param>
public sealed record BackupFileInfo(string Name, long SizeBytes, DateTime CreatedUtc, string Kind);

/// <summary>Ergebnis eines Restores. Eine abgelehnte Datei ist kein Fehler des Servers, sondern ein Ergebnis mit Grund.</summary>
/// <param name="Success">true = die Live-Datenbank wurde ersetzt (Neustart erforderlich).</param>
/// <param name="ErrorCode">Bei einer Ablehnung der Fehlercode (<c>invalid_backup_file</c>), sonst null.</param>
/// <param name="Error">Bei einer Ablehnung der Grund für Menschen, sonst null.</param>
/// <param name="SafetyBackupName">Dateiname der Sicherheitskopie der Live-DB vor dem Austausch.</param>
/// <param name="SizeBytes">Größe der neuen Live-Datenbank.</param>
public sealed record RestoreOutcome(bool Success, string? ErrorCode, string? Error, string? SafetyBackupName, long SizeBytes)
{
    public static RestoreOutcome Rejected(string code, string error) => new(false, code, error, null, 0);
}

/// <summary>
/// Die Live-Datenbankdatei ließ sich nicht ersetzen, weil sie noch in Benutzung ist (Windows verweigert das Ersetzen einer
/// offenen Datei). Eigene Ausnahme, damit ein Schreibfehler an anderer Stelle (Platte voll beim Upload) nicht als
/// "in Benutzung" gemeldet wird; der Aufrufer übersetzt sie in einen Konflikt.
/// </summary>
public sealed class DatabaseFileInUseException : IOException
{
    public DatabaseFileInUseException(string message, Exception innerException) : base(message, innerException) { }
}
