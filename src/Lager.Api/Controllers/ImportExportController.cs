using System.Runtime.CompilerServices;
using System.Text;
using Lager.Application.ImportExport;
using Lager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Lager.Api.Controllers;

/// <summary>Formularfeld <c>file</c> des Uploads (Multipart). Die Prüfung (vorhanden, nicht leer, höchstens 5 MB) steht in <c>ImportUploadValidator</c>.</summary>
public sealed class ImportUpload
{
    /// <summary>Die CSV-Datei (Formularfeld <c>file</c>, höchstens 5 MB).</summary>
    public IFormFile? File { get; set; }
}

/// <summary>
/// Query-Parameter eines Imports. <c>dryRun</c> ist standardmäßig <c>true</c>: ein vergessener Parameter prüft nur und schreibt nichts.
/// <c>skipErrors</c> übernimmt die fehlerfreien Zeilen auch bei Zeilenfehlern. <c>delimiter</c>: <c>semicolon</c> (Standard) oder <c>comma</c>.
/// </summary>
public sealed class ImportQuery
{
    /// <summary>Nur prüfen, nichts schreiben (Standard: <c>true</c>). Erst <c>dryRun=false</c> übernimmt die Daten.</summary>
    public bool DryRun { get; set; } = true;

    /// <summary>Die fehlerfreien Zeilen auch dann übernehmen, wenn andere Zeilen Fehler haben.</summary>
    public bool SkipErrors { get; set; }

    /// <summary>Trennzeichen: <c>semicolon</c> (Standard) oder <c>comma</c>.</summary>
    public string? Delimiter { get; set; }
}

/// <summary>Query-Parameter der Exporte ohne Filter (Artikel, Bestand, Bestellungen): nur das Trennzeichen.</summary>
public sealed class ExportFormatQuery
{
    /// <summary>Trennzeichen: <c>semicolon</c> (Standard, Dezimalkomma) oder <c>comma</c> (Dezimalpunkt).</summary>
    public string? Delimiter { get; set; }
}

/// <summary>Bewegungen: Zeitraum (<c>from</c> inklusive, <c>to</c> ohne Uhrzeit = ganzer Tag inklusive, mit Uhrzeit exklusive; UTC).</summary>
public sealed class MovementExportQuery
{
    /// <summary>Trennzeichen: <c>semicolon</c> (Standard) oder <c>comma</c>.</summary>
    public string? Delimiter { get; set; }

    /// <summary>Beginn des Zeitraums (inklusive, UTC), als Datum <c>2026-09-30</c> oder mit Uhrzeit.</summary>
    public string? From { get; set; }

    /// <summary>Ende des Zeitraums: ohne Uhrzeit ist der ganze Tag eingeschlossen, mit Uhrzeit ist das Ende exklusiv (UTC).</summary>
    public string? To { get; set; }
}

/// <summary>Audit: Zeitraum wie bei den Bewegungen, dazu <c>user</c> (Benutzername, ohne Beachtung der Schreibweise).</summary>
public sealed class AuditExportQuery
{
    /// <summary>Trennzeichen: <c>semicolon</c> (Standard) oder <c>comma</c>.</summary>
    public string? Delimiter { get; set; }

    /// <summary>Beginn des Zeitraums (inklusive, UTC).</summary>
    public string? From { get; set; }

    /// <summary>Ende des Zeitraums (ohne Uhrzeit: ganzer Tag eingeschlossen; UTC).</summary>
    public string? To { get; set; }

    /// <summary>Nur Einträge dieses Benutzernamens (ohne Beachtung der Schreibweise).</summary>
    public string? User { get; set; }
}

/// <summary>
/// CSV-Export und -Import (Rolle Manager). Die Dateien sind Excel-DE-tauglich: UTF-8 mit BOM, Semikolon und Dezimalkomma
/// (auf Wunsch Komma als Trennzeichen mit Dezimalpunkt), Zeilenende CRLF, Texte gegen Formel-Injection entschärft.
/// Der Export streamt Zeile für Zeile aus der Datenbank (keine Tabelle liegt vollständig im Speicher), der Dateiname trägt die
/// UTC-Zeit. Der Import prüft zuerst (<c>dryRun=true</c>, Standard) und schreibt erst mit <c>dryRun=false</c>, dann in einer Transaktion
/// und nur über die vorhandenen Dienste. Fehler der ganzen Datei sind 400 mit Code, Fehler einzelner Zeilen stehen im Ergebnis.
/// Einzelheiten: docs/features/csv-import-export.md.
/// </summary>
[ApiController]
[Route("api")]
[Authorize(Policy = "Manager")]
public class ImportExportController : ControllerBase
{
    private readonly LagerDbContext _db;
    private readonly ImportService _imports;

