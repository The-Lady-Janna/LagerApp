using Lager.Contracts.Reports;
using Lager.Domain.Inbound;
using Lager.Domain.Orders;
using Lager.Domain.PickLists;
using Lager.Domain.Stock;

namespace Lager.Application.Reports;

/// <summary>
/// Read-only aggregate KPIs for the dashboard. Reads through the narrow
/// <see cref="IReportQueryGateway"/> because each KPI is a one-off projection
/// that doesn't fit any existing repository contract and the read load on
/// /api/reports is low.
///
/// Datenbasis der Auswertungen (Top-Artikel, ABC, Heatmap, Slotting, Picks/h):
/// tatsächlich gepickte Positionen. Das sind Picklisten im Status Picked oder
/// Completed; die Menge ist die bestätigte Menge (ConfirmedQuantity) bzw. bei
/// noch nicht gepackten Listen die Sollmenge. Offene, laufende und stornierte
/// Picklisten und Positionen mit Ist-Menge 0 (Kurzpick auf 0) zählen nicht.
/// Alle Zeitfenster und Tage sind UTC.
/// </summary>
public class ReportService
{
    /// <summary>
    /// Währung der Bestandsbewertung. Die Artikelstammdaten (PurchasePriceCents) tragen keine
    /// Währung; das System bewertet durchgängig in EUR (Mehrwährungs-Bewertung ist nicht vorgesehen).
    /// </summary>
    public const string ValuationCurrency = "EUR";

    /// <summary>Kurzbeschreibung der Bewertungsmethode, wird mit dem Ergebnis ausgeliefert.</summary>
    public const string ValuationBasis =
        "FIFO über die Zugangsbuchungen des Bewegungs-Ledgers (Kosten-Snapshot je Buchung); " +
        "Umlagerungen/Nachschub sowie Sperr-/Ausschussbuchungen (Retoure B-Ware/Defekt) bewertungsneutral; " +
        "Menge ohne Ledger-Historie zum Stammpreis (FallbackQuantity)";

    private readonly IReportQueryGateway _gateway;

    public ReportService(IReportQueryGateway gateway) => _gateway = gateway;

    /// <summary>
    /// Dashboard-Kennzahlen für [jetzt - rangeDays, jetzt].
    /// <list type="bullet">
    ///   <item>Bestellungen/Picklisten (Zeitraum): nach Anlagedatum (CreatedAt), alle Status.</item>
    ///   <item>Picks pro Stunde: gepickte Positionen (Zeilen mit Ist-Menge &gt; 0) geteilt durch die tatsächliche
    ///         Zeitspanne zwischen erstem und letztem Pick im Fenster (mindestens 1 h, höchstens das Fenster).</item>
    ///   <item>Top-Artikel: gepickte Zeilen und Ist-Menge (siehe Klassenkommentar).</item>
    ///   <item>Tagesreihen: ein Punkt je UTC-Kalendertag vom Starttag bis einschließlich heute.</item>
    /// </list>
    /// </summary>
    public async Task<ReportDashboardDto> DashboardAsync(int rangeDays, CancellationToken ct = default)
    {
        rangeDays = Math.Clamp(rangeDays, 1, 365);
        var to = DateTime.UtcNow;
        var from = to.AddDays(-rangeDays);

        var orders = await _gateway.OrdersInRangeAsync(from, to, ct);
        var picklists = await _gateway.PickListsInRangeAsync(from, to, ct);
        var picked = await _gateway.PickedItemsInRangeAsync(from, to, ct);
        var stockSummary = await _gateway.BinStockSummaryAsync(ct);
        var articleNames = await _gateway.ArticleNamesAsync(picked.Select(i => i.ArticleId).Distinct(), ct);
        var binCodes = await _gateway.BinCodesAsync(stockSummary.Select(s => s.BinId).Distinct(), ct);

        var completedPickLists = picklists.Where(p => p.Status == PickListStatus.Completed).ToList();
        var picksPerHour = DashboardMath.PicksPerHour(picked.Select(p => p.PickedAt).ToList(), from, to);
        var avgDistanceMm = completedPickLists.Count == 0
            ? 0
            : (int)completedPickLists.Average(p => p.TotalDistanceMm);

        var headline = new List<DashboardKpiDto>
        {
            new("Bestellungen (Zeitraum)", orders.Count.ToString(), null, $"seit {from:dd.MM.yyyy}"),
            new("Picklisten (Zeitraum)", picklists.Count.ToString(), null, $"{completedPickLists.Count} fertig"),
            new("Picks pro Stunde", picksPerHour.ToString("0.00"), "/h", "gepickte Positionen / Zeitspanne erster bis letzter Pick"),
            new("Ø Pickdistanz", (avgDistanceMm / 1000.0).ToString("0.0"), "m", "über abgeschlossene Picklisten"),
        };

        var topArticles = picked
            .GroupBy(i => i.ArticleId)
            .Select(g =>
            {
                var meta = articleNames.TryGetValue(g.Key, out var m) ? m : (Sku: "?", Name: "?");
                return new TopArticleDto(g.Key, meta.Sku, meta.Name, g.Count(), ClampToInt(g.Sum(i => (long)i.Quantity)));
            })
            .OrderByDescending(x => x.PickCount)
            .ThenByDescending(x => x.TotalQuantity)
            .ThenBy(x => x.Sku, StringComparer.Ordinal)
            .Take(10)
            .ToList();

        var topBins = stockSummary
            .Select(s => new TopBinDto(
                s.BinId,
                binCodes.GetValueOrDefault(s.BinId, "?"),
                s.ArticleCount,
                s.TotalQuantity))
            .OrderByDescending(b => b.TotalQuantity)
            .Take(10)
            .ToList();

        var orderStatusBreakdown = orders
            .GroupBy(o => o.Status)
            .Select(g => new OrderStatusBucketDto(g.Key.ToString(), g.Count()))
            .OrderBy(b => b.Status)
            .ToList();

        var picklistsPerDay = DashboardMath.BucketByDay(picklists.Select(p => p.CreatedAt), from, to);
        var ordersPerDay = DashboardMath.BucketByDay(orders.Select(o => o.CreatedAt), from, to);

        return new ReportDashboardDto(
            rangeDays, from, to,
            headline, topArticles, topBins,
            orderStatusBreakdown, picklistsPerDay, ordersPerDay);
    }

