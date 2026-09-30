namespace Lager.Contracts.Reports;

public record DashboardKpiDto(
    string Label,
    string Value,
    string? Unit,
    string? Hint);

public record TopArticleDto(
    Guid ArticleId,
    string Sku,
    string Name,
    /// <summary>Anzahl tatsächlich gepickter Positionen (Pickzeilen mit Ist-Menge &gt; 0) im Zeitraum.</summary>
    int PickCount,
    /// <summary>Summe der Ist-Menge (bestätigte Menge bzw. Sollmenge bei noch nicht gepackten Listen).</summary>
    int TotalQuantity);

public record TopBinDto(
    Guid BinId,
    string BinCode,
    int ArticleCount,
    int TotalQuantity);

public record OrderStatusBucketDto(
    string Status,
    int Count);

public record DailySeriesPointDto(
    DateOnly Date,
    int Count);

/// <summary>
/// Snapshot of the warehouse activity for the requested time window.
/// Computed on-demand — small operations don't need a materialized warehouse.
/// </summary>
public record ReportDashboardDto(
    int RangeDays,
    DateTime From,
    DateTime To,
    IReadOnlyList<DashboardKpiDto> Headline,
    IReadOnlyList<TopArticleDto> TopArticles,
    IReadOnlyList<TopBinDto> TopBins,
    IReadOnlyList<OrderStatusBucketDto> OrderStatusBreakdown,
    IReadOnlyList<DailySeriesPointDto> PickListsPerDay,
    IReadOnlyList<DailySeriesPointDto> OrdersPerDay);

/// <summary>PickCount = tatsächlich gepickte Positionen (Pickzeilen mit Ist-Menge &gt; 0) aus diesem Bin.</summary>
public record BinHeatPointDto(Guid BinId, string BinCode, int PickCount);

public record DeadStockArticleDto(
    Guid ArticleId,
    string Sku,
    string Name,
    int TotalQuantity,
    /// <summary>Anzahl Lagerplätze, an denen der Artikel Bestand hat.</summary>
    int LocationCount,
    /// <summary>
    /// Tage seit der letzten Ledger-Bewegung (Wareneingang, Pick, Retoure, Inventur, Korrektur); interne
    /// Umlagerungen (Nachschub) zählen nicht. null = nie bewegt.
    /// </summary>
    int? DaysSinceLastMovement);

public record AbcArticleDto(
    Guid ArticleId,
    string Sku,
    string Name,
    /// <summary>Anzahl tatsächlich gepickter Positionen im Zeitraum.</summary>
    int PickCount,
    /// <summary>Summe der Ist-Pickmenge (Basis der Klassifizierung).</summary>
    int TotalQuantity,
    /// <summary>Mengenanteil in Prozent, auf 0,01 gerundet; die Anteile aller Artikel summieren sich auf 100,00.</summary>
    decimal SharePercent,
    /// <summary>
    /// A, B oder C — nach dem kumulierten Mengenanteil VOR dem Artikel (klassisches Pareto):
    /// A &lt; 80 %, B von 80 % bis &lt; 95 %, C ab 95 %. Ein Einzelartikel ist A.
    /// </summary>
    string Class);

public record LiveStatusDto(
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
    int StockAlertsWarning,
    DateTime At);

public record StockTrendPointDto(DateTime At, int Quantity);

public record StockTrendDto(
    Guid ArticleId,
    string Sku,
    string Name,
    int CurrentQuantity,
    IReadOnlyList<StockTrendPointDto> Series);

// Welle 4 — Charge / Lot tracing

/// <summary>
/// Eine Wareneingangszeile mit dieser Charge. <paramref name="Status"/> ist der Status der Lieferung
/// (Draft/Received/Cancelled): nur gebuchte Lieferungen (Received) zählen in <see cref="ChargeTraceDto.InboundTotal"/>.
/// <paramref name="TargetBinCode"/> ergänzt der Dienst für die Anzeige.
/// </summary>
public record ChargeInboundDto(
    Guid ShipmentId,
    string ShipmentNumber,
    Guid ArticleId,
    string ArticleSku,
    Guid TargetBinId,
    int Quantity,
    DateTime? ExpiryDate,
    DateTime ReceivedAt,
    string Status = "Received",
    string? TargetBinCode = null);

public record ChargeStockDto(
    Guid StockItemId,
    Guid ArticleId,
    string ArticleSku,
    Guid BinId,
    string BinCode,
    int Quantity,
    DateTime? ExpiryDate);

/// <summary>
/// Eine Bewegung dieser Charge aus dem StockMovement-Ledger — typischerweise
/// Picks, aber auch Inventur-Korrekturen oder Retouren. QuantityDelta ist
/// signed (negativ = Abgang). <paramref name="BinCode"/>, <paramref name="ArticleId"/> und
/// <paramref name="ArticleSku"/> ergänzt der Dienst für die Anzeige.
/// </summary>
public record ChargeMovementDto(
    DateTime At,
    int QuantityDelta,
    string Reason,
    Guid BinId,
    string? ReferenceType,
    Guid? ReferenceId,
    string? BinCode = null,
    Guid? ArticleId = null,
    string? ArticleSku = null);

