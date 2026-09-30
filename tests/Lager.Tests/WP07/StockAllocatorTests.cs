using Lager.Application.PickLists;
using Lager.Domain.Stock;
using Lager.Domain.Warehouse;

namespace Lager.Tests.WP07;

/// <summary>
/// Unit-Tests (ohne Host) für die reine Bestandsallokation: laufender Restbestand über alle Anforderungen,
/// FEFO, Ablauf, Bin-Typ, Fehlmenge.
/// </summary>
public class StockAllocatorTests
{
    private static readonly DateTime Today = new(2026, 9, 30);
    private static readonly Guid ArticleA = Guid.NewGuid();
    private static readonly Guid ArticleB = Guid.NewGuid();

    private static StorageLocation Bin(string code, BinType type = BinType.Standard)
    {
        var bin = new StorageLocation(Guid.NewGuid(), code, Position.Origin, 600, 600, 500, 50_000);
        if (type != BinType.Standard) bin.SetBinType(type, 0);
        return bin;
    }

    private static StockAllocator Allocator(IEnumerable<StockItem> stock, params StorageLocation[] bins) =>
        new(stock, bins.ToDictionary(b => b.Id), Today);

    private static PickRequest Request(Guid order, Guid article, int quantity, Guid? line = null) =>
        new(order, line ?? Guid.NewGuid(), article, quantity);

    private static int Sum(IEnumerable<PickCandidate> candidates, Guid bin) =>
        candidates.Where(c => c.StorageLocationId == bin).Sum(c => c.Quantity);

    [Fact]
    public void Two_orders_never_get_the_same_units_twice()
    {
        // Bin X hält 10, Bin Y 100: 8 + 8 dürfen nicht 16 aus X ziehen.
        var x = Bin("X");
        var y = Bin("Y");
        var allocator = Allocator(new[]
        {
            new StockItem(ArticleA, x.Id, 10),
            new StockItem(ArticleA, y.Id, 100),
        }, x, y);

        var result = allocator.Allocate(new[]
        {
            Request(Guid.NewGuid(), ArticleA, 8),
            Request(Guid.NewGuid(), ArticleA, 8),
        });

        Assert.Equal(16, result.Sum(c => c.Quantity));
        Assert.Equal(10, Sum(result, x.Id));   // X ist genau leer, nie überzogen
        Assert.Equal(6, Sum(result, y.Id));
        Assert.Equal(94, allocator.Available(ArticleA));     // 110 - 16
    }

    [Fact]
    public void Shortage_throws_with_sku_and_missing_quantity_and_leaves_state_untouched()
    {
        var x = Bin("X");
        var allocator = Allocator(new[] { new StockItem(ArticleA, x.Id, 10) }, x);
        var skus = new Dictionary<Guid, string> { [ArticleA] = "SKU-KNAPP" };

        var ex = Assert.Throws<InvalidOperationException>(() => allocator.Allocate(new[]
        {
            Request(Guid.NewGuid(), ArticleA, 8),
            Request(Guid.NewGuid(), ArticleA, 8),
        }, skus));

        Assert.Contains("SKU-KNAPP", ex.Message);
        Assert.Contains("Fehlmenge 6", ex.Message);       // 16 gefordert, 10 da
        Assert.Equal(10, allocator.Available(ArticleA));   // alles oder nichts: nichts wurde verbraucht
    }

    [Fact]
    public void Duplicate_article_lines_of_one_order_share_the_same_stock()
    {
        var x = Bin("X");
        var allocator = Allocator(new[] { new StockItem(ArticleA, x.Id, 10) }, x);
        var order = Guid.NewGuid();

        // zwei Zeilen desselben Artikels (8 + 8) gegen 10 Stück
        Assert.Throws<InvalidOperationException>(() => allocator.Allocate(new[]
        {
            Request(order, ArticleA, 8),
            Request(order, ArticleA, 8),
        }));

        // 4 + 6 passt genau
        var result = allocator.Allocate(new[] { Request(order, ArticleA, 4), Request(order, ArticleA, 6) });
        Assert.Equal(10, result.Sum(c => c.Quantity));
        Assert.Equal(0, allocator.Available(ArticleA));
    }

    [Fact]
    public void Earliest_expiry_goes_first_and_stock_without_expiry_goes_last()
    {
        var x = Bin("X");
        var y = Bin("Y");
        var z = Bin("Z");
        var allocator = Allocator(new[]
        {
            new StockItem(ArticleA, z.Id, 500),                                  // ohne MHD
            new StockItem(ArticleA, y.Id, 100, "LOT-Y", Today.AddDays(200)),
            new StockItem(ArticleA, x.Id, 5, "LOT-X", Today.AddDays(30)),
        }, x, y, z);

        var result = allocator.Allocate(new[] { Request(Guid.NewGuid(), ArticleA, 8) });

        Assert.Equal(5, Sum(result, x.Id));
        Assert.Equal(3, Sum(result, y.Id));
        Assert.Equal(0, Sum(result, z.Id));
    }

