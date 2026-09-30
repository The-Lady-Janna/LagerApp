using System.Globalization;

namespace Lager.Infrastructure.Backup;

/// <summary>
/// Der Zeitplan des Backup-Jobs: eine tägliche Uhrzeit "HH:mm" in UTC. Reine Funktionen ohne Uhr und Dateisystem
/// (der Hintergrunddienst reicht die aktuelle Zeit herein), damit sich die Berechnung des nächsten Laufs einfach testen lässt.
/// UTC statt Ortszeit: keine Sommerzeit-Lücken ("02:30" gibt es dann nicht), im Docker-Container ohnehin die Vorgabe.
/// </summary>
public static class BackupSchedule
{
    private static readonly string[] Formats = { "HH:mm", "H:mm" };

    /// <summary>Liest "HH:mm" (auch "H:mm"). Leer, nur Leerraum oder eine ungültige Zeit ergeben false.</summary>
    public static bool TryParse(string? schedule, out TimeOnly time)
    {
        time = default;
        return !string.IsNullOrWhiteSpace(schedule)
               && TimeOnly.TryParseExact(schedule.Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time);
    }

    /// <summary>
    /// Der nächste Lauf strikt NACH <paramref name="nowUtc"/>: heute zur Startzeit, wenn sie noch bevorsteht, sonst morgen.
    /// (Strikt, damit ein Lauf, der genau zur Startzeit fertig wird, nicht sofort noch einmal startet.)
    /// </summary>
    public static DateTimeOffset NextRun(DateTimeOffset nowUtc, TimeOnly at)
    {
        var utc = nowUtc.ToUniversalTime();
        var today = new DateTimeOffset(DateOnly.FromDateTime(utc.UtcDateTime).ToDateTime(at), TimeSpan.Zero);
        return today > utc ? today : today.AddDays(1);
    }
}