    // ---- Welle 8: Pick-Heatmap ---------------------------------------------

    /// <summary>Gepickte Positionen je Bin im Zeitraum (siehe Klassenkommentar zur Datenbasis).</summary>
    public async Task<IReadOnlyList<BinHeatPointDto>> BinHeatmapAsync(int rangeDays, CancellationToken ct = default)
    {
        rangeDays = Math.Clamp(rangeDays, 1, 365);
        var to = DateTime.UtcNow;
        var from = to.AddDays(-rangeDays);
        var heat = await _gateway.BinPickCountsAsync(from, to, ct);
        var codes = await _gateway.BinCodesAsync(heat.Select(h => h.BinId), ct);
        return heat
            .Select(h => new BinHeatPointDto(h.BinId, codes.GetValueOrDefault(h.BinId, "?"), h.PickCount))
            .OrderByDescending(h => h.PickCount)
            .ThenBy(h => h.BinCode, StringComparer.Ordinal)
            .ToList();
    }

    // ---- Welle 8: Dead-Stock ------------------------------------------------

    /// <summary>
    /// Artikel mit Bestand, die seit mindestens <paramref name="days"/> Tagen nicht bewegt wurden.
    /// "Bewegung" = letzter Eintrag im StockMovement-Ledger ohne interne Umlagerungen (Nachschub,
    /// Bin-Verschiebung): eine reine Umlagerung ist keine Nachfrage. Artikel ohne Ledger-Eintrag gelten
    /// als "nie bewegt" (DaysSinceLastMovement = null). LocationCount = Anzahl Lagerplätze mit Bestand.
    /// </summary>
    public async Task<IReadOnlyList<DeadStockArticleDto>> DeadStockAsync(int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 3650);
        var lastMoves = await _gateway.ArticleLastMovementAsync(ct);
        var totals = await _gateway.ArticleStockTotalsAsync(ct);

        var now = DateTime.UtcNow;
        var threshold = now.AddDays(-days);
        var candidates = new List<(Guid Id, ArticleStockTotals Totals, DateTime? LastMove)>();
        foreach (var (id, t) in totals)
        {
            if (t.Quantity <= 0) continue;  // leer = nicht "dead"
            var lastMove = lastMoves.GetValueOrDefault(id);
            if (lastMove is DateTime moved && moved >= threshold) continue;  // bewegt sich noch
            candidates.Add((id, t, lastMove));
        }

