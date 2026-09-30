using System.Diagnostics;
using Lager.Application.Packing;
using Lager.Domain.Articles;
using Lager.Domain.Packing;

namespace Lager.Tests.WP08;

/// <summary>
/// Reine Unit-Tests (ohne Host) für <see cref="FirstFitDecreasingPackingOptimizer"/>: Geometrie statt Volumensumme,
/// Gewichtsregel inklusive Tara, größerer Karton bei Misserfolg, MaxStackCount, Mengen ohne Expansion.
/// Standardkartons: S 200x150x100 (max 5000 g, Tara 100), M 400x300x200 (15.000/200), L 600x400x300 (30.000/400),
/// XL 800x600x400 (60.000/700).
/// </summary>
public class PackingOptimizerTests
{
    private static readonly IReadOnlyList<CartonType> Standard = StandardCartons.All;
    private static readonly CartonType S = Standard[0];

    /// <summary>Grobe Obergrenze für Laufzeit-Schutzprüfungen: bewusst weit über der echten Dauer (Millisekunden), siehe WP33.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    private static Article Art(string sku, int l, int w, int h, int weightGrams = 100, StackingInfo? stacking = null) =>
        new(sku, sku, new Dimensions(l, w, h), weightGrams, stacking ?? StackingInfo.NotStackable);

    private static PackingPlan Plan(IReadOnlyList<CartonType> types, params (Article Article, int Quantity)[] items) =>
        new FirstFitDecreasingPackingOptimizer().Plan(Guid.NewGuid(), "TEST-1", items.Select(i => new PackInput(i.Article, i.Quantity)), types);

    private static int Units(PackedCarton c) => c.Items.Sum(i => i.Quantity);

    [Fact]
    public void Single_item_goes_into_the_smallest_carton_that_holds_it()
    {
        var article = Art("SINGLE", 100, 50, 20, weightGrams: 200);

        var plan = Plan(Standard, (article, 1));

        var carton = Assert.Single(plan.Cartons);
        Assert.Equal("S", carton.CartonType.Name);
        Assert.Equal(300, carton.TotalWeightGrams); // 200 g + 100 g Tara
        Assert.Equal(100_000, carton.UsedVolumeMm3);
        Assert.Equal(100_000.0 / 3_000_000, carton.FillRatio, 6);
        Assert.Equal(new[] { new PackedItem(article.Id, "SINGLE", 1) }, carton.Items);
        Assert.Empty(plan.Unpacked);
    }

    [Fact]
    public void Item_longer_than_every_carton_is_unpacked()
    {
        var article = Art("LONG", 1000, 100, 100);

        var plan = Plan(Standard, (article, 2));

        Assert.Empty(plan.Cartons);
        Assert.Equal(new[] { new PackedItem(article.Id, "LONG", 2) }, plan.Unpacked);
    }

    [Fact]
    public void Item_that_fits_by_volume_but_not_by_geometry_is_not_packed_into_that_carton()
    {
        // Zwei Stück 120x120x100 haben 2,88 Mio mm³ (< 3 Mio des Kartons S), die Grundfläche 2x120x120 passt aber nicht in 200x150.
        var article = Art("BLOCK", 120, 120, 100);

        var onlySmall = Plan(new[] { S }, (article, 2));
        Assert.Equal(new[] { 1, 1 }, onlySmall.Cartons.Select(Units));
        Assert.Empty(onlySmall.Unpacked);

        var standard = Plan(Standard, (article, 2));
        var carton = Assert.Single(standard.Cartons);
        Assert.Equal("M", carton.CartonType.Name); // beide zusammen brauchen den nächsten Karton, nicht S
        Assert.Equal(2, Units(carton));
    }

    [Fact]
    public void Retries_with_the_next_larger_carton_and_consolidates_instead_of_using_three_small_ones()
    {
        // Drei Stück 200x150x100 füllen je einen Karton S; zusammen passen sie in einen Karton M.
        var plan = Plan(Standard, (Art("FLAT", 200, 150, 100), 3));

        var carton = Assert.Single(plan.Cartons);
        Assert.Equal("M", carton.CartonType.Name);
        Assert.Equal(3, Units(carton));
    }

