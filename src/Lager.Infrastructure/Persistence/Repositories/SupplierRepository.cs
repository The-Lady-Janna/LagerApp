using Lager.Application.Abstractions;
using Lager.Domain.Suppliers;
using Microsoft.EntityFrameworkCore;

namespace Lager.Infrastructure.Persistence.Repositories;

public class SupplierRepository : ISupplierRepository
{
    private readonly LagerDbContext _db;
    public SupplierRepository(LagerDbContext db) => _db = db;

    public Task<Supplier?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.Suppliers.FirstOrDefaultAsync(s => s.Id == id, ct);

    /// <summary>Base contract — defaults to active-only to be safe.</summary>
    public Task<IReadOnlyList<Supplier>> ListAsync(CancellationToken ct = default) =>
        ListAsync(includeInactive: false, ct);

    public async Task<IReadOnlyList<Supplier>> ListAsync(bool includeInactive, CancellationToken ct = default) =>
        await _db.Suppliers.AsNoTracking()
            .Where(s => includeInactive || s.IsActive)
            .OrderBy(s => s.Name)
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, Supplier>> GetManyAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return new Dictionary<Guid, Supplier>();
        var rows = await _db.Suppliers.Where(s => idList.Contains(s.Id)).ToListAsync(ct);
        return rows.ToDictionary(s => s.Id);
    }

    public Task<bool> CodeExistsAsync(string code, CancellationToken ct = default) =>
        _db.Suppliers.AnyAsync(s => s.Code == code, ct);

    public async Task AddAsync(Supplier entity, CancellationToken ct = default) =>
        await _db.Suppliers.AddAsync(entity, ct);

    public void Remove(Supplier entity) => _db.Suppliers.Remove(entity);
}