        var names = await _gateway.ArticleNamesAsync(candidates.Select(c => c.Id), ct);
        var result = new List<DeadStockArticleDto>(candidates.Count);
        foreach (var (id, t, lastMove) in candidates)
        {
            var meta = names.TryGetValue(id, out var m) ? m : (Sku: "?", Name: "?");
            int? daysSince = lastMove is null ? null : (int)(now - lastMove.Value).TotalDays;
            result.Add(new DeadStockArticleDto(id, meta.Sku, meta.Name, t.Quantity, t.LocationCount, daysSince));
        }
        return result
            .OrderByDescending(d => d.DaysSinceLastMovement ?? int.MaxValue)
            .ThenBy(d => d.Sku, StringComparer.Ordinal)
            .Take(50)
            .ToList();
    }

    // ---- Welle 8: ABC-Analyse ----------------------------------------------

    /// <summary>
    /// ABC-Analyse über die Ist-Pickmenge je Artikel im Zeitraum (nur Artikel mit Picks).
    /// Klassifizierung siehe <see cref="AbcClassifier"/> (klassisches Pareto A=80 % / B=15 % / C=5 %).
    /// </summary>
    public async Task<IReadOnlyList<AbcArticleDto>> AbcAnalysisAsync(int rangeDays, CancellationToken ct = default)
    {
        rangeDays = Math.Clamp(rangeDays, 1, 365);
        var to = DateTime.UtcNow;
        var from = to.AddDays(-rangeDays);
        var picked = await _gateway.PickedItemsInRangeAsync(from, to, ct);
        if (picked.Count == 0) return Array.Empty<AbcArticleDto>();

        var grouped = picked.GroupBy(i => i.ArticleId)
            .Select(g => new { Id = g.Key, PickCount = g.Count(), Qty = g.Sum(i => (long)i.Quantity) })
            .ToList();
        var names = await _gateway.ArticleNamesAsync(grouped.Select(g => g.Id), ct);
        string SkuOf(Guid id) => names.TryGetValue(id, out var m) ? m.Sku : "?";

        var classified = AbcClassifier.Classify(grouped.Select(g => new AbcInput(g.Id, g.Qty, SkuOf(g.Id))));
        var pickCounts = grouped.ToDictionary(g => g.Id, g => g.PickCount);

        return classified
            .Select(c =>
            {
                var meta = names.TryGetValue(c.Id, out var m) ? m : (Sku: "?", Name: "?");
                return new AbcArticleDto(c.Id, meta.Sku, meta.Name, pickCounts[c.Id], ClampToInt(c.Quantity), c.SharePercent, c.Class);
            })
            .ToList();
    }

    // ---- Welle 8: Live-Status ----------------------------------------------

    public async Task<LiveStatusDto> LiveStatusAsync(CancellationToken ct = default)
    {
        var s = await _gateway.LiveStatusAsync(ct);
        return new LiveStatusDto(
            s.OrdersOpen, s.OrdersInPicking, s.OrdersPicked, s.OrdersPacked,
            s.PicklistsPending, s.PicklistsInProgress, s.PicklistsPickedReadyToPack, s.PicklistsCompletedToday,
            s.InventoryCountsOpen, s.ReplenishmentTasksOpen,
            s.StockAlertsCritical, s.StockAlertsWarning,
            DateTime.UtcNow);
    }

    // ---- Picker-Performance --------------------------------------------------

    public async Task<PickerPerformanceDto> PickerPerformanceAsync(int rangeDays, CancellationToken ct = default)
    {
        rangeDays = Math.Clamp(rangeDays, 1, 365);
        var to = DateTime.UtcNow;
        var from = to.AddDays(-rangeDays);
        var rows = await _gateway.PickerActivityAsync(from, to, ct);

        // Pro Picker aggregieren: Anzahl Picklisten, Summe Items, Ø Distanz, Ø Dauer.
        var grouped = rows
            .GroupBy(r => r.Picker)
            .Select(g => new PickerPerformanceRowDto(
                g.Key,
                g.Count(),
                g.Sum(r => r.ItemsPicked),
                Math.Round(g.Average(r => r.TotalDistanceMm) / 1000.0, 2),
                g.Any(r => r.DurationMinutes.HasValue)
                    ? Math.Round(g.Where(r => r.DurationMinutes.HasValue).Average(r => r.DurationMinutes!.Value), 1)
                    : (double?)null))
            .OrderByDescending(r => r.PickListsCompleted)
            .ToList();

        return new PickerPerformanceDto(from, to, grouped);
    }

    // ---- Welle 4: Bestandsbewertung ----------------------------------------

    /// <summary>
    /// Aktueller Lagerwert pro Artikel und insgesamt. FIFO-Walk über die Zugangsbuchungen des
    /// StockMovement-Ledgers, siehe <see cref="StockValuationCalculator"/>: Bewertet wird mit dem
    /// Kosten-Snapshot der Buchung (Stammpreis zum Buchungszeitpunkt, NICHT der tatsächliche
    /// Rechnungs-/PO-Preis, solange der Wareneingang keinen Preis erfasst). Umlagerungen und
    /// Sperr-/Ausschussbuchungen sind bewertungsneutral. Menge ohne Ledger-Historie wird zum aktuellen Stammpreis bewertet und
    /// je Zeile in <see cref="StockValuationLineDto.FallbackQuantity"/> ausgewiesen.
    /// </summary>
    public async Task<StockValuationDto> StockValuationAsync(CancellationToken ct = default)
    {
        var baseRows = await _gateway.StockValuationBaseAsync(ct);
        var ledger = await _gateway.ValuationLedgerAsync(ct);
        var lines = StockValuationCalculator.Compute(baseRows, ledger)
            .Where(l => l.Quantity > 0)
            .OrderByDescending(l => l.TotalValueCents)
            .ThenBy(l => l.Sku, StringComparer.Ordinal)
            .ToList();
        return new StockValuationDto(
            DateTime.UtcNow,
            lines.Count,
            lines.Sum(l => (long)l.Quantity),
            lines.Sum(l => l.TotalValueCents),
            ValuationCurrency,
            lines,
            lines.Sum(l => (long)l.FallbackQuantity * l.UnitPriceCents),
            ValuationBasis);
    }

    // ---- Welle 4: Charge-/Lot-Trace ----------------------------------------

    /// <summary>
    /// Rückverfolgung einer Charge: Wareneingänge (Zeilen mit dieser Charge samt Lieferungsstatus), aktueller Bestand je
    /// Lagerplatz, alle Ledger-Bewegungen und die daraus ableitbaren Bestellungen (Pick-Buchungen -> Pickliste ->
    /// Positionen). Die Chargennummer wird wie beim Bestand normalisiert (getrimmt). Unbekannte Charge = null.
    /// Für die Anzeige bekommen Wareneingänge und Bewegungen den Lagerplatz-Code, Bewegungen zusätzlich den Artikel.
    /// </summary>
    public async Task<ChargeTraceDto?> ChargeTraceAsync(string lotNumber, CancellationToken ct = default)
    {
        var lot = StockItem.NormalizeLot(lotNumber);
        if (lot is null) return null;
        var inbounds = await _gateway.ChargeInboundsAsync(lot, ct);
        var stock = await _gateway.ChargeStockAsync(lot, ct);
        var movements = await _gateway.ChargeMovementsAsync(lot, ct);
        if (inbounds.Count == 0 && stock.Count == 0 && movements.Count == 0) return null;

        var binCodes = await _gateway.BinCodesAsync(
            inbounds.Select(i => i.TargetBinId).Concat(movements.Select(m => m.BinId)).Distinct().ToList(), ct);
        var articleIds = movements.Select(m => m.ArticleId).OfType<Guid>().Distinct().ToList();
        var articles = articleIds.Count == 0
            ? new Dictionary<Guid, (string Sku, string Name)>()
            : await _gateway.ArticleNamesAsync(articleIds, ct);
        var orders = await _gateway.ChargeOrdersAsync(lot, ct);

        return new ChargeTraceDto(
            lot,
            inbounds.Select(i => i with { TargetBinCode = binCodes.GetValueOrDefault(i.TargetBinId) }).ToList(),
            stock,
            movements.Select(m => m with
            {
                BinCode = binCodes.GetValueOrDefault(m.BinId),
                ArticleSku = m.ArticleId is Guid id && articles.TryGetValue(id, out var article) ? article.Sku : null,
            }).ToList(),
            stock.Sum(s => s.Quantity),
            inbounds.Where(i => i.Status == nameof(InboundShipmentStatus.Received)).Sum(i => i.Quantity),
            orders);
    }

    // ---- MHD-Warnliste ------------------------------------------------------

    /// <summary>
    /// Ab so vielen Tagen bis zum Ablauf (einschließlich) gilt eine Charge als kritisch, davor als "bald" (Grenze fest, nicht
    /// konfigurierbar; die Oberfläche spiegelt sie in features/traceability/expiry.ts).
    /// </summary>
    public const int ExpiryCriticalDays = 7;

    /// <summary>
    /// Obergrenze der abfragbaren Frist in Tagen (10 Jahre, wie die API-Grenze für <c>days</c>; über HTTP sind ohnehin nur 1 bis
    /// 3660 erlaubt, der Dienst kappt zusätzlich für andere Aufrufer).
    /// </summary>
    public const int ExpiryMaxDays = 3660;

    /// <summary>
    /// Bestandszeilen mit Menge &gt; 0, deren MHD abgelaufen ist oder innerhalb von <paramref name="days"/> Tagen abläuft
    /// (gekappt auf 0 bis <see cref="ExpiryMaxDays"/>; 0 = nur Abgelaufenes und heute Ablaufendes). Alles in UTC-Kalendertagen:
    /// am Ablauftag selbst ist die Ware noch verwendbar (Status Critical, 0 Tage), ab dem Folgetag Expired. Sortiert nach MHD
    /// (älteste zuerst), dann SKU und Lagerplatz. Zeilen ohne MHD kommen nie vor.
    /// </summary>
    public async Task<IReadOnlyList<ExpiringStockDto>> ExpiringStockAsync(int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 0, ExpiryMaxDays);
        var today = DateTime.UtcNow.Date;
        var rows = await _gateway.ExpiringStockAsync(today.AddDays(days), ct);
        return rows
            .Select(r =>
            {
                var daysUntil = (int)(r.ExpiryDate.Date - today).TotalDays;
                var status = daysUntil < 0 ? ExpiryStatus.Expired
                    : daysUntil <= ExpiryCriticalDays ? ExpiryStatus.Critical
                    : ExpiryStatus.Soon;
                return new ExpiringStockDto(r.StockItemId, r.ArticleId, r.Sku, r.Name, r.BinId, r.BinCode,
                    r.LotNumber, r.Quantity, r.ExpiryDate, daysUntil, status);
            })
            .OrderBy(x => x.ExpiryDate)
            .ThenBy(x => x.ArticleSku, StringComparer.Ordinal)
            .ThenBy(x => x.BinCode, StringComparer.Ordinal)
            .ThenBy(x => x.LotNumber, StringComparer.Ordinal)
            .ToList();
    }

    // ---- Welle 8: Bestands-Trend pro Artikel -------------------------------

    public async Task<StockTrendDto?> StockTrendAsync(Guid articleId, int days, CancellationToken ct = default)
    {
        days = Math.Clamp(days, 1, 365);
        var summary = await _gateway.ArticleStockSummaryAsync(articleId, ct);
        if (summary is null) return null;

        var to = DateTime.UtcNow;
        var from = to.AddDays(-days);
        var events = await _gateway.StockMovementsAsync(articleId, from, to, ct);

        // Reconstruct backwards from current state: subtract each delta to walk
        // back in time. Then re-walk forward to produce daily points.
        var cur = summary.Value.CurrentQty;
        var orderedEvents = events.OrderByDescending(e => e.At).ToList();
        var walkedBack = cur;
        foreach (var e in orderedEvents) walkedBack -= e.QuantityDelta;
        var startQty = walkedBack;

        var series = new List<StockTrendPointDto>();
        series.Add(new StockTrendPointDto(from, startQty));
        var running = startQty;
        foreach (var e in events.OrderBy(e => e.At))
        {
            running += e.QuantityDelta;
            series.Add(new StockTrendPointDto(e.At, running));
        }
        series.Add(new StockTrendPointDto(to, cur));

        return new StockTrendDto(articleId, summary.Value.Sku, summary.Value.Name, cur, series);
    }

    private static int ClampToInt(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);
}

