namespace Lager.Application.Abstractions;

/// <summary>
/// Externe Order-Quelle (Shopify, JTL, SAP, …). Pull-orientiert: der Lager-
/// Service ruft den Connector periodisch ab und holt sich offene Orders. Push
/// (Webhooks) wäre Folge-Iteration — braucht öffentliche URL + Signature-Check.
///
/// Today we only ship a NullOrderSource (no external sources configured).
/// Real connectors live in separate Infrastructure-classes, eg.
/// ShopifyOrderSource, WooCommerceOrderSource, JtlOrderSource.
/// </summary>
public interface IExternalOrderSource
{
    /// <summary>Identifier z. B. "shopify-shop1", "jtl-prod"</summary>
    string SourceCode { get; }
    string DisplayName { get; }

    /// <summary>
    /// Hole alle offenen Orders seit dem letzten Sync. Implementierungen
    /// dürfen idempotent sein — bestehende Orders mit gleicher OrderNumber
    /// werden vom OrderService nicht doppelt angelegt.
    /// </summary>
    Task<IReadOnlyList<ExternalOrderDto>> FetchOpenOrdersAsync(DateTime? since, CancellationToken ct = default);
}

public record ExternalOrderDto(
    string ExternalId,
    string OrderNumber,
    string? CustomerReference,
    DateTime CreatedAt,
    IReadOnlyList<ExternalOrderLineDto> Lines);

public record ExternalOrderLineDto(string Sku, int Quantity);

public interface IExternalOrderSourceRegistry
{
    IReadOnlyList<IExternalOrderSource> All { get; }
}
