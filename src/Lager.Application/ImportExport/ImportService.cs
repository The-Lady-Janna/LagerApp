using System.Globalization;
using Lager.Application.Abstractions;
using Lager.Application.Articles;
using Lager.Application.Orders;

namespace Lager.Application.ImportExport;

/// <summary>
/// CSV-Import für Artikel, Bestand und Bestellungen. Der Ablauf ist für alle Arten derselbe:
///  1. Die Datei wird geprüft und gelesen: Größe (5 MB), Kodierung (UTF-8 mit/ohne BOM, sonst Windows-1252), Trennzeichen,
///     Zeilenzahl (20000), Kopfzeile. Verstöße sind ein Fehler der ganzen Datei (400 mit Code).
///  2. Jede Zeile wird gegen die Regeln des Systems UND den vorhandenen Datenstand geprüft, ohne zu schreiben
///     (<see cref="IImportHandler.PlanAsync"/>). Das Ergebnis - je Zeile ein Fehler, dazu "x neu, y aktualisiert, z Fehler" - ist
///     die Antwort des Trockenlaufs (<see cref="ImportOptions.DryRun"/>).
///  3. Die Übernahme führt dieselbe Prüfung aus und schreibt nur, wenn es keinen Fehler gibt oder ausdrücklich
///     <see cref="ImportOptions.SkipErrors"/> gesetzt ist (dann werden die fehlerhaften Zeilen ausgelassen). Prüfung und Schreiben
///     laufen in EINER Transaktion (<see cref="IImportTransactionFactory"/>): scheitert etwas, bleibt nichts zurück.
///
/// Geschrieben wird ausschließlich über die vorhandenen Dienste (ArticleService, StockBooking, OrderService): der Import
/// umgeht keine Regel und kein Ledger. Der Audit-Trail bekommt für den ganzen Import EINEN Sammel-Eintrag
/// ("CSV-Import: 1200 Artikel, Nutzer X", siehe <see cref="IAuditBatch"/>) statt einer Zeile je Datensatz.
/// </summary>
public sealed class ImportService
{
    /// <summary>Entitätstyp und Vorgang des Sammel-Eintrags im Audit-Trail.</summary>
    public const string AuditEntityType = "CsvImport";
    public const string AuditOperation = "Import";

    private const int MaxFileNameLength = 128;

    private readonly ArticleService _articleService;
    private readonly OrderService _orderService;
    private readonly IArticleRepository _articles;
    private readonly ISupplierRepository _suppliers;
    private readonly IWarehouseRepository _warehouse;
    private readonly IStockRepository _stock;
    private readonly IStockMovementRepository _movements;
    private readonly IOrderRepository _orders;
    private readonly IUnitOfWork _uow;
    private readonly IAuditBatch _audit;
    private readonly ICurrentUser? _currentUser;

    public ImportService(
        ArticleService articleService, OrderService orderService,
        IArticleRepository articles, ISupplierRepository suppliers, IWarehouseRepository warehouse,
        IStockRepository stock, IStockMovementRepository movements, IOrderRepository orders,
        IUnitOfWork uow, IAuditBatch audit, ICurrentUser? currentUser = null)
    {
        _articleService = articleService;
        _orderService = orderService;
        _articles = articles;
        _suppliers = suppliers;
        _warehouse = warehouse;
        _stock = stock;
        _movements = movements;
        _orders = orders;
        _uow = uow;
        _audit = audit;
        _currentUser = currentUser;
    }

