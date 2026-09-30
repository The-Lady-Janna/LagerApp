namespace Lager.Contracts.Purchasing;

public record PurchaseOrderLineDto(
    Guid Id,
    Guid ArticleId,
    string ArticleSku,
    int OrderedQty,
    int ReceivedQty,
    int UnitPriceCents);

public record PurchaseOrderDto(
    Guid Id,
    string PoNumber,
    Guid SupplierId,
    string? SupplierName,
    string Status,
    string Currency,
    string? Notes,
    DateTime CreatedAt,
    DateTime? SentAt,
    DateTime? ExpectedDate,
    DateTime? ReceivedAt,
    long TotalValueCents,
    IReadOnlyList<PurchaseOrderLineDto> Lines);

public record CreatePurchaseOrderRequest(
    Guid SupplierId,
    DateTime? ExpectedDate,
    string? Notes,
    IReadOnlyList<CreatePurchaseOrderLineRequest> Lines);

public record CreatePurchaseOrderLineRequest(
    Guid ArticleId,
    int OrderedQty,
    int? UnitPriceCents = null);

public record AddPurchaseOrderLineRequest(Guid ArticleId, int OrderedQty, int? UnitPriceCents = null);

/// <summary>
/// Reine Statuskorrektur an der Bestellzeile (bucht KEINEN Bestand). Regulär wird der Empfang über den Wareneingang
/// gebucht (<see cref="CreateInboundFromPurchaseOrderRequest"/>), der die Bestellzeile beim Buchen fortschreibt.
/// </summary>
public record ReceivePurchaseOrderLineRequest(int ReceivedQty);

/// <summary>
/// Legt aus den offenen Mengen einer versendeten Bestellung einen Wareneingang (Entwurf) an: eine Zeile je Bestellzeile
/// mit Restmenge, alle mit dem Ziel-Lagerplatz <paramref name="TargetBinId"/>. Charge und MHD ergänzt der Empfänger
/// (Zeile entfernen und mit Charge/MHD und derselben Bestellzeile neu anlegen), bevor er den Wareneingang bucht.
/// </summary>
public record CreateInboundFromPurchaseOrderRequest(Guid TargetBinId, string? Notes = null);

/// <summary>
/// <paramref name="CurrentStock"/> ist der physische Bestand; <paramref name="OpenOrderedQty"/> die bereits bestellte,
/// noch nicht gelieferte Menge offener Bestellungen (Versendet/Teilweise geliefert). Bestand plus offene Menge
/// wird gegen den Meldebestand verglichen, damit derselbe Bedarf nicht doppelt bestellt wird.
/// </summary>
public record PurchaseSuggestionLineDto(
    Guid ArticleId,
    string Sku,
    string Name,
    int CurrentStock,
    int MinStock,
    int ReorderPoint,
    int MaxStock,
    int SuggestedOrderQty,
    int OpenOrderedQty = 0);

public record PurchaseSuggestionDto(
    Guid? SupplierId,
    string? SupplierName,
    int LeadTimeDays,
    IReadOnlyList<PurchaseSuggestionLineDto> Lines);
