namespace Lager.Contracts.PickLists;

public record PickItemDto(
    Guid Id,
    int SequenceNumber,
    Guid OrderId,
    string OrderNumber,
    Guid ArticleId,
    string ArticleSku,
    string ArticleName,
    Guid StorageLocationId,
    string StorageLocationCode,
    int Quantity,
    bool Picked,
    int? ConfirmedQuantity,
    DateTime? ConfirmedAt);

public record PickListDto(
    Guid Id,
    string PickListNumber,
    string Status,
    string? AssignedTo,
    int TotalDistanceMm,
    DateTime CreatedAt,
    IReadOnlyList<PickItemDto> Items,
    IReadOnlyList<Lager.Contracts.Warehouse.PositionDto> Waypoints,
    Guid? PickCartConfigId = null,
    string? PickCartConfigName = null);

public record GeneratePickListRequest(
    IReadOnlyList<Guid> OrderIds,
    Guid? StartPickPointId = null,
    Guid? EndPickPointId = null);

public record RecalculatePickListRequest(
    Guid? StartPickPointId = null,
    Guid? EndPickPointId = null);

/// <summary>
/// Ergebnis von DELETE /api/picklists: gelöscht werden nur Picklisten ohne Buchungswirkung
/// (Pending, InProgress, Picked). Abgeschlossene (Completed) und stornierte Listen bleiben bestehen
/// und werden in <paramref name="SkippedCompleted"/> gezählt.
/// </summary>
public record ResetPickListsResult(int Deleted, int SkippedCompleted);
