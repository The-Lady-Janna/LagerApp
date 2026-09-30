using System.Globalization;
using Lager.Domain.Articles;

namespace Lager.Application.ImportExport;

/// <summary>
/// Schreibt die Exportdateien (Kopfzeile und je Datensatz eine Zeile) aus den Datenströmen der <see cref="IExportSource"/>.
/// Nichts wird gesammelt: jede Zeile geht sofort an den <see cref="CsvWriter"/>. Texte sind gegen Formel-Injection entschärft,
/// Zahlen und Daten nicht (siehe <see cref="CsvCell"/>). Die Spalten der Artikel-, Bestands- und Bestelldatei sind dieselben,
/// die der Import liest.
/// </summary>
public static class CsvExporter
{
    /// <summary>Dateiname mit UTC-Zeit: <c>articles-20260930T101500Z.csv</c> (ohne Doppelpunkte, damit Windows ihn mag).</summary>
    public static string FileName(string name, DateTime utcNow) =>
        $"{name}-{utcNow.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}.csv";

    public static async Task WriteArticlesAsync(CsvWriter writer, IAsyncEnumerable<ArticleExportRow> rows, CancellationToken ct)
    {
        await writer.WriteHeaderAsync(CsvColumns.Articles);
        await foreach (var row in rows.WithCancellation(ct))
        {
            var a = row.Article;
            await writer.WriteRowAsync(
                a.Sku, a.Name, a.Description, a.Gtin,
                a.Dimensions.LengthMm, a.Dimensions.WidthMm, a.Dimensions.HeightMm, a.WeightGrams,
                a.Stacking.IsStackable, CsvCell.Verbatim(a.Stacking.StackingAxis.ToString()), a.Stacking.StackingIncrementMm,
                a.Stacking.MaxStackCount is int max ? CsvCell.Number(max) : default,
                a.MinStock, a.ReorderPoint, a.MaxStock,
                CsvCell.Decimal(a.PurchasePriceCents / 100m, 2), row.SupplierCode,
                string.Join(ArticleColumnRules.ListSeparator, a.AlternativeSkus),
                CsvCell.Date(a.ValidFrom), CsvCell.Date(a.ValidUntil));
        }
    }

    public static async Task WriteStockAsync(CsvWriter writer, IAsyncEnumerable<StockExportRow> rows, CancellationToken ct)
    {
        await writer.WriteHeaderAsync(CsvColumns.Stock);
        await foreach (var row in rows.WithCancellation(ct))
            await writer.WriteRowAsync(row.Sku, row.ArticleName, row.Location, row.Quantity, row.LotNumber, CsvCell.Date(row.ExpiryDate));
    }

    public static async Task WriteOrdersAsync(CsvWriter writer, IAsyncEnumerable<OrderLineExportRow> rows, CancellationToken ct)
    {
        await writer.WriteHeaderAsync(CsvColumns.Orders);
        await foreach (var row in rows.WithCancellation(ct))
            await writer.WriteRowAsync(
                row.OrderNumber, CsvCell.Verbatim(row.Status.ToString()), CsvCell.Verbatim(row.Source.ToString()),
                CsvCell.Timestamp(row.CreatedAt), row.CustomerReference, row.Priority, CsvCell.Date(row.DueDate),
                row.ExternalReference, row.Sku, row.Quantity);
    }

    public static async Task WriteMovementsAsync(CsvWriter writer, IAsyncEnumerable<MovementExportRow> rows, CancellationToken ct)
    {
        await writer.WriteHeaderAsync(CsvColumns.Movements);
        await foreach (var row in rows.WithCancellation(ct))
            await writer.WriteRowAsync(
                CsvCell.Timestamp(row.At), row.Sku, row.Location, row.QuantityDelta, CsvCell.Verbatim(row.Reason.ToString()),
                row.ReferenceType, CsvCell.Verbatim(row.ReferenceId?.ToString()), row.LotNumber, CsvCell.Date(row.ExpiryDate),
                CsvCell.Decimal(row.UnitCostCents / 100m, 2));
    }

    public static async Task WriteAuditAsync(CsvWriter writer, IAsyncEnumerable<AuditExportRow> rows, CancellationToken ct)
    {
        await writer.WriteHeaderAsync(CsvColumns.Audit);
        await foreach (var row in rows.WithCancellation(ct))
            await writer.WriteRowAsync(CsvCell.Timestamp(row.At), row.User, row.EntityType, row.EntityId, row.Operation, row.ChangesJson);
    }
}

/// <summary>Regeln der Artikelspalten, die Export und Import gemeinsam brauchen.</summary>
internal static class ArticleColumnRules
{
    /// <summary>Trenner der Alternativ-SKUs in einer Zelle beim Schreiben (beim Lesen sind auch Komma und Semikolon erlaubt).</summary>
    public const string ListSeparator = "|";
}
