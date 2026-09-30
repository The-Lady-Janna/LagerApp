using Lager.Application.Abstractions;
using Lager.Domain.Purchasing;
using Microsoft.EntityFrameworkCore;

namespace Lager.Infrastructure.Persistence.Repositories;

public class PurchaseOrderRepository : IPurchaseOrderRepository
{
    private readonly LagerDbContext _db;
    public PurchaseOrderRepository(LagerDbContext db) => _db = db;

    public Task<PurchaseOrder?> GetAsync(Guid id, CancellationToken ct = default) =>
        GetWithLinesAsync(id, ct);

    public Task<PurchaseOrder?> GetWithLinesAsync(Guid id, CancellationToken ct = default) =>
        _db.PurchaseOrders.Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<PurchaseOrder>> ListAsync(CancellationToken ct = default) =>
        await _db.PurchaseOrders.AsNoTracking().Include(p => p.Lines).OrderByDescending(p => p.CreatedAt).ToListAsync(ct);

    public async Task AddAsync(PurchaseOrder entity, CancellationToken ct = default) =>
        await _db.PurchaseOrders.AddAsync(entity, ct);

    public void Remove(PurchaseOrder entity) => _db.PurchaseOrders.Remove(entity);

    /// <summary>Atomarer Zähler, siehe <see cref="NumberSequences.NextAsync"/>.</summary>
    public Task<long> NextSequenceAsync(CancellationToken ct = default) =>
        NumberSequences.NextAsync(_db, NumberSequences.PurchaseOrder, ct);

    public async Task<IReadOnlyList<PurchaseOrder>> ListOpenAsync(CancellationToken ct = default) =>
        await _db.PurchaseOrders.AsNoTracking().Include(p => p.Lines)
            .Where(p => p.Status == PurchaseOrderStatus.Sent || p.Status == PurchaseOrderStatus.PartiallyReceived)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);
}
