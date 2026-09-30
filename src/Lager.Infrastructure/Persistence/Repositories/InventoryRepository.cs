using Lager.Application.Abstractions;
using Lager.Domain.Inventory;
using Microsoft.EntityFrameworkCore;

namespace Lager.Infrastructure.Persistence.Repositories;

public class InventoryRepository : IInventoryRepository
{
    private readonly LagerDbContext _db;

    public InventoryRepository(LagerDbContext db) => _db = db;

    public Task<InventoryCount?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.InventoryCounts.Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<InventoryCount?> GetWithLinesAsync(Guid id, CancellationToken ct = default) =>
        _db.InventoryCounts.Include(c => c.Lines).FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<InventoryCount>> ListAsync(CancellationToken ct = default) =>
        await _db.InventoryCounts.AsNoTracking().Include(c => c.Lines).OrderByDescending(c => c.CreatedAt).ToListAsync(ct);

    public async Task AddAsync(InventoryCount entity, CancellationToken ct = default) =>
        await _db.InventoryCounts.AddAsync(entity, ct);

    public void Remove(InventoryCount entity) => _db.InventoryCounts.Remove(entity);
}
