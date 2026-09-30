using Lager.Application.Abstractions;
using Lager.Domain.Returns;
using Microsoft.EntityFrameworkCore;

namespace Lager.Infrastructure.Persistence.Repositories;

public class ReturnRepository : IReturnRepository
{
    private readonly LagerDbContext _db;
    public ReturnRepository(LagerDbContext db) => _db = db;

    public Task<ReturnShipment?> GetAsync(Guid id, CancellationToken ct = default) =>
        GetWithLinesAsync(id, ct);

    public Task<ReturnShipment?> GetWithLinesAsync(Guid id, CancellationToken ct = default) =>
        _db.ReturnShipments.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == id, ct);

    public async Task<IReadOnlyList<ReturnShipment>> ListAsync(CancellationToken ct = default) =>
        await _db.ReturnShipments.AsNoTracking().Include(r => r.Lines).OrderByDescending(r => r.CreatedAt).ToListAsync(ct);

    public async Task AddAsync(ReturnShipment entity, CancellationToken ct = default) =>
        await _db.ReturnShipments.AddAsync(entity, ct);

    public void Remove(ReturnShipment entity) => _db.ReturnShipments.Remove(entity);

    /// <summary>Atomarer Zähler, siehe <see cref="NumberSequences.NextAsync"/>.</summary>
    public Task<long> NextSequenceAsync(CancellationToken ct = default) =>
        NumberSequences.NextAsync(_db, NumberSequences.ReturnShipment, ct);

    public async Task<IReadOnlyList<ReturnShipment>> ListForOrderAsync(Guid orderId, CancellationToken ct = default) =>
        await _db.ReturnShipments.AsNoTracking().Include(r => r.Lines)
            .Where(r => r.OrderId == orderId && r.Status != ReturnStatus.Cancelled)
            .ToListAsync(ct);
}