    public ImportExportController(LagerDbContext db, ImportService imports)
    {
        _db = db;
        _imports = imports;
    }

    // ---- Export ---------------------------------------------------------------------------------------------

    /// <summary>Exportiert alle Artikel als CSV (nach SKU sortiert).</summary>
    [HttpGet("export/articles.csv")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK, "text/csv")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task ExportArticles([FromQuery] ExportFormatQuery query, CancellationToken ct) =>
        StreamAsync("articles", query.Delimiter, (writer, source) => CsvExporter.WriteArticlesAsync(writer, source.ArticlesAsync(ct), ct), ct);

    /// <summary>Exportiert den Bestand als CSV: eine Zeile je Artikel, Lagerplatz und Charge (nur Menge über 0).</summary>
    [HttpGet("export/stock.csv")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK, "text/csv")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task ExportStock([FromQuery] ExportFormatQuery query, CancellationToken ct) =>
        StreamAsync("stock", query.Delimiter, (writer, source) => CsvExporter.WriteStockAsync(writer, source.StockAsync(ct), ct), ct);

    /// <summary>Exportiert die Bestellungen als CSV: eine Zeile je Position, Kopfdaten wiederholt.</summary>
    [HttpGet("export/orders.csv")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK, "text/csv")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task ExportOrders([FromQuery] ExportFormatQuery query, CancellationToken ct) =>
        StreamAsync("orders", query.Delimiter, (writer, source) => CsvExporter.WriteOrdersAsync(writer, source.OrderLinesAsync(ct), ct), ct);

    /// <summary>Exportiert das Ledger (Bewegungen) als CSV, älteste zuerst, im Zeitraum <c>from</c>/<c>to</c>.</summary>
    [HttpGet("export/movements.csv")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK, "text/csv")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task ExportMovements([FromQuery] MovementExportQuery query, CancellationToken ct)
    {
        var range = ExportRange.Parse(query.From, query.To);
        return StreamAsync("movements", query.Delimiter,
            (writer, source) => CsvExporter.WriteMovementsAsync(writer, source.MovementsAsync(range.FromUtc, range.ToExclusiveUtc, ct), ct), ct);
    }

    /// <summary>Exportiert den Audit-Trail als CSV, älteste zuerst, im Zeitraum <c>from</c>/<c>to</c> und optional nur eines Benutzers.</summary>
    [HttpGet("export/audit.csv")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK, "text/csv")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task ExportAudit([FromQuery] AuditExportQuery query, CancellationToken ct)
    {
        var range = ExportRange.Parse(query.From, query.To);
        return StreamAsync("audit", query.Delimiter,
            (writer, source) => CsvExporter.WriteAuditAsync(writer, source.AuditAsync(range.FromUtc, range.ToExclusiveUtc, query.User, ct), ct), ct);
    }

    private async Task StreamAsync(string name, string? delimiter, Func<CsvWriter, IExportSource, Task> write, CancellationToken ct)
    {
        // Ein ungültiges Trennzeichen scheitert hier, bevor das erste Byte geschrieben ist (sauberes 400 statt einer halben Datei).
        var format = CsvFormat.Parse(delimiter);

        Response.ContentType = "text/csv; charset=utf-8";
        Response.Headers.ContentDisposition = $"attachment; filename=\"{CsvExporter.FileName(name, DateTime.UtcNow)}\"";
        Response.Headers.CacheControl = "no-store";

        await Response.Body.WriteAsync(CsvWriter.Utf8Preamble, ct);
        // Kestrel erlaubt kein synchrones Schreiben: der StreamWriter wird nur asynchron benutzt (CsvWriter schreibt je Zeile ein WriteAsync).
        await using var text = new StreamWriter(Response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 16 * 1024, leaveOpen: true);
        await write(new CsvWriter(text, format), new EfExportSource(_db));
        await text.FlushAsync(ct);
    }

    // ---- Import ---------------------------------------------------------------------------------------------

