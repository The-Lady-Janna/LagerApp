namespace Lager.Infrastructure.Backup;

/// <summary>
/// Einstellungen des Abschnitts <c>Backup</c> (appsettings, Umgebungsvariablen <c>Backup__…</c>). Alle Werte haben einen
/// Standard im Code; die appsettings.json muss nichts enthalten.
/// </summary>
public sealed class BackupOptions
{
    /// <summary>Name des Konfigurationsabschnitts.</summary>
    public const string SectionName = "Backup";

    // Die vollständigen Konfigurationsschlüssel (Umgebungsvariablen: Backup__Directory usw.), wie sie in Meldungen und Doku stehen.
    public const string DirectoryKey = "Backup:Directory";
    public const string ScheduleKey = "Backup:Schedule";
    public const string RetentionCountKey = "Backup:RetentionCount";
    public const string AllowRestoreKey = "Backup:AllowRestore";

    /// <summary>Standard für <see cref="RetentionCount"/>: die letzten 14 Backups bleiben erhalten.</summary>
    public const int DefaultRetentionCount = 14;

    /// <summary>
    /// Zielverzeichnis für Backups und die Sicherheitskopie vor einem Restore (Docker: ein Volume). Ein relativer Pfad gilt
    /// relativ zum Content-Root. Leer = das Verzeichnis der SQLite-Datei (so, wie es schon vor der Oberfläche war).
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>
    /// Tägliche Startzeit des Zeitplans als "HH:mm" in <b>UTC</b> (z. B. "02:00"). Leer = kein Zeitplan (nur manuelle
    /// Backups). Ein ungültiger Wert wird geloggt und wie "aus" behandelt.
    /// </summary>
    public string? Schedule { get; set; }

    /// <summary>
    /// Wie viele Backups (Dateien <c>lager-backup-…</c>) nach jedem neuen Backup erhalten bleiben; ältere werden gelöscht.
    /// 0 oder weniger = nie automatisch löschen. Die Sicherheitskopien <c>lager-before-restore-…</c> zählen nicht mit und
    /// werden nie automatisch gelöscht.
    /// </summary>
    public int RetentionCount { get; set; } = DefaultRetentionCount;

    /// <summary>
    /// Erlaubt den Restore auch außerhalb von <c>Development</c> (Schlüssel <c>Backup:AllowRestore</c>, seit WP09).
    /// Ohne dieses Flag ist der Restore in Produktion gesperrt.
    /// </summary>
    public bool AllowRestore { get; set; }
}
