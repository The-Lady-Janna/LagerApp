using Lager.Application.Reports;
using Lager.Application.Stock;
using Lager.Domain.Articles;
using Lager.Domain.PickLists;
using Lager.Domain.Stock;
using Lager.Domain.Warehouse;
using Lager.Infrastructure.Persistence.Repositories;

namespace Lager.Tests.WP10;

/// <summary>
/// Slotting: Paar-Tausch mit Netto-Ersparnis, kein Vorschlag bei Netto &lt;= 0, Reserve-Bins und nicht passende
/// Bins sind keine Ziele, Ersparnis läuft nicht über (long), deterministische Wahl.
/// </summary>
public class SlottingTests
{
    /// <summary>Kleine Lagerwelt: Start im Ursprung, Distanz = Luftlinie in X-Richtung.</summary>
    private sealed class World
    {
        public List<StorageLocation> Bins { get; } = new();
        public List<StockItem> Stock { get; } = new();
        public Dictionary<Guid, Article> Articles { get; } = new();
        public List<PickedItemSnapshot> Picks { get; } = new();

        public StorageLocation Bin(string code, int x, int y = 0, int w = 600, int d = 600, int h = 500,
            int maxWeight = 0, BinType type = BinType.Standard)
        {
            var bin = Wp10.NewBin(code, x, y, w, d, h, maxWeight, type);
            Bins.Add(bin);
            return bin;
        }

        public Article Art(string sku, int l = 10, int w = 10, int h = 10, int weight = 100)
        {
            var a = Wp10.NewArticle(sku, l, w, h, weight);
            Articles[a.Id] = a;
            return a;
        }

        public void Put(Article a, StorageLocation bin, int qty) => Stock.Add(new StockItem(a.Id, bin.Id, qty));

        public void PicksFrom(Article a, StorageLocation bin, int count)
        {
            for (var i = 0; i < count; i++) Picks.Add(Wp10.Pick(a.Id, bin.Id));
        }

        public IReadOnlyList<SlottingMove> Compute(int maxMoves = 10) =>
            SlottingCalculator.Compute(Bins, Stock, Articles, Picks,
                p => SlottingCalculator.DistanceMm(Position.Origin, p), maxMoves);
    }

    [Fact]
    public void Pair_swap_reports_the_net_saving_over_both_articles()
    {
        // Start (0,0); B1 1000 mm, B3 3000 mm. X (10 Picks) liegt in B3, Y (5 Picks) in B1.
        // Früher: einseitig "X: B3 -> B1, spart 20.000"; das Verdrängen von Y (5 x 2.000 = 10.000) fehlte.
        var w = new World();
        var b1 = w.Bin("B1", 1000);
        var b3 = w.Bin("B3", 3000);
        var x = w.Art("X");
        var y = w.Art("Y");
        w.Put(x, b3, 10); w.Put(y, b1, 10);
        w.PicksFrom(x, b3, 10); w.PicksFrom(y, b1, 5);

        var move = Assert.Single(w.Compute());

        Assert.Equal(x.Id, move.ArticleId);
        Assert.Equal(b3.Id, move.FromBinId);
        Assert.Equal(b1.Id, move.ToBinId);
        Assert.Equal(y.Id, move.PartnerArticleId);
        Assert.Equal(20_000, move.ArticleGainMm);
        Assert.Equal(10_000, move.PartnerLossMm);
        Assert.Equal(10_000, move.NetSavingsMm);
    }

    [Theory]
    [InlineData(5, 5)]   // gleiche Frequenz: Netto 0
    [InlineData(6, 10)]  // der Partner ist heißer und liegt schon vorne
    public void No_swap_is_suggested_when_the_total_saving_is_zero_or_negative(int freqFar, int freqNear)
    {
        var w = new World();
        var near = w.Bin("NEAR", 1000);
        var far = w.Bin("FAR", 3000);
        var x = w.Art("X");
        var y = w.Art("Y");
        w.Put(x, far, 10); w.Put(y, near, 10);
        w.PicksFrom(x, far, freqFar); w.PicksFrom(y, near, freqNear);

        Assert.Empty(w.Compute());
    }

    [Fact]
    public void Empty_target_is_preferred_over_a_swap_with_the_same_net_saving()
    {
        // X -> B1 (Tausch mit Y) und X -> B2 (leer) sparen beide netto 10.000; weniger Umlagerungen gewinnt.
        var w = new World();
        var b1 = w.Bin("B1", 1000);
        var b2 = w.Bin("B2", 2000);
        var b3 = w.Bin("B3", 3000);
        var x = w.Art("X");
        var y = w.Art("Y");
        w.Put(x, b3, 10); w.Put(y, b1, 10);
        w.PicksFrom(x, b3, 10); w.PicksFrom(y, b1, 5);

        var move = Assert.Single(w.Compute());

        Assert.Equal(b2.Id, move.ToBinId);
        Assert.Null(move.PartnerArticleId);
        Assert.Equal(10_000, move.NetSavingsMm);
    }