/// <summary>Reine Rechenfunktionen der Dashboard-Kennzahlen (ohne Gateway, direkt testbar).</summary>
public static class DashboardMath
{
    /// <summary>
    /// Picks pro Stunde = Anzahl Picks / Stunden zwischen dem ersten und dem letzten Pick. Die Zeitspanne
    /// beträgt mindestens 1 Stunde (ein einzelner Pick-Schub ergibt keine unendliche Rate) und höchstens
    /// das Auswertungsfenster. Ohne Picks: 0. Ein "Pick" ist eine gepickte Position (Pickzeile).
    /// </summary>
    public static double PicksPerHour(IReadOnlyCollection<DateTime> pickTimes, DateTime from, DateTime to)
    {
        if (pickTimes.Count == 0) return 0;
        var spanHours = (pickTimes.Max() - pickTimes.Min()).TotalHours;
        var windowHours = Math.Max((to - from).TotalHours, 1.0);
        spanHours = Math.Min(Math.Max(spanHours, 1.0), windowHours);
        return pickTimes.Count / spanHours;
    }

    /// <summary>
    /// Zählt Zeitstempel je UTC-Kalendertag und liefert eine lückenlose Reihe vom Tag von
    /// <paramref name="from"/> bis einschließlich dem Tag von <paramref name="to"/> (also auch "heute"),
    /// damit das Frontend keine Lücken füllen muss. Leere Tage haben Count 0.
    /// </summary>
    public static List<DailySeriesPointDto> BucketByDay(IEnumerable<DateTime> timestamps, DateTime from, DateTime to)
    {
        var counts = timestamps
            .GroupBy(t => DateOnly.FromDateTime(t))
            .ToDictionary(g => g.Key, g => g.Count());

        var fromDay = DateOnly.FromDateTime(from);
        var toDay = DateOnly.FromDateTime(to);
        var result = new List<DailySeriesPointDto>();
        for (var d = fromDay; d <= toDay; d = d.AddDays(1))
            result.Add(new DailySeriesPointDto(d, counts.GetValueOrDefault(d, 0)));
        return result;
    }
}

