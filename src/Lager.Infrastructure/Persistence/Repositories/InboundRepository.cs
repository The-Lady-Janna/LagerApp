using Lager.Application.Abstractions;
using Lager.Domain.Inbound;
using Microsoft.EntityFrameworkCore;

namespace Lager.Infrastructure.Persistence.Repositories;

public class InboundRepository : IInboundRepository
{
    private readonly LagerDbContext _db;

    public InboundRepository(LagerDbContext db) => _db = db;

    public Task<InboundShipment?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.InboundShipments.Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == id, ct);

    public Task<InboundShipment?> GetWithLinesAsync(Guid id, CancellationToken ct = default) =>
        _db.InboundShipments.Include(s => s.Lines).FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyList<InboundShipment>> ListAsync(CancellationToken ct = default) =>
        await _db.InboundShipments.Include(s => s.Lines).OrderByDescending(s => s.CreatedAt).ToListAsync(ct);

    public async Task AddAsync(InboundShipment entity, CancellationToken ct = default) =>
        await _db.InboundShipments.AddAsync(entity, ct);

    public void Remove(InboundShipment entity) => _db.InboundShipments.Remove(entity);

    public Task<InboundShipment?> GetByNumberAsync(string number, CancellationToken ct = default) =>
        _db.InboundShipments.Include(s => s.Lines).FirstOrDefaultAsync(s => s.ShipmentNumber == number, ct);

    public Task<InboundShipment?> FindDraftForPurchaseOrderAsync(Guid purchaseOrderId, CancellationToken ct = default) =>
        _db.InboundShipments.AsNoTracking().Include(s => s.Lines)
            .Where(s => s.PurchaseOrderLink!.PurchaseOrderId == purchaseOrderId && s.Status == InboundShipmentStatus.Draft)
            .OrderBy(s => s.CreatedAt)
            .FirstOrDefaultAsync(ct);
}
