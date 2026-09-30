namespace Lager.Application.ImportExport;

/// <summary>
/// Schreibweise einer CSV-Datei: das Trennzeichen und die davon abhängige Schreibweise der Dezimalzahlen.
/// Standard ist das Semikolon mit Dezimalkomma (so erwartet es Excel mit deutscher Einstellung); das Komma als Trennzeichen
/// schreibt Dezimalzahlen mit Punkt. Beim Einlesen werden beide Dezimalschreibweisen angenommen (<see cref="CsvText.TryParseDecimal"/>).
/// </summary>
public sealed record CsvFormat(char Delimiter)
{
    public static readonly CsvFormat Semicolon = new(';');
    public static readonly CsvFormat Comma = new(',');

    /// <summary>Semikolon.</summary>
    public static CsvFormat Default => Semicolon;

    /// <summary>Dezimaltrennzeichen beim Schreiben: Komma zum Semikolon (Excel-DE), sonst Punkt.</summary>
    public char DecimalSeparator => Delimiter == ';' ? ',' : '.';

    /// <summary>Kurzname für Anfragen und Antworten: <c>semicolon</c> oder <c>comma</c>.</summary>
    public string Name => Delimiter == ';' ? "semicolon" : "comma";

    /// <summary>
    /// Liest die Angabe aus dem Query-String: leer = Semikolon; erlaubt sind <c>semicolon</c>/<c>semikolon</c>/<c>;</c> und
    /// <c>comma</c>/<c>komma</c>/<c>,</c> (ohne Beachtung der Schreibweise). Alles andere ist ungültig.
    /// </summary>
    public static bool TryParse(string? text, out CsvFormat format)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value) || value.Equals("semicolon", StringComparison.OrdinalIgnoreCase)
            || value.Equals("semikolon", StringComparison.OrdinalIgnoreCase) || value == ";")
        {
            format = Semicolon;
            return true;
        }
        if (value.Equals("comma", StringComparison.OrdinalIgnoreCase) || value.Equals("komma", StringComparison.OrdinalIgnoreCase) || value == ",")
        {
            format = Comma;
            return true;
        }
        format = Semicolon;
        return false;
    }

    /// <summary>Wie <see cref="TryParse"/>, wirft bei einem ungültigen Wert <see cref="ArgumentException"/> (Code <c>invalid_delimiter</c>).</summary>
    public static CsvFormat Parse(string? text)
    {
        if (TryParse(text, out var format)) return format;
        var ex = new ArgumentException("Das Trennzeichen muss 'semicolon' (;) oder 'comma' (,) sein.", nameof(text));
        ex.Data["code"] = "invalid_delimiter";
        throw ex;
    }
}
