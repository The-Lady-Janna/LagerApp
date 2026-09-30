using Lager.Application.Abstractions;
using Lager.Contracts.Stock;
using Lager.Domain.Stock;

namespace Lager.Application.Stock;

public class StockService
{
    private readonly IStockRepository _stock;
    private readonly IStockMovementRepository _movements;
    private readonly IArticleRepository _articles;
    private readonly IWarehouseRepository _warehouse;
    private readonly IUnitOfWork _uow;

    public StockService(IStockRepository stock, IStockMovementRepository movements, IArticleRepository articles, IWarehouseRepository warehouse, IUnitOfWork uow)
    {
        _stock = stock;
        _movements = movements;
        _articles = articles;
        _warehouse = warehouse;
        _uow = uow;
    }

    public async Task<IReadOnlyList<StockItemDto>> ListAsync(CancellationToken ct = default)
    {
        var items = await _stock.ListAllAsync(ct);
        return await MapAsync(items, ct);
    }

    public async Task<IReadOnlyList<StockItemDto>> ListForArticleAsync(Guid articleId, CancellationToken ct = default)
    {
        var items = await _stock.ListForArticleAsync(articleId, ct);
        return await MapAsync(items, ct);
    }

    public async Task<IReadOnlyList<StockSummaryDto>> SummaryAsync(CancellationToken ct = default)
    {
        var items = await _stock.ListAllAsync(ct);
        var articleIds = items.Select(i => i.ArticleId).Distinct().ToList();
        var articleMap = await _articles.GetManyAsync(articleIds, ct);

        return items
            .GroupBy(i => i.ArticleId)
            .Select(g =>
            {
                var article = articleMap[g.Key];
                return new StockSummaryDto(article.Id, article.Sku, article.Name, g.Sum(x => x.Quantity), g.Count());
            })
            .OrderBy(s => s.ArticleSku)
            .ToList();
    }

    /// <summary>
    /// Returns articles whose total stock has crossed their MinStock or
    /// ReorderPoint thresholds. Articles without thresholds set are ignored.
    /// </summary>
    public async Task<IReadOnlyList<StockAlertDto>> AlertsAsync(CancellationToken ct = default)
    {
        var allArticles = await _articles.ListAsync(ct);
        var relevant = allArticles.Where(a => a.MinStock > 0 || a.ReorderPoint > 0).ToList();
        if (relevant.Count == 0) return Array.Empty<StockAlertDto>();

        var stock = await _stock.ListAllAsync(ct);
        var totals = stock
            .GroupBy(s => s.ArticleId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

        var alerts = new List<StockAlertDto>();
        foreach (var a in relevant)
        {
            var total = totals.GetValueOrDefault(a.Id, 0);
            string? severity = null;
            if (a.MinStock > 0 && total < a.MinStock) severity = "critical";
            else if (a.ReorderPoint > 0 && total < a.ReorderPoint) severity = "warning";
            if (severity is null) continue;
            alerts.Add(new StockAlertDto(a.Id, a.Sku, a.Name, total, a.MinStock, a.ReorderPoint, a.MaxStock, severity));
        }
        // critical first, then by smallest available stock
        return alerts
            .OrderBy(a => a.Severity == "critical" ? 0 : 1)
            .ThenBy(a => a.TotalQuantity)
            .ToList();
    }

    /// <summary>
    /// Manuelle Bestandskorrektur über den einheitlichen Buchungsweg (<see cref="StockBooking"/>), lot-genau:
    ///  - Zugang: bucht auf die Bestandszeile der genannten Charge (samt MHD) bzw. der chargenlosen Zeile; die Zeile
    ///    wird bei Bedarf angelegt. Dieselbe Charge mit anderem MHD ist ein Fehler (lot_expiry_mismatch).
    ///  - Abgang mit Charge: genau diese Zeile, sie braucht genug Bestand (insufficient_stock).
    ///  - Abgang ohne Charge: zuerst die chargenlose Zeile, danach FEFO über die Chargen des Lagerplatzes.
    /// Liefert die (erste) betroffene Bestandszeile.
    /// </summary>
    public async Task<StockItemDto> AdjustAsync(AdjustStockRequest request, CancellationToken ct = default)
    {
        if (request.Delta == 0)
            throw StockErrors.Invalid("quantity_zero", "Delta must be non-zero", nameof(request));
        if (Math.Abs((long)request.Delta) > StockBooking.MaxQuantity)
            throw StockErrors.Invalid("quantity_out_of_range", $"Die Korrekturmenge darf höchstens {StockBooking.MaxQuantity} betragen", nameof(request));

        // Artikel und Lagerplatz werden hier bewusst nicht vorab geprüft: ein unbekannter Verweis scheitert beim
        // Speichern am Fremdschlüssel, und die zentrale Fehlerabbildung der API macht daraus 409 invalid_reference
        // (so festgeschrieben in den WP12-Tests der Fehlerantworten).
        var article = await _articles.GetAsync(request.ArticleId, ct);
        var costCents = article?.PurchasePriceCents ?? 0;
        var lot = StockItem.NormalizeLot(request.LotNumber);
        var explicitLot = lot is not null || request.ExpiryDate is not null;

        StockItem row;
        if (request.Delta > 0 || explicitLot)
        {
            row = await StockBooking.BookAsync(_stock, _movements,
                request.ArticleId, request.StorageLocationId, request.Delta, StockMovementReason.Adjust,
                "ManualAdjust", null, lot, request.ExpiryDate, costCents, ct);
        }
        else
        {
            var parts = await StockBooking.BookOutAsync(_stock, _movements,
                request.ArticleId, request.StorageLocationId, -request.Delta, StockMovementReason.Adjust,
                "ManualAdjust", null, costCents, lotlessFirst: true, ct: ct);
            row = parts[0].Row;
        }

        await _uow.SaveChangesAsync(ct);
        return (await MapAsync(new[] { row }, ct))[0];
    }

    private async Task<IReadOnlyList<StockItemDto>> MapAsync(IEnumerable<StockItem> items, CancellationToken ct)
    {
        var list = items.ToList();
        var articleIds = list.Select(i => i.ArticleId).Distinct();
        var locationIds = list.Select(i => i.StorageLocationId).Distinct();

        var articleMap = await _articles.GetManyAsync(articleIds, ct);
        var locationMap = await _warehouse.GetStorageLocationsAsync(locationIds, ct);

        return list.Select(i =>
        {
            var a = articleMap[i.ArticleId];
            var l = locationMap[i.StorageLocationId];
            return new StockItemDto(i.Id, i.ArticleId, a.Sku, a.Name, i.StorageLocationId, l.Code, i.Quantity, i.LotNumber, i.ExpiryDate);
        }).ToList();
    }
}
