namespace Lager.Contracts.Inbound;

public record InboundLineDto(
    Guid Id,
    Guid ArticleId,
    string ArticleSku,
    string ArticleName,
    Guid TargetBinId,
    string TargetBinCode,
    int Quantity,
    string? LotNumber,
    DateTime? ExpiryDate,
    Guid? PurchaseOrderLineId = null,
    int? UnitCostCents = null);

public record InboundShipmentDto(
    Guid Id,
    string ShipmentNumber,
    string? SupplierReference,
    string? Notes,
    string Status,
    DateTime? ReceivedAt,
    DateTime CreatedAt,
    IReadOnlyList<InboundLineDto> Lines,
    Guid? PurchaseOrderId = null);

/// <summary>
/// Neue Lieferung (Entwurf). <paramref name="Lines"/> (optional) legt die Zeilen atomar mit dem Kopf an - Validierung wie
/// bei <c>POST /api/inbound/{id}/lines</c> (Artikel und Lagerplatz müssen existieren, Menge, Charge, MHD, Charge nur mit
/// einem MHD je Lagerplatz); scheitert eine Zeile, wird nichts angelegt. Ohne Zeilen entsteht eine leere Lieferung, die
/// erst über die Zeilen-Endpunkte gefüllt wird. Eine Bestellzeile kann hier nicht angegeben werden (die Lieferung hat
/// keine Bestellung).
/// </summary>
public record CreateInboundShipmentRequest(
    string ShipmentNumber,
    string? SupplierReference,
    string? Notes,
    IReadOnlyList<AddInboundLineRequest>? Lines = null);

/// <summary>
/// Eine Wareneingangszeile. <paramref name="PurchaseOrderLineId"/> ordnet sie einer Bestellzeile zu (nur bei einer
/// Lieferung mit Bestellbezug); <paramref name="UnitCostCents"/> ist der Einkaufspreis je Stück (Cent) für die
/// Bewertung - ohne Angabe gilt der Preis der Bestellzeile bzw. der aktuelle Artikelpreis.
/// </summary>
public record AddInboundLineRequest(
    Guid ArticleId,
    Guid TargetBinId,
    int Quantity,
    string? LotNumber,
    DateTime? ExpiryDate,
    Guid? PurchaseOrderLineId = null,
    int? UnitCostCents = null);
