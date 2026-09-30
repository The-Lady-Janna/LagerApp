using Lager.Application.Reports;
using Lager.Contracts.Reports;
using Lager.Domain.PickLists;
using Lager.Domain.Stock;
using Microsoft.EntityFrameworkCore;

namespace Lager.Infrastructure.Persistence.Repositories;

/// <summary>
/// EF-backed implementation of the read-side gateway used by ReportService.
/// All queries are AsNoTracking — pure projections, never written back.
/// </summary>
public class ReportQueryGateway : IReportQueryGateway
{
    private readonly LagerDbContext _db;
    public ReportQueryGateway(LagerDbContext db) => _db = db;

    public async Task<IReadOnlyList<OrderSnapshot>> OrdersInRangeAsync(DateTime from, DateTime to, CancellationToken ct) =>
        await _db.Orders.AsNoTracking()
            .Where(o => o.CreatedAt >= from && o.CreatedAt <= to)
            .Select(o => new OrderSnapshot(o.Id, o.Status, o.CreatedAt))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<PickListSnapshot>> PickListsInRangeAsync(DateTime from, DateTime to, CancellationToken ct) =>
        await _db.PickLists.AsNoTracking()
            .Where(p => p.CreatedAt >= from && p.CreatedAt <= to)
            .Select(p => new PickListSnapshot(p.Id, p.Status, p.CreatedAt, p.TotalDistanceMm))
            .ToListAsync(ct);

