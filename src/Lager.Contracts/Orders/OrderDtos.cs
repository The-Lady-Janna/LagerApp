using Lager.Contracts.Customers;

namespace Lager.Contracts.Orders;

public record OrderLineDto(Guid Id, Guid ArticleId, string ArticleSku, int Quantity);

/// <summary>
/// Bestellung. <paramref name="HasStockNow"/> / <paramref name="HasStockAfterFifo"/> beziehen sich auf den physischen
/// Bedarf (Bundles sind in ihre Komponenten aufgelöst). Kunde und Lieferadresse sind optional
/// (<paramref name="CustomerName"/> und <paramref name="ShippingAddress"/> kommen aus dem Kundenstamm);
/// <paramref name="Priority"/> 0..3 und <paramref name="DueDate"/> steuern die Reihenfolge beim Kommissionieren;
/// <paramref name="ExternalReference"/> ist die Kennung im Quellsystem (Idempotenz der externen Bestell-API).
/// </summary>
public record OrderDto(
    Guid Id,
    string OrderNumber,
    string? CustomerReference,
    string Status,
    string Source,
    DateTime CreatedAt,
    IReadOnlyList<OrderLineDto> Lines,
    bool HasStockNow = true,
    bool HasStockAfterFifo = true,
    Guid? CustomerId = null,
    string? CustomerName = null,
    Guid? ShippingAddressId = null,
    CustomerAddressDto? ShippingAddress = null,
    int Priority = 0,
    DateTime? DueDate = null,
    string? ExternalReference = null);

/// <summary>
/// Bestellzeile: Artikel wahlweise per <paramref name="ArticleId"/> oder per <paramref name="Sku"/> (eines von beiden;
/// bei beiden müssen sie denselben Artikel meinen).
/// </summary>
public record CreateOrderLineRequest(Guid? ArticleId, int Quantity, string? Sku = null);

/// <summary>
/// Neue Bestellung. <paramref name="ExternalReference"/> (bzw. der Header <c>Idempotency-Key</c> der externen API)
/// macht das Anlegen wiederholbar: dieselbe Referenz liefert die bereits angelegte Bestellung statt eines Duplikats.
/// <paramref name="ShippingAddressId"/> muss zum Kunden <paramref name="CustomerId"/> gehören.
/// </summary>
public record CreateOrderRequest(
    string OrderNumber,
    string? CustomerReference,
    IReadOnlyList<CreateOrderLineRequest> Lines,
    Guid? CustomerId = null,
    Guid? ShippingAddressId = null,
    int Priority = 0,
    DateTime? DueDate = null,
    string? ExternalReference = null);
