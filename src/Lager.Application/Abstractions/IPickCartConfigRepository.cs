using Lager.Domain.PickLists;

namespace Lager.Application.Abstractions;

public interface IPickCartConfigRepository : IRepository<PickCartConfig>
{
    Task<PickCartConfig?> GetByNameAsync(string name, CancellationToken ct = default);
}
