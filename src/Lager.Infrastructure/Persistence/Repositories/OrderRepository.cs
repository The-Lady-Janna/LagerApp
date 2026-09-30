using Lager.Application.Abstractions;
using Lager.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace Lager.Infrastructure.Persistence.Repositories;

public class OrderRepository : IOrderRepository
{
    private readonly LagerDbContext _db;

    public OrderRepository(LagerDbContext db) => _db = db;

    public Task<Order?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct);

    public async Task<IReadOnlyList<Order>> ListAsync(CancellationToken ct = default) =>
        await _db.Orders.AsNoTracking().Include(o => o.Lines).OrderByDescending(o => o.CreatedAt).ToListAsync(ct);

    public async Task AddAsync(Order entity, CancellationToken ct = default) =>
        await _db.Orders.AddAsync(entity, ct);

    public void Remove(Order entity) => _db.Orders.Remove(entity);

    public Task<Order?> GetByNumberAsync(string orderNumber, CancellationToken ct = default) =>
        _db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.OrderNumber == orderNumber, ct);

    public async Task<IReadOnlyList<Order>> GetManyAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var idSet = ids.ToHashSet();
        return await _db.Orders.Include(o => o.Lines).Where(o => idSet.Contains(o.Id)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Order>> ListByStatusAsync(OrderStatus status, CancellationToken ct = default) =>
        await _db.Orders.AsNoTracking().Include(o => o.Lines)
            .Where(o => o.Status == status)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync(ct);
}
