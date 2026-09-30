using Lager.Application.Abstractions;
using Lager.Domain.Stock;
using Microsoft.EntityFrameworkCore;

namespace Lager.Infrastructure.Persistence.Repositories;

public class ReplenishmentRepository : IReplenishmentRepository
{
    private readonly LagerDbContext _db;
    public ReplenishmentRepository(LagerDbContext db) => _db = db;

    public Task<ReplenishmentTask?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.ReplenishmentTasks.FirstOrDefaultAsync(t => t.Id == id, ct);

    public async Task<IReadOnlyList<ReplenishmentTask>> ListAsync(CancellationToken ct = default) =>
        await _db.ReplenishmentTasks.AsNoTracking().OrderByDescending(t => t.CreatedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<ReplenishmentTask>> ListOpenAsync(CancellationToken ct = default) =>
        await _db.ReplenishmentTasks
            .AsNoTracking()
            .Where(t => t.Status == ReplenishmentStatus.Open)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(ct);

    public Task<bool> HasOpenTaskAsync(Guid articleId, Guid targetBinId, CancellationToken ct = default) =>
        _db.ReplenishmentTasks.AnyAsync(
            t => t.ArticleId == articleId &&
                 t.TargetBinId == targetBinId &&
                 t.Status == ReplenishmentStatus.Open, ct);

    public async Task AddAsync(ReplenishmentTask entity, CancellationToken ct = default) =>
        await _db.ReplenishmentTasks.AddAsync(entity, ct);

    public void Remove(ReplenishmentTask entity) => _db.ReplenishmentTasks.Remove(entity);
}
