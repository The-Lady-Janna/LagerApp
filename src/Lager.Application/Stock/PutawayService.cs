using Lager.Application.Abstractions;
using Lager.Contracts.Stock;
using Lager.Domain.Articles;
using Lager.Domain.Stock;
using Lager.Domain.Warehouse;

namespace Lager.Application.Stock;

/// <summary>
/// Given an article + quantity (typically from Wareneingang), suggest the
/// best bins to store the goods in. Nur Bins, in die die Ware wirklich passt
/// (Kantenlängen, freies Volumen, Restgewicht), werden vorgeschlagen. Ranking in
/// klar getrennten Stufen (siehe <see cref="PutawayScorer"/>):
///   1. Bins that already hold the same article (consolidate, less search later)
///   2. Empty bins in the same shelf as other instances of the article
///   3. Any other empty bin of suitable size
///   4. Mixed bins (weniger andere Artikel besser); innerhalb der Stufe Best-Fit (engste Passung)
/// </summary>
public class PutawayService
{
    private readonly IWarehouseRepository _warehouse;
    private readonly IStockRepository _stock;
    private readonly IArticleRepository _articles;

    public PutawayService(IWarehouseRepository warehouse, IStockRepository stock, IArticleRepository articles)
    {
        _warehouse = warehouse;
        _stock = stock;
        _articles = articles;
    }

    /// <summary>
    /// Liefert höchstens <paramref name="topN"/> (1 bis 50) passende Bins, beste zuerst. Unbekannter Artikel,
    /// Menge &lt;= 0 oder kein passender Bin ergeben eine leere Liste.
    /// </summary>
    public async Task<IReadOnlyList<PutawaySuggestionDto>> SuggestAsync(Guid articleId, int quantity, int topN = 5, CancellationToken ct = default)
    {
        if (quantity <= 0) return Array.Empty<PutawaySuggestionDto>();
        var article = await _articles.GetAsync(articleId, ct);
        if (article is null) return Array.Empty<PutawaySuggestionDto>();

        var allBins = await _warehouse.ListStorageLocationsAsync(ct);
        var allStock = (await _stock.ListAllAsync(ct)).Where(s => s.Quantity > 0).ToList();

        var stockByBin = allStock.GroupBy(s => s.StorageLocationId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<StockItem>)g.ToList());

        // Masse/Gewicht der bereits eingelagerten Artikel (Belegung der Ziel-Bins) einmal gebündelt laden.
        var articles = new Dictionary<Guid, Article>(
            await _articles.GetManyAsync(allStock.Select(s => s.ArticleId).Distinct().ToList(), ct))
        {
            [article.Id] = article,
        };

        return PutawayScorer.Rank(article, quantity, allBins, stockByBin, articles, topN);
    }
}

/// <summary>
/// Passungsprüfung Artikel/Bin als reine Funktion. Bewusst grob und für Lager-Vorschläge gedacht:
/// Kantenlängen (Rotation in 90°-Schritten erlaubt, Vergleich der sortierten Kanten), Volumen
/// (Stückzahl × Artikelvolumen gegen das freie Bin-Volumen, ohne Stapel-/Packlogik) und Gewicht
/// (MaxWeightGrams des Bins, 0 = unbegrenzt). Unbekannte Artikelmaße (0×0×0) blockieren nicht.
/// Volumen wird in <see cref="Int128"/> gerechnet und kann nicht überlaufen.
/// </summary>
public static class BinFit
{
    /// <summary>Bin-Volumen in mm³ (überlaufsicher).</summary>
    public static Int128 BinVolumeMm3(StorageLocation bin) =>
        (Int128)bin.WidthMm * bin.DepthMm * bin.HeightMm;

    /// <summary>Volumenbedarf von <paramref name="quantity"/> Stück in mm³ (überlaufsicher).</summary>
    public static Int128 VolumeNeededMm3(Article article, int quantity) =>
        (Int128)article.Dimensions.LengthMm * article.Dimensions.WidthMm * article.Dimensions.HeightMm * quantity;

    /// <summary>True, wenn ein einzelnes Stück (in beliebiger 90°-Lage) in die Bin-Kanten passt.</summary>
    public static bool EdgesFit(Dimensions item, StorageLocation bin)
    {
        var a = new[] { item.LengthMm, item.WidthMm, item.HeightMm };
        var b = new[] { bin.WidthMm, bin.DepthMm, bin.HeightMm };
        Array.Sort(a);
        Array.Sort(b);
        return a[0] <= b[0] && a[1] <= b[1] && a[2] <= b[2];
    }

    /// <summary>
    /// Belegung eines Bins: Volumen (mm³) und Gewicht (g) des vorhandenen Bestands, optional ohne einen
    /// Artikel (z. B. den Artikel, der gerade auszieht). Unbekannte Artikel zählen mit 0.
    /// </summary>
    public static (Int128 VolumeMm3, long WeightGrams) Occupancy(
        IEnumerable<StockItem>? stockInBin, IReadOnlyDictionary<Guid, Article> articles, Guid? excludeArticleId = null)
    {
        Int128 volume = 0;
        long weight = 0;
        if (stockInBin is null) return (volume, weight);
        foreach (var s in stockInBin)
        {
            if (s.Quantity <= 0 || s.ArticleId == excludeArticleId) continue;
            if (!articles.TryGetValue(s.ArticleId, out var other)) continue;
            volume += VolumeNeededMm3(other, s.Quantity);
            weight += (long)other.WeightGrams * s.Quantity;
        }
        return (volume, weight);
    }

