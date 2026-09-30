using Lager.Application.Abstractions;
using Lager.Application.Stock;
using Lager.Domain.Articles;
using Lager.Domain.Stock;

namespace Lager.Application.ImportExport;

/// <summary>Eine Bestandsbuchung: auf die Zeile (Artikel, Lagerplatz, Charge, MHD) kommt <c>Delta</c> (Sollbestand minus Istbestand).</summary>
internal sealed record StockChange(
    int Line, string Key, Guid ArticleId, Guid LocationId, string? Lot, DateTime? Expiry, int Delta, int CostCents)
    : PlannedChange(Line, Key);

/// <summary>
/// Bestandsimport: setzt den Bestand je Artikel, Lagerplatz und Charge auf den Wert der Datei (Sollbestand).
///  - Gebucht wird die DIFFERENZ zum aktuellen Bestand, ausschließlich über <see cref="StockBooking"/> (Grund Adjust, Vorgangstyp
///    <c>CsvImport</c>, Vorgangs-Id = Kennung des Imports): jede Änderung steht im Ledger, die Invariante "Summe der Buchungen =
///    Bestand" bleibt erhalten. Bestandszeilen werden nie direkt eingefügt oder überschrieben.
///  - Ist der Bestand schon gleich, passiert nichts ("unverändert"): derselbe Import zweimal ändert nichts. Zeilen der Datenbank,
///    die nicht in der Datei stehen, bleiben unberührt.
///  - Charge und MHD sind Teil der Bestandsidentität (wie im Wareneingang): dieselbe Charge mit anderem MHD im selben
///    Lagerplatz ist ein Fehler (<c>lot_expiry_mismatch</c>), auch innerhalb der Datei.
/// </summary>
internal sealed class StockImporter : IImportHandler
{
    private const int MaxLot = 64;
    private const int MinExpiryYear = 2000;

    private readonly IArticleRepository _articles;
    private readonly IWarehouseRepository _warehouse;
    private readonly IStockRepository _stock;
    private readonly IStockMovementRepository _movements;
    private readonly IUnitOfWork _uow;

    public StockImporter(
        IArticleRepository articles, IWarehouseRepository warehouse, IStockRepository stock, IStockMovementRepository movements, IUnitOfWork uow)
    {
        _articles = articles;
        _warehouse = warehouse;
        _stock = stock;
        _movements = movements;
        _uow = uow;
    }

    public async Task<ImportPlan> PlanAsync(CsvTable table, CancellationToken ct)
    {
        var skuCol = ImportHeaders.Require(table, "Sku");
        var locationCol = ImportHeaders.Require(table, "Location", CsvColumns.LocationAliases.ToArray());
        var quantityCol = ImportHeaders.Require(table, "Quantity");
        var lotCol = table.IndexOf("LotNumber");
        var expiryCol = table.IndexOf("ExpiryDate");

        var articles = new Dictionary<string, Article>(StringComparer.OrdinalIgnoreCase);
        foreach (var article in await _articles.ListAsync(ct)) articles.TryAdd(article.Sku, article);
        var locations = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var location in await _warehouse.ListStorageLocationsAsync(ct)) locations.TryAdd(location.Code, location.Id);

        var changes = new List<PlannedChange>();
        var errors = new List<ImportRowError>();
        var seen = new Dictionary<(Guid, Guid, string, DateTime?), int>();
        var lotExpiries = new Dictionary<(Guid, Guid, string), (DateTime? Expiry, int Line)>();
        var problems = new ProblemCollector();
        int created = 0, updated = 0, unchanged = 0;