    /// <summary>Importiert Artikel aus CSV: Upsert je SKU, idempotent (derselbe Import zweimal ändert nichts).</summary>
    /// <remarks>
    /// Multipart-Formular mit dem Feld <c>file</c>. Antwort 200 mit dem Ergebnis (neu, aktualisiert, unverändert, Zeilenfehler);
    /// Fehler der ganzen Datei sind 400 mit Code. Ohne <c>dryRun=false</c> wird nur geprüft.
    /// </remarks>
    [HttpPost("import/articles")]
    [RequestSizeLimit(ImportLimits.MaxRequestBytes)]
    [ProducesResponseType<ImportResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    public Task<ImportResult> ImportArticles([FromForm] ImportUpload upload, [FromQuery] ImportQuery query, CancellationToken ct) =>
        RunImportAsync(ImportKind.Articles, upload, query, ct);

    /// <summary>Importiert den Bestand aus CSV: setzt die Menge je Artikel, Lagerplatz und Charge auf den Wert der Datei, gebucht als Differenz im Ledger.</summary>
    /// <remarks>Multipart-Formular mit dem Feld <c>file</c>; Antwort und <c>dryRun</c> wie beim Artikel-Import.</remarks>
    [HttpPost("import/stock")]
    [RequestSizeLimit(ImportLimits.MaxRequestBytes)]
    [ProducesResponseType<ImportResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    public Task<ImportResult> ImportStock([FromForm] ImportUpload upload, [FromQuery] ImportQuery query, CancellationToken ct) =>
        RunImportAsync(ImportKind.Stock, upload, query, ct);

    /// <summary>Importiert Bestellungen aus CSV: legt neue Bestellungen an (eine Zeile je Position); eine vorhandene Bestellnummer ist ein Zeilenfehler.</summary>
    /// <remarks>Multipart-Formular mit dem Feld <c>file</c>; Antwort und <c>dryRun</c> wie beim Artikel-Import.</remarks>
    [HttpPost("import/orders")]
    [RequestSizeLimit(ImportLimits.MaxRequestBytes)]
    [ProducesResponseType<ImportResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status413PayloadTooLarge)]
    public Task<ImportResult> ImportOrders([FromForm] ImportUpload upload, [FromQuery] ImportQuery query, CancellationToken ct) =>
        RunImportAsync(ImportKind.Orders, upload, query, ct);

    private async Task<ImportResult> RunImportAsync(ImportKind kind, ImportUpload upload, ImportQuery query, CancellationToken ct)
    {
        var file = upload.File ?? throw new ArgumentException("Es wurde keine Datei hochgeladen (Formularfeld 'file').");
        var format = CsvFormat.Parse(query.Delimiter);

        byte[] content;
        await using (var stream = file.OpenReadStream())
            content = await ImportService.ReadLimitedAsync(stream, ct);

        var options = new ImportOptions(query.DryRun, query.SkipErrors, format, file.FileName);
        return await _imports.ImportAsync(kind, content, options, new EfImportTransactionFactory(_db), ct);
    }
}

/// <summary>
/// Die Transaktion um eine Import-Übernahme, auf dem DbContext des Requests: alle SaveChanges der Dienste laufen darin und
/// werden erst mit <see cref="IImportTransaction.CommitAsync"/> dauerhaft; ohne Commit (Fehler, Abbruch) rollt Dispose alles zurück.
/// Sitzt im Host, weil die Anwendungsschicht EF Core nicht kennt.
/// </summary>
internal sealed class EfImportTransactionFactory : IImportTransactionFactory
{
    private readonly LagerDbContext _db;

    public EfImportTransactionFactory(LagerDbContext db) => _db = db;

    public async Task<IImportTransaction> BeginAsync(CancellationToken ct) =>
        new EfImportTransaction(_db, await _db.Database.BeginTransactionAsync(ct));

    private sealed class EfImportTransaction : IImportTransaction
    {
        private readonly LagerDbContext _db;
        private readonly IDbContextTransaction _transaction;

        public EfImportTransaction(LagerDbContext db, IDbContextTransaction transaction)
        {
            _db = db;
            _transaction = transaction;
        }

        public Task CommitAsync(CancellationToken ct) => _transaction.CommitAsync(ct);

        public void ReleaseTrackedEntities() => _db.ChangeTracker.Clear();

        public ValueTask DisposeAsync() => _transaction.DisposeAsync();
    }
}

/// <summary>
/// Die Zeilen der Exporte als Datenströme aus der Datenbank (<c>AsNoTracking</c>, <c>AsAsyncEnumerable</c>): Zeile für Zeile gelesen und
/// sofort geschrieben, nie eine ganze Tabelle im Speicher. Sitzt im Host, weil die Anwendungsschicht EF Core nicht kennt.
/// </summary>
internal sealed class EfExportSource : IExportSource
{
    private readonly LagerDbContext _db;

    public EfExportSource(LagerDbContext db) => _db = db;

