namespace Lager.Contracts.PickLists;

public record PickWaveDto(
    Guid Id,
    string WaveNumber,
    string? Description,
    string Status,
    DateTime? CutoffAt,
    DateTime CreatedAt,
    DateTime? ReleasedAt,
    DateTime? CompletedAt,
    IReadOnlyList<Guid> OrderIds,
    IReadOnlyList<Guid> PickListIds);

public record CreatePickWaveRequest(
    string? Description,
    DateTime? CutoffAt,
    IReadOnlyList<Guid> OrderIds);

public record AddOrdersToWaveRequest(IReadOnlyList<Guid> OrderIds);

public record ReleaseWaveRequest(
    Guid? StartPickPointId = null,
    Guid? EndPickPointId = null,
    /// <summary>
    /// Future: split into multiple picklists by zone. For now we always
    /// generate one consolidated picklist with the wave's orders.
    /// </summary>
    bool SplitByZone = false);
