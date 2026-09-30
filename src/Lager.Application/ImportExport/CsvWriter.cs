using System.Globalization;
using System.Text;

namespace Lager.Application.ImportExport;

/// <summary>
/// Eine Zelle der Ausgabe. Es gibt drei Arten, und nur die erste wird gegen Formel-Injection entschärft:
///  - <b>Text</b> (<see cref="string"/>): stammt meist von Benutzern (Name, Beschreibung, Lagerplatz, Audit-Benutzer) und wird
///    bei Bedarf mit einem Apostroph entschärft (<see cref="CsvText.Neutralize"/>).
///  - <b>Zahl/Datum</b> (<see cref="Number"/>, <see cref="Date"/>, implizit aus <see cref="int"/>/<see cref="long"/>): vom Programm
///    formatiert und nie vom Benutzer bestimmt. Ein Minuszeichen (Bewegung -5) bleibt deshalb unangetastet und die Zelle eine Zahl.
///  - <b>Dezimalzahl</b> (<see cref="Decimal"/>): wie eine Zahl; das Dezimaltrennzeichen richtet sich nach dem Trennzeichen des Formats.
/// </summary>
public readonly struct CsvCell
{
    private enum Kind { Text, Verbatim, Decimal }

    private readonly string? _value;
    private readonly Kind _kind;

    private CsvCell(string? value, Kind kind)
    {
        _value = value;
        _kind = kind;
    }

    public static implicit operator CsvCell(string? text) => new(text, Kind.Text);
    public static implicit operator CsvCell(int number) => Number(number);
    public static implicit operator CsvCell(long number) => Number(number);
    public static implicit operator CsvCell(bool flag) => new(flag ? "true" : "false", Kind.Verbatim);

    public static CsvCell Number(long number) => new(number.ToString(CultureInfo.InvariantCulture), Kind.Verbatim);

    /// <summary>Dezimalzahl mit fester Nachkommastellenzahl (Preis: 2).</summary>
    public static CsvCell Decimal(decimal number, int scale) =>
        new(number.ToString("F" + scale, CultureInfo.InvariantCulture), Kind.Decimal);

    /// <summary>Datum bzw. Zeitpunkt (UTC); leer bei <c>null</c>. Siehe <see cref="CsvText.FormatDate"/>.</summary>
    public static CsvCell Date(DateTime? value) => value is null ? default : new(CsvText.FormatDate(value.Value), Kind.Verbatim);

    /// <summary>Zeitpunkt mit Sekunden (Bewegungen, Audit).</summary>
    public static CsvCell Timestamp(DateTime value) => new(CsvText.FormatTimestamp(value), Kind.Verbatim);

    /// <summary>Ein vom Programm erzeugter Text (Enum-Name, Liste von SKUs), der nicht entschärft wird.</summary>
    public static CsvCell Verbatim(string? text) => new(text, Kind.Verbatim);

    internal string Render(CsvFormat format)
    {
        if (_value is null) return string.Empty;
        return _kind switch
        {
            Kind.Text => CsvText.Neutralize(_value),
            Kind.Decimal => format.DecimalSeparator == '.' ? _value : _value.Replace('.', format.DecimalSeparator),
            _ => _value,
        };
    }
}

/// <summary>
/// RFC-4180-Schreiber: Zeilenende CRLF, ein Feld kommt in Anführungszeichen, wenn es Trennzeichen, Anführungszeichen,
/// Zeilenumbruch oder führenden/nachgestellten Leerraum enthält (Anführungszeichen werden verdoppelt). Jede Zeile wird
/// zuerst zusammengesetzt und mit EINEM Schreibaufruf ausgegeben: die Ausgabe ist beliebig lang streambar, ohne die Tabelle
/// im Speicher zu halten, und kommt ohne synchrones Schreiben aus (Kestrel verbietet es).
/// Das BOM (<see cref="Utf8Preamble"/>) schreibt der Aufrufer als erste Bytes des Streams.
/// </summary>
public sealed class CsvWriter
{
    /// <summary>UTF-8-BOM: Excel erkennt daran die Kodierung (ohne BOM läse es Umlaute als Windows-1252).</summary>
    public static readonly byte[] Utf8Preamble = { 0xEF, 0xBB, 0xBF };

    public const string LineEnd = "\r\n";

    private readonly TextWriter _writer;

    public CsvWriter(TextWriter writer, CsvFormat format)
    {
        _writer = writer;
        Format = format;
    }

    public CsvFormat Format { get; }

    /// <summary>Schreibt eine Zeile aus Kopfzeilen-Namen (Text, nie entschärft: es sind feste Spaltennamen).</summary>
    public Task WriteHeaderAsync(IReadOnlyList<string> names) =>
        _writer.WriteAsync(FormatRow(Format, names.Select(CsvCell.Verbatim).ToArray()));

    public Task WriteRowAsync(params CsvCell[] cells) => _writer.WriteAsync(FormatRow(Format, cells));

    /// <summary>Die fertige Zeile samt Zeilenende.</summary>
    public static string FormatRow(CsvFormat format, IReadOnlyList<CsvCell> cells)
    {
        var line = new StringBuilder();
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0) line.Append(format.Delimiter);
            AppendField(line, cells[i].Render(format), format.Delimiter);
        }
        return line.Append(LineEnd).ToString();
    }

    private static void AppendField(StringBuilder line, string value, char delimiter)
    {
        var quote = value.Length > 0 && (value.Contains(delimiter) || value.Contains('"') || value.Contains('\r') || value.Contains('\n')
            || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]));
        if (!quote)
        {
            line.Append(value);
            return;
        }
        line.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
    }
}
