using Lager.Domain.Shipping;

namespace Lager.Application.Abstractions;

public interface IShipmentRepository : IRepository<Shipment>
{
    /// <summary>
    /// Nächste Sendungsnummer. Atomar: das Inkrement läuft direkt in der Datenbank (Nummernkreis
    /// <c>NumberSequences.Shipment</c>), parallele Aufrufer bekommen nie denselben Wert.
    /// </summary>
    Task<long> NextSequenceAsync(CancellationToken ct = default);

    /// <summary>
    /// Tracked: alle Sendungen der Bestellung (auch stornierte), neueste zuerst. Grundlage der Regel, wann eine
    /// Bestellung als versendet gilt (keine offene Sendung mehr).
    /// </summary>
    Task<IReadOnlyList<Shipment>> ListByOrderAsync(Guid orderId, CancellationToken ct = default);
}
