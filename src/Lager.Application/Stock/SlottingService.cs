using Lager.Application.Abstractions;
using Lager.Application.Reports;
using Lager.Contracts.Stock;
using Lager.Domain.Articles;
using Lager.Domain.Stock;
using Lager.Domain.Warehouse;

namespace Lager.Application.Stock;

/// <summary>
/// Periodically suggest "move article X from cold bin A to hot bin B" based
/// on pick frequency × bin distance from the first Start-PickPoint. Read-only:
/// produces suggestions, never executes the moves automatically.
///
/// Ein Vorschlag ist entweder ein Tausch (X zieht in einen belegten Bin, dessen Artikel Y dafür in den
/// bisherigen Bin von X zieht) oder eine Verschiebung in einen leeren Bin. Die Ersparnis wird immer
/// netto über beide Artikel gerechnet, siehe <see cref="SlottingCalculator"/>. Grundlage sind nur
/// tatsächlich gepickte Positionen (Picklisten Picked/Completed mit Ist-Menge &gt; 0).
/// </summary>
public class SlottingService
{
    private readonly IReportQueryGateway _reports;
    private readonly IWarehouseRepository _warehouse;
    private readonly IStockRepository _stock;
    private readonly IArticleRepository _articles;

    public SlottingService(
        IReportQueryGateway reports,
        IWarehouseRepository warehouse,
        IStockRepository stock,
        IArticleRepository articles)
    {
        _reports = reports;
        _warehouse = warehouse;
        _stock = stock;
        _articles = articles;
    }

    /// <summary>
    /// Liefert Umlagerungs-Empfehlungen, beste (höchste Netto-Ersparnis) zuerst. Ein Tausch erscheint als zwei
    /// direkt aufeinanderfolgende Zeilen (heißer Artikel, dann Tauschpartner); beide tragen die
    /// Netto-Ersparnis des gesamten Tauschs, keine Einzelwerte. <paramref name="topN"/> (1 bis 100) zählt
    /// Empfehlungen, nicht Zeilen. EstimatedSavingsMm ist ein int der API und wird bei Überlauf auf
    /// int.MaxValue begrenzt (gerechnet wird in long). Startpunkt der Distanz ist der erste Start-PickPoint,
    /// sonst der Ursprung — wie bei der Pickroute.
    /// </summary>
    public async Task<IReadOnlyList<SlottingSuggestionDto>> SuggestAsync(int rangeDays = 30, int topN = 10, CancellationToken ct = default)
    {
        rangeDays = Math.Clamp(rangeDays, 1, 365);
        topN = Math.Clamp(topN, 1, SlottingCalculator.MaxTopN);
        var to = DateTime.UtcNow;
        var from = to.AddDays(-rangeDays);

        var picks = await _reports.PickedItemsInRangeAsync(from, to, ct);
        if (picks.Count == 0) return Array.Empty<SlottingSuggestionDto>();

        var bins = await _warehouse.ListStorageLocationsAsync(ct);
        if (bins.Count == 0) return Array.Empty<SlottingSuggestionDto>();
        var pickPoints = await _warehouse.ListPickPointsAsync(ct);
        var start = pickPoints.FirstOrDefault(p => p.Type == PickPointType.Start || p.Type == PickPointType.Both)?.Position
            ?? Position.Origin;

        var allStock = (await _stock.ListAllAsync(ct)).Where(s => s.Quantity > 0).ToList();
        var articles = await _articles.GetManyAsync(allStock.Select(s => s.ArticleId).Distinct().ToList(), ct);

        var moves = SlottingCalculator.Compute(bins, allStock, articles, picks, p => SlottingCalculator.DistanceMm(start, p), topN);

        var binById = bins.ToDictionary(b => b.Id);
        var rows = new List<SlottingSuggestionDto>();
        foreach (var m in moves)
        {
            var oldBin = binById[m.FromBinId];
            var newBin = binById[m.ToBinId];
            var savings = ToInt(m.NetSavingsMm);
            rows.Add(ToDto(m.ArticleId, m.ArticleFrequency, oldBin, newBin, savings, articles, start));
            if (m.PartnerArticleId is Guid partner)
                rows.Add(ToDto(partner, m.PartnerFrequency, newBin, oldBin, savings, articles, start));
        }
        return rows;
    }

