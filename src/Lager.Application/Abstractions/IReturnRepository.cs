using Lager.Domain.Returns;

namespace Lager.Application.Abstractions;

public interface IReturnRepository : IRepository<ReturnShipment>
{
    Task<ReturnShipment?> GetWithLinesAsync(Guid id, CancellationToken ct = default);

    /// <summary>Nächste Nummer des Nummernkreises (atomar in der Datenbank, siehe NumberSequences).</summary>
    Task<long> NextSequenceAsync(CancellationToken ct = default);

    /// <summary>
    /// Read-only: alle nicht stornierten Retouren (Entwurf und abgeschlossen) zu einer Bestellung, mit Zeilen.
    /// Grundlage der Mengenprüfung: schon zurückgemeldete Mengen zählen mit.
    /// </summary>
    Task<IReadOnlyList<ReturnShipment>> ListForOrderAsync(Guid orderId, CancellationToken ct = default);
}
