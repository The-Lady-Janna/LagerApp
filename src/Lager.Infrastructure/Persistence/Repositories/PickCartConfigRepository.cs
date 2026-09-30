using Lager.Application.Abstractions;
using Lager.Domain.PickLists;
using Microsoft.EntityFrameworkCore;

namespace Lager.Infrastructure.Persistence.Repositories;

public class PickCartConfigRepository : IPickCartConfigRepository
{
    private readonly LagerDbContext _db;

    public PickCartConfigRepository(LagerDbContext db) => _db = db;

    public Task<PickCartConfig?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.PickCartConfigs.FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<PickCartConfig>> ListAsync(CancellationToken ct = default) =>
        await _db.PickCartConfigs.AsNoTracking().OrderBy(c => c.Name).ToListAsync(ct);

    public async Task AddAsync(PickCartConfig entity, CancellationToken ct = default) =>
        await _db.PickCartConfigs.AddAsync(entity, ct);

    public void Remove(PickCartConfig entity) => _db.PickCartConfigs.Remove(entity);

    public Task<PickCartConfig?> GetByNameAsync(string name, CancellationToken ct = default) =>
        _db.PickCartConfigs.FirstOrDefaultAsync(c => c.Name == name, ct);
}
