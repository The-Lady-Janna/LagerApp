using Lager.Domain.Orders;

namespace Lager.Application.Abstractions;

public interface IOrderRepository : IRepository<Order>
{
    Task<Order?> GetByNumberAsync(string orderNumber, CancellationToken ct = default);
    Task<IReadOnlyList<Order>> GetManyAsync(IEnumerable<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// Read-only: alle Bestellungen (samt Zeilen) im angegebenen Status, neueste zuerst.
    /// Mit Standardimplementierung (Filter über <see cref="IRepository{T}.ListAsync"/>), damit bestehende
    /// Implementierungen der Schnittstelle (z. B. Test-Fakes) nicht angepasst werden müssen; das
    /// Datenbank-Repository überschreibt sie mit einem Filter in der Abfrage.
    /// </summary>
    async Task<IReadOnlyList<Order>> ListByStatusAsync(OrderStatus status, CancellationToken ct = default) =>
        (await ListAsync(ct)).Where(o => o.Status == status).ToList();
}
