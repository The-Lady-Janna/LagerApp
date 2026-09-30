namespace Lager.Contracts.Inventory;

public record InventoryLineDto(
    Guid Id,
    Guid BinId,
    string BinCode,
    Guid ArticleId,
    string ArticleSku,
    int ExpectedQty,
    int? CountedQty,
    int Diff,
    string? Reason,
    string? LotNumber = null,
    DateTime? ExpiryDate = null);

public record InventoryCountDto(
    Guid Id,
    string Name,
    string Status,
    DateTime CreatedAt,
    DateTime? ReconciledAt,
    IReadOnlyList<InventoryLineDto> Lines);

public record StartInventoryRequest(string Name, Guid? OnlyForBinId = null);

public record SetCountRequest(int CountedQty, string? Reason);