    [Fact]
    public void Item_needs_a_rotation_to_fit_and_gets_it()
    {
        // 150x190x90: nur gedreht (190 entlang der langen Kartonkante) passt der Artikel in S (200x150x100).
        var plan = Plan(Standard, (Art("TURN", 150, 190, 90), 1));

        Assert.Equal("S", Assert.Single(plan.Cartons).CartonType.Name);
    }

    [Fact]
    public void Stackable_article_keeps_its_stacking_axis_upright_while_a_plain_one_may_lie_down()
    {
        // 100x100x150: liegend (150x100x100) passt er in S, stehend (150 hoch) nicht.
        var lyingAllowed = Plan(Standard, (Art("PLAIN", 100, 100, 150), 1));
        Assert.Equal("S", Assert.Single(lyingAllowed.Cartons).CartonType.Name);

        var stackable = new StackingInfo(true, StackingAxis.Z, 100, 2);
        var mustStand = Plan(Standard, (Art("STACK", 100, 100, 150, stacking: stackable), 1));
        Assert.Equal("M", Assert.Single(mustStand.Cartons).CartonType.Name);
    }

    [Fact]
    public void Tare_counts_towards_the_maximum_weight()
    {
        // Karton bis 1000 g brutto, Tara 100 g: nur 4 Stück à 200 g (900 g Nutzlast) passen, das fünfte würde 1100 g wiegen.
        var box = new CartonType("T", 100, 100, 100, MaxWeightGrams: 1000, TareWeightGrams: 100);

        var plan = Plan(new[] { box }, (Art("HEAVY", 50, 50, 50, weightGrams: 200), 5));

        Assert.Equal(new[] { 4, 1 }, plan.Cartons.Select(Units));
        Assert.Equal(new[] { 900, 300 }, plan.Cartons.Select(c => c.TotalWeightGrams));
        Assert.All(plan.Cartons, c => Assert.True(c.TotalWeightGrams <= box.MaxWeightGrams));
        Assert.Empty(plan.Unpacked);
    }

    [Theory]
    [InlineData(4_950, "M")]   // S: 4.900 g Nutzlast reichen nicht, M passt (früher: unverpackt)
    [InlineData(14_900, "L")]  // M: 14.800 g Nutzlast reichen nicht, L passt
    [InlineData(4_900, "S")]   // exakt die Nutzlast von S
    public void Item_too_heavy_for_the_smallest_carton_moves_to_the_next_one(int weightGrams, string expectedCarton)
    {
        var plan = Plan(Standard, (Art("WEIGHT", 100, 100, 50, weightGrams), 1));

        Assert.Equal(expectedCarton, Assert.Single(plan.Cartons).CartonType.Name);
        Assert.Empty(plan.Unpacked);
    }

    [Fact]
    public void Item_heavier_than_every_carton_payload_is_unpacked()
    {
        var plan = Plan(Standard, (Art("TOOHEAVY", 100, 100, 50, weightGrams: 59_400), 1)); // XL: 60.000 - 700 = 59.300 g

        Assert.Empty(plan.Cartons);
        Assert.Equal(1, Assert.Single(plan.Unpacked).Quantity);
    }

    [Fact]
    public void MaxStackCount_limits_the_units_per_stack()
    {
        // Karton 10x10x12: ein Stapel aus n Stück braucht 10 + (n-1)*1 mm. Ohne Grenze passen 3 Stück, mit MaxStackCount 2 nur 2.
        var box = new CartonType("TALL", 10, 10, 12, MaxWeightGrams: 100_000, TareWeightGrams: 0);

        var limited = Plan(new[] { box }, (Art("NEST2", 10, 10, 10, 1, new StackingInfo(true, StackingAxis.Z, 1, 2)), 3));
        Assert.Equal(new[] { 2, 1 }, limited.Cartons.Select(Units));

        var unlimited = Plan(new[] { box }, (Art("NESTX", 10, 10, 10, 1, new StackingInfo(true, StackingAxis.Z, 1, null)), 3));
        Assert.Equal(new[] { 3 }, unlimited.Cartons.Select(Units));

        var notStackable = Plan(new[] { box }, (Art("PLAIN", 10, 10, 10, 1), 3));
        Assert.Equal(new[] { 1, 1, 1 }, notStackable.Cartons.Select(Units));
    }

