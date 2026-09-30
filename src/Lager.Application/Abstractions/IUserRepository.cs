using Lager.Domain.Auth;

namespace Lager.Application.Abstractions;

public interface IUserRepository : IRepository<User>
{
    Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default);
    Task<bool> AnyAsync(CancellationToken ct = default);
}