/// <summary>Eingabe der ABC-Klassifizierung: Artikel, Basismenge und Sortierschlüssel (SKU) für den Gleichstand.</summary>
public readonly record struct AbcInput(Guid Id, long Quantity, string SortKey);

/// <summary>Ergebnis je Artikel: Anteil in Prozent (2 Nachkommastellen) und Klasse A/B/C.</summary>
public readonly record struct AbcResult(Guid Id, long Quantity, decimal SharePercent, string Class);

/// <summary>
/// Klassisches Pareto/ABC über eine Basismenge (hier: Ist-Pickmenge). Reine Ganzzahl-/decimal-Rechnung,
/// keine Gleitkomma-Drift an den Klassengrenzen.
///
/// Regel: Artikel absteigend nach Menge (Gleichstand: SKU aufsteigend, dann Id). Die Klasse hängt vom
/// kumulierten Mengenanteil VOR dem Artikel ab; der Artikel, der die Grenze überschreitet, gehört also noch
/// zur oberen Klasse:
/// <list type="bullet">
///   <item>A: Anteil vor dem Artikel &lt; aPercent (Standard 80 %)</item>
///   <item>B: aPercent &lt;= Anteil vor dem Artikel &lt; bPercent (Standard 95 %)</item>
///   <item>C: Anteil vor dem Artikel &gt;= bPercent</item>
/// </list>
/// Untergrenzen sind inklusive, Obergrenzen exklusive: ein Artikel, der genau bei 80 % beginnt, ist B.
/// Ein Einzelartikel (100 %) ist damit A, ebenso ein dominanter Top-Artikel mit mehr als 80 %.
/// Die Anteile sind auf 0,01 gerundet und nach dem Verfahren der größten Reste so verteilt, dass sie
/// sich exakt auf 100,00 summieren. Artikel mit Menge &lt;= 0 werden ignoriert.
/// </summary>
public static class AbcClassifier
{
    public const int DefaultAPercent = 80;
    public const int DefaultBPercent = 95;

    public static IReadOnlyList<AbcResult> Classify(
        IEnumerable<AbcInput> items, int aPercent = DefaultAPercent, int bPercent = DefaultBPercent)
    {
        if (aPercent <= 0 || aPercent >= bPercent || bPercent > 100)
            throw new ArgumentOutOfRangeException(nameof(aPercent), "Es muss 0 < aPercent < bPercent <= 100 gelten");

        var sorted = items
            .Where(i => i.Quantity > 0)
            .OrderByDescending(i => i.Quantity)
            .ThenBy(i => i.SortKey, StringComparer.Ordinal)
            .ThenBy(i => i.Id)
            .ToList();
        if (sorted.Count == 0) return Array.Empty<AbcResult>();

        long total = sorted.Sum(i => i.Quantity);

        // Anteile in Hundertstel-Prozent (Summe 10000) nach größten Resten verteilen.
        var hundredths = new long[sorted.Count];
        var remainders = new long[sorted.Count];
        long assigned = 0;
        for (var i = 0; i < sorted.Count; i++)
        {
            var scaled = sorted[i].Quantity * 10_000;
            hundredths[i] = scaled / total;
            remainders[i] = scaled % total;
            assigned += hundredths[i];
        }
        var deficit = 10_000 - assigned;
        foreach (var idx in Enumerable.Range(0, sorted.Count)
                     .OrderByDescending(i => remainders[i]).ThenBy(i => i).Take((int)deficit))
            hundredths[idx]++;

        var result = new List<AbcResult>(sorted.Count);
        long cumulativeBefore = 0;
        for (var i = 0; i < sorted.Count; i++)
        {
            var cls = cumulativeBefore * 100 < (long)aPercent * total ? "A"
                : cumulativeBefore * 100 < (long)bPercent * total ? "B"
                : "C";
            result.Add(new AbcResult(sorted[i].Id, sorted[i].Quantity, hundredths[i] / 100m, cls));
            cumulativeBefore += sorted[i].Quantity;
        }
        return result;
    }
}