    private static SlottingSuggestionDto ToDto(
        Guid articleId, int frequency, StorageLocation current, StorageLocation suggested, int savings,
        IReadOnlyDictionary<Guid, Article> articles, Position start)
    {
        var meta = articles.TryGetValue(articleId, out var a) ? (a.Sku, a.Name) : ("?", "?");
        return new SlottingSuggestionDto(
            articleId, meta.Item1, meta.Item2, frequency,
            current.Id, current.Code, ToInt(SlottingCalculator.DistanceMm(start, current.Position)),
            suggested.Id, suggested.Code, ToInt(SlottingCalculator.DistanceMm(start, suggested.Position)),
            savings);
    }

    private static int ToInt(long value) => (int)Math.Clamp(value, int.MinValue, int.MaxValue);
}

/// <summary>
/// Eine Umlagerungs-Empfehlung. Verschiebung in einen leeren Bin (PartnerArticleId = null) oder Paar-Tausch.
/// Alle Distanzwerte und Ersparnisse in mm, als long gerechnet.
/// </summary>
/// <param name="ArticleId">Der heiße Artikel, der näher an den Start rückt.</param>
/// <param name="FromBinId">Bisheriger Bin des heißen Artikels (liegt weiter vom Start entfernt).</param>
/// <param name="ToBinId">Ziel-Bin, näher am Start.</param>
/// <param name="PartnerArticleId">Artikel, der aus dem Ziel-Bin in <paramref name="FromBinId"/> ausweicht; null bei leerem Ziel.</param>
/// <param name="ArticleGainMm">Wegersparnis des heißen Artikels: (Distanz alt − Distanz neu) × Pickfrequenz.</param>
/// <param name="PartnerLossMm">Wegmehraufwand des Partners durch das Ausweichen (0 ohne Partner).</param>
public sealed record SlottingMove(
    Guid ArticleId,
    Guid FromBinId,
    Guid ToBinId,
    Guid? PartnerArticleId,
    int ArticleFrequency,
    int PartnerFrequency,
    long ArticleGainMm,
    long PartnerLossMm)
{
    /// <summary>Gesamtersparnis der Empfehlung: Gewinn des heißen Artikels minus Verlust des Partners.</summary>
    public long NetSavingsMm => ArticleGainMm - PartnerLossMm;
}

/// <summary>
/// Slotting-Berechnung als reine Funktion (kein Repository, direkt testbar).
///
/// Modell: Der heißeste Artikel zuerst (Pickzeilen absteigend, Gleichstand SKU). Sein "aktueller Bin" ist der
/// Bin mit den meisten Pickzeilen dieses Artikels, in dem er auch Bestand hat (sonst der mit dem größten
/// Bestand). Zielkandidaten sind alle Bins, die näher am Start liegen, kein Reserve-Bin sind (Reserve speist
/// die Pick-Bins und wird nicht bepickt), noch nicht in einer anderen Empfehlung vergeben sind und in die der
/// gesamte Bestand des Artikels nach <see cref="BinFit"/> passt. Ein Ziel ist entweder leer
/// (Verschiebung, Gewinn = (Distanz alt − neu) × Frequenz) oder enthält genau einen anderen Artikel Y
/// (Tausch: Y zieht in den alten Bin von X, sofern Y dort nach <see cref="BinFit"/> Platz hat;
/// netto = (Distanz alt − neu) × (Frequenz X − Frequenz Y)). Bins mit mehreren Artikeln sind keine Ziele.
/// Es wird nur vorgeschlagen, wenn die Netto-Ersparnis &gt; 0 ist; bester Kandidat nach Netto, dann weniger
/// Umlagerungen (leeres Ziel vor Tausch), dann näher am Start, dann Bin-Code. Greedy: kein Mehrfachring.
///
/// Distanz: <see cref="DistanceMm"/> ist die euklidische Luftlinie in der Bodenebene (X/Y) — dieselbe Metrik
/// wie die Pickrouten. Wände werden nicht umgangen (die Route tut das); die Luftlinie ist daher eine untere
/// Schranke der Wegstrecke und als Rangsignal gedacht. Alle Größen sind long, ein Überlauf ist ausgeschlossen.
/// </summary>
public static class SlottingCalculator
{
    public const int MaxTopN = 100;