    [Fact]
    public void Expired_lots_are_not_available_but_expiry_today_still_is()
    {
        var expiredBin = Bin("EXP");
        var todayBin = Bin("TODAY");
        var freshBin = Bin("FRESH");
        var allocator = Allocator(new[]
        {
            new StockItem(ArticleA, expiredBin.Id, 50, "ALT", Today.AddDays(-1)),
            new StockItem(ArticleA, todayBin.Id, 5, "HEUTE", Today.AddHours(15)),   // läuft heute ab: noch gültig
            new StockItem(ArticleA, freshBin.Id, 50, "NEU", Today.AddDays(90)),
        }, expiredBin, todayBin, freshBin);

        Assert.Equal(55, allocator.Available(ArticleA));   // die abgelaufenen 50 zählen nicht

        var result = allocator.Allocate(new[] { Request(Guid.NewGuid(), ArticleA, 10) });
        Assert.Equal(0, Sum(result, expiredBin.Id));
        Assert.Equal(5, Sum(result, todayBin.Id));
        Assert.Equal(5, Sum(result, freshBin.Id));
    }

    [Fact]
    public void Only_expired_stock_counts_as_shortage()
    {
        var x = Bin("X");
        var allocator = Allocator(new[] { new StockItem(ArticleA, x.Id, 100, "ALT", Today.AddDays(-3)) }, x);

        var ex = Assert.Throws<InvalidOperationException>(() => allocator.Allocate(new[] { Request(Guid.NewGuid(), ArticleA, 1) }));
        Assert.Contains("Fehlmenge 1", ex.Message);
    }

    [Fact]
    public void HotPick_comes_before_standard_before_reserve_when_expiry_is_equal()
    {
        var hot = Bin("HOT", BinType.HotPick);
        var standard = Bin("STD");
        var reserve = Bin("RES", BinType.Reserve);
        var allocator = Allocator(new[]
        {
            new StockItem(ArticleA, reserve.Id, 500),   // der größte Bestand liegt im Reserve-Bin
            new StockItem(ArticleA, standard.Id, 100),
            new StockItem(ArticleA, hot.Id, 30),
        }, hot, standard, reserve);

        var result = allocator.Allocate(new[] { Request(Guid.NewGuid(), ArticleA, 5) });
        Assert.Equal(5, Sum(result, hot.Id));

        // Reicht der HotPick-Bin nicht, geht es Standard, dann Reserve.
        var next = allocator.Allocate(new[] { Request(Guid.NewGuid(), ArticleA, 40) });
        Assert.Equal(25, Sum(next, hot.Id));
        Assert.Equal(15, Sum(next, standard.Id));
        Assert.Equal(0, Sum(next, reserve.Id));
    }

    [Fact]
    public void Smaller_stock_is_used_first_and_ties_break_by_bin_code()
    {
        var big = Bin("B-BIG");
        var small = Bin("A-SMALL");
        var allocator = Allocator(new[]
        {
            new StockItem(ArticleA, big.Id, 10),
            new StockItem(ArticleA, small.Id, 3),
        }, big, small);

        var result = allocator.Allocate(new[] { Request(Guid.NewGuid(), ArticleA, 5) });
        Assert.Equal(3, Sum(result, small.Id));   // angebrochener kleiner Bestand zuerst
        Assert.Equal(2, Sum(result, big.Id));

        // Gleicher Bestand: der Lagerplatz-Code entscheidet - unabhängig von der Eingabereihenfolge.
        var b = Bin("B");
        var a = Bin("A");
        foreach (var order in new[] { new[] { b, a }, new[] { a, b } })
        {
            var tie = Allocator(order.Select(bin => new StockItem(ArticleA, bin.Id, 7)), a, b);
            var picked = tie.Allocate(new[] { Request(Guid.NewGuid(), ArticleA, 2) });
            Assert.Equal(a.Id, Assert.Single(picked).StorageLocationId);
        }
    }

    [Fact]
    public void Lots_in_the_same_bin_are_merged_into_one_candidate()
    {
        var x = Bin("X");
        var allocator = Allocator(new[]
        {
            new StockItem(ArticleA, x.Id, 5, "L1", Today.AddDays(10)),
            new StockItem(ArticleA, x.Id, 5, "L2", Today.AddDays(20)),
        }, x);

        var result = allocator.Allocate(new[] { Request(Guid.NewGuid(), ArticleA, 8) });

        // Ein Kandidat (sonst würde der Routenoptimierer zwei gleiche Datensätze zu einem zusammenfallen lassen).
        var candidate = Assert.Single(result);
        Assert.Equal(8, candidate.Quantity);
    }

    [Fact]
    public void Preview_does_not_consume_stock_and_reports_shortage()
    {
        var x = Bin("X");
        var allocator = Allocator(new[] { new StockItem(ArticleA, x.Id, 10) }, x);

        Assert.True(allocator.TryPreview(new[] { Request(Guid.NewGuid(), ArticleA, 10) }, out var preview));
        Assert.Equal(10, preview.Sum(c => c.Quantity));
        Assert.Equal(10, allocator.Available(ArticleA));   // Trockenlauf verbraucht nichts

        Assert.False(allocator.TryPreview(new[] { Request(Guid.NewGuid(), ArticleA, 11) }, out _));
        Assert.Equal(10, allocator.Available(ArticleA));
    }

    [Fact]
    public void Articles_are_allocated_independently()
    {
        var x = Bin("X");
        var y = Bin("Y");
        var allocator = Allocator(new[]
        {
            new StockItem(ArticleA, x.Id, 4),
            new StockItem(ArticleB, y.Id, 9),
        }, x, y);

        var result = allocator.Allocate(new[]
        {
            Request(Guid.NewGuid(), ArticleA, 4),
            Request(Guid.NewGuid(), ArticleB, 9),
        });

        Assert.Equal(4, Sum(result, x.Id));
        Assert.Equal(9, Sum(result, y.Id));
    }
}
