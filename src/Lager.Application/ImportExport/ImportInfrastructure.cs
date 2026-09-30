using Lager.Domain.Articles;
using Lager.Domain.Orders;
using Lager.Domain.Stock;

namespace Lager.Application.ImportExport;

/// <summary>
/// Was die Anwendungsschicht für einen atomaren Import vom Host braucht, ohne EF Core zu kennen: eine Datenbank-Transaktion
/// um die ganze Übernahme. Der Host (API) stellt sie bereit und reicht sie dem Import durch; bricht etwas ab, gehört alles
/// zurückgerollt (Dispose ohne <see cref="CommitAsync"/>).
/// </summary>
public interface IImportTransactionFactory
{
    Task<IImportTransaction> BeginAsync(CancellationToken ct);
}

public interface IImportTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);

    /// <summary>
    /// Gibt die vom Change-Tracker verfolgten Entitäten frei (nur nach einem SaveChanges aufrufen). Bei tausenden Zeilen
    /// würde jeder Speichervorgang sonst alle bisherigen Entitäten erneut auf Änderungen prüfen (quadratischer Aufwand).
    /// </summary>
    void ReleaseTrackedEntities();
}

/// <summary>
/// Woher der Export seine Zeilen bekommt: als Datenströme, damit große Tabellen (Bewegungen, Audit) nie vollständig im
/// Speicher liegen. Die Implementierung sitzt im Host (API, per EF Core); die Anwendungsschicht formatiert nur.
/// </summary>
public interface IExportSource
{
    IAsyncEnumerable<ArticleExportRow> ArticlesAsync(CancellationToken ct);
    IAsyncEnumerable<StockExportRow> StockAsync(CancellationToken ct);
    IAsyncEnumerable<OrderLineExportRow> OrderLinesAsync(CancellationToken ct);

    /// <summary>Ledger-Zeilen, älteste zuerst. <paramref name="fromUtc"/> inklusive, <paramref name="toExclusiveUtc"/> exklusive.</summary>
    IAsyncEnumerable<MovementExportRow> MovementsAsync(DateTime? fromUtc, DateTime? toExclusiveUtc, CancellationToken ct);

    /// <summary>Audit-Einträge, älteste zuerst; <paramref name="user"/> filtert auf einen Benutzer (ohne Beachtung der Schreibweise).</summary>
    IAsyncEnumerable<AuditExportRow> AuditAsync(DateTime? fromUtc, DateTime? toExclusiveUtc, string? user, CancellationToken ct);
}

public sealed record ArticleExportRow(Article Article, string? SupplierCode);

public sealed record StockExportRow(string Sku, string ArticleName, string Location, int Quantity, string? LotNumber, DateTime? ExpiryDate);

/// <summary>Eine Position einer Bestellung samt den Kopfdaten der Bestellung (eine Zeile je Position).</summary>
public sealed record OrderLineExportRow(
    string OrderNumber, OrderStatus Status, OrderSource Source, DateTime CreatedAt, string? CustomerReference,
    int Priority, DateTime? DueDate, string? ExternalReference, string Sku, int Quantity);

public sealed record MovementExportRow(
    DateTime At, string? Sku, string? Location, int QuantityDelta, StockMovementReason Reason, string? ReferenceType,
    Guid? ReferenceId, string? LotNumber, DateTime? ExpiryDate, int UnitCostCents);

public sealed record AuditExportRow(DateTime At, string? User, string EntityType, string EntityId, string Operation, string? ChangesJson);
