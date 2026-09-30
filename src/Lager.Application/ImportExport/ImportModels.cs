namespace Lager.Application.ImportExport;

/// <summary>Was importiert wird. Der Kurzname (<see cref="ImportKinds.Name"/>) steht in Route und Antwort.</summary>
public enum ImportKind
{
    Articles,
    Stock,
    Orders,
}

public static class ImportKinds
{
    public static string Name(ImportKind kind) => kind switch
    {
        ImportKind.Articles => "articles",
        ImportKind.Stock => "stock",
        ImportKind.Orders => "orders",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Wie der Gegenstand im Text heißt ("CSV-Import: 12 Artikel").</summary>
    public static string Unit(ImportKind kind) => kind switch
    {
        ImportKind.Articles => "Artikel",
        ImportKind.Stock => "Bestandszeilen",
        ImportKind.Orders => "Bestellungen",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

/// <summary>Grenzen eines Imports: schützen den Server vor riesigen Dateien, nicht vor fachlich falschen Werten.</summary>
public static class ImportLimits
{
    /// <summary>Größe der hochgeladenen Datei: 5 MB.</summary>
    public const int MaxFileBytes = 5 * 1024 * 1024;

    /// <summary>Obergrenze der ganzen Anfrage (Datei plus Overhead des Multipart-Formulars): die Datei selbst prüft der Dienst gegen <see cref="MaxFileBytes"/>.</summary>
    public const int MaxRequestBytes = MaxFileBytes + 256 * 1024;

    /// <summary>Datenzeilen ohne Kopfzeile.</summary>
    public const int MaxRows = 20_000;

    /// <summary>Wie viele Zeilenfehler die Antwort höchstens einzeln nennt (die Zählung <c>errorCount</c> ist immer vollständig).</summary>
    public const int MaxReportedErrors = 500;
}

/// <param name="DryRun">Nur prüfen und zählen, nichts schreiben.</param>
/// <param name="SkipErrors">Zeilen mit Fehlern auslassen und die fehlerfreien übernehmen; sonst wird bei einem Fehler nichts übernommen.</param>
/// <param name="Format">Trennzeichen der Datei.</param>
/// <param name="FileName">Name der hochgeladenen Datei (nur für den Sammel-Audit-Eintrag).</param>
public sealed record ImportOptions(bool DryRun, bool SkipErrors, CsvFormat Format, string? FileName = null);

/// <summary>Ein Fehler einer Datenzeile: Zeile in der Datei, Schlüssel (SKU, Bestellnummer ...), maschinenlesbarer Code und Text.</summary>
public sealed record ImportRowError(int Line, string? Key, string Code, string Message);

/// <summary>
/// Ergebnis eines Imports (auch des Trockenlaufs: dieselbe Prüfung, nur ohne zu schreiben).
/// <c>Created</c>/<c>Updated</c>/<c>Unchanged</c> zählen Datensätze (Artikel, Bestandszeilen, Bestellungen), <c>ErrorCount</c> die
/// Zeilen mit Fehlern, <c>Rows</c> die Datenzeilen der Datei. <c>Applied</c> = es wurde geschrieben (bei einem Trockenlauf nie;
/// bei Fehlern ohne <c>skipErrors</c> ebenfalls nicht - dann ist nichts passiert).
/// </summary>
public sealed record ImportResult(
    string Kind,
    bool DryRun,
    bool Applied,
    string Delimiter,
    int Rows,
    int Created,
    int Updated,
    int Unchanged,
    int ErrorCount,
    IReadOnlyList<ImportRowError> Errors,
    bool ErrorsTruncated,
    IReadOnlyList<string> Warnings,
    string Summary,
    string Message,
    Guid? ImportId);

/// <summary>Zwischenergebnis der Prüfung einer Datei: was zu schreiben ist, was unverändert bleibt, was fehlerhaft ist.</summary>
internal sealed record ImportPlan(
    int Rows,
    IReadOnlyList<PlannedChange> Changes,
    int Created,
    int Updated,
    int Unchanged,
    IReadOnlyList<ImportRowError> Errors,
    IReadOnlyList<string> Warnings);

/// <summary>Ein zu schreibender Datensatz (je Art eine eigene Ableitung), in der Reihenfolge der Datei.</summary>
internal abstract record PlannedChange(int Line, string Key);

/// <summary>Schlüssel und Zeile eines fehlerhaften Datensatzes beim Sammeln der Fehler.</summary>
internal sealed class ProblemCollector
{
    private readonly List<(string Code, string Message)> _problems = new();

    public bool Any => _problems.Count > 0;

    public void Add(string code, string message) => _problems.Add((code, message));

    /// <summary>Alle Meldungen einer Zeile zu EINEM Zeilenfehler (Code des ersten Problems).</summary>
    public ImportRowError ToError(int line, string? key) =>
        new(line, key, _problems[0].Code, string.Join(" ", _problems.Select(p => p.Message)));

    public void Clear() => _problems.Clear();
}
