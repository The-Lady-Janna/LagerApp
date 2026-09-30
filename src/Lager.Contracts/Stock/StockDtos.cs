namespace Lager.Contracts.Stock;

public record StockItemDto(
    Guid Id,
    Guid ArticleId,
    string ArticleSku,
    string ArticleName,
    Guid StorageLocationId,
    string StorageLocationCode,
    int Quantity,
    string? LotNumber,
    DateTime? ExpiryDate);

public record StockSummaryDto(
    Guid ArticleId,
    string ArticleSku,
    string ArticleName,
    int TotalQuantity,
    int LocationCount);

/// <summary>
/// Manuelle Bestandskorrektur. Mit <paramref name="LotNumber"/> (und ggf. <paramref name="ExpiryDate"/>) wird die
/// Bestandszeile dieser Charge gebucht; ohne Charge zuerst die Zeile ohne Charge, dann FEFO über die Chargen des
/// Lagerplatzes.
/// </summary>
public record AdjustStockRequest(
    Guid ArticleId,
    Guid StorageLocationId,
    int Delta,
    string? LotNumber,
    DateTime? ExpiryDate);

public record StockAlertDto(
    Guid ArticleId,
    string ArticleSku,
    string ArticleName,
    int TotalQuantity,
    int MinStock,
    int ReorderPoint,
    int MaxStock,
    string Severity);
