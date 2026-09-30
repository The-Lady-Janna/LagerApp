using Lager.Domain.Stock;

namespace Lager.Application.Abstractions;

public interface IReplenishmentRepository : IRepository<ReplenishmentTask>
{
    Task<IReadOnlyList<ReplenishmentTask>> ListOpenAsync(CancellationToken ct = default);
    /// <summary>True if an Open task already exists for this (article × target-bin).</summary>
    Task<bool> HasOpenTaskAsync(Guid articleId, Guid targetBinId, CancellationToken ct = default);
}