/// <summary>
/// FIFO-Bestandsbewertung als reine Funktion über Artikelbestand und Ledger.
///
/// Regeln:
/// <list type="number">
///   <item>Jeder Zugang (Wareneingang, Retoure, positive Inventur-/Korrekturbuchung) legt einen Layer mit dem
///         Kosten-Snapshot der Buchung an; jeder Abgang verbraucht die ältesten Layer zuerst.</item>
///   <item>Umlagerungen (ReplenishmentOut/In, BinMove) sind bewertungsneutral: sie werden nicht als Abgang plus
///         Neuzugang gewertet, sondern übersprungen. Der Wert ändert sich durch eine Umlagerung nie, auch
///         nicht, wenn sich der Stammpreis dazwischen geändert hat.</item>
///   <item>Ebenso bewertungsneutral sind die Sperr-/Ausschussbuchungen der Retoure (ReturnB, ReturnScrap): das
///         Ledger führt sie als Paar aus Zugang und Abgang derselben Menge (netto 0), die Ware erreicht den
///         Verkaufsbestand nie. Als normaler Zu-/Abgang gewertet, entstünde ein neuer Layer zum aktuellen Preis,
///         während der Abgang den ältesten Layer verbraucht - bei geändertem Einkaufspreis verschöbe sich der
///         Lagerwert um Menge mal Preisdifferenz.</item>
///   <item>Menge ohne Ledger-Historie (Bestand, der schon vor dem Ledger existierte) ist der älteste Bestand:
///         sie steht als erster Layer am Anfang und wird vor allen Ledger-Zugängen verbraucht. Sie wird zum
///         aktuellen Stammpreis bewertet und als FallbackQuantity gekennzeichnet.</item>
///   <item>Defensiv: Übersteigen die Layer den echten Bestand (Ledger sagt mehr als vorhanden), werden die
///         ältesten Layer gekappt; fehlen Layer, wird die Differenz als Fallback zum Stammpreis bewertet.</item>
/// </list>
/// Retouren und positive Inventurdifferenzen werden mit dem Snapshot zum Buchungszeitpunkt bewertet
/// (aktueller Stammpreis, nicht Originalpreis der Lieferung). Die Summen sind long.
/// </summary>
public static class StockValuationCalculator
{
    /// <summary>Buchungen, die den Lagerwert nie ändern: Umlagerungen und die netto-0-Paare der Sperr-/Ausschussbuchung.</summary>
    private static bool IsValuationNeutral(StockMovementReason reason) =>
        reason is StockMovementReason.ReplenishmentOut
            or StockMovementReason.ReplenishmentIn
            or StockMovementReason.BinMove
            or StockMovementReason.ReturnB
            or StockMovementReason.ReturnScrap;

    /// <summary>Bewertet alle übergebenen Artikel; Artikel mit Bestand &lt;= 0 werden mit Wert 0 geführt.</summary>
    public static IReadOnlyList<StockValuationLineDto> Compute(
        IReadOnlyList<ValuationArticleSnapshot> articles,
        IReadOnlyList<LedgerMovementSnapshot> ledger)
    {
        var byArticle = ledger.ToLookup(m => m.ArticleId);
        var lines = new List<StockValuationLineDto>(articles.Count);
        foreach (var a in articles)
        {
            var (value, fallbackQty) = ValueArticle(a.OnHandQuantity, a.PurchasePriceCents, byArticle[a.ArticleId]);
            lines.Add(new StockValuationLineDto(
                a.ArticleId, a.Sku, a.Name,
                a.OnHandQuantity,
                a.PurchasePriceCents,   // "Last-known" Einkaufspreis bleibt info-only
                value,
                fallbackQty));
        }
        return lines;
    }