    /// <summary>
    /// Passen <paramref name="quantity"/> Stück des Artikels zusätzlich in den Bin, der bereits
    /// <paramref name="usedVolumeMm3"/> / <paramref name="usedWeightGrams"/> belegt hat?
    /// </summary>
    public static bool Fits(Article article, int quantity, StorageLocation bin,
        Int128 usedVolumeMm3 = default, long usedWeightGrams = 0)
    {
        if (quantity <= 0) return false;
        if (!EdgesFit(article.Dimensions, bin)) return false;
        if (usedVolumeMm3 + VolumeNeededMm3(article, quantity) > BinVolumeMm3(bin)) return false;
        if (bin.MaxWeightGrams > 0 &&
            usedWeightGrams + (long)article.WeightGrams * quantity > bin.MaxWeightGrams) return false;
        return true;
    }
}

/// <summary>
/// Rangfolge der Einlagerungs-Vorschläge als reine Funktion (direkt testbar, ohne Repositories).
///
/// Der Score ist ein Summen-Score mit bewusst getrennten Größenordnungen, sodass er wie eine
/// lexikografische Sortierung wirkt (höhere Stufe schlägt jede Kombination der niedrigeren):
/// <list type="bullet">
///   <item>10000 — Artikel liegt schon im Bin (Konsolidieren)</item>
///   <item>2000 — leerer Bin im selben Regal wie vorhandener Bestand des Artikels</item>
///   <item>1000 — leerer Bin</item>
///   <item>0..100 — Best-Fit: 50 + halber Füllgrad nach der Einlagerung (engste Passung gewinnt) minus
///         10 je anderem Artikel im Bin (höchstens 50)</item>
///   <item>0..10 — Bin-Typ: Reserve bei großer Menge (&gt; <see cref="BulkQuantityThreshold"/>: +10),
///         HotPick bei kleiner Menge (+5)</item>
/// </list>
/// Nicht passende Bins (Kanten, freies Volumen, Restgewicht) werden nicht geliefert. Gleicher Score:
/// Bin-Code aufsteigend. "Zone" ist ohne Regal/Gang-Abfragen nicht verfügbar; das Regal (ShelfId) des
/// Bins dient als Näherung.
/// </summary>
public static class PutawayScorer
{
    /// <summary>Ab dieser Menge gilt eine Einlagerung als Großmenge (Reserve-Bins bevorzugt), sonst Kleinmenge (HotPick).</summary>
    public const int BulkQuantityThreshold = 20;

    public const int MaxTopN = 50;

    public static IReadOnlyList<PutawaySuggestionDto> Rank(
        Article article,
        int quantity,
        IReadOnlyList<StorageLocation> bins,
        IReadOnlyDictionary<Guid, IReadOnlyList<StockItem>> stockByBin,
        IReadOnlyDictionary<Guid, Article> articles,
        int topN = 5)
    {
        if (quantity <= 0) return Array.Empty<PutawaySuggestionDto>();
        topN = Math.Clamp(topN, 1, MaxTopN);

        // Regale, in denen der Artikel schon liegt (Näherung für "gleiche Zone").
        var binById = bins.ToDictionary(b => b.Id);
        var shelvesWithArticle = new HashSet<Guid>();
        foreach (var (binId, items) in stockByBin)
        {
            if (!binById.TryGetValue(binId, out var b)) continue;
            if (items.Any(s => s.ArticleId == article.Id && s.Quantity > 0)) shelvesWithArticle.Add(b.ShelfId);
        }

        var needed = BinFit.VolumeNeededMm3(article, quantity);
        var ranked = new List<(int Score, PutawaySuggestionDto Dto)>();
        foreach (var bin in bins)
        {
            stockByBin.TryGetValue(bin.Id, out var stockInBin);
            var (usedVolume, usedWeight) = BinFit.Occupancy(stockInBin, articles);
            if (!BinFit.Fits(article, quantity, bin, usedVolume, usedWeight)) continue;

            var thisArticleQty = stockInBin?.Where(s => s.ArticleId == article.Id).Sum(s => s.Quantity) ?? 0;
            var otherArticles = stockInBin?
                .Where(s => s.Quantity > 0 && s.ArticleId != article.Id)
                .Select(s => s.ArticleId).Distinct().Count() ?? 0;

            // Füllgrad nach der Einlagerung 0..100 (Best-Fit: engste Passung zuerst).
            var binVolume = BinFit.BinVolumeMm3(bin);
            var fillPercent = binVolume <= 0
                ? 0
                : (int)Int128.Clamp((usedVolume + needed) * 100 / binVolume, 0, 100);
            var score = 50 + fillPercent / 2 - Math.Min(otherArticles, 5) * 10;

            string reason;
            if (thisArticleQty > 0)
            {
                reason = "Konsolidieren — Artikel liegt schon hier";
                score += 10_000;
            }
            else if (otherArticles == 0 && shelvesWithArticle.Contains(bin.ShelfId))
            {
                reason = "Leerer Bin im selben Regal wie vorhandener Bestand";
                score += 2_000;
            }
            else if (otherArticles == 0)
            {
                reason = "Leerer Bin mit passender Größe";
                score += 1_000;
            }
            else
            {
                reason = $"{otherArticles} andere Artikel — gemischter Bin";
            }

            if (bin.BinType == BinType.Reserve && quantity > BulkQuantityThreshold) score += 10;
            if (bin.BinType == BinType.HotPick && quantity <= BulkQuantityThreshold) score += 5;

            ranked.Add((score, new PutawaySuggestionDto(
                bin.Id, bin.Code, bin.BinType.ToString(),
                thisArticleQty, otherArticles, score, reason)));
        }

        return ranked
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Dto.BinCode, StringComparer.Ordinal)
            .Take(topN)
            .Select(r => r.Dto)
            .ToList();
    }
}