/// <summary>
/// Eine Bestellung, die aus dieser Charge beliefert worden sein kann. Abgeleitet aus dem Ledger: die Pick-Buchungen
/// der Charge verweisen auf eine Pickliste, deren Positionen (gleicher Artikel, gleicher Lagerplatz) zu Bestellungen
/// gehören. Eine Pickposition kennt die Charge nicht - lagen im Lagerplatz mehrere Chargen, kann die Bestellung auch
/// aus einer anderen Charge beliefert worden sein (Rückverfolgung "möglicherweise betroffen").
/// </summary>
public record ChargeOrderDto(
    Guid OrderId,
    string OrderNumber,
    string? CustomerReference,
    string Status,
    Guid PickListId,
    string? PickListNumber,
    DateTime PickedAt);

public record ChargeTraceDto(
    string LotNumber,
    IReadOnlyList<ChargeInboundDto> Inbounds,
    IReadOnlyList<ChargeStockDto> CurrentStock,
    /// <summary>
    /// Alle historischen Bewegungen dieser Charge aus dem StockMovement-Ledger
    /// (Picks/Inventur/Retouren). Auch dann komplett, wenn der ursprüngliche
    /// StockItem längst verbraucht ist — der Ledger ist append-only.
    /// </summary>
    IReadOnlyList<ChargeMovementDto> Movements,
    int CurrentStockTotal,
    /// <summary>Summe der gebuchten (Received) Wareneingänge dieser Charge; Entwürfe und stornierte Lieferungen zählen nicht.</summary>
    int InboundTotal,
    /// <summary>Möglicherweise betroffene Bestellungen (siehe <see cref="ChargeOrderDto"/>), neueste zuerst.</summary>
    IReadOnlyList<ChargeOrderDto>? Orders = null);

// MHD-Warnliste

/// <summary>Status einer Charge nach MHD; Grenzen siehe <c>ReportService.ExpiryCriticalDays</c>.</summary>
public static class ExpiryStatus
{
    /// <summary>MHD liegt vor dem heutigen Tag (UTC); am Ablauftag selbst ist die Ware noch verwendbar.</summary>
    public const string Expired = "Expired";
    /// <summary>Läuft heute oder in den nächsten Tagen ab (höchstens <c>ExpiryCriticalDays</c>).</summary>
    public const string Critical = "Critical";
    /// <summary>Läuft innerhalb der abgefragten Frist ab, aber noch nicht kritisch.</summary>
    public const string Soon = "Soon";
}

/// <summary>
/// Eine Bestandszeile (Charge) mit abgelaufenem oder bald ablaufendem MHD. <paramref name="DaysUntilExpiry"/> zählt
/// Kalendertage in UTC (negativ = seit so vielen Tagen abgelaufen, 0 = läuft heute ab).
/// </summary>
public record ExpiringStockDto(
    Guid StockItemId,
    Guid ArticleId,
    string ArticleSku,
    string ArticleName,
    Guid BinId,
    string BinCode,
    string? LotNumber,
    int Quantity,
    DateTime ExpiryDate,
    int DaysUntilExpiry,
    string Status);

// Picker-Performance

public record PickerPerformanceRowDto(
    /// <summary>Username aus AssignedTo des PickList. "anonymous" wenn kein User gesetzt.</summary>
    string Picker,
    /// <summary>Anzahl abgeschlossener Picklisten (Status Picked oder Completed) im Zeitraum.</summary>
    int PickListsCompleted,
    /// <summary>Gepickte Stückzahl (Summe ConfirmedQuantity oder Quantity falls null).</summary>
    int ItemsPicked,
    /// <summary>Ø Wegstrecke pro Pickliste in Metern.</summary>
    double AvgDistanceMeters,
    /// <summary>Ø Dauer pro Pickliste in Minuten (CreatedAt → UpdatedAt). null wenn keine Zeit messbar.</summary>
    double? AvgDurationMinutes);

public record PickerPerformanceDto(
    DateTime From,
    DateTime To,
    IReadOnlyList<PickerPerformanceRowDto> Rows);

// Welle 4 — Bestandsbewertung

public record StockValuationLineDto(
    Guid ArticleId,
    string Sku,
    string Name,
    int Quantity,
    /// <summary>Aktueller Stammpreis (Info); die Bewertung selbst folgt den Ledger-Layern.</summary>
    int UnitPriceCents,
    long TotalValueCents,
    /// <summary>
    /// Menge ohne Ledger-Historie, die zum aktuellen Stammpreis bewertet wurde (Fallback). 0 = der Wert
    /// stammt vollständig aus den Kosten-Snapshots der Ledger-Zugänge.
    /// </summary>
    int FallbackQuantity = 0);

public record StockValuationDto(
    DateTime At,
    int ArticleCount,
    long TotalQuantity,
    long TotalValueCents,
    /// <summary>Immer EUR: Artikelpreise haben keine Währung, das System bewertet nur in EUR.</summary>
    string Currency,
    IReadOnlyList<StockValuationLineDto> Lines,
    /// <summary>Anteil von <see cref="TotalValueCents"/>, der aus dem Stammpreis-Fallback stammt (ohne Ledger-Historie).</summary>
    long FallbackValueCents = 0,
    /// <summary>Kurzbeschreibung der Bewertungsmethode.</summary>
    string Basis = "");