    /// <summary>Wert (Cent) und Fallback-Menge (zum Stammpreis bewertet) eines Artikels.</summary>
    public static (long ValueCents, int FallbackQuantity) ValueArticle(
        int onHand, int fallbackUnitCostCents, IEnumerable<LedgerMovementSnapshot> movements)
    {
        if (onHand <= 0) return (0, 0);

        // Umlagerungen und Sperr-/Ausschussbuchungen übersprungen; bei gleichem Zeitstempel kommen Zugänge vor Abgängen.
        var regular = movements
            .Where(m => !IsValuationNeutral(m.Reason))
            .OrderBy(m => m.At)
            .ThenByDescending(m => m.QuantityDelta)
            .ToList();

        // Layer: (Menge, Stückkosten, aus Stammpreis-Fallback?)
        var layers = new List<(int Qty, int UnitCost, bool Fallback)>(regular.Count + 1);

        // Bestand ohne Ledger-Historie = Ist-Bestand minus Netto-Summe des Ledgers; ältester Bestand.
        long ledgerNet = 0;
        foreach (var m in regular) ledgerNet += m.QuantityDelta;
        var preLedger = onHand - ledgerNet;
        if (preLedger > 0) layers.Add(((int)Math.Min(preLedger, int.MaxValue), fallbackUnitCostCents, true));

        foreach (var m in regular)
        {
            if (m.QuantityDelta > 0)
            {
                layers.Add((m.QuantityDelta, m.UnitCostCents, false));
                continue;
            }
            // Ledger sagt mehr verbraucht als reingekommen: den Überhang ignorieren (keine negativen Layer).
            ConsumeFromFront(layers, -(long)m.QuantityDelta);
        }

        long layerQty = 0;
        foreach (var l in layers) layerQty += l.Qty;
        if (layerQty > onHand) ConsumeFromFront(layers, layerQty - onHand);
        else if (layerQty < onHand)
            layers.Add(((int)Math.Min(onHand - layerQty, int.MaxValue), fallbackUnitCostCents, true));

        long value = 0;
        long fallbackQty = 0;
        foreach (var (qty, cost, fallback) in layers)
        {
            value += (long)qty * cost;
            if (fallback) fallbackQty += qty;
        }
        return (value, (int)Math.Min(fallbackQty, int.MaxValue));
    }

    private static void ConsumeFromFront(List<(int Qty, int UnitCost, bool Fallback)> layers, long toRemove)
    {
        while (toRemove > 0 && layers.Count > 0)
        {
            var (qty, cost, fallback) = layers[0];
            if (qty <= toRemove)
            {
                toRemove -= qty;
                layers.RemoveAt(0);
            }
            else
            {
                layers[0] = ((int)(qty - toRemove), cost, fallback);
                toRemove = 0;
            }
        }
    }
}

/// <summary>
/// Narrow read-side interface to keep ReportService testable and Application
/// free of EF Core. The Infrastructure implementation uses LagerDbContext.
/// </summary>
public interface IReportQueryGateway
{
    Task<IReadOnlyList<OrderSnapshot>> OrdersInRangeAsync(DateTime from, DateTime to, CancellationToken ct);

    /// <summary>Picklisten (alle Status), die im Zeitraum angelegt wurden (CreatedAt).</summary>
    Task<IReadOnlyList<PickListSnapshot>> PickListsInRangeAsync(DateTime from, DateTime to, CancellationToken ct);

    /// <summary>
    /// Tatsächlich gepickte Positionen: Pickzeilen von Picklisten im Status Picked/Completed, deren letzte
    /// Änderung (UpdatedAt) im Zeitraum liegt, mit der Ist-Menge (ConfirmedQuantity, sonst Sollmenge) &gt; 0.
    /// Basis für Top-Artikel, ABC, Picks/h und Slotting.
    /// </summary>
    Task<IReadOnlyList<PickedItemSnapshot>> PickedItemsInRangeAsync(DateTime from, DateTime to, CancellationToken ct);

