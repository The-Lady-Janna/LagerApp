namespace Lager.Contracts.PickLists;

public record PickCartConfigDto(
    Guid Id,
    string Name,
    int LevelCount,
    int LevelWidthMm,
    int LevelDepthMm,
    int LevelHeightMm,
    int MaxWeightGrams,
    long TotalVolumeMm3);

public record CreatePickCartConfigRequest(
    string Name,
    int LevelCount,
    int LevelWidthMm,
    int LevelDepthMm,
    int LevelHeightMm,
    int MaxWeightGrams);

public record UpdatePickCartConfigRequest(
    string Name,
    int LevelCount,
    int LevelWidthMm,
    int LevelDepthMm,
    int LevelHeightMm,
    int MaxWeightGrams);

public record GenerateCartPickListRequest(
    Guid PickCartConfigId,
    Guid? StartPickPointId = null,
    Guid? EndPickPointId = null,
    /// <summary>
    /// When true, the cart greedily picks orders whose stock locations overlap
    /// with already-chosen orders, so the resulting pick list visits as few
    /// distinct bins as possible. When false (default), orders are taken in
    /// FIFO order until the cart is full.
    /// </summary>
    bool OptimizeForBinReuse = false);

public record ConfirmPackedItemRequest(Guid PickItemId, int ActualQuantity);

public record PackPickListRequest(IReadOnlyList<ConfirmPackedItemRequest> Items);