    public async IAsyncEnumerable<ArticleExportRow> ArticlesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var query =
            from a in _db.Articles.AsNoTracking()
            join s in _db.Suppliers.AsNoTracking() on a.PrimarySupplierId equals (Guid?)s.Id into suppliers
            from s in suppliers.DefaultIfEmpty()
            orderby a.Sku
            select new { Article = a, SupplierCode = s == null ? null : s.Code };

        await foreach (var row in query.AsAsyncEnumerable().WithCancellation(ct))
            yield return new ArticleExportRow(row.Article, row.SupplierCode);
    }

    /// <summary>Nur Zeilen mit Bestand (Menge 0 ist eine leere Bestandszeile und kein Bestand).</summary>
    public async IAsyncEnumerable<StockExportRow> StockAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var query =
            from i in _db.StockItems.AsNoTracking()
            where i.Quantity > 0
            join a in _db.Articles.AsNoTracking() on i.ArticleId equals a.Id
            join l in _db.StorageLocations.AsNoTracking() on i.StorageLocationId equals l.Id
            orderby a.Sku, l.Code, i.LotNumber, i.ExpiryDate
            select new { a.Sku, ArticleName = a.Name, Location = l.Code, i.Quantity, i.LotNumber, i.ExpiryDate };

        await foreach (var row in query.AsAsyncEnumerable().WithCancellation(ct))
            yield return new StockExportRow(row.Sku, row.ArticleName, row.Location, row.Quantity, row.LotNumber, row.ExpiryDate);
    }

    public async IAsyncEnumerable<OrderLineExportRow> OrderLinesAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var query =
            from o in _db.Orders.AsNoTracking()
            from l in o.Lines
            join a in _db.Articles.AsNoTracking() on l.ArticleId equals a.Id
            orderby o.CreatedAt, o.OrderNumber, l.CreatedAt
            select new
            {
                o.OrderNumber, o.Status, o.Source, o.CreatedAt, o.CustomerReference, o.Priority, o.DueDate, o.ExternalReference,
                a.Sku, l.Quantity,
            };

        await foreach (var row in query.AsAsyncEnumerable().WithCancellation(ct))
            yield return new OrderLineExportRow(
                row.OrderNumber, row.Status, row.Source, row.CreatedAt, row.CustomerReference, row.Priority, row.DueDate,
                row.ExternalReference, row.Sku, row.Quantity);
    }

    public async IAsyncEnumerable<MovementExportRow> MovementsAsync(
        DateTime? fromUtc, DateTime? toExclusiveUtc, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var movements = _db.StockMovements.AsNoTracking().AsQueryable();
        if (fromUtc is { } from) movements = movements.Where(m => m.At >= from);
        if (toExclusiveUtc is { } to) movements = movements.Where(m => m.At < to);

        // Linke Verbindungen: die Zeile des Ledgers bleibt auch dann erhalten, wenn Artikel oder Lagerplatz inzwischen fehlen.
        var query =
            from m in movements
            join a in _db.Articles.AsNoTracking() on m.ArticleId equals a.Id into articles
            from a in articles.DefaultIfEmpty()
            join l in _db.StorageLocations.AsNoTracking() on m.BinId equals l.Id into locations
            from l in locations.DefaultIfEmpty()
            orderby m.At, m.Id
            select new
            {
                m.At, Sku = a == null ? null : a.Sku, Location = l == null ? null : l.Code, m.QuantityDelta, m.Reason,
                m.ReferenceType, m.ReferenceId, m.LotNumber, m.ExpiryDate, m.UnitCostCents,
            };

        await foreach (var row in query.AsAsyncEnumerable().WithCancellation(ct))
            yield return new MovementExportRow(
                row.At, row.Sku, row.Location, row.QuantityDelta, row.Reason, row.ReferenceType, row.ReferenceId,
                row.LotNumber, row.ExpiryDate, row.UnitCostCents);
    }

    public async IAsyncEnumerable<AuditExportRow> AuditAsync(
        DateTime? fromUtc, DateTime? toExclusiveUtc, string? user, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var entries = _db.AuditEntries.AsNoTracking().AsQueryable();
        if (fromUtc is { } from) entries = entries.Where(e => e.At >= from);
        if (toExclusiveUtc is { } to) entries = entries.Where(e => e.At < to);
        if (!string.IsNullOrWhiteSpace(user))
        {
            var lower = user.Trim().ToLower();
            entries = entries.Where(e => e.User != null && e.User.ToLower() == lower);
        }

        await foreach (var e in entries.OrderBy(e => e.At).ThenBy(e => e.Id).AsAsyncEnumerable().WithCancellation(ct))
            yield return new AuditExportRow(e.At, e.User, e.EntityType, e.EntityId, e.Operation, e.ChangesJson);
    }
}
