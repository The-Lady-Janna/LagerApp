using Lager.Domain.Common;

namespace Lager.Application.Abstractions;

public interface IRepository<T> where T : Entity
{
    Task<T?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<T>> ListAsync(CancellationToken ct = default);
    Task AddAsync(T entity, CancellationToken ct = default);
    void Remove(T entity);
}

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct = default);
}
