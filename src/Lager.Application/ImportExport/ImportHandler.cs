namespace Lager.Application.ImportExport;

/// <summary>Was die Übernahme braucht: die Transaktion, die Kennung des Imports (Verweis im Ledger und im Audit) und einen Haken für den Audit-Sammel-Eintrag.</summary>
internal sealed record ApplyContext(IImportTransaction Transaction, Guid ImportId, Action BeforeLastChange);

/// <summary>
/// Eine Importart (Artikel, Bestand, Bestellungen) in zwei Schritten:
///  1. <see cref="PlanAsync"/> prüft JEDE Zeile gegen die Regeln des Systems und den vorhandenen Datenstand, ohne zu schreiben.
///     Der Trockenlauf ist genau dieser Schritt; die Übernahme führt ihn ebenfalls aus und schreibt nur, was er als gültig
///     eingestuft hat. So sagt der Trockenlauf voraus, was die Übernahme tut.
///  2. <see cref="ApplyAsync"/> schreibt die geplanten Änderungen, ausschließlich über die vorhandenen Dienste
///     (ArticleService, StockBooking, OrderService) - nie an ihnen vorbei.
/// </summary>
internal interface IImportHandler
{
    Task<ImportPlan> PlanAsync(CsvTable table, CancellationToken ct);

    /// <summary>
    /// Schreibt die Änderungen in der Reihenfolge der Datei. Vor der LETZTEN Änderung ruft sie <c>context.BeforeLastChange</c>:
    /// der Sammel-Eintrag des Audits soll im selben SaveChanges landen wie diese (siehe <see cref="IAuditBatch"/>).
    /// </summary>
    Task ApplyAsync(IReadOnlyList<PlannedChange> changes, ApplyContext context, CancellationToken ct);
}

internal static class ImportHeaders
{
    /// <summary>Index der Pflichtspalte; fehlt sie, ein 400 <c>import_missing_column</c> mit den gefundenen Spalten (und dem Hinweis auf das Trennzeichen).</summary>
    public static int Require(CsvTable table, string display, params string[] names)
    {
        var index = table.IndexOf(names.Length == 0 ? new[] { display } : names);
        if (index >= 0) return index;

        var found = table.Header.Count == 0 ? "(keine)" : string.Join(", ", table.Header.Select(h => $"'{h}'"));
        var hint = table.Header.Count == 1 && table.Header[0].IndexOfAny(new[] { ';', ',' }) >= 0
            ? " Die Kopfzeile wurde nicht in Spalten getrennt: stimmt das gewählte Trennzeichen?"
            : string.Empty;
        throw CsvTable.Problem("import_missing_column", $"Die Pflichtspalte '{display}' fehlt in der Kopfzeile. Gefundene Spalten: {found}.{hint}");
    }

    /// <summary>Warnungen zu Spalten, die der Import nicht kennt (Tippfehler wie "Mindestbestand" würden sonst still ignoriert).</summary>
    public static IReadOnlyList<string> Warnings(CsvTable table, IEnumerable<string> known) =>
        table.UnknownColumns(known).Select(c => $"Die Spalte '{c}' ist unbekannt und wird ignoriert.").ToList();
}