    [Fact]
    public void Stacking_increment_zero_gives_no_space_advantage()
    {
        // Demo-Daten (Schrauben, Muttern) sind "stapelbar" mit Inkrement 0. Früher kostete jede weitere Einheit 0 mm³:
        // 2000 Kugeln 60x60x60 passten in einen einzigen Karton S. Jetzt braucht jede Einheit ihren vollen Platz.
        var balls = Art("BALL", 60, 60, 60, weightGrams: 1, stacking: new StackingInfo(true, StackingAxis.Z, 0, 5000));

        var plan = Plan(Standard, (balls, 2000));

        Assert.Empty(plan.Unpacked);
        Assert.Equal(2000, plan.Cartons.Sum(Units));
        Assert.True(plan.Cartons.Sum(c => c.UsedVolumeMm3) >= 2000L * 216_000);
        Assert.True(plan.Cartons.Count >= 3, $"nur {plan.Cartons.Count} Kartons für 432 Mio mm³");
        Assert.All(plan.Cartons, c => Assert.True(c.FillRatio <= 1.0));
    }

    [Fact]
    public void Smaller_articles_fill_the_gaps_left_by_larger_ones()
    {
        // FFD: 190x140x90 zuerst, die zehn 10-mm-Würfel passen in die Restquader desselben Kartons S.
        var big = Art("BIG", 190, 140, 90, weightGrams: 500);
        var cube = Art("CUBE", 10, 10, 10, weightGrams: 1);

        var plan = Plan(Standard, (cube, 10), (big, 1));

        var carton = Assert.Single(plan.Cartons);
        Assert.Equal("S", carton.CartonType.Name);
        Assert.Equal(new[] { "BIG", "CUBE" }, carton.Items.Select(i => i.ArticleSku));
        Assert.Equal(11, Units(carton));
    }

    [Fact]
    public void Lines_of_the_same_article_are_aggregated_and_zero_quantities_are_ignored()
    {
        var article = Art("SAME", 100, 100, 100);
        var other = Art("NONE", 100, 100, 100);

        var plan = new FirstFitDecreasingPackingOptimizer().Plan(Guid.NewGuid(), "T",
            new[] { new PackInput(article, 1), new PackInput(other, 0), new PackInput(article, 1) }, Standard);

        var carton = Assert.Single(plan.Cartons);
        Assert.Equal(new[] { new PackedItem(article.Id, "SAME", 2) }, carton.Items);
    }

