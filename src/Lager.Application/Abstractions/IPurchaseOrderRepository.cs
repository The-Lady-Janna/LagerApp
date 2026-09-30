using Lager.Domain.Purchasing;

namespace Lager.Application.Abstractions;

public interface IPurchaseOrderRepository : IRepository<PurchaseOrder>
{
    Task<PurchaseOrder?> GetWithLinesAsync(Guid id, CancellationToken ct = default);

    /// <summary>Nächste Nummer des Nummernkreises (atomar in der Datenbank, siehe NumberSequences).</summary>
    Task<long> NextSequenceAsync(CancellationToken ct = default);

    /// <summary>Read-only: alle Bestellungen, die noch Ware erwarten (Versendet oder Teilweise geliefert), mit Zeilen.</summary>
    Task<IReadOnlyList<PurchaseOrder>> ListOpenAsync(CancellationToken ct = default);
}