    /// <summary>
    /// Liest die hochgeladene Datei in den Speicher und bricht ab, sobald sie <see cref="ImportLimits.MaxFileBytes"/> überschreitet
    /// (<see cref="ArgumentException"/>, Code <c>import_file_too_large</c>): eine größere Datei wird nie vollständig gelesen.
    /// </summary>
    public static async Task<byte[]> ReadLimitedAsync(Stream stream, CancellationToken ct = default)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81_920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > ImportLimits.MaxFileBytes) throw TooLarge();
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Prüft (Trockenlauf) oder übernimmt (<c>DryRun = false</c>) die Datei. Fachliche Fehler der GANZEN Datei (zu groß, leer,
    /// kein gültiges CSV, Pflichtspalte fehlt, zu viele Zeilen) sind <see cref="ArgumentException"/> mit Code; Fehler einzelner
    /// Zeilen stehen im Ergebnis.
    /// </summary>
    public async Task<ImportResult> ImportAsync(
        ImportKind kind, byte[] content, ImportOptions options, IImportTransactionFactory transactions, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(transactions);
        if (content.Length > ImportLimits.MaxFileBytes) throw TooLarge();

        var table = CsvTable.Parse(CsvText.Decode(content), options.Format, ImportLimits.MaxRows);
        var handler = CreateHandler(kind);

        if (options.DryRun)
        {
            var preview = await handler.PlanAsync(table, ct);
            return Result(kind, options, preview, applied: false, importId: null);
        }

        // Prüfung UND Schreiben in derselben Transaktion: was die Prüfung sah (Bestand, vorhandene Bestellnummern), gilt beim Schreiben noch.
        await using var transaction = await transactions.BeginAsync(ct);
        var plan = await handler.PlanAsync(table, ct);

        if ((plan.Errors.Count > 0 && !options.SkipErrors) || plan.Changes.Count == 0)
            return Result(kind, options, plan, applied: false, importId: null);

        var importId = Guid.NewGuid();
        var written = plan.Created + plan.Updated;
        var user = _currentUser is { IsAuthenticated: true, Username: { Length: > 0 } name } ? name : "system";
        var summary = $"CSV-Import: {written} {ImportKinds.Unit(kind)}, Nutzer {user}";
        var details = new Dictionary<string, object?>
        {
            ["kind"] = ImportKinds.Name(kind),
            ["file"] = CleanFileName(options.FileName),
            ["created"] = plan.Created,
            ["updated"] = plan.Updated,
            ["unchanged"] = plan.Unchanged,
            ["skippedErrors"] = plan.Errors.Count,
        };

        var completed = false;
        using (_audit.Begin(AuditEntityType, importId.ToString(), AuditOperation))
        {
            var context = new ApplyContext(transaction, importId, () =>
            {
                completed = true;
                _audit.Complete(summary, details);
            });
            await handler.ApplyAsync(plan.Changes, context, ct);
        }

        // Ein Handler, der den Sammel-Eintrag nie freigibt, würde die Änderungen ohne Audit-Spur übernehmen: lieber zurückrollen.
        if (!completed)
            throw new InvalidOperationException("Der Import hat den Audit-Sammel-Eintrag nicht geschrieben und wird zurückgerollt.");

        await transaction.CommitAsync(ct);
        return Result(kind, options, plan, applied: true, importId);
    }

    private IImportHandler CreateHandler(ImportKind kind) => kind switch
    {
        ImportKind.Articles => new ArticleImporter(_articleService, _articles, _suppliers),
        ImportKind.Stock => new StockImporter(_articles, _warehouse, _stock, _movements, _uow),
        ImportKind.Orders => new OrderImporter(_orderService, _articles, _orders),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    // ---- Ergebnis -------------------------------------------------------------------------------------------

    private static ImportResult Result(ImportKind kind, ImportOptions options, ImportPlan plan, bool applied, Guid? importId)
    {
        var errors = plan.Errors.OrderBy(e => e.Line).ToList();
        var summary = $"{plan.Created} neu, {plan.Updated} aktualisiert, {errors.Count} Fehler";
        var unit = ImportKinds.Unit(kind);

        string message;
        if (options.DryRun)
        {
            message = $"Trockenlauf: {summary}" + Unchanged(plan) + ". Es wurde nichts geschrieben.";
        }
        else if (applied)
        {
            message = $"Übernommen: {summary}" + Unchanged(plan) + "." +
                      (errors.Count > 0 ? $" {errors.Count} fehlerhafte Zeilen wurden ausgelassen." : string.Empty);
        }
        else if (errors.Count > 0 && !options.SkipErrors)
        {
            message = $"Nichts übernommen: {errors.Count} Zeilen mit Fehlern. Die Datei korrigieren oder die fehlerhaften Zeilen auslassen.";
        }
        else
        {
            message = $"Nichts zu übernehmen: alle gültigen {unit} ({plan.Unchanged}) stimmen schon mit der Datei überein." +
                      (errors.Count > 0 ? $" {errors.Count} fehlerhafte Zeilen wurden ausgelassen." : string.Empty);
        }

        return new ImportResult(
            ImportKinds.Name(kind), options.DryRun, applied, options.Format.Name,
            plan.Rows, plan.Created, plan.Updated, plan.Unchanged, errors.Count,
            errors.Take(ImportLimits.MaxReportedErrors).ToList(), errors.Count > ImportLimits.MaxReportedErrors,
            plan.Warnings, summary, message, importId);
    }

    private static string Unchanged(ImportPlan plan) =>
        plan.Unchanged > 0 ? $" ({plan.Unchanged.ToString(CultureInfo.InvariantCulture)} unverändert)" : string.Empty;

    // ---- Hilfen ---------------------------------------------------------------------------------------------

    /// <summary>Nur der Dateiname (kein Pfad), ohne Steuerzeichen und höchstens <see cref="MaxFileNameLength"/> Zeichen.</summary>
    private static string? CleanFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var bare = name.Replace('\\', '/');
        bare = bare[(bare.LastIndexOf('/') + 1)..];
        var clean = new string(bare.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) return null;
        return clean.Length <= MaxFileNameLength ? clean : clean[..MaxFileNameLength];
    }

    private static ArgumentException TooLarge() =>
        CsvTable.Problem("import_file_too_large", $"Die Datei ist größer als {ImportLimits.MaxFileBytes / (1024 * 1024)} MB. Bitte in mehrere Dateien aufteilen.");
}