    [Fact]
    public void Reserve_bins_are_never_a_target_even_when_they_are_closest()
    {
        var w = new World();
        var reserve = w.Bin("RES", 500, type: BinType.Reserve);
        var far = w.Bin("FAR", 3000);
        var x = w.Art("X");
        w.Put(x, far, 10);
        w.PicksFrom(x, far, 10);
        Assert.Empty(w.Compute());

        var standard = w.Bin("STD", 2000);
        var move = Assert.Single(w.Compute());
        Assert.Equal(standard.Id, move.ToBinId);
        Assert.NotEqual(reserve.Id, move.ToBinId);
    }

    [Fact]
    public void Target_bin_must_fit_the_article_edges()
    {
        // 900 x 600 x 8 passt volumenmäßig in 600 x 600 x 500 (2,16 Mio. mm3 < 180 Mio.), aber 900 > 600 in jeder Lage.
        var w = new World();
        var tooSmall = w.Bin("SMALL", 500, w: 600, d: 600, h: 500);
        var far = w.Bin("FAR", 3000, w: 1000, d: 700, h: 100);
        var flat = w.Art("FLAT", l: 900, w: 600, h: 8);
        w.Put(flat, far, 1);
        w.PicksFrom(flat, far, 10);
        Assert.Empty(w.Compute());

        var wide = w.Bin("WIDE", 1000, w: 1000, d: 700, h: 100);
        var move = Assert.Single(w.Compute());
        Assert.Equal(wide.Id, move.ToBinId);
        Assert.NotEqual(tooSmall.Id, move.ToBinId);
    }

    [Theory]
    [InlineData(1000, true)]   // 1000 x 1000 mm3 = 1 Mio. mm3 = Bin-Volumen: passt genau
    [InlineData(1001, false)]  // ein Stück zu viel
    public void Target_bin_must_have_room_for_the_whole_stock(int quantity, bool suggested)
    {
        var w = new World();
        w.Bin("NEAR", 500, w: 100, d: 100, h: 100);
        var far = w.Bin("FAR", 3000, w: 1000, d: 1000, h: 1000);
        var x = w.Art("X", l: 10, w: 10, h: 10);
        w.Put(x, far, quantity);
        w.PicksFrom(x, far, 10);

        Assert.Equal(suggested, w.Compute().Count == 1);
    }

    [Fact]
    public void Target_bin_must_carry_the_weight()
    {
        var w = new World();
        w.Bin("NEAR", 500, maxWeight: 500);
        var far = w.Bin("FAR", 3000);
        var x = w.Art("X", weight: 100);
        w.Put(x, far, 6); // 600 g > 500 g
        w.PicksFrom(x, far, 10);

        Assert.Empty(w.Compute());
    }

    [Fact]
    public void Savings_are_computed_in_long_and_do_not_overflow()
    {
        // Distanzgewinn 60.000 mm x 40.000 Picks = 2,4 Mrd. > int.MaxValue (vorher negativ -> verworfen).
        var w = new World();
        w.Bin("NEAR", 0);
        var far = w.Bin("FAR", 60_000);
        var x = w.Art("X");
        w.Put(x, far, 10);
        w.PicksFrom(x, far, 40_000);

        var move = Assert.Single(w.Compute());

        Assert.Equal(2_400_000_000L, move.NetSavingsMm);
    }

    [Fact]
    public void Equal_distances_are_decided_deterministically_by_bin_code()
    {
        var w = new World();
        w.Bin("B-Z", 0, 1000);
        var a = w.Bin("B-A", 1000, 0);
        w.Bin("B-M", 0, -1000);
        var far = w.Bin("FAR", 5000);
        var x = w.Art("X");
        w.Put(x, far, 1);
        w.PicksFrom(x, far, 3);

        Assert.Equal(a.Id, Assert.Single(w.Compute()).ToBinId);
    }

    [Fact]
    public void Top_n_limits_recommendations_and_extreme_values_do_not_overflow()
    {
        var w = new World();
        w.Bin("N1", 1000);
        w.Bin("N2", 1100);
        var f1 = w.Bin("F1", 5000);
        var f2 = w.Bin("F2", 6000);
        var x1 = w.Art("X1");
        var x2 = w.Art("X2");
        w.Put(x1, f1, 1); w.Put(x2, f2, 1);
        w.PicksFrom(x1, f1, 10); w.PicksFrom(x2, f2, 9);

        Assert.Equal(2, w.Compute().Count);
        Assert.Single(w.Compute(maxMoves: 1));
        Assert.Equal(2, w.Compute(maxMoves: int.MaxValue).Count); // früher topN * 3 -> Überlauf
    }

