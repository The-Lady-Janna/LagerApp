using System.Text;

namespace Lager.Application.ImportExport;

/// <summary>Eine Datenzeile einer <see cref="CsvTable"/>: Zeilennummer in der Datei (1 = Kopfzeile) und die Felder.</summary>
public sealed record CsvRow(int Line, IReadOnlyList<string> Fields);

/// <summary>
/// Eine eingelesene CSV-Datei mit Kopfzeile: Spalten werden über ihren Namen angesprochen, nicht über die Position
/// (Excel-Nutzer vertauschen und ergänzen Spalten). Der Vergleich der Namen ignoriert Groß-/Kleinschreibung, Leerzeichen,
/// Unterstriche und Bindestriche ("Purchase Price" = "purchase_price" = "PurchasePrice").
/// Die Tabelle erzwingt die Grenzen (<see cref="ImportLimits"/>): zu viele Zeilen sind ein Fehler (400 <c>import_too_many_rows</c>).
/// Völlig leere Zeilen (";;;", Excel hängt sie gern an) zählen nicht.
/// </summary>
public sealed class CsvTable
{
    private readonly Dictionary<string, int> _columns = new(StringComparer.Ordinal);

    private CsvTable(IReadOnlyList<string> header, IReadOnlyList<CsvRow> rows)
    {
        Header = header;
        Rows = rows;
        for (var i = 0; i < header.Count; i++)
        {
            var key = Canonical(header[i]);
            if (key.Length == 0) continue;
            if (!_columns.TryAdd(key, i))
                throw Problem("import_duplicate_column", $"Die Spalte '{header[i].Trim()}' kommt mehrfach in der Kopfzeile vor.");
        }
    }

    /// <summary>Spaltennamen der Kopfzeile, wie in der Datei geschrieben (getrimmt).</summary>
    public IReadOnlyList<string> Header { get; }

    public IReadOnlyList<CsvRow> Rows { get; }

    /// <summary>Spaltenname in Vergleichsform: Kleinbuchstaben ohne Leerzeichen, Unterstriche und Bindestriche.</summary>
    public static string Canonical(string? name)
    {
        var builder = new StringBuilder();
        foreach (var c in (name ?? string.Empty).Trim())
            if (!char.IsWhiteSpace(c) && c is not ('_' or '-')) builder.Append(char.ToLowerInvariant(c));
        return builder.ToString();
    }

    /// <summary>Index der Spalte (erster passender Name oder Alias), -1 wenn sie fehlt.</summary>
    public int IndexOf(params string[] names)
    {
        foreach (var name in names)
            if (_columns.TryGetValue(Canonical(name), out var index)) return index;
        return -1;
    }

    /// <summary>Kopfzeilen-Namen, die zu keinem der bekannten Namen gehören (die Datei enthält Spalten, die der Import nicht kennt).</summary>
    public IReadOnlyList<string> UnknownColumns(IEnumerable<string> knownNames)
    {
        var known = knownNames.Select(Canonical).ToHashSet(StringComparer.Ordinal);
        return Header.Where(h => Canonical(h).Length > 0 && !known.Contains(Canonical(h))).Select(h => h.Trim()).ToList();
    }

    /// <summary>
    /// Der Wert der Zelle: getrimmt, ein vorangestelltes Schutz-Apostroph des Exports (<see cref="CsvText.Unescape"/>) entfernt;
    /// leer, wenn die Spalte fehlt (<paramref name="index"/> &lt; 0) oder die Zeile kürzer ist.
    /// </summary>
    public static string Cell(CsvRow row, int index)
    {
        if (index < 0 || index >= row.Fields.Count) return string.Empty;
        return CsvText.Unescape(row.Fields[index].Trim()).Trim();
    }

    /// <summary>
    /// Hat die Zeile mehr Felder als die Kopfzeile Spalten (nicht-leere Zellen rechts davon)? Dann steckt meist ein
    /// Trennzeichen in einem nicht zitierten Text, und alle folgenden Zellen wären verschoben.
    /// </summary>
    public string? OverflowProblem(CsvRow row)
    {
        for (var i = Header.Count; i < row.Fields.Count; i++)
            if (row.Fields[i].Trim().Length > 0)
                return $"Die Zeile hat mehr Spalten ({row.Fields.Count}) als die Kopfzeile ({Header.Count}). " +
                       "Ein Text mit Trennzeichen muss in Anführungszeichen stehen.";
        return null;
    }

    /// <summary>
    /// Liest den Text. Kopfzeile = erste nicht leere Zeile (eine Excel-Zeile "sep=;" davor wird übersprungen). Ohne Kopfzeile,
    /// ohne Datenzeilen oder mit mehr als <paramref name="maxRows"/> Datenzeilen: <see cref="ArgumentException"/> mit Code.
    /// </summary>
    public static CsvTable Parse(string text, CsvFormat format, int maxRows)
    {
        var rows = new List<CsvRow>();
        IReadOnlyList<string>? header = null;

        using var reader = new StringReader(text);
        foreach (var record in CsvReader.Read(reader, format.Delimiter))
        {
            if (header is null)
            {
                if (IsSeparatorHint(record)) continue;
                header = record.Fields.Select(f => f.Trim()).ToList();
                continue;
            }
            if (record.Fields.All(f => f.Trim().Length == 0)) continue;

            if (rows.Count >= maxRows)
            {
                // Tausenderpunkt von Hand: die Kultur des Servers (oder der invariante Modus) soll die Meldung nicht bestimmen.
                var limit = maxRows.ToString("N0", System.Globalization.CultureInfo.InvariantCulture).Replace(",", ".");
                throw Problem("import_too_many_rows", $"Die Datei hat mehr als {limit} Datenzeilen (erlaubt: {limit}). Bitte in mehrere Dateien aufteilen.");
            }
            rows.Add(new CsvRow(record.Line, record.Fields));
        }

        if (header is null)
            throw Problem("import_empty", "Die Datei ist leer: es fehlt die Kopfzeile mit den Spaltennamen.");
        if (rows.Count == 0)
            throw Problem("import_no_rows", "Die Datei enthält nur die Kopfzeile, aber keine Datenzeilen.");

        return new CsvTable(header, rows);
    }

    /// <summary>Excel schreibt auf Wunsch eine erste Zeile "sep=;" (bzw. "sep=,"), die das Trennzeichen verrät; sie ist keine Kopfzeile.</summary>
    private static bool IsSeparatorHint(CsvRecord record) =>
        record.Fields.Count >= 1 && record.Fields[0].Trim().StartsWith("sep=", StringComparison.OrdinalIgnoreCase)
        && record.Fields[0].Trim().Length <= 5 && record.Fields.Skip(1).All(f => f.Trim().Length == 0);

    internal static ArgumentException Problem(string code, string message)
    {
        var ex = new ArgumentException(message);
        ex.Data["code"] = code;
        return ex;
    }
}
