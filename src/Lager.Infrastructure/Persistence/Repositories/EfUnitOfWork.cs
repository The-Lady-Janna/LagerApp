using Lager.Application.Abstractions;

namespace Lager.Infrastructure.Persistence.Repositories;

public class EfUnitOfWork : IUnitOfWork
{
    private readonly LagerDbContext _db;

    public EfUnitOfWork(LagerDbContext db) => _db = db;

    public Task<int> SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
