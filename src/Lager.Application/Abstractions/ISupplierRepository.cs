using Lager.Domain.Suppliers;

namespace Lager.Application.Abstractions;

public interface ISupplierRepository : IRepository<Supplier>
{
    Task<IReadOnlyList<Supplier>> ListAsync(bool includeInactive, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, Supplier>> GetManyAsync(IEnumerable<Guid> ids, CancellationToken ct = default);
    Task<bool> CodeExistsAsync(string code, CancellationToken ct = default);
}
