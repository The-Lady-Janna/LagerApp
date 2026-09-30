using Lager.Application.Stock;
using Lager.Domain.Articles;
using Lager.Domain.Stock;
using Lager.Domain.Warehouse;
using Lager.Infrastructure.Persistence.Repositories;

namespace Lager.Tests.WP10;

/// <summary>
/// Putaway: Konsolidieren steht vor "großer leerer Bin" (Score-Stufen), nur passende Bins
/// (Kanten, freies Volumen, Restgewicht), stabile Reihenfolge, keine Überläufe.
/// </summary>
public class PutawayTests
{
    private sealed class World
    {
        public List<StorageLocation> Bins { get; } = new();
        public Dictionary<Guid, List<StockItem>> StockByBin { get; } = new();
        public Dictionary<Guid, Article> Articles { get; } = new();

        public StorageLocation Bin(string code, int w = 600, int d = 600, int h = 500, int maxWeight = 0,
            BinType type = BinType.Standard, Guid? shelf = null)
        {
            var bin = Wp10.NewBin(code, 0, 0, w, d, h, maxWeight, type, shelf);
            Bins.Add(bin);
            return bin;
        }

        public Article Art(string sku, int l = 20, int w = 6, int h = 6, int weight = 10)
        {
            var a = Wp10.NewArticle(sku, l, w, h, weight);
            Articles[a.Id] = a;
            return a;
        }

        public void Put(Article a, StorageLocation bin, int qty)
        {
            if (!StockByBin.TryGetValue(bin.Id, out var list)) StockByBin[bin.Id] = list = new List<StockItem>();
            list.Add(new StockItem(a.Id, bin.Id, qty));
        }

