using System.Globalization;
using System.Text;

namespace Lager.Application.ImportExport;

/// <summary>
/// Text- und Zahlenregeln der CSV-Dateien, ohne Bezug zu einem bestimmten Import:
///  - <see cref="Neutralize"/>/<see cref="Unescape"/>: Schutz gegen Formel-Injection. Eine Tabellenkalkulation wertet Zellen, die
///    mit <c>=</c>, <c>+</c>, <c>-</c>, <c>@</c>, Tab oder CR beginnen, als Formel aus (<c>=HYPERLINK(...)</c>, DDE). Der Export
///    setzt solchen Texten ein Apostroph voran; der Import nimmt genau dieses Apostroph wieder weg, damit Export und Import
///    verlustfrei zueinander passen.
///  - <see cref="Decode"/>: UTF-8 (mit oder ohne BOM), UTF-16 mit BOM, sonst Windows-1252 (so speichert Excel-DE "CSV").
///  - <see cref="TryParseDecimal"/>/<see cref="TryParseInt"/>/<see cref="TryParseDate"/>/<see cref="TryParseBool"/>: tolerant
///    gegenüber deutscher Schreibweise (Dezimalkomma, Tausenderpunkt, 30.09.2026, "ja").
/// </summary>
public static class CsvText
{
    // ---- Formel-Injection -----------------------------------------------------------------------------------

    /// <summary>Beginnt der Text mit einem Zeichen, mit dem eine Tabellenkalkulation eine Formel einleitet?</summary>
    public static bool IsFormulaTrigger(char c) => c is '=' or '+' or '-' or '@' or '\t' or '\r';

    /// <summary>
    /// Entschärft einen Text für die Ausgabe: beginnt er (nach evtl. schon vorhandenen Apostrophen) mit einem Formelzeichen,
    /// kommt ein Apostroph davor. Die zusätzlichen Apostrophe vor dem Formelzeichen sind nötig, damit <see cref="Unescape"/>
    /// den Originaltext eindeutig zurückbekommt (aus <c>'=x</c> wird <c>''=x</c>, nicht <c>'=x</c>).
    /// </summary>
    public static string Neutralize(string text) => NeedsPrefix(text) ? "'" + text : text;

    /// <summary>Gegenstück zu <see cref="Neutralize"/>: nimmt das vorangestellte Apostroph wieder weg.</summary>
    public static string Unescape(string text) =>
        text.Length > 1 && text[0] == '\'' && NeedsPrefix(text.AsSpan(1)) ? text[1..] : text;

    private static bool NeedsPrefix(ReadOnlySpan<char> text)
    {
        var index = 0;
        while (index < text.Length && text[index] == '\'') index++;
        return index < text.Length && IsFormulaTrigger(text[index]);
    }

    private static bool NeedsPrefix(string text) => NeedsPrefix(text.AsSpan());

    // ---- Kodierung ------------------------------------------------------------------------------------------

