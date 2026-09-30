namespace Lager.Application.ImportExport;

/// <summary>
/// Liest die Zellen EINER Zeile typisiert und sammelt Probleme (statt beim ersten zu werfen): eine Zeile mit drei kaputten
/// Zellen meldet alle drei. Eine fehlende Spalte (Index &lt; 0) heißt "nicht angegeben": der Aufrufer bekommt den
/// Ersatzwert zurück (bei Änderungen: der bisherige Wert bleibt). Eine vorhandene, aber leere Zelle ist ein Wert: leer.
/// </summary>
internal sealed class RowReader
{
    private readonly CsvRow _row;
    private readonly ProblemCollector _problems;

    public RowReader(CsvRow row, ProblemCollector problems)
    {
        _row = row;
        _problems = problems;
    }

    public int Line => _row.Line;

    /// <summary>Der Text der Zelle (getrimmt, Schutz-Apostroph entfernt); leer bei fehlender Spalte.</summary>
    public string Text(int column) => CsvTable.Cell(_row, column);

    /// <summary>Ganze Zahl. Fehlende Spalte: <paramref name="absent"/>; leere Zelle: <paramref name="whenEmpty"/>.</summary>
    public int Int(int column, string name, int absent, int whenEmpty = 0)
    {
        if (column < 0) return absent;
        var text = Text(column);
        if (text.Length == 0) return whenEmpty;
        if (CsvText.TryParseInt(text, out var value)) return value;
        _problems.Add("invalid_number", $"{name}: '{Shorten(text)}' ist keine ganze Zahl.");
        return absent;
    }

    /// <summary>Wie <see cref="Int"/>, eine leere Zelle bedeutet "keine Angabe" (null).</summary>
    public int? NullableInt(int column, string name, int? absent)
    {
        if (column < 0) return absent;
        var text = Text(column);
        if (text.Length == 0) return null;
        if (CsvText.TryParseInt(text, out var value)) return value;
        _problems.Add("invalid_number", $"{name}: '{Shorten(text)}' ist keine ganze Zahl.");
        return absent;
    }

    public bool Bool(int column, string name, bool absent)
    {
        if (column < 0) return absent;
        var text = Text(column);
        if (text.Length == 0) return false;
        if (CsvText.TryParseBool(text, out var value)) return value;
        _problems.Add("invalid_boolean", $"{name}: '{Shorten(text)}' ist weder wahr noch falsch (erlaubt: true/false, ja/nein, 1/0).");
        return absent;
    }

    /// <summary>Datum/Zeitpunkt (UTC). Leere Zelle: null.</summary>
    public DateTime? Date(int column, string name, DateTime? absent)
    {
        if (column < 0) return absent;
        var text = Text(column);
        if (text.Length == 0) return null;
        if (CsvText.TryParseDate(text, out var value)) return value;
        _problems.Add("invalid_date", $"{name}: '{Shorten(text)}' ist kein Datum (erlaubt: 2026-09-30 oder 30.09.2026).");
        return absent;
    }

    /// <summary>Betrag in Euro mit Dezimalkomma oder -punkt; Ergebnis in Cent. Mehr als zwei Nachkommastellen sind ein Fehler (kein stilles Runden).</summary>
    public int Cents(int column, string name, int absent)
    {
        if (column < 0) return absent;
        var text = Text(column);
        if (text.Length == 0) return 0;
        if (!CsvText.TryParseDecimal(text, out var euros))
        {
            _problems.Add("invalid_number", $"{name}: '{Shorten(text)}' ist kein Betrag (z. B. 12,50).");
            return absent;
        }
        var cents = euros * 100m;
        if (cents != decimal.Truncate(cents))
        {
            _problems.Add("invalid_price", $"{name}: {Shorten(text)} hat mehr als zwei Nachkommastellen.");
            return absent;
        }
        if (cents < 0 || cents > int.MaxValue)
        {
            _problems.Add("invalid_price", $"{name}: {Shorten(text)} liegt außerhalb des erlaubten Bereichs.");
            return absent;
        }
        return (int)cents;
    }

    /// <summary>Kürzt einen Zelleninhalt für Fehlermeldungen (eine ganze Beschreibung im Fehlertext wäre unlesbar).</summary>
    public static string Shorten(string text) => text.Length <= 40 ? text : text[..37] + "...";
}
