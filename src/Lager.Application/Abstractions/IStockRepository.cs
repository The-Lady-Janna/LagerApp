using Lager.Domain.Stock;

namespace Lager.Application.Abstractions;

public interface IStockRepository : IRepository<StockItem>
{
    Task<IReadOnlyList<StockItem>> ListForArticleAsync(Guid articleId, CancellationToken ct = default);

    /// <summary>
    /// Read-only batch load of all in-stock items for the given articles in a
    /// single query. Callers group the result by <see cref="StockItem.ArticleId"/>.
    /// </summary>
    Task<IReadOnlyList<StockItem>> ListForArticlesAsync(IReadOnlyCollection<Guid> articleIds, CancellationToken ct = default);

    /// <summary>
    /// Total on-hand quantity per article (Quantity &gt; 0), aggregated server-side
    /// in one query. Articles with no stock are absent from the dictionary.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, int>> SumQuantitiesByArticleAsync(IReadOnlyCollection<Guid> articleIds, CancellationToken ct = default);

    Task<IReadOnlyList<StockItem>> ListAllAsync(CancellationToken ct = default);

    /// <summary>
    /// Kompatibilität: liefert die erste Bestandszeile ohne Chargennummer (Lot null) für Artikel und Lagerplatz,
    /// fällt sonst auf irgendeine Zeile des Lagerplatzes zurück. Neue Aufrufer nehmen die lot-genaue Überladung
    /// bzw. <see cref="ListForBinAsync"/>.
    /// </summary>
    Task<StockItem?> FindAsync(Guid articleId, Guid storageLocationId, CancellationToken ct = default);

    /// <summary>
    /// Findet die exakt passende Bestandszeile für Artikel, Lagerplatz, Charge und MHD (tracked). Die Charge wird
    /// normalisiert (leer -> null, siehe <see cref="StockItem.NormalizeLot"/>). Noch nicht gespeicherte, im
    /// Change-Tracker hinzugefügte Zeilen werden ebenfalls gefunden.
    /// </summary>
    Task<StockItem?> FindAsync(Guid articleId, Guid storageLocationId, string? lotNumber, DateTime? expiryDate, CancellationToken ct = default);

    /// <summary>
    /// Tracked: alle Bestandszeilen mit Menge &gt; 0 von Artikel und Lagerplatz, FEFO sortiert
    /// (frühestes MHD zuerst, ohne MHD zuletzt, dann älteste Zeile zuerst).
    /// </summary>
    Task<IReadOnlyList<StockItem>> ListForBinAsync(Guid articleId, Guid storageLocationId, CancellationToken ct = default);
}