    private static readonly Lazy<Encoding> Windows1252 = new(() =>
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1252);
        }
        catch (Exception)
        {
            return Encoding.Latin1;
        }
    });

    /// <summary>
    /// Wandelt die Bytes einer hochgeladenen Datei in Text: BOM (UTF-8, UTF-16 LE/BE) bestimmt die Kodierung; ohne BOM gilt UTF-8,
    /// und nur wenn die Bytes kein gültiges UTF-8 sind, Windows-1252 (Excel-DE "CSV (Trennzeichen-getrennt)" speichert so).
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes[3..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);

        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Windows1252.Value.GetString(bytes);
        }
    }

    // ---- Zahlen ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Liest eine Dezimalzahl in deutscher oder englischer Schreibweise: "12,50", "12.50", "1.234,56", "1,234.56", "1 234,5",
    /// "12,50 €". Kommen beide Zeichen vor, ist das letzte das Dezimaltrennzeichen und das andere der Tausendertrenner.
    /// Ein einzelnes Komma ist immer ein Dezimalkomma; ein einzelner Punkt ein Dezimalpunkt, außer er steht genau vor drei
    /// Ziffern hinter einer 1- bis 3-stelligen Zahl ("1.234" = 1234). Mehrfach vorkommende Zeichen sind Tausendertrenner und
    /// müssen Dreiergruppen bilden. Kein Exponent, keine Buchstaben.
    /// </summary>
    public static bool TryParseDecimal(string? text, out decimal value)
    {
        value = 0;
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s)) return false;

        s = s.Replace("€", "").Replace("EUR", "", StringComparison.OrdinalIgnoreCase);
        var compact = new StringBuilder(s.Length);
        foreach (var c in s)
            if (!char.IsWhiteSpace(c) && c != '\u00A0' && c != '\u202F') compact.Append(c);
        s = compact.ToString();
        if (s.Length == 0) return false;

        var sign = string.Empty;
        if (s[0] is '-' or '+')
        {
            sign = s[0] == '-' ? "-" : string.Empty;
            s = s[1..];
        }
        if (s.Length == 0 || !s.All(c => char.IsAsciiDigit(c) || c is '.' or ',')) return false;

        var commas = s.Count(c => c == ',');
        var dots = s.Count(c => c == '.');
        string normalized;

        if (commas > 0 && dots > 0)
        {
            var decimalSeparator = s.LastIndexOf(',') > s.LastIndexOf('.') ? ',' : '.';
            var thousandsSeparator = decimalSeparator == ',' ? '.' : ',';
            if (s.Count(c => c == decimalSeparator) != 1) return false;
            var whole = s[..s.IndexOf(decimalSeparator)];
            if (!IsGrouped(whole, thousandsSeparator)) return false;
            normalized = s.Replace(thousandsSeparator.ToString(), "").Replace(decimalSeparator, '.');
        }
        else if (commas > 0)
        {
            if (commas == 1) normalized = s.Replace(',', '.');
            else if (IsGrouped(s, ',')) normalized = s.Replace(",", "");
            else return false;
        }
        else if (dots > 1)
        {
            if (!IsGrouped(s, '.')) return false;
            normalized = s.Replace(".", "");
        }
        else if (dots == 1 && IsGrouped(s, '.') && s.Length >= 5 && s[0] != '0')
        {
            normalized = s.Replace(".", "");
        }
        else
        {
            normalized = s;
        }

        if (!decimal.TryParse(sign + normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out value))
            return false;
        return true;
    }

    /// <summary>"1.234.567" (Trenner '.'): eine 1- bis 3-stellige erste Gruppe, danach nur Dreiergruppen aus Ziffern.</summary>
    private static bool IsGrouped(string text, char separator)
    {
        var groups = text.Split(separator);
        if (groups.Length < 2) return false;
        if (groups[0].Length is < 1 or > 3 || !groups[0].All(char.IsAsciiDigit)) return false;
        return groups.Skip(1).All(g => g.Length == 3 && g.All(char.IsAsciiDigit));
    }

    /// <summary>Ganze Zahl im int-Bereich; eine Dezimalschreibweise wie "5,00" oder "1.000" ist erlaubt, solange der Wert ganzzahlig ist.</summary>
    public static bool TryParseInt(string? text, out int value)
    {
        value = 0;
        if (!TryParseDecimal(text, out var number)) return false;
        if (number != decimal.Truncate(number) || number < int.MinValue || number > int.MaxValue) return false;
        value = (int)number;
        return true;
    }

    // ---- Datum und Wahrheitswert ----------------------------------------------------------------------------

    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ssK", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mmK", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm",
        "dd.MM.yyyy", "d.M.yyyy", "dd.MM.yyyy HH:mm", "dd.MM.yyyy HH:mm:ss", "d.M.yyyy H:mm", "d.M.yyyy H:mm:ss",
    };

    /// <summary>
    /// Datum oder Zeitpunkt, immer als UTC: "2026-09-30", "30.09.2026", "2026-09-30T14:30:00Z", "30.09.2026 14:30". Ohne
    /// Zeitangabe gilt 00:00 UTC; ohne Zonenangabe gilt UTC.
    /// </summary>
    public static bool TryParseDate(string? text, out DateTime value)
    {
        value = default;
        var s = text?.Trim();
        if (string.IsNullOrEmpty(s)) return false;
        return DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }

    /// <summary>true/false, 1/0, ja/nein, yes/no, wahr/falsch, x (ohne Beachtung der Schreibweise).</summary>
    public static bool TryParseBool(string? text, out bool value)
    {
        value = false;
        switch (text?.Trim().ToLowerInvariant())
        {
            case "true" or "1" or "ja" or "yes" or "wahr" or "x" or "y" or "j":
                value = true;
                return true;
            case "false" or "0" or "nein" or "no" or "falsch" or "n":
                return true;
            default:
                return false;
        }
    }

    // ---- Ausgabe --------------------------------------------------------------------------------------------

    /// <summary>Datum für die Ausgabe: "2026-09-30" bei Mitternacht, sonst "2026-09-30T14:30:00Z" (immer UTC).</summary>
    public static string FormatDate(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        return utc.TimeOfDay == TimeSpan.Zero
            ? utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>Zeitpunkt für die Ausgabe mit Sekunden: "2026-09-30T14:30:00Z".</summary>
    public static string FormatTimestamp(DateTime value)
    {
        var utc = value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
        return utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }
}
