using Lager.Domain.Inbound;

namespace Lager.Application.Abstractions;

public interface IInboundRepository : IRepository<InboundShipment>
{
    Task<InboundShipment?> GetByNumberAsync(string number, CancellationToken ct = default);
    Task<InboundShipment?> GetWithLinesAsync(Guid id, CancellationToken ct = default);

    /// <summary>Der offene Wareneingang (Draft) zu einer Bestellung, falls es einen gibt (read-only, mit Zeilen).</summary>
    Task<InboundShipment?> FindDraftForPurchaseOrderAsync(Guid purchaseOrderId, CancellationToken ct = default);
}
