using System.Globalization;

namespace Lager.Application.ImportExport;

/// <summary>
/// Der Zeitraum-Filter der Exporte von Bewegungen und Audit: <see cref="FromUtc"/> inklusive, <see cref="ToExclusiveUtc"/>
/// exklusive (halboffen, damit sich Zeiträume lückenlos aneinanderreihen lassen). Angegeben wird er als Datum
/// ("2026-09-01", "01.09.2026") oder Zeitpunkt ("2026-09-01T08:00:00Z"), immer in UTC. Ein "bis" OHNE Uhrzeit meint den ganzen Tag
/// (<c>to=2026-09-30</c> schließt den 30.09. ein), ein "bis" MIT Uhrzeit genau diesen Zeitpunkt (exklusive).
/// </summary>
public sealed record ExportRange(DateTime? FromUtc, DateTime? ToExclusiveUtc)
{
    public static readonly ExportRange Unbounded = new(null, null);

    private static readonly string[] DateOnlyFormats = { "yyyy-MM-dd", "dd.MM.yyyy", "d.M.yyyy" };

    /// <summary>
    /// Liest die beiden Angaben (leer = offen). Bei einem Fehler ist <paramref name="error"/> die deutsche Meldung; der Zeitraum
    /// ist dann <see cref="Unbounded"/>. Ein leerer oder verkehrter Zeitraum (von nicht vor bis) ist ebenfalls ein Fehler.
    /// </summary>
    public static bool TryParse(string? from, string? to, out ExportRange range, out string? error)
    {
        range = Unbounded;
        error = null;

        DateTime? fromUtc = null;
        DateTime? toExclusive = null;

        if (!string.IsNullOrWhiteSpace(from))
        {
            if (!CsvText.TryParseDate(from, out var value))
            {
                error = $"von: '{Shorten(from)}' ist kein Datum (erlaubt: 2026-09-30, 30.09.2026 oder 2026-09-30T14:30:00Z).";
                return false;
            }
            fromUtc = value;
        }

        if (!string.IsNullOrWhiteSpace(to))
        {
            if (!CsvText.TryParseDate(to, out var value))
            {
                error = $"bis: '{Shorten(to)}' ist kein Datum (erlaubt: 2026-09-30, 30.09.2026 oder 2026-09-30T14:30:00Z).";
                return false;
            }
            toExclusive = IsDateOnly(to) ? value.AddDays(1) : value;
        }

        if (fromUtc is not null && toExclusive is not null && fromUtc >= toExclusive)
        {
            error = "Der Zeitraum ist leer: 'von' muss vor 'bis' liegen.";
            return false;
        }

        range = new ExportRange(fromUtc, toExclusive);
        return true;
    }

    /// <summary>Wie <see cref="TryParse"/>, wirft bei einem Fehler <see cref="ArgumentException"/> (Code <c>invalid_range</c>).</summary>
    public static ExportRange Parse(string? from, string? to)
    {
        if (TryParse(from, to, out var range, out var error)) return range;
        var ex = new ArgumentException(error);
        ex.Data["code"] = "invalid_range";
        throw ex;
    }

    private static bool IsDateOnly(string text) =>
        DateTime.TryParseExact(text.Trim(), DateOnlyFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    private static string Shorten(string text) => text.Length <= 40 ? text : text[..37] + "...";
}