    [Fact]
    public void Picked_articles_without_stock_are_skipped()
    {
        var w = new World();
        w.Bin("NEAR", 1000);
        var far = w.Bin("FAR", 3000);
        var ghost = w.Art("GHOST");
        w.PicksFrom(ghost, far, 5);

        Assert.Empty(w.Compute());
    }

    // ---- Service mit echtem EF-Modell (In-Memory-SQLite) ---------------------------------------------------

    private static SlottingService NewService(Wp10Db db) =>
        new(db.Gateway, new WarehouseRepository(db.Db), new StockRepository(db.Db), new ArticleRepository(db.Db));

    [Fact]
    public async Task Service_counts_only_actually_picked_lines_and_returns_both_rows_of_a_swap()
    {
        using var db = new Wp10Db();
        var near = Wp10.NewBin("NEAR", 1000, 0);
        var far = Wp10.NewBin("FAR", 3000, 0);
        var x = Wp10.NewArticle("SKU-X");
        var y = Wp10.NewArticle("SKU-Y");
        var xLines = Enumerable.Range(0, 10).Select(_ => (x.Id, far.Id, 1, (int?)1)).ToArray();
        var yLines = Enumerable.Range(0, 5).Select(_ => (y.Id, near.Id, 1, (int?)1)).ToArray();
        // Diese Picklisten zählen NICHT: nie gepackt, in Arbeit, und eine Liste, in der nichts gepackt wurde (Ist = 0).
        var pendingNoise = Enumerable.Range(0, 100).Select(_ => (y.Id, near.Id, 1, (int?)null)).ToArray();
        var shortPickedNoise = Enumerable.Range(0, 100).Select(_ => (y.Id, near.Id, 1, (int?)0)).ToArray();
        await db.SaveAsync(near, far, x, y,
            new StockItem(x.Id, far.Id, 10), new StockItem(y.Id, near.Id, 10),
            Wp10.NewPickList("PL-X", PickListStatus.Completed, xLines),
            Wp10.NewPickList("PL-Y", PickListStatus.Completed, yLines),
            Wp10.NewPickList("PL-PENDING", PickListStatus.Pending, pendingNoise),
            Wp10.NewPickList("PL-WORK", PickListStatus.InProgress, pendingNoise),
            Wp10.NewPickList("PL-SHORT", PickListStatus.Completed, shortPickedNoise));

        var rows = await NewService(db).SuggestAsync();

        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "SKU-X", "SKU-Y" }, rows.Select(r => r.ArticleSku).ToArray());
        Assert.Equal("FAR", rows[0].CurrentBinCode);
        Assert.Equal("NEAR", rows[0].SuggestedBinCode);
        Assert.Equal("NEAR", rows[1].CurrentBinCode);   // Y weicht in den Bin von X aus
        Assert.Equal("FAR", rows[1].SuggestedBinCode);
        Assert.All(rows, r => Assert.Equal(10_000, r.EstimatedSavingsMm)); // netto, für beide Zeilen gleich
        Assert.Equal(10, rows[0].PickFrequency);
        Assert.Equal(5, rows[1].PickFrequency);
    }

    [Fact]
    public async Task Service_saturates_the_int_field_instead_of_overflowing_and_survives_extreme_top()
    {
        using var db = new Wp10Db();
        var near = Wp10.NewBin("NEAR", 0, 0);
        var far = Wp10.NewBin("FAR", 2_000_000_000, 0);
        var x = Wp10.NewArticle("SKU-X");
        await db.SaveAsync(near, far, x, new StockItem(x.Id, far.Id, 1),
            Wp10.NewPickList("PL-1", PickListStatus.Completed, (x.Id, far.Id, 1, 1), (x.Id, far.Id, 1, 1)));

        var rows = await NewService(db).SuggestAsync(rangeDays: 30, topN: int.MaxValue);

        var row = Assert.Single(rows);
        Assert.Equal(int.MaxValue, row.EstimatedSavingsMm); // 2 x 2 Mrd. mm = 4 Mrd. -> begrenzt
        Assert.Equal(2_000_000_000, row.CurrentDistanceMm);
    }

    [Fact]
    public async Task Service_without_picks_returns_nothing()
    {
        using var db = new Wp10Db();
        await db.SaveAsync(Wp10.NewBin("ONLY", 1000, 0));

        Assert.Empty(await NewService(db).SuggestAsync());
    }
}
