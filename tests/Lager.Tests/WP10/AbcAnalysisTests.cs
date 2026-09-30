using Lager.Application.Reports;

namespace Lager.Tests.WP10;

/// <summary>
/// ABC-Analyse: klassisches Pareto (Klasse nach dem kumulierten Anteil VOR dem Artikel), reine
/// Ganzzahl-/decimal-Rechnung. Vorher: Einzelartikel = "C", Gleitkomma-Drift an den Grenzen.
/// </summary>
public class AbcAnalysisTests
{
    private static string[] Classes(params long[] quantities)
    {
        var items = quantities.Select((q, i) => new AbcInput(Guid.NewGuid(), q, $"SKU-{i:D3}"));
        return AbcClassifier.Classify(items).Select(r => r.Class).ToArray();
    }

    [Fact]
    public void Single_article_is_class_A_with_100_percent()
    {
        var result = AbcClassifier.Classify(new[] { new AbcInput(Guid.NewGuid(), 42, "X") });

        var only = Assert.Single(result);
        Assert.Equal("A", only.Class);
        Assert.Equal(100m, only.SharePercent);
    }

    [Fact]
    public void Dominant_article_above_80_percent_stays_A_and_the_rest_moves_down()
    {
        Assert.Equal(new[] { "A", "B" }, Classes(90, 10));
        Assert.Equal(new[] { "A", "B", "C" }, Classes(85, 10, 5));
    }

    [Fact]
    public void Classic_pareto_50_30_15_5_gives_A_A_B_C()
    {
        Assert.Equal(new[] { "A", "A", "B", "C" }, Classes(50, 30, 15, 5));
    }

    [Fact]
    public void Exact_boundaries_are_lower_inclusive_and_upper_exclusive_without_float_drift()
    {
        // Menge 44 von 55 vor Artikel 3 = exakt 80 %: Untergrenze der Klasse B (inklusive).
        // Mit double kumuliert war das 79,99999999999999 -> falsch "A".
        Assert.Equal(new[] { "A", "A", "B" }, Classes(29, 15, 11));

        // Menge 57 von 60 vor Artikel 3 = exakt 95 %: Untergrenze der Klasse C.
        // Mit double kumuliert war das 94,99999999999999 -> falsch "B".
        Assert.Equal(new[] { "A", "A", "C" }, Classes(40, 17, 3));
    }

    [Theory]
    [InlineData(new long[] { 1, 1, 1 })]
    [InlineData(new long[] { 7, 7, 7, 7, 7, 7 })]
    [InlineData(new long[] { 43, 39, 32, 6 })]
    [InlineData(new long[] { 1000, 1, 1, 1, 1, 1, 1 })]
    public void Shares_add_up_to_exactly_100_percent(long[] quantities)
    {
        var items = quantities.Select((q, i) => new AbcInput(Guid.NewGuid(), q, $"SKU-{i:D3}"));

        var result = AbcClassifier.Classify(items);

        Assert.Equal(100m, result.Sum(r => r.SharePercent));
        Assert.All(result, r => Assert.Equal(r.SharePercent, Math.Round(r.SharePercent, 2)));
    }

    [Fact]
    public void Three_equal_articles_share_33_34_33_33_and_tie_order_follows_sku()
    {
        var b = new AbcInput(Guid.NewGuid(), 10, "SKU-B");
        var c = new AbcInput(Guid.NewGuid(), 10, "SKU-C");
        var a = new AbcInput(Guid.NewGuid(), 10, "SKU-A");

        var result = AbcClassifier.Classify(new[] { c, a, b });

        Assert.Equal(new[] { a.Id, b.Id, c.Id }, result.Select(r => r.Id).ToArray());
        Assert.Equal(new[] { 33.34m, 33.33m, 33.33m }, result.Select(r => r.SharePercent).ToArray());
        Assert.Equal(new[] { "A", "A", "A" }, result.Select(r => r.Class).ToArray()); // 0 %, 33 %, 67 % vor dem Artikel
    }

    [Fact]
    public void Empty_input_and_zero_quantities_give_empty_result()
    {
        Assert.Empty(AbcClassifier.Classify(Array.Empty<AbcInput>()));
        Assert.Empty(AbcClassifier.Classify(new[] { new AbcInput(Guid.NewGuid(), 0, "X") }));
    }

    [Fact]
    public void Invalid_thresholds_are_rejected()
    {
        var items = new[] { new AbcInput(Guid.NewGuid(), 1, "X") };
        Assert.Throws<ArgumentOutOfRangeException>(() => AbcClassifier.Classify(items, 95, 80));
        Assert.Throws<ArgumentOutOfRangeException>(() => AbcClassifier.Classify(items, 80, 101));
    }

    [Fact]
    public async Task Service_reports_single_picked_article_as_A_with_picked_quantity()
    {
        var article = Guid.NewGuid();
        var bin = Guid.NewGuid();
        var gateway = new FakeGateway();
        gateway.Names[article] = ("SKU-1", "Einzelartikel");
        gateway.Picked.Add(Wp10.Pick(article, bin, quantity: 3));
        gateway.Picked.Add(Wp10.Pick(article, bin, quantity: 4));

        var result = await new ReportService(gateway).AbcAnalysisAsync(30);

        var row = Assert.Single(result);
        Assert.Equal("A", row.Class);
        Assert.Equal(7, row.TotalQuantity);
        Assert.Equal(2, row.PickCount);
        Assert.Equal(100m, row.SharePercent);
    }

    [Fact]
    public async Task Service_returns_empty_without_picks()
    {
        Assert.Empty(await new ReportService(new FakeGateway()).AbcAnalysisAsync(30));
    }
}
