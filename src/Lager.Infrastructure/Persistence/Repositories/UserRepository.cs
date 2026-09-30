using Lager.Application.Abstractions;
using Lager.Domain.Auth;
using Microsoft.EntityFrameworkCore;

namespace Lager.Infrastructure.Persistence.Repositories;

public class UserRepository : IUserRepository
{
    private readonly LagerDbContext _db;
    public UserRepository(LagerDbContext db) => _db = db;

    public Task<User?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default) =>
        _db.Users.FirstOrDefaultAsync(u => u.Username == username, ct);

    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken ct = default) =>
        await _db.Users.AsNoTracking().OrderBy(u => u.Username).ToListAsync(ct);

    public Task<bool> AnyAsync(CancellationToken ct = default) => _db.Users.AnyAsync(ct);

    public async Task AddAsync(User entity, CancellationToken ct = default) =>
        await _db.Users.AddAsync(entity, ct);

    public void Remove(User entity) => _db.Users.Remove(entity);
}
