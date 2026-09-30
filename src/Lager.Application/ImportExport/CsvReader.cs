using System.Text;

namespace Lager.Application.ImportExport;

/// <summary>Ein Datensatz der CSV-Datei: die Felder und die Zeilennummer (1-basiert), in der der Datensatz beginnt.</summary>
public sealed record CsvRecord(int Line, IReadOnlyList<string> Fields);

/// <summary>
/// RFC-4180-Leser ohne Fremdabhängigkeit. Regeln:
///  - Trennzeichen frei wählbar (Semikolon oder Komma); Datensätze enden mit CRLF, LF oder CR.
///  - Felder in Anführungszeichen dürfen Trennzeichen, Zeilenumbrüche und <c>""</c> (= ein Anführungszeichen) enthalten.
///  - Ein Anführungszeichen mitten in einem unquotierten Feld ist ein normales Zeichen (Excel schreibt so etwas nicht, liest es aber).
///  - Ein BOM am Anfang wird entfernt; völlig leere Zeilen werden übersprungen.
///  - Nicht geschlossene Anführungszeichen und Text hinter dem schließenden Anführungszeichen sind Fehler
///    (<see cref="CsvFormatException"/>, 400 mit Zeilennummer) - still falsch eingelesene Zeilen wären schlimmer.
/// </summary>
public static class CsvReader
{
    public static IEnumerable<CsvRecord> Read(TextReader reader, char delimiter)
    {
        var field = new StringBuilder();
        var fields = new List<string>();
        var line = 1;
        var recordLine = 1;
        var inQuotes = false;
        var afterQuote = false;
        var quoteLine = 1;
        var fieldQuoted = false;

        var next = reader.Read();
        if (next == '\uFEFF') next = reader.Read();

        while (next != -1)
        {
            var c = (char)next;
            next = reader.Read();

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (next == '"')
                    {
                        field.Append('"');
                        next = reader.Read();
                    }
                    else
                    {
                        inQuotes = false;
                        afterQuote = true;
                    }
                }
                else
                {
                    if (c == '\n') line++;
                    field.Append(c);
                }
                continue;
            }

            if (afterQuote && c != delimiter && c != '\r' && c != '\n')
            {
                // Leerraum hinter dem schließenden Anführungszeichen tolerieren, alles andere ist kaputt.
                if (c is ' ' or '\t') continue;
                throw new CsvFormatException($"Zeile {line}: Nach dem schließenden Anführungszeichen folgt Text ('{c}'). " +
                                             "Anführungszeichen im Text müssen verdoppelt werden (\"\").");
            }

            if (c == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
                fieldQuoted = false;
                afterQuote = false;
            }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && next == '\n') next = reader.Read();
                fields.Add(field.ToString());
                var blank = fields.Count == 1 && fields[0].Length == 0 && !fieldQuoted;
                if (!blank) yield return new CsvRecord(recordLine, fields.ToArray());
                fields.Clear();
                field.Clear();
                fieldQuoted = false;
                afterQuote = false;
                line++;
                recordLine = line;
            }
            else if (c == '"' && field.Length == 0 && !fieldQuoted)
            {
                inQuotes = true;
                fieldQuoted = true;
                quoteLine = line;
            }
            else
            {
                field.Append(c);
            }
        }

        if (inQuotes)
            throw new CsvFormatException($"Zeile {quoteLine}: Das Anführungszeichen wird nie geschlossen (die Datei endet mitten in einem Feld).");

        if (field.Length > 0 || fields.Count > 0 || fieldQuoted)
        {
            fields.Add(field.ToString());
            var blank = fields.Count == 1 && fields[0].Length == 0 && !fieldQuoted;
            if (!blank) yield return new CsvRecord(recordLine, fields.ToArray());
        }
    }

    /// <summary>Liest den ganzen Text (für kleine Eingaben und Tests).</summary>
    public static IReadOnlyList<CsvRecord> Parse(string text, char delimiter)
    {
        using var reader = new StringReader(text);
        return Read(reader, delimiter).ToList();
    }
}

/// <summary>Die Datei ist keine gültige CSV-Datei (siehe <see cref="CsvReader"/>). Fachlicher Fehler: 400 mit Code <c>csv_invalid</c>.</summary>
public sealed class CsvFormatException : ArgumentException
{
    public CsvFormatException(string message) : base(message) => Data["code"] = "csv_invalid";
}
