using Lager.Application.Abstractions;
using Lager.Domain.Stock;
using Microsoft.EntityFrameworkCore;

namespace Lager.Infrastructure.Persistence.Repositories;

public class StockRepository : IStockRepository
{
    private readonly LagerDbContext _db;

    public StockRepository(LagerDbContext db) => _db = db;

    public Task<StockItem?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.StockItems.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyList<StockItem>> ListAsync(CancellationToken ct = default) =>
        await _db.StockItems.ToListAsync(ct);

    public async Task AddAsync(StockItem entity, CancellationToken ct = default) =>
        await _db.StockItems.AddAsync(entity, ct);

    public void Remove(StockItem entity) => _db.StockItems.Remove(entity);

    public async Task<IReadOnlyList<StockItem>> ListForArticleAsync(Guid articleId, CancellationToken ct = default) =>
        await _db.StockItems.AsNoTracking().Where(s => s.ArticleId == articleId && s.Quantity > 0).ToListAsync(ct);

    public async Task<IReadOnlyList<StockItem>> ListForArticlesAsync(IReadOnlyCollection<Guid> articleIds, CancellationToken ct = default)
    {
        if (articleIds.Count == 0) return Array.Empty<StockItem>();
        return await _db.StockItems.AsNoTracking()
            .Where(s => articleIds.Contains(s.ArticleId) && s.Quantity > 0)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> SumQuantitiesByArticleAsync(IReadOnlyCollection<Guid> articleIds, CancellationToken ct = default)
    {
        if (articleIds.Count == 0) return new Dictionary<Guid, int>();
        var rows = await _db.StockItems.AsNoTracking()
            .Where(s => articleIds.Contains(s.ArticleId) && s.Quantity > 0)
            .GroupBy(s => s.ArticleId)
            .Select(g => new { g.Key, Total = g.Sum(x => x.Quantity) })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => r.Total);
    }

    public async Task<IReadOnlyList<StockItem>> ListAllAsync(CancellationToken ct = default) =>
        await _db.StockItems.AsNoTracking().Where(s => s.Quantity > 0).ToListAsync(ct);

    /// <summary>
    /// Kompatibilität für Aufrufer, die noch keine Charge kennen: bevorzugt die chargenlose Zeile (Lot null,
    /// ohne MHD), sonst die älteste Zeile des Lagerplatzes. Deterministisch sortiert.
    /// </summary>
    public async Task<StockItem?> FindAsync(Guid articleId, Guid storageLocationId, CancellationToken ct = default)
    {
        var rows = await _db.StockItems
            .Where(s => s.ArticleId == articleId && s.StorageLocationId == storageLocationId)
            .ToListAsync(ct);
        return rows
            .OrderBy(s => StockItem.NormalizeLot(s.LotNumber) is null ? 0 : 1)
            .ThenBy(s => s.ExpiryDate is null ? 0 : 1)
            .ThenBy(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .FirstOrDefault();
    }

    public async Task<StockItem?> FindAsync(Guid articleId, Guid storageLocationId, string? lotNumber, DateTime? expiryDate, CancellationToken ct = default)
    {
        var lot = StockItem.NormalizeLot(lotNumber);

        // Zuerst der Change-Tracker: eine in derselben Einheit schon hinzugefügte, noch ungespeicherte Zeile
        // muss gefunden werden, sonst entstehen Doppelzeilen.
        var local = _db.StockItems.Local.FirstOrDefault(s =>
            s.ArticleId == articleId && s.StorageLocationId == storageLocationId
            && StockItem.NormalizeLot(s.LotNumber) == lot && s.ExpiryDate == expiryDate);
        if (local is not null) return local;

        var query = _db.StockItems.Where(s => s.ArticleId == articleId && s.StorageLocationId == storageLocationId);
        query = lot is null
            ? query.Where(s => s.LotNumber == null || s.LotNumber == "")
            : query.Where(s => s.LotNumber == lot);
        query = expiryDate is null
            ? query.Where(s => s.ExpiryDate == null)
            : query.Where(s => s.ExpiryDate == expiryDate);

        // Mehrere Treffer wären Altlast (kein Unique-Index): die älteste Zeile gewinnt, damit das Ergebnis stabil ist.
        var hits = await query.ToListAsync(ct);
        return hits.OrderBy(s => s.CreatedAt).ThenBy(s => s.Id).FirstOrDefault();
    }

    public async Task<IReadOnlyList<StockItem>> ListForBinAsync(Guid articleId, Guid storageLocationId, CancellationToken ct = default)
    {
        var rows = await _db.StockItems
            .Where(s => s.ArticleId == articleId && s.StorageLocationId == storageLocationId && s.Quantity > 0)
            .ToListAsync(ct);
        // FEFO im Speicher: EF/SQLite sortiert NULL-Werte je nach Provider unterschiedlich.
        return rows
            .OrderBy(s => s.ExpiryDate ?? DateTime.MaxValue)
            .ThenBy(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .ToList();
    }
}