        public IReadOnlyList<Lager.Contracts.Stock.PutawaySuggestionDto> Rank(Article a, int quantity, int topN = 5) =>
            PutawayScorer.Rank(a, quantity, Bins,
                StockByBin.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<StockItem>)kv.Value), Articles, topN);
    }

    [Fact]
    public void Consolidating_beats_a_bigger_empty_bin()
    {
        // Artikel 20x6x6, Menge 100 (72.000 mm3). Bin A 600x600x500 enthält ihn schon, Bin B 1200x800x1000 ist leer.
        // Früher: B (Score 13.333) vor A (2.600), entgegen der dokumentierten Priorität.
        var w = new World();
        var a = w.Bin("A");
        w.Bin("B", 1200, 800, 1000);
        var art = w.Art("SKU-1");
        w.Put(art, a, 50);

        var ranking = w.Rank(art, 100);

        Assert.Equal(new[] { "A", "B" }, ranking.Select(r => r.BinCode).ToArray());
        Assert.StartsWith("Konsolidieren", ranking[0].Reason);
        Assert.Equal(50, ranking[0].CurrentQuantityOfArticle);
        Assert.True(ranking[0].CapacityScore > ranking[1].CapacityScore);
    }

    [Fact]
    public void Best_fit_prefers_the_tightest_fitting_empty_bin()
    {
        var w = new World();
        w.Bin("BIG", 1200, 800, 1000);
        w.Bin("SNUG", 100, 100, 100);
        var art = w.Art("SKU-1", 20, 6, 6);

        var ranking = w.Rank(art, 100); // 72.000 mm3 in 1.000.000 mm3 (SNUG) bzw. 960 Mio. mm3

        Assert.Equal(new[] { "SNUG", "BIG" }, ranking.Select(r => r.BinCode).ToArray());
    }

    [Fact]
    public void Empty_bin_in_the_same_shelf_as_existing_stock_beats_any_other_empty_bin()
    {
        var shelf = Guid.NewGuid();
        var w = new World();
        var holder = w.Bin("HOLDER", 100, 100, 100, shelf: shelf);   // zu voll für die neue Menge, aber gleiches Regal
        w.Bin("AAA-OTHER-SHELF", 600, 600, 500);
        w.Bin("ZZZ-SAME-SHELF", 600, 600, 500, shelf: shelf);
        var art = w.Art("SKU-1", 20, 6, 6);
        w.Put(art, holder, 8000);                                     // 8000 x 720 = 5,76 Mio. > 1 Mio.: voll

        var ranking = w.Rank(art, 10);

        Assert.DoesNotContain("HOLDER", ranking.Select(r => r.BinCode));
        Assert.Equal(new[] { "ZZZ-SAME-SHELF", "AAA-OTHER-SHELF" }, ranking.Select(r => r.BinCode).ToArray());
        Assert.StartsWith("Leerer Bin im selben Regal", ranking[0].Reason);
    }

    [Fact]
    public void Bin_that_is_too_narrow_in_every_orientation_is_not_suggested()
    {
        // 900 x 600 x 8: Volumen passt in 600 x 600 x 500, die Kante 900 aber nirgends.
        var w = new World();
        w.Bin("SMALL", 600, 600, 500);
        var flat = w.Art("FLAT", 900, 600, 8);

        Assert.Empty(w.Rank(flat, 1));

        w.Bin("WIDE", 1000, 700, 100);
        Assert.Equal(new[] { "WIDE" }, w.Rank(flat, 1).Select(r => r.BinCode).ToArray());
    }

    [Fact]
    public void Bin_weight_limit_is_respected_including_what_already_lies_there()
    {
        var w = new World();
        var limited = w.Bin("LIMITED", maxWeight: 1000);
        w.Bin("FREE", maxWeight: 0);                          // 0 = unbegrenzt
        var heavy = w.Art("HEAVY", 10, 10, 10, weight: 400);
        Assert.Equal(new[] { "FREE", "LIMITED" }, w.Rank(heavy, 2).Select(r => r.BinCode).ToArray()); // 800 g <= 1000 g

        Assert.Equal(new[] { "FREE" }, w.Rank(heavy, 3).Select(r => r.BinCode).ToArray()); // 1200 g > 1000 g

        w.Put(heavy, limited, 1); // schon 400 g im Bin: 2 x 400 g = 1200 g passen dort nicht mehr dazu
        Assert.Equal(new[] { "FREE" }, w.Rank(heavy, 2).Select(r => r.BinCode).ToArray());
    }

    [Fact]
    public void Full_bin_holding_the_same_article_is_not_a_consolidation_target()
    {
        var w = new World();
        var full = w.Bin("FULL", 100, 100, 100);       // 1 Mio. mm3
        w.Bin("EMPTY", 600, 600, 500);
        var art = w.Art("SKU-1", 10, 10, 10);           // 1000 mm3 je Stück
        w.Put(art, full, 1000);                          // Bin ist randvoll

        var ranking = w.Rank(art, 1);

        Assert.Equal(new[] { "EMPTY" }, ranking.Select(r => r.BinCode).ToArray());
    }

    [Fact]
    public void Only_fitting_bins_are_returned_when_fewer_than_top_n_fit()
    {
        // Früher: nicht passende Bins mit Score -1 und dem Grund "Passt knapp" füllten die Liste auf.
        var w = new World();
        w.Bin("TINY-1", 5, 5, 5);
        w.Bin("TINY-2", 5, 5, 5);
        w.Bin("OK", 600, 600, 500);
        var art = w.Art("SKU-1", 20, 6, 6);

        var ranking = w.Rank(art, 1, topN: 5);

        Assert.Equal(new[] { "OK" }, ranking.Select(r => r.BinCode).ToArray());
        Assert.All(ranking, r => Assert.True(r.CapacityScore > 0));
    }

    [Fact]
    public void Equal_scores_are_ordered_by_bin_code()
    {
        var w = new World();
        w.Bin("B-3");
        w.Bin("B-1");
        w.Bin("B-2");
        var art = w.Art("SKU-1");

        Assert.Equal(new[] { "B-1", "B-2", "B-3" }, w.Rank(art, 1).Select(r => r.BinCode).ToArray());
    }

    [Fact]
    public void Mixed_bins_rank_below_empty_bins_and_fewer_foreign_articles_rank_higher()
    {
        var w = new World();
        var one = w.Bin("ONE-OTHER");
        var three = w.Bin("THREE-OTHERS");
        w.Bin("EMPTY");
        var art = w.Art("SKU-1");
        w.Put(w.Art("O1"), one, 1);
        w.Put(w.Art("O2"), three, 1); w.Put(w.Art("O3"), three, 1); w.Put(w.Art("O4"), three, 1);

        var ranking = w.Rank(art, 1);

        Assert.Equal(new[] { "EMPTY", "ONE-OTHER", "THREE-OTHERS" }, ranking.Select(r => r.BinCode).ToArray());
        Assert.Contains("gemischter Bin", ranking[1].Reason);
    }

    [Fact]
    public void Quantity_below_one_yields_nothing_and_huge_values_do_not_overflow()
    {
        var w = new World();
        w.Bin("HUGE", int.MaxValue, int.MaxValue, int.MaxValue);
        var art = w.Art("ZERO-DIMS", 0, 0, 0);

        Assert.Empty(w.Rank(art, 0));
        Assert.Empty(w.Rank(art, -5));

        // Bin-Volumen ~ 2^93 und Bonus-Addition: früher konnte der int-Score überlaufen und negativ werden.
        var ranking = w.Rank(art, int.MaxValue);
        Assert.Single(ranking);
        Assert.True(ranking[0].CapacityScore > 0);
    }

    [Fact]
    public void Type_bonus_never_outranks_the_tiers()
    {
        var w = new World();
        w.Bin("RESERVE-EMPTY", type: BinType.Reserve);
        var art = w.Art("SKU-1", 10, 10, 10);
        var stdHolder = w.Bin("STD-HOLDER");
        w.Put(art, stdHolder, 1);

        // Große Menge: Reserve bekommt +10, Konsolidieren bleibt trotzdem vorn. Bonus sprunghaft erst > 20 Stück.
        var big = w.Rank(art, 21);
        var small = w.Rank(art, 20);

        Assert.Equal("STD-HOLDER", big[0].BinCode);
        Assert.Equal("STD-HOLDER", small[0].BinCode);
        var reserveBig = big.Single(r => r.BinCode == "RESERVE-EMPTY").CapacityScore;
        var reserveSmall = small.Single(r => r.BinCode == "RESERVE-EMPTY").CapacityScore;
        Assert.True(reserveBig - reserveSmall is >= 9 and <= 11); // +10 Reserve-Bonus, dazu minimal geänderter Füllgrad
    }

    // ---- Service mit echtem EF-Modell ------------------------------------------------------------------------

    [Fact]
    public async Task Service_loads_occupancy_from_the_database_and_validates_input()
    {
        using var db = new Wp10Db();
        var withArticle = Wp10.NewBin("A", 0, 0, 600, 600, 500);
        var bigEmpty = Wp10.NewBin("B", 0, 0, 1200, 800, 1000);
        var art = Wp10.NewArticle("SKU-1", 20, 6, 6);
        await db.SaveAsync(withArticle, bigEmpty, art, new StockItem(art.Id, withArticle.Id, 50));
        var service = new PutawayService(new WarehouseRepository(db.Db), new StockRepository(db.Db), new ArticleRepository(db.Db));

        var ranking = await service.SuggestAsync(art.Id, 100);

        Assert.Equal(new[] { "A", "B" }, ranking.Select(r => r.BinCode).ToArray());
        Assert.Empty(await service.SuggestAsync(art.Id, 0));                 // früher: alle Bins, Score = Bin-Volumen
        Assert.Empty(await service.SuggestAsync(Guid.NewGuid(), 10));        // unbekannter Artikel
        Assert.Equal(2, (await service.SuggestAsync(art.Id, 10, topN: int.MaxValue)).Count); // begrenzt, kein Überlauf
        Assert.Single(await service.SuggestAsync(art.Id, 10, topN: -3));    // topN wird auf mindestens 1 geklemmt
    }
}
