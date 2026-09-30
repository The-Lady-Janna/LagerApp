namespace Lager.Contracts.Stock;

public record ReplenishmentTaskDto(
    Guid Id,
    Guid ArticleId,
    string ArticleSku,
    Guid SourceBinId,
    string SourceBinCode,
    Guid TargetBinId,
    string TargetBinCode,
    int SuggestedQty,
    int? CompletedQty,
    string Status,
    DateTime CreatedAt,
    DateTime? CompletedAt);

public record CompleteReplenishmentRequest(int ActualQty);

/// <summary>
/// "Scan all Hot-Pick bins, generate refill tasks for everything under threshold."
/// Returns the freshly-created tasks. Idempotent: if a task for the same
/// (article × target-bin) already exists in Open status, no new task is created.
/// </summary>
public record ScanReplenishmentRequest(Guid? OnlyForBinId = null);

public record PutawaySuggestionDto(
    Guid BinId,
    string BinCode,
    string BinType,
    int CurrentQuantityOfArticle,
    int OtherArticlesInBin,
    int CapacityScore,
    string Reason);

public record SlottingSuggestionDto(
    Guid ArticleId,
    string ArticleSku,
    string ArticleName,
    int PickFrequency,
    Guid CurrentBinId,
    string CurrentBinCode,
    int CurrentDistanceMm,
    Guid SuggestedBinId,
    string SuggestedBinCode,
    int SuggestedDistanceMm,
    int EstimatedSavingsMm);
