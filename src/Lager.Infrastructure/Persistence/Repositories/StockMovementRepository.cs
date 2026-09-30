using Lager.Application.Abstractions;
using Lager.Domain.Stock;
using Microsoft.EntityFrameworkCore;

namespace Lager.Infrastructure.Persistence.Repositories;

public class StockMovementRepository : IStockMovementRepository
{
    private readonly LagerDbContext _db;
    public StockMovementRepository(LagerDbContext db) => _db = db;

    public Task<StockMovement?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.StockMovements.FirstOrDefaultAsync(m => m.Id == id, ct);

    public async Task<IReadOnlyList<StockMovement>> ListAsync(CancellationToken ct = default) =>
        await _db.StockMovements.OrderByDescending(m => m.At).Take(500).ToListAsync(ct);

    public async Task AddAsync(StockMovement entity, CancellationToken ct = default) =>
        await _db.StockMovements.AddAsync(entity, ct);

    public void Remove(StockMovement entity) => _db.StockMovements.Remove(entity);

    public async Task<IReadOnlyList<DateTime>> ListExpiriesForLotAsync(Guid articleId, string lotNumber, CancellationToken ct = default) =>
        await _db.StockMovements.AsNoTracking()
            .Where(m => m.ArticleId == articleId && m.LotNumber == lotNumber && m.ExpiryDate != null)
            .Select(m => m.ExpiryDate!.Value)
            .Distinct()
            .ToListAsync(ct);
}
