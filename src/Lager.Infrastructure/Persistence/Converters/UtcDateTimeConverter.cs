using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Lager.Infrastructure.Persistence.Converters;

/// <summary>
/// Alle Zeitstempel im System sind UTC (die Domain setzt <c>DateTime.UtcNow</c>). SQLite und MySQL liefern
/// gelesene <c>DateTime</c>-Werte aber mit <c>Kind=Unspecified</c>; System.Text.Json schreibt die dann ohne
/// Offset, und JavaScript liest so ein Datum als Ortszeit (Anzeige um den UTC-Offset verschoben).
/// Dieser Converter liest jeden Wert als <c>Kind=Utc</c> (JSON-Ausgabe mit "Z") und schreibt ihn als UTC.
/// Die gespeicherten Werte bleiben unverändert (waren schon UTC), ein Umbau der Datenbank ist nicht nötig.
/// Registriert wird er zentral in <c>LagerDbContext.ConfigureConventions</c> für alle DateTime-Properties.
/// </summary>
public sealed class UtcDateTimeConverter : ValueConverter<DateTime, DateTime>
{
    public UtcDateTimeConverter()
        : base(v => UtcDateTimeRules.ToUtc(v), v => DateTime.SpecifyKind(v, DateTimeKind.Utc))
    {
    }
}

/// <summary>Wie <see cref="UtcDateTimeConverter"/>, für <c>DateTime?</c> (NULL bleibt NULL).</summary>
public sealed class UtcNullableDateTimeConverter : ValueConverter<DateTime?, DateTime?>
{
    public UtcNullableDateTimeConverter()
        : base(v => UtcDateTimeRules.ToUtc(v), v => UtcDateTimeRules.AsUtc(v))
    {
    }
}

/// <summary>Die Umrechnungsregeln der Converter (öffentlich, damit sie als Unit-Test ohne DbContext prüfbar sind).</summary>
public static class UtcDateTimeRules
{
    /// <summary>
    /// Schreibrichtung: UTC bleibt UTC, Ortszeit wird nach UTC umgerechnet, Werte ohne Zonenangabe
    /// (<c>Unspecified</c>, z. B. ein Datum aus dem Request-JSON) gelten als UTC.
    /// </summary>
    public static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    public static DateTime? ToUtc(DateTime? value) => value.HasValue ? ToUtc(value.Value) : null;

    /// <summary>Leserichtung: der gespeicherte Wert ist UTC, nur die Kennzeichnung fehlt.</summary>
    public static DateTime? AsUtc(DateTime? value) => value.HasValue ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null;
}