    [Fact]
    public void Negative_quantity_and_missing_carton_types_are_rejected()
    {
        var article = Art("ANY", 10, 10, 10);
        var optimizer = new FirstFitDecreasingPackingOptimizer();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            optimizer.Plan(Guid.NewGuid(), "T", new[] { new PackInput(article, -1) }, Standard));
        Assert.Throws<ArgumentException>(() =>
            optimizer.Plan(Guid.NewGuid(), "T", new[] { new PackInput(article, 1) }, Array.Empty<CartonType>()));
        Assert.Empty(optimizer.Plan(Guid.NewGuid(), "T", Array.Empty<PackInput>(), Standard).Cartons);
    }

    [Fact]
    public void Article_without_dimensions_is_reported_and_left_unpacked_instead_of_looping()
    {
        var article = new Article("NODIM", "ohne Maße", Dimensions.Zero, 100, StackingInfo.NotStackable);

        var plan = Plan(Standard, (article, 3));

        Assert.Empty(plan.Cartons);
        Assert.Equal(3, Assert.Single(plan.Unpacked).Quantity);
        Assert.Contains(plan.Warnings, w => w.Contains("NODIM"));
    }

    [Fact]
    public void Huge_quantities_are_planned_without_expanding_single_units()
    {
        var dust = Art("DUST", 10, 10, 10, weightGrams: 1);

        var watch = Stopwatch.StartNew();
        var plan = Plan(Standard, (dust, 1_000_000));
        watch.Stop();

        // Die Zeit ist hier nur ein Schutz gegen den Rückfall in eine Schleife über Einzeleinheiten (die Laufzeit wüchse mit der
        // Menge; die aggregierte Planung braucht Millisekunden). Die Grenze ist deshalb großzügig (30 s statt früher 2 s), damit
        // ein langsamer oder ausgelasteter Rechner (CI, parallele Testläufe) den Test nicht kippt; geprüft wird die Korrektheit darunter.
        Assert.True(watch.Elapsed < HangGuard, $"Planung dauerte {watch.Elapsed.TotalSeconds:0.0} s (Grenze {HangGuard.TotalSeconds:0} s)");
        Assert.Equal(1_000_000, plan.Cartons.Sum(Units) + plan.Unpacked.Sum(i => i.Quantity));
        Assert.All(plan.Cartons, c => Assert.True(c.TotalWeightGrams <= c.CartonType.MaxWeightGrams));
    }

    [Fact]
    public void More_than_the_carton_limit_is_reported_and_stops_the_plan()
    {
        var box = new CartonType("ONE", 10, 10, 10, 1_000, 0);

        var plan = Plan(new[] { box }, (Art("MANY", 10, 10, 10, 1), FirstFitDecreasingPackingOptimizer.MaxCartons + 5));

        Assert.Equal(FirstFitDecreasingPackingOptimizer.MaxCartons, plan.Cartons.Count);
        Assert.Equal(5, Assert.Single(plan.Unpacked).Quantity);
        Assert.Single(plan.Warnings);
    }

    [Fact]
    public void Random_orders_keep_all_invariants_and_do_not_depend_on_input_order()
    {
        for (var seed = 1; seed <= 12; seed++)
        {
            var rnd = new Random(seed);
            var items = Enumerable.Range(0, 25).Select(i =>
            {
                var stackable = rnd.Next(3) == 0;
                var stacking = stackable
                    ? new StackingInfo(true, (StackingAxis)rnd.Next(3), rnd.Next(0, 40), rnd.Next(3) == 0 ? null : rnd.Next(1, 6))
                    : StackingInfo.NotStackable;
                return (Article: Art($"R{seed}-{i:D2}", rnd.Next(10, 350), rnd.Next(10, 350), rnd.Next(5, 250), rnd.Next(1, 3500), stacking),
                        Quantity: rnd.Next(1, 40));
            }).ToArray();

            var plan = Plan(Standard, items);

            // Alles ist gezählt: gepackt + unverpackt = bestellt, je Artikel.
            foreach (var (article, quantity) in items)
            {
                var packed = plan.Cartons.SelectMany(c => c.Items).Where(i => i.ArticleId == article.Id).Sum(i => i.Quantity);
                var left = plan.Unpacked.Where(i => i.ArticleId == article.Id).Sum(i => i.Quantity);
                Assert.Equal(quantity, packed + left);
            }

            var weights = items.ToDictionary(i => i.Article.Id, i => i.Article.WeightGrams);
            foreach (var carton in plan.Cartons)
            {
                var content = carton.Items.Sum(i => (long)i.Quantity * weights[i.ArticleId]);
                Assert.Equal(carton.CartonType.TareWeightGrams + content, carton.TotalWeightGrams);
                Assert.True(carton.TotalWeightGrams <= carton.CartonType.MaxWeightGrams, $"Seed {seed}: Karton zu schwer");
                Assert.True(carton.UsedVolumeMm3 <= carton.CartonType.InnerVolumeMm3, $"Seed {seed}: Karton überfüllt");
            }

            // Determinismus: umgekehrte Eingabereihenfolge ergibt denselben Plan.
            var reversed = Plan(Standard, items.Reverse().ToArray());
            Assert.Equal(Describe(plan), Describe(reversed));
        }
    }

    private static string Describe(PackingPlan plan) =>
        string.Join("|", plan.Cartons.Select(c =>
            $"{c.CartonType.Name}:{c.TotalWeightGrams}:{c.UsedVolumeMm3}:" + string.Join(",", c.Items.Select(i => $"{i.ArticleSku}x{i.Quantity}"))))
        + "#" + string.Join(",", plan.Unpacked.Select(i => $"{i.ArticleSku}x{i.Quantity}"));
}