    /// <summary>
    /// Nur Picklisten, deren Ware tatsächlich gepickt wurde (Status Picked/Completed), und nur Zeilen mit
    /// Ist-Menge &gt; 0. Ist-Menge = ConfirmedQuantity (gesetzt beim Packen), bei Picked-Listen ohne
    /// Bestätigung die Sollmenge. Zeitbezug ist der Abschluss (UpdatedAt der Liste), nicht die Anlage.
    /// </summary>
    public async Task<IReadOnlyList<PickedItemSnapshot>> PickedItemsInRangeAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        var rows = await _db.PickItems.AsNoTracking()
            .Join(PickedListsInRange(from, to),
                  item => item.PickListId,
                  pl => pl.Id,
                  (item, pl) => new
                  {
                      item.PickListId,
                      item.ArticleId,
                      item.StorageLocationId,
                      Quantity = item.ConfirmedQuantity ?? item.Quantity,
                      PickedAt = item.ConfirmedAt ?? pl.UpdatedAt,
                  })
            .Where(x => x.Quantity > 0)
            .ToListAsync(ct);
        return rows
            .Select(x => new PickedItemSnapshot(x.PickListId, x.ArticleId, x.StorageLocationId, x.Quantity, x.PickedAt))
            .ToList();
    }

    private IQueryable<PickList> PickedListsInRange(DateTime from, DateTime to) =>
        _db.PickLists.AsNoTracking()
            .Where(pl => (pl.Status == PickListStatus.Picked || pl.Status == PickListStatus.Completed)
                      && pl.UpdatedAt >= from && pl.UpdatedAt <= to);

    /// <summary>
    /// Per-bin summary: distinct article count + total quantity. One round-trip,
    /// grouped server-side.
    /// </summary>
    public async Task<IReadOnlyList<BinStockSnapshot>> BinStockSummaryAsync(CancellationToken ct) =>
        await _db.StockItems.AsNoTracking()
            .GroupBy(s => s.StorageLocationId)
            .Select(g => new BinStockSnapshot(
                g.Key,
                g.Select(x => x.ArticleId).Distinct().Count(),
                g.Sum(x => x.Quantity)))
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, (string Sku, string Name)>> ArticleNamesAsync(IEnumerable<Guid> articleIds, CancellationToken ct)
    {
        var ids = articleIds.ToList();
        if (ids.Count == 0) return new Dictionary<Guid, (string, string)>();
        var rows = await _db.Articles.AsNoTracking()
            .Where(a => ids.Contains(a.Id))
            .Select(a => new { a.Id, a.Sku, a.Name })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => (r.Sku, r.Name));
    }

    public async Task<IReadOnlyDictionary<Guid, string>> BinCodesAsync(IEnumerable<Guid> binIds, CancellationToken ct)
    {
        var ids = binIds.ToList();
        if (ids.Count == 0) return new Dictionary<Guid, string>();
        var rows = await _db.StorageLocations.AsNoTracking()
            .Where(b => ids.Contains(b.Id))
            .Select(b => new { b.Id, b.Code })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => r.Code);
    }

    // ---- Welle 8 -----------------------------------------------------------

    public async Task<IReadOnlyList<BinHeatSnapshot>> BinPickCountsAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        // Nur tatsächlich gepickte Zeilen (Picklisten Picked/Completed im Fenster, Ist-Menge > 0).
        // PickCount = number of items (line picks), not summed quantity — that's
        // what "Heatmap" intuitively means.
        var rows = await _db.PickItems.AsNoTracking()
            .Join(PickedListsInRange(from, to),
                  item => item.PickListId,
                  pl => pl.Id,
                  (item, pl) => item)
            .Where(item => (item.ConfirmedQuantity ?? item.Quantity) > 0)
            .GroupBy(item => item.StorageLocationId)
            .Select(g => new BinHeatSnapshot(g.Key, g.Count()))
            .ToListAsync(ct);
        return rows;
    }

    public async Task<IReadOnlyDictionary<Guid, DateTime?>> ArticleLastMovementAsync(CancellationToken ct)
    {
        // "Letzte Bewegung" = letzter Eintrag im StockMovement-Ledger (Pick/Inbound/
        // Return/Adjust/Inventory). Interne Umlagerungen (Nachschub, Bin-Verschiebung)
        // zählen nicht: sie sind keine Nachfrage und würden Ladenhüter als "bewegt"
        // erscheinen lassen.
        var movements = await _db.StockMovements.AsNoTracking()
            .Where(m => m.Reason != StockMovementReason.ReplenishmentOut
                     && m.Reason != StockMovementReason.ReplenishmentIn
                     && m.Reason != StockMovementReason.BinMove)
            .GroupBy(m => m.ArticleId)
            .Select(g => new { ArticleId = g.Key, LastAt = g.Max(m => m.At) })
            .ToListAsync(ct);

        var perArticle = movements.ToDictionary(m => m.ArticleId, m => (DateTime?)m.LastAt);

        // Artikel ohne Bewegung trotzdem aufnehmen (LastAt = null = "nie bewegt").
        var allArticleIds = await _db.Articles.AsNoTracking().Select(a => a.Id).ToListAsync(ct);
        foreach (var aid in allArticleIds)
            perArticle.TryAdd(aid, null);

        return perArticle;
    }

    public async Task<LiveStatusSnapshot> LiveStatusAsync(CancellationToken ct)
    {
        var todayStart = DateTime.UtcNow.Date;

        var ordersByStatus = await _db.Orders.AsNoTracking()
            .GroupBy(o => o.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        int OrderCount(Lager.Domain.Orders.OrderStatus s) =>
            ordersByStatus.FirstOrDefault(x => x.Status == s)?.Count ?? 0;

        var picklistsByStatus = await _db.PickLists.AsNoTracking()
            .GroupBy(p => p.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        int PlCount(Lager.Domain.PickLists.PickListStatus s) =>
            picklistsByStatus.FirstOrDefault(x => x.Status == s)?.Count ?? 0;

        var completedToday = await _db.PickLists.AsNoTracking()
            .CountAsync(p => p.Status == Lager.Domain.PickLists.PickListStatus.Completed && p.UpdatedAt >= todayStart, ct);

        var inventoryOpen = await _db.InventoryCounts.AsNoTracking()
            .CountAsync(i => i.Status == Lager.Domain.Inventory.InventoryStatus.Open, ct);

        var replenOpen = await _db.ReplenishmentTasks.AsNoTracking()
            .CountAsync(t => t.Status == Lager.Domain.Stock.ReplenishmentStatus.Open, ct);

        // Stock-Alerts: aggregate per article quantity vs Article thresholds.
        var stockTotals = await _db.StockItems.AsNoTracking()
            .GroupBy(s => s.ArticleId)
            .Select(g => new { ArticleId = g.Key, Total = g.Sum(s => s.Quantity) })
            .ToListAsync(ct);
        var thresholds = await _db.Articles.AsNoTracking()
            .Where(a => a.MinStock > 0 || a.ReorderPoint > 0)
            .Select(a => new { a.Id, a.MinStock, a.ReorderPoint })
            .ToListAsync(ct);
        var totalByArticle = stockTotals.ToDictionary(x => x.ArticleId, x => x.Total);
        var critical = thresholds.Count(t => totalByArticle.GetValueOrDefault(t.Id) < t.MinStock);
        var warning = thresholds.Count(t =>
        {
            var have = totalByArticle.GetValueOrDefault(t.Id);
            return have >= t.MinStock && have < t.ReorderPoint;
        });

        return new LiveStatusSnapshot(
            OrderCount(Lager.Domain.Orders.OrderStatus.New),
            OrderCount(Lager.Domain.Orders.OrderStatus.Picking),
            OrderCount(Lager.Domain.Orders.OrderStatus.Picked),
            OrderCount(Lager.Domain.Orders.OrderStatus.Packed),
            PlCount(Lager.Domain.PickLists.PickListStatus.Pending),
            PlCount(Lager.Domain.PickLists.PickListStatus.InProgress),
            PlCount(Lager.Domain.PickLists.PickListStatus.Picked),
            completedToday,
            inventoryOpen,
            replenOpen,
            critical, warning);
    }

    public async Task<IReadOnlyList<StockMovementEvent>> StockMovementsAsync(Guid articleId, DateTime from, DateTime to, CancellationToken ct)
    {
        // Echter Bewegungs-Stream aus dem StockMovement-Ledger. Jede Mutation
        // (Inbound/Pick/Return/Inventory/Replen/Adjust) schreibt dort einen
        // signierten Delta-Eintrag — perfekt für den Trend-Chart.
        var rows = await _db.StockMovements.AsNoTracking()
            .Where(m => m.ArticleId == articleId && m.At >= from && m.At <= to)
            .OrderBy(m => m.At)
            .Select(m => new StockMovementEvent(m.At, m.QuantityDelta))
            .ToListAsync(ct);
        return rows;
    }

    public async Task<(Guid Id, string Sku, string Name, int CurrentQty)?> ArticleStockSummaryAsync(Guid articleId, CancellationToken ct)
    {
        var article = await _db.Articles.AsNoTracking()
            .Where(a => a.Id == articleId)
            .Select(a => new { a.Id, a.Sku, a.Name })
            .FirstOrDefaultAsync(ct);
        if (article is null) return null;
        var qty = await _db.StockItems.AsNoTracking()
            .Where(s => s.ArticleId == articleId)
            .SumAsync(s => (int?)s.Quantity, ct) ?? 0;
        return (article.Id, article.Sku, article.Name, qty);
    }

    public async Task<IReadOnlyDictionary<Guid, ArticleStockTotals>> ArticleStockTotalsAsync(CancellationToken ct)
    {
        var rows = await _db.StockItems.AsNoTracking()
            .Where(s => s.Quantity > 0)
            .GroupBy(s => s.ArticleId)
            .Select(g => new
            {
                g.Key,
                Qty = g.Sum(x => x.Quantity),
                Locations = g.Select(x => x.StorageLocationId).Distinct().Count(),
            })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => new ArticleStockTotals(r.Qty, r.Locations));
    }

    // ---- Welle 4: Charge tracing -------------------------------------------

    public async Task<IReadOnlyList<ChargeInboundDto>> ChargeInboundsAsync(string lotNumber, CancellationToken ct)
    {
        // Join InboundLines → InboundShipments + Articles for context.
        var lot = lotNumber;
        var rows = await _db.InboundLines.AsNoTracking()
            .Where(l => l.LotNumber == lot)
            .Join(_db.InboundShipments.AsNoTracking(),
                  l => l.InboundShipmentId, s => s.Id,
                  (l, s) => new { l, s })
            .Join(_db.Articles.AsNoTracking(),
                  ls => ls.l.ArticleId, a => a.Id,
                  (ls, a) => new
                  {
                      ShipmentId = ls.s.Id, ls.s.ShipmentNumber, ls.s.Status,
                      ArticleId = a.Id, a.Sku,
                      ls.l.TargetBinId, ls.l.Quantity, ls.l.ExpiryDate,
                      At = ls.s.ReceivedAt ?? ls.s.CreatedAt,
                  })
            .ToListAsync(ct);
        return rows
            .OrderBy(r => r.At)
            .Select(r => new ChargeInboundDto(
                r.ShipmentId, r.ShipmentNumber, r.ArticleId, r.Sku, r.TargetBinId, r.Quantity, r.ExpiryDate, r.At,
                r.Status.ToString()))
            .ToList();
    }

    public async Task<IReadOnlyList<PickerActivitySnapshot>> PickerActivityAsync(DateTime from, DateTime to, CancellationToken ct)
    {
        // Picklisten die im Zeitraum den Endstatus Picked oder Completed
        // erreicht haben — Picker-Status ist die letzte gepushte Mutation.
        // Wir filtern nach UpdatedAt (= letzter Touch, üblicherweise der
        // Pick-Abschluss) statt CreatedAt, um auch ältere Wagen mitzuzählen
        // die jetzt durchgegangen sind.
        var picklists = await _db.PickLists.AsNoTracking()
            .Where(p => (p.Status == Lager.Domain.PickLists.PickListStatus.Picked
                      || p.Status == Lager.Domain.PickLists.PickListStatus.Completed)
                     && p.UpdatedAt >= from && p.UpdatedAt <= to)
            .Select(p => new
            {
                p.Id, p.AssignedTo, p.TotalDistanceMm,
                p.CreatedAt, p.UpdatedAt,
            })
            .ToListAsync(ct);

        if (picklists.Count == 0) return Array.Empty<PickerActivitySnapshot>();

        // Items pro Pickliste — ConfirmedQuantity bevorzugt (echte Pickmenge),
        // sonst Quantity (Soll). Wenn beide 0 sind, zählen wir den Item-Count.
        var ids = picklists.Select(p => p.Id).ToList();
        var itemCounts = await _db.PickItems.AsNoTracking()
            .Where(i => ids.Contains(i.PickListId))
            .GroupBy(i => i.PickListId)
            .Select(g => new
            {
                PickListId = g.Key,
                Items = g.Sum(i => i.ConfirmedQuantity ?? i.Quantity),
            })
            .ToListAsync(ct);
        var itemsByPl = itemCounts.ToDictionary(x => x.PickListId, x => x.Items);

        // Fallback für PickLists ohne AssignedTo: User aus AuditEntries holen
        // (letzter Audit-Stempel für diese Pickliste). Falls auch dort nichts
        // ist (Background/system), zählen wir auf "system".
        var plIdStrings = ids.Select(g => g.ToString()).ToList();
        var auditLastUser = await _db.AuditEntries.AsNoTracking()
            .Where(a => a.EntityType == "PickList" && plIdStrings.Contains(a.EntityId))
            .GroupBy(a => a.EntityId)
            .Select(g => new
            {
                EntityId = g.Key,
                LastUser = g.OrderByDescending(a => a.At).Select(a => a.User).FirstOrDefault(),
            })
            .ToListAsync(ct);
        var userByPl = auditLastUser.ToDictionary(x => x.EntityId, x => x.LastUser);

        var result = new List<PickerActivitySnapshot>(picklists.Count);
        foreach (var pl in picklists)
        {
            var picker = !string.IsNullOrEmpty(pl.AssignedTo)
                ? pl.AssignedTo!
                : (userByPl.GetValueOrDefault(pl.Id.ToString()) ?? "anonymous");

            double? duration = null;
            if (pl.UpdatedAt > pl.CreatedAt)
                duration = (pl.UpdatedAt - pl.CreatedAt).TotalMinutes;

            result.Add(new PickerActivitySnapshot(
                picker, pl.UpdatedAt, pl.TotalDistanceMm,
                itemsByPl.GetValueOrDefault(pl.Id, 0),
                duration));
        }
        return result;
    }

    public async Task<IReadOnlyList<ValuationArticleSnapshot>> StockValuationBaseAsync(CancellationToken ct)
    {
        var stockTotals = await _db.StockItems.AsNoTracking()
            .GroupBy(s => s.ArticleId)
            .Select(g => new { ArticleId = g.Key, Total = g.Sum(x => x.Quantity) })
            .ToListAsync(ct);

        var articles = await _db.Articles.AsNoTracking()
            .Select(a => new { a.Id, a.Sku, a.Name, a.PurchasePriceCents })
            .ToListAsync(ct);
        var articleDict = articles.ToDictionary(a => a.Id);

        var result = new List<ValuationArticleSnapshot>(stockTotals.Count);
        foreach (var s in stockTotals)
        {
            if (s.Total <= 0) continue;
            if (!articleDict.TryGetValue(s.ArticleId, out var art)) continue;
            result.Add(new ValuationArticleSnapshot(art.Id, art.Sku, art.Name, art.PurchasePriceCents, s.Total));
        }
        return result;
    }

    public async Task<IReadOnlyList<LedgerMovementSnapshot>> ValuationLedgerAsync(CancellationToken ct)
    {
        // Der FIFO-Walk (StockValuationCalculator) läuft in der Application-Schicht. Hier nur die
        // Rohbuchungen der Artikel, die überhaupt noch Bestand haben — Artikel ohne Bestand haben
        // keinen Wert und müssen nicht in den Speicher.
        return await _db.StockMovements.AsNoTracking()
            .Where(m => _db.StockItems.Any(s => s.ArticleId == m.ArticleId && s.Quantity > 0))
            .OrderBy(m => m.ArticleId).ThenBy(m => m.At)
            .Select(m => new LedgerMovementSnapshot(
                m.ArticleId, m.At, m.QuantityDelta, m.UnitCostCents, m.Reason, m.ReferenceId))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ChargeMovementDto>> ChargeMovementsAsync(string lotNumber, CancellationToken ct)
    {
        // Append-only Ledger via LotNumber — überlebt das Verschwinden des
        // ursprünglichen StockItems. Liefert historische Picks, Inventur-Diffs
        // und Retouren in einem Stream chronologisch sortiert.
        var lot = lotNumber;
        var rows = await _db.StockMovements.AsNoTracking()
            .Where(m => m.LotNumber == lot)
            .OrderBy(m => m.At)
            .Select(m => new ChargeMovementDto(
                m.At, m.QuantityDelta, m.Reason.ToString(),
                m.BinId, m.ReferenceType, m.ReferenceId, null, m.ArticleId, null))
            .ToListAsync(ct);
        return rows;
    }

    /// <summary>
    /// Bestellungen, die aus der Charge beliefert worden sein können. Der Ledger kennt für Picks nur die Pickliste
    /// (ReferenceType "PickList"), die Pickposition nicht die Charge: gesucht werden die Positionen dieser Picklisten
    /// mit gleichem Artikel und Lagerplatz wie die Pick-Buchung der Charge. Eine Bestellung erscheint einmal (mit der
    /// letzten passenden Buchung); neueste zuerst.
    /// </summary>
    public async Task<IReadOnlyList<ChargeOrderDto>> ChargeOrdersAsync(string lotNumber, CancellationToken ct)
    {
        var lot = lotNumber;
        var picks = await _db.StockMovements.AsNoTracking()
            .Where(m => m.LotNumber == lot && m.Reason == StockMovementReason.Pick
                     && m.ReferenceType == "PickList" && m.ReferenceId != null)
            .Select(m => new { PickListId = m.ReferenceId!.Value, m.ArticleId, m.BinId, m.At })
            .ToListAsync(ct);
        if (picks.Count == 0) return Array.Empty<ChargeOrderDto>();

        var pickListIds = picks.Select(p => p.PickListId).Distinct().ToList();
        var items = await _db.PickItems.AsNoTracking()
            .Where(i => pickListIds.Contains(i.PickListId))
            .Select(i => new { i.PickListId, i.OrderId, i.ArticleId, i.StorageLocationId })
            .ToListAsync(ct);

        // (Bestellung, Pickliste) -> späteste passende Pick-Buchung
        var hits = new Dictionary<(Guid OrderId, Guid PickListId), DateTime>();
        foreach (var item in items)
        {
            var matching = picks.Where(p => p.PickListId == item.PickListId && p.ArticleId == item.ArticleId && p.BinId == item.StorageLocationId)
                .Select(p => (DateTime?)p.At).Max();
            if (matching is not DateTime at) continue;
            var key = (item.OrderId, item.PickListId);
            if (!hits.TryGetValue(key, out var known) || at > known) hits[key] = at;
        }
        if (hits.Count == 0) return Array.Empty<ChargeOrderDto>();

        var orderIds = hits.Keys.Select(k => k.OrderId).Distinct().ToList();
        var orders = await _db.Orders.AsNoTracking()
            .Where(o => orderIds.Contains(o.Id))
            .Select(o => new { o.Id, o.OrderNumber, o.CustomerReference, o.Status })
            .ToListAsync(ct);
        var lists = await _db.PickLists.AsNoTracking()
            .Where(p => pickListIds.Contains(p.Id))
            .Select(p => new { p.Id, p.PickListNumber })
            .ToListAsync(ct);
        var orderMap = orders.ToDictionary(o => o.Id);
        var listMap = lists.ToDictionary(l => l.Id, l => l.PickListNumber);

        return hits
            .Where(h => orderMap.ContainsKey(h.Key.OrderId))
            .Select(h =>
            {
                var o = orderMap[h.Key.OrderId];
                return new ChargeOrderDto(o.Id, o.OrderNumber, o.CustomerReference, o.Status.ToString(),
                    h.Key.PickListId, listMap.GetValueOrDefault(h.Key.PickListId), h.Value);
            })
            .OrderByDescending(x => x.PickedAt)
            .ThenBy(x => x.OrderNumber, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Bestandszeilen mit Menge &gt; 0 und MHD bis einschließlich <paramref name="untilDate"/> (Kalendertag): die Grenze
    /// ist der Beginn des Folgetags, damit auch ein MHD mit Uhrzeit am letzten Tag zählt.
    /// </summary>
    public async Task<IReadOnlyList<ExpiringStockSnapshot>> ExpiringStockAsync(DateTime untilDate, CancellationToken ct)
    {
        var limit = untilDate.Date.AddDays(1);
        var rows = await _db.StockItems.AsNoTracking()
            .Where(s => s.Quantity > 0 && s.ExpiryDate != null && s.ExpiryDate < limit)
            .Join(_db.Articles.AsNoTracking(),
                  s => s.ArticleId, a => a.Id,
                  (s, a) => new { s, a })
            .Join(_db.StorageLocations.AsNoTracking(),
                  sa => sa.s.StorageLocationId, b => b.Id,
                  (sa, b) => new { sa.s, sa.a, b })
            .Select(x => new { x.s.Id, x.a.Sku, x.a.Name, ArticleId = x.a.Id, BinId = x.b.Id, BinCode = x.b.Code,
                               x.s.LotNumber, x.s.Quantity, x.s.ExpiryDate })
            .ToListAsync(ct);
        return rows
            .Select(r => new ExpiringStockSnapshot(r.Id, r.ArticleId, r.Sku, r.Name, r.BinId, r.BinCode,
                r.LotNumber, r.Quantity, r.ExpiryDate!.Value))
            .ToList();
    }

    public async Task<IReadOnlyList<ChargeStockDto>> ChargeStockAsync(string lotNumber, CancellationToken ct)
    {
        var lot = lotNumber;
        var rows = await _db.StockItems.AsNoTracking()
            .Where(s => s.LotNumber == lot && s.Quantity > 0)
            .Join(_db.Articles.AsNoTracking(),
                  s => s.ArticleId, a => a.Id,
                  (s, a) => new { s, a })
            .Join(_db.StorageLocations.AsNoTracking(),
                  sa => sa.s.StorageLocationId, b => b.Id,
                  (sa, b) => new ChargeStockDto(
                      sa.s.Id, sa.a.Id, sa.a.Sku, b.Id, b.Code,
                      sa.s.Quantity, sa.s.ExpiryDate))
            .ToListAsync(ct);
        return rows;
    }
}