    /// <summary>Euklidische Luftlinie X/Y in mm (Höhe ignoriert, wie in den Pickrouten), gerundet.</summary>
    public static long DistanceMm(Position from, Position to)
    {
        double dx = (double)to.XMm - from.XMm;
        double dy = (double)to.YMm - from.YMm;
        return (long)Math.Round(Math.Sqrt(dx * dx + dy * dy));
    }

    public static IReadOnlyList<SlottingMove> Compute(
        IReadOnlyList<StorageLocation> bins,
        IReadOnlyList<StockItem> stock,
        IReadOnlyDictionary<Guid, Article> articles,
        IReadOnlyList<PickedItemSnapshot> picks,
        Func<Position, long> distanceFromStartMm,
        int maxMoves)
    {
        maxMoves = Math.Clamp(maxMoves, 1, MaxTopN);
        if (picks.Count == 0 || bins.Count == 0) return Array.Empty<SlottingMove>();

        var dist = bins.ToDictionary(b => b.Id, b => distanceFromStartMm(b.Position));
        var binById = bins.ToDictionary(b => b.Id);

        // Bestand je Bin (nur Menge > 0) und je Artikel.
        var stockInBin = new Dictionary<Guid, List<StockItem>>();
        var qtyByArticleBin = new Dictionary<(Guid Article, Guid Bin), int>();
        foreach (var s in stock)
        {
            if (s.Quantity <= 0 || !binById.ContainsKey(s.StorageLocationId)) continue;
            if (!stockInBin.TryGetValue(s.StorageLocationId, out var list))
                stockInBin[s.StorageLocationId] = list = new List<StockItem>();
            list.Add(s);
            var key = (s.ArticleId, s.StorageLocationId);
            qtyByArticleBin[key] = qtyByArticleBin.GetValueOrDefault(key) + s.Quantity;
        }

        // Pickfrequenz = Anzahl gepickter Zeilen (nicht Summenmenge), gesamt und je Bin.
        var freq = new Dictionary<Guid, int>();
        var freqByBin = new Dictionary<(Guid Article, Guid Bin), int>();
        foreach (var p in picks)
        {
            freq[p.ArticleId] = freq.GetValueOrDefault(p.ArticleId) + 1;
            var key = (p.ArticleId, p.BinId);
            freqByBin[key] = freqByBin.GetValueOrDefault(key) + 1;
        }

        string SkuOf(Guid id) => articles.TryGetValue(id, out var a) ? a.Sku : "";

        // Distinkte Artikel je Bin.
        List<Guid> ArticlesIn(Guid binId) => stockInBin.TryGetValue(binId, out var l)
            ? l.Select(s => s.ArticleId).Distinct().ToList()
            : new List<Guid>();

        bool ArticleFits(Guid articleId, int quantity, StorageLocation bin, Guid? leavingArticleId)
        {
            // Unbekannter Artikel (gelöscht/nicht geladen): keine Maße prüfbar, nicht blockieren.
            if (!articles.TryGetValue(articleId, out var article)) return true;
            var (usedVolume, usedWeight) = BinFit.Occupancy(
                stockInBin.GetValueOrDefault(bin.Id), articles, leavingArticleId);
            return BinFit.Fits(article, quantity, bin, usedVolume, usedWeight);
        }

        var claimedBins = new HashSet<Guid>();
        var usedArticles = new HashSet<Guid>();
        var moves = new List<SlottingMove>();

        var hotFirst = freq.Keys
            .OrderByDescending(id => freq[id])
            .ThenBy(id => SkuOf(id), StringComparer.Ordinal)
            .ThenBy(id => id);

        foreach (var x in hotFirst)
        {
            if (usedArticles.Contains(x)) continue;
            var fx = freq[x];

            // Aktueller Bin: dort, wo der Artikel tatsächlich gepickt wird und Bestand hat.
            var current = bins
                .Where(b => qtyByArticleBin.ContainsKey((x, b.Id)))
                .OrderByDescending(b => freqByBin.GetValueOrDefault((x, b.Id)))
                .ThenByDescending(b => qtyByArticleBin[(x, b.Id)])
                .ThenBy(b => b.Code, StringComparer.Ordinal)
                .FirstOrDefault();
            if (current is null || claimedBins.Contains(current.Id)) continue;

            var qtyX = qtyByArticleBin[(x, current.Id)];
            var distCurrent = dist[current.Id];

            SlottingMove? best = null;
            long bestDist = 0;
            string bestCode = "";
            foreach (var target in bins)
            {
                if (target.Id == current.Id || claimedBins.Contains(target.Id)) continue;
                if (target.BinType == BinType.Reserve) continue;
                var distTarget = dist[target.Id];
                if (distTarget >= distCurrent) continue;

                var occupants = ArticlesIn(target.Id);
                if (occupants.Count > 1) continue;                       // Mischbin: kein Tausch
                Guid? partner = occupants.Count == 1 ? occupants[0] : null;
                if (partner == x || (partner is Guid p0 && usedArticles.Contains(p0))) continue;

                // X zieht ein; der Partner (falls vorhanden) zieht aus, der Bin ist danach nur von X belegt.
                if (!ArticleFits(x, qtyX, target, partner)) continue;

                var gain = (distCurrent - distTarget) * fx;
                long loss = 0;
                int fy = 0;
                if (partner is Guid y)
                {
                    // Y zieht in den alten Bin von X (X zieht aus).
                    if (!ArticleFits(y, qtyByArticleBin[(y, target.Id)], current, x)) continue;
                    fy = freq.GetValueOrDefault(y);
                    loss = (distCurrent - distTarget) * fy;
                }

                var move = new SlottingMove(x, current.Id, target.Id, partner, fx, fy, gain, loss);
                if (move.NetSavingsMm <= 0) continue;

                if (best is null || IsBetter(move, distTarget, target.Code, best, bestDist, bestCode))
                {
                    best = move;
                    bestDist = distTarget;
                    bestCode = target.Code;
                }
            }

            if (best is null) continue;
            moves.Add(best);
            claimedBins.Add(best.FromBinId);
            claimedBins.Add(best.ToBinId);
            usedArticles.Add(x);
            if (best.PartnerArticleId is Guid used) usedArticles.Add(used);
            if (moves.Count >= maxMoves) break;
        }

        return moves
            .OrderByDescending(m => m.NetSavingsMm)
            .ThenByDescending(m => m.ArticleFrequency)
            .ThenBy(m => SkuOf(m.ArticleId), StringComparer.Ordinal)
            .ToList();
    }

    private static bool IsBetter(SlottingMove move, long dist, string code, SlottingMove best, long bestDist, string bestCode)
    {
        if (move.NetSavingsMm != best.NetSavingsMm) return move.NetSavingsMm > best.NetSavingsMm;
        var moveIsSimple = move.PartnerArticleId is null;
        var bestIsSimple = best.PartnerArticleId is null;
        if (moveIsSimple != bestIsSimple) return moveIsSimple;   // weniger Umlagerungen
        if (dist != bestDist) return dist < bestDist;
        return string.CompareOrdinal(code, bestCode) < 0;
    }
}
