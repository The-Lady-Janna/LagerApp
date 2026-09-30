using Lager.Domain.Articles;

namespace Lager.Application.Abstractions;

public interface IArticleRepository : IRepository<Article>
{
    /// <summary>Artikel zur SKU, Groß-/Kleinschreibung und Leerraum am Rand egal (null = unbekannt).</summary>
    Task<Article?> GetBySkuAsync(string sku, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, Article>> GetManyAsync(IEnumerable<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// Alle Artikel mit dieser GTIN, auch wenn sie in gleichwertiger Schreibweise gespeichert ist (UPC-A/EAN-13/GTIN-14,
    /// siehe <see cref="Gtin.EquivalentForms"/>). Wegen des Unique-Index höchstens einer; die Liste fängt Altdaten ab.
    /// Mit Standardimplementierung (Filter über <see cref="IRepository{T}.ListAsync"/>), damit bestehende Implementierungen
    /// der Schnittstelle (z. B. Test-Fakes) nicht angepasst werden müssen; das Datenbank-Repository überschreibt sie mit einer Abfrage.
    /// </summary>
    async Task<IReadOnlyList<Article>> FindByGtinAsync(string gtin, CancellationToken ct = default) =>
        (await ListAsync(ct)).Where(a => a.HasGtin(gtin)).ToList();

    /// <summary>
    /// Alle Artikel, die <paramref name="sku"/> als Alternativ-SKU führen (Groß-/Kleinschreibung und Leerraum egal).
    /// Standardimplementierung und Überschreibung wie bei <see cref="FindByGtinAsync"/>.
    /// </summary>
    async Task<IReadOnlyList<Article>> FindByAlternativeSkuAsync(string sku, CancellationToken ct = default) =>
        (await ListAsync(ct)).Where(a => a.HasAlternativeSku(sku)).ToList();
}