        foreach (var row in table.Rows)
        {
            problems.Clear();
            if (table.OverflowProblem(row) is { } overflow)
            {
                errors.Add(new ImportRowError(row.Line, null, "too_many_columns", overflow));
                continue;
            }

            var reader = new RowReader(row, problems);
            var sku = reader.Text(skuCol);
            var locationCode = reader.Text(locationCol);
            var key = $"{RowReader.Shorten(sku)} @ {RowReader.Shorten(locationCode)}";

            Article? article = null;
            if (sku.Length == 0) problems.Add("sku_missing", "Die SKU fehlt.");
            else if (!articles.TryGetValue(sku, out article)) problems.Add("unknown_sku", $"Unbekannte SKU '{RowReader.Shorten(sku)}'.");
            else if (article.IsBundle) problems.Add("article_is_bundle", $"'{article.Sku}' ist ein Bundle und hat keinen eigenen Bestand (gebucht werden die Komponenten).");

            Guid locationId = default;
            if (locationCode.Length == 0) problems.Add("location_missing", "Der Lagerplatz fehlt.");
            else if (!locations.TryGetValue(locationCode, out locationId)) problems.Add("unknown_location", $"Unbekannter Lagerplatz '{RowReader.Shorten(locationCode)}'.");

            var quantityText = reader.Text(quantityCol);
            var quantity = 0;
            if (quantityText.Length == 0) problems.Add("quantity_missing", "Die Menge fehlt.");
            else
            {
                quantity = reader.Int(quantityCol, "Quantity", 0);
                if (quantity < 0) problems.Add("negative_quantity", $"Die Menge darf nicht negativ sein ({quantity}).");
                else if (quantity > StockBooking.MaxQuantity) problems.Add("quantity_out_of_range", $"Die Menge darf höchstens {StockBooking.MaxQuantity} betragen.");
            }

            var lot = lotCol >= 0 ? StockItem.NormalizeLot(reader.Text(lotCol)) : null;
            if (lot is { Length: > MaxLot }) problems.Add("lot_too_long", $"Die Charge darf höchstens {MaxLot} Zeichen lang sein.");
            var expiry = reader.Date(expiryCol, "ExpiryDate", null);
            if (expiry is { Year: < MinExpiryYear }) problems.Add("invalid_date", $"ExpiryDate muss nach dem Jahr {MinExpiryYear - 1} liegen.");

            if (problems.Any)
            {
                errors.Add(problems.ToError(row.Line, key));
                continue;
            }

            var identity = (article!.Id, locationId, lot ?? string.Empty, expiry?.Date);
            if (seen.TryGetValue(identity, out var firstLine))
            {
                errors.Add(new ImportRowError(row.Line, key, "duplicate_row", $"Dieselbe Bestandszeile (Artikel, Lagerplatz, Charge, MHD) steht schon in Zeile {firstLine}."));
                continue;
            }

            if (lot is not null)
            {
                var lotKey = (article.Id, locationId, lot);
                if (lotExpiries.TryGetValue(lotKey, out var known) && known.Expiry?.Date != expiry?.Date)
                {
                    errors.Add(new ImportRowError(row.Line, key, "lot_expiry_mismatch",
                        $"Die Charge {lot} steht in Zeile {known.Line} {Describe(known.Expiry)}, hier {Describe(expiry)} - eine Charge hat genau ein MHD."));
                    continue;
                }
                lotExpiries.TryAdd(lotKey, (expiry, row.Line));
            }

            StockItem? current;
            try
            {
                current = await StockBooking.FindAsync(_stock, article.Id, locationId, lot, expiry, ct);
            }
            catch (InvalidOperationException ex) when (ex.Data["code"] is string code)
            {
                errors.Add(new ImportRowError(row.Line, key, code, ex.Message));
                continue;
            }

            seen[identity] = row.Line;
            var delta = quantity - (current?.Quantity ?? 0);
            if (delta == 0)
            {
                unchanged++;
                continue;
            }
            if (Math.Abs((long)delta) > StockBooking.MaxQuantity)
            {
                errors.Add(new ImportRowError(row.Line, key, "quantity_out_of_range",
                    $"Die Änderung von {current?.Quantity ?? 0} auf {quantity} ({delta:+#;-#}) überschreitet die Höchstmenge je Buchung ({StockBooking.MaxQuantity})."));
                continue;
            }

            changes.Add(new StockChange(row.Line, key, article.Id, locationId, lot, expiry, delta, article.PurchasePriceCents));
            if (current is null) created++;
            else updated++;
        }

        return new ImportPlan(table.Rows.Count, changes, created, updated, unchanged, errors,
            ImportHeaders.Warnings(table, CsvColumns.Stock.Concat(CsvColumns.LocationAliases)));
    }

    private static string Describe(DateTime? expiry) => expiry is null ? "ohne MHD" : $"mit MHD {expiry:yyyy-MM-dd}";

    public async Task ApplyAsync(IReadOnlyList<PlannedChange> changes, ApplyContext context, CancellationToken ct)
    {
        for (var i = 0; i < changes.Count; i++)
        {
            var change = (StockChange)changes[i];
            if (i == changes.Count - 1) context.BeforeLastChange();

            await StockBooking.BookAsync(_stock, _movements, change.ArticleId, change.LocationId, change.Delta,
                StockMovementReason.Adjust, "CsvImport", context.ImportId, change.Lot, change.Expiry, change.CostCents, ct);

            // Gebündelt speichern (eine Zeile je Speichern wäre bei 20000 Zeilen unnötig langsam), dann den Change-Tracker leeren.
            if ((i + 1) % 100 == 0 || i == changes.Count - 1)
            {
                await _uow.SaveChangesAsync(ct);
                context.Transaction.ReleaseTrackedEntities();
            }
        }
    }
}
