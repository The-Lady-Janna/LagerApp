using Lager.Application.Abstractions;
using Lager.Domain.Shipping;
using Microsoft.EntityFrameworkCore;

namespace Lager.Infrastructure.Persistence.Repositories;

public class ShipmentRepository : IShipmentRepository
{
    private readonly LagerDbContext _db;
    public ShipmentRepository(LagerDbContext db) => _db = db;

    public Task<Shipment?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.Shipments.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyList<Shipment>> ListAsync(CancellationToken ct = default) =>
        await _db.Shipments.AsNoTracking().OrderByDescending(s => s.CreatedAt).ToListAsync(ct);

    public async Task AddAsync(Shipment entity, CancellationToken ct = default) =>
        await _db.Shipments.AddAsync(entity, ct);

    public void Remove(Shipment entity) => _db.Shipments.Remove(entity);

    /// <summary>Atomarer Zähler, siehe <see cref="NumberSequences.NextAsync"/> (vorher Lesen-Ändern-Schreiben ohne Sperre).</summary>
    public Task<long> NextSequenceAsync(CancellationToken ct = default) =>
        NumberSequences.NextAsync(_db, NumberSequences.Shipment, ct);

    public async Task<IReadOnlyList<Shipment>> ListByOrderAsync(Guid orderId, CancellationToken ct = default) =>
        await _db.Shipments.Where(s => s.OrderId == orderId).OrderByDescending(s => s.CreatedAt).ToListAsync(ct);
}
