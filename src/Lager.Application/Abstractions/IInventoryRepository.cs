using Lager.Domain.Inventory;

namespace Lager.Application.Abstractions;

public interface IInventoryRepository : IRepository<InventoryCount>
{
    Task<InventoryCount?> GetWithLinesAsync(Guid id, CancellationToken ct = default);
}