    Task<IReadOnlyList<BinStockSnapshot>> BinStockSummaryAsync(CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, (string Sku, string Name)>> ArticleNamesAsync(IEnumerable<Guid> articleIds, CancellationToken ct);
    Task<IReadOnlyDictionary<Guid, string>> BinCodesAsync(IEnumerable<Guid> binIds, CancellationToken ct);

    // Welle 8: heatmap + analytics

    /// <summary>
    /// Gepickte Positionen je Bin im Zeitraum [from, to] (gleiche Datenbasis wie
    /// <see cref="PickedItemsInRangeAsync"/>): Anzahl Pickzeilen mit Ist-Menge &gt; 0, nicht die Summenmenge.
    /// </summary>
    Task<IReadOnlyList<BinHeatSnapshot>> BinPickCountsAsync(DateTime from, DateTime to, CancellationToken ct);

    /// <summary>
    /// Alle Artikel mit dem Zeitpunkt der letzten Ledger-Bewegung (für "Dead-Stock"; null = nie bewegt).
    /// Interne Umlagerungen (Nachschub, Bin-Verschiebung) zählen nicht als Bewegung.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, DateTime?>> ArticleLastMovementAsync(CancellationToken ct);

    /// <summary>Live-status: aggregate counts by Order/PickList status + Inventory + Replenishment + StockAlerts.</summary>
    Task<LiveStatusSnapshot> LiveStatusAsync(CancellationToken ct);

    /// <summary>Audit-trail-based stock trend for one article — quantity delta per event.</summary>
    Task<IReadOnlyList<StockMovementEvent>> StockMovementsAsync(Guid articleId, DateTime from, DateTime to, CancellationToken ct);

    Task<(Guid Id, string Sku, string Name, int CurrentQty)?> ArticleStockSummaryAsync(Guid articleId, CancellationToken ct);

    /// <summary>
    /// Aktueller Gesamtbestand und Anzahl Lagerplätze mit Bestand pro Artikel in einer Abfrage
    /// (Dead-Stock-Liste). Nur Artikel mit Bestand &gt; 0 sind enthalten.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ArticleStockTotals>> ArticleStockTotalsAsync(CancellationToken ct);

    Task<IReadOnlyList<ChargeInboundDto>> ChargeInboundsAsync(string lotNumber, CancellationToken ct);
    Task<IReadOnlyList<ChargeStockDto>> ChargeStockAsync(string lotNumber, CancellationToken ct);
    Task<IReadOnlyList<ChargeMovementDto>> ChargeMovementsAsync(string lotNumber, CancellationToken ct);

    /// <summary>
    /// Bestellungen, die aus dieser Charge beliefert worden sein können: Pick-Buchungen der Charge (Ledger, Verweis auf die
    /// Pickliste) -> Positionen dieser Pickliste mit gleichem Artikel und Lagerplatz -> Bestellung. Neueste zuerst.
    /// Standardimplementierung: keine (Gateways ohne Bestell-Bezug, z. B. Test-Attrappen, müssen sie nicht kennen).
    /// </summary>
    Task<IReadOnlyList<ChargeOrderDto>> ChargeOrdersAsync(string lotNumber, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ChargeOrderDto>>(Array.Empty<ChargeOrderDto>());

    /// <summary>
    /// Bestandszeilen mit Menge &gt; 0 und MHD am oder vor <paramref name="untilDate"/> (Kalendertag, UTC), mit Artikel und Lagerplatz.
    /// Reihenfolge beliebig - Status, Tage und Sortierung setzt <see cref="ReportService.ExpiringStockAsync"/>.
    /// Standardimplementierung: nicht unterstützt (nur das EF-Gateway liest den Bestand).
    /// </summary>
    Task<IReadOnlyList<ExpiringStockSnapshot>> ExpiringStockAsync(DateTime untilDate, CancellationToken ct) =>
        throw new NotSupportedException("Dieses Gateway liefert keine MHD-Auswertung");

    /// <summary>Artikel mit Bestand &gt; 0: Ist-Bestand und aktueller Stammpreis (Fallback-Bewertung).</summary>
    Task<IReadOnlyList<ValuationArticleSnapshot>> StockValuationBaseAsync(CancellationToken ct);

    /// <summary>
    /// Ledger-Buchungen aller Artikel mit Bestand &gt; 0 (ohne Zeitgrenze: FIFO braucht die volle Historie),
    /// Basis der Bewertung durch <see cref="StockValuationCalculator"/>.
    /// </summary>
    Task<IReadOnlyList<LedgerMovementSnapshot>> ValuationLedgerAsync(CancellationToken ct);

    /// <summary>
    /// Rohdaten für den Picker-Performance-Report: pro abgeschlossener
    /// PickList im Zeitraum den Picker (AssignedTo, oder via Audit-Trail-
    /// Fallback), Distanz, Item-Count, Dauer (Created→Completed).
    /// </summary>
    Task<IReadOnlyList<PickerActivitySnapshot>> PickerActivityAsync(DateTime from, DateTime to, CancellationToken ct);
}

public record PickerActivitySnapshot(
    string Picker,
    DateTime CompletedAt,
    int TotalDistanceMm,
    int ItemsPicked,
    double? DurationMinutes);

public record OrderSnapshot(Guid Id, OrderStatus Status, DateTime CreatedAt);
public record PickListSnapshot(Guid Id, PickListStatus Status, DateTime CreatedAt, int TotalDistanceMm);

/// <summary>Eine tatsächlich gepickte Position: Ist-Menge (&gt; 0), Bin und Zeitpunkt (Bestätigung bzw. Listenabschluss).</summary>
public record PickedItemSnapshot(Guid PickListId, Guid ArticleId, Guid BinId, int Quantity, DateTime PickedAt);

public record BinStockSnapshot(Guid BinId, int ArticleCount, int TotalQuantity);
public record BinHeatSnapshot(Guid BinId, int PickCount);
public record StockMovementEvent(DateTime At, int QuantityDelta);

/// <summary>Eine Bestandszeile mit MHD samt Artikel und Lagerplatz (Rohdaten der MHD-Warnliste).</summary>
public record ExpiringStockSnapshot(
    Guid StockItemId, Guid ArticleId, string Sku, string Name, Guid BinId, string BinCode,
    string? LotNumber, int Quantity, DateTime ExpiryDate);

/// <summary>Gesamtbestand eines Artikels und Anzahl Lagerplätze, an denen Bestand liegt.</summary>
public record ArticleStockTotals(int Quantity, int LocationCount);

/// <summary>Artikel mit Ist-Bestand und Stammpreis als Ausgangspunkt der Bewertung.</summary>
public record ValuationArticleSnapshot(Guid ArticleId, string Sku, string Name, int PurchasePriceCents, int OnHandQuantity);

/// <summary>Eine Ledger-Buchung (signiertes Delta, Kosten-Snapshot, Grund, Vorgangs-Referenz).</summary>
public record LedgerMovementSnapshot(
    Guid ArticleId, DateTime At, int QuantityDelta, int UnitCostCents,
    StockMovementReason Reason, Guid? ReferenceId);

public record LiveStatusSnapshot(
    int OrdersOpen,
    int OrdersInPicking,
    int OrdersPicked,
    int OrdersPacked,
    int PicklistsPending,
    int PicklistsInProgress,
    int PicklistsPickedReadyToPack,
    int PicklistsCompletedToday,
    int InventoryCountsOpen,
    int ReplenishmentTasksOpen,
    int StockAlertsCritical,
    int StockAlertsWarning);
