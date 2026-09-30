using Lager.Application.Abstractions;
using Lager.Domain.Articles;
using Microsoft.EntityFrameworkCore;

namespace Lager.Infrastructure.Persistence.Repositories;

public class ArticleRepository : IArticleRepository
{
    private readonly LagerDbContext _db;

    public ArticleRepository(LagerDbContext db) => _db = db;

    public Task<Article?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.Articles.Include(a => a.BundleComponents).FirstOrDefaultAsync(a => a.Id == id, ct);

    public async Task<IReadOnlyList<Article>> ListAsync(CancellationToken ct = default) =>
        await _db.Articles.AsNoTracking().Include(a => a.BundleComponents).OrderBy(a => a.Sku).ToListAsync(ct);

    public async Task AddAsync(Article entity, CancellationToken ct = default) =>
        await _db.Articles.AddAsync(entity, ct);

    public void Remove(Article entity) => _db.Articles.Remove(entity);

    /// <summary>
    /// Erst der direkte Vergleich (Index; bei neuen SQLite-Datenbanken hat die Spalte die Collation NOCASE, MySQL vergleicht
    /// ohnehin case-insensitiv), dann - nur bei einem Fehlschlag - ein Vergleich in Kleinschreibung: Legacy-SQLite-Spalten
    /// vergleichen binär, dort fände "sku-1" sonst "SKU-1" nicht.
    /// </summary>
    public async Task<Article?> GetBySkuAsync(string sku, CancellationToken ct = default)
    {
        var trimmed = sku?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;

        var exact = await _db.Articles.Include(a => a.BundleComponents).FirstOrDefaultAsync(a => a.Sku == trimmed, ct);
        if (exact is not null) return exact;

        var lower = trimmed.ToLowerInvariant();
        return await _db.Articles.Include(a => a.BundleComponents).FirstOrDefaultAsync(a => a.Sku.ToLower() == lower, ct);
    }

    public async Task<IReadOnlyList<Article>> FindByGtinAsync(string gtin, CancellationToken ct = default)
    {
        var forms = Gtin.EquivalentForms(gtin).ToArray();
        if (forms.Length == 0) return Array.Empty<Article>();

        return await _db.Articles.AsNoTracking().Include(a => a.BundleComponents)
            .Where(a => a.Gtin != null && forms.Contains(a.Gtin))
            .OrderBy(a => a.Sku)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Die Alternativ-SKUs stehen als Text mit Komma-Trennung in einer Spalte. Die Abfrage holt die Kandidaten per Teilstring
    /// (ohne Beachtung der Schreibweise), der genaue Vergleich Eintrag für Eintrag geschieht danach im Speicher.
    /// </summary>
    public async Task<IReadOnlyList<Article>> FindByAlternativeSkuAsync(string sku, CancellationToken ct = default)
    {
        var trimmed = sku?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return Array.Empty<Article>();

        var lower = trimmed.ToLowerInvariant();
        var candidates = await _db.Articles.AsNoTracking().Include(a => a.BundleComponents)
            .Where(a => a.AlternativeSkusCsv != null && a.AlternativeSkusCsv.ToLower().Contains(lower))
            .ToListAsync(ct);
        return candidates.Where(a => a.HasAlternativeSku(trimmed)).OrderBy(a => a.Sku, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<IReadOnlyDictionary<Guid, Article>> GetManyAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var idSet = ids.ToHashSet();
        var items = await _db.Articles.Include(a => a.BundleComponents)
            .Where(a => idSet.Contains(a.Id)).ToListAsync(ct);
        return items.ToDictionary(a => a.Id);
    }
}
