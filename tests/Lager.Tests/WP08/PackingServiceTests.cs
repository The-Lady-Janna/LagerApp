using Lager.Application.Abstractions;
using Lager.Application.Packing;
using Lager.Domain.Articles;
using Lager.Domain.Orders;

namespace Lager.Tests.WP08;

/// <summary>
/// Unit-Tests (ohne Host, mit einfachen Fakes) für <see cref="PackingService"/>: Bundle-Auflösung in Komponenten,
/// fehlende Artikel als Hinweis statt Absturz, unbekannte Bestellung als null.
/// </summary>
public class PackingServiceTests
{
    private static Article Art(string sku, int l = 100, int w = 50, int h = 20, int weightGrams = 100) =>
        new(sku, sku, new Dimensions(l, w, h), weightGrams, StackingInfo.NotStackable);

    private static Order OrderOf(params (Guid ArticleId, int Quantity)[] lines) =>
        new("PS-1", OrderSource.Manual, null, lines.Select(l => new OrderLine(l.ArticleId, l.Quantity)));

    private static PackingService ServiceFor(Order? order, params Article[] articles) =>
        new(new FakeOrders(order), new FakeArticles(articles), new FirstFitDecreasingPackingOptimizer());

    [Fact]
    public async Task Unknown_order_gives_null_instead_of_an_exception()
    {
        var service = ServiceFor(null);

        Assert.Null(await service.PlanForOrderAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Bundle_lines_are_packed_as_their_components()
    {
        var screw = Art("SCREW");
        var nut = Art("NUT");
        var bundle = Art("KIT", 500, 500, 500, weightGrams: 9_999); // eigene Maße/Gewicht dürfen keine Rolle spielen
        bundle.ReplaceBundleComponents(new[] { (screw.Id, 2), (nut.Id, 1) });
        var order = OrderOf((bundle.Id, 3), (nut.Id, 1));

        var dto = await ServiceFor(order, screw, nut, bundle).PlanForOrderAsync(order.Id);

        Assert.NotNull(dto);
        var allocations = dto!.Cartons.SelectMany(c => c.Allocations).Concat(dto.Unpacked).ToList();
        Assert.DoesNotContain(allocations, a => a.ArticleSku == "KIT");
        Assert.Equal(6, allocations.Where(a => a.ArticleSku == "SCREW").Sum(a => a.Quantity)); // 3 x 2
        Assert.Equal(4, allocations.Where(a => a.ArticleSku == "NUT").Sum(a => a.Quantity));   // 3 x 1 + 1 einzeln
        Assert.Empty(dto.Warnings ?? Array.Empty<string>());
    }

    [Fact]
    public async Task Nested_bundles_are_resolved_down_to_the_components()
    {
        var part = Art("PART");
        var inner = Art("INNER");
        inner.ReplaceBundleComponents(new[] { (part.Id, 2) });
        var outer = Art("OUTER");
        outer.ReplaceBundleComponents(new[] { (inner.Id, 3) });
        var order = OrderOf((outer.Id, 2));

        var dto = await ServiceFor(order, part, inner, outer).PlanForOrderAsync(order.Id);

        var allocations = dto!.Cartons.SelectMany(c => c.Allocations).ToList();
        Assert.Equal(new[] { "PART" }, allocations.Select(a => a.ArticleSku).Distinct());
        Assert.Equal(12, allocations.Sum(a => a.Quantity)); // 2 x 3 x 2
    }

    [Fact]
    public async Task Bundle_cycle_does_not_loop_forever_and_is_reported()
    {
        var a = Art("CYCLE-A");
        var b = Art("CYCLE-B");
        a.ReplaceBundleComponents(new[] { (b.Id, 1) });
        b.ReplaceBundleComponents(new[] { (a.Id, 1) });
        var order = OrderOf((a.Id, 1));

        var dto = await ServiceFor(order, a, b).PlanForOrderAsync(order.Id);

        Assert.NotNull(dto);
        Assert.Contains(dto!.Warnings!, w => w.Contains("verschachtelt"));
    }

    [Fact]
    public async Task Missing_article_is_reported_and_the_rest_of_the_order_is_still_planned()
    {
        var known = Art("KNOWN");
        var vanished = Guid.NewGuid();
        var order = OrderOf((known.Id, 2), (vanished, 1));

        var dto = await ServiceFor(order, known).PlanForOrderAsync(order.Id);

        Assert.NotNull(dto);
        Assert.Equal(2, dto!.Cartons.SelectMany(c => c.Allocations).Sum(a => a.Quantity));
        Assert.Contains(dto.Warnings!, w => w.Contains(vanished.ToString()));
    }

    [Fact]
    public async Task Huge_bundle_quantity_does_not_overflow_and_is_reported()
    {
        // 3 Komponenten je Bundle x int.MaxValue Bundles passt nicht in ein int: früher OverflowException (HTTP 500).
        var part = Art("PART", 10, 10, 10, weightGrams: 1);
        var bundle = Art("MEGA");
        bundle.ReplaceBundleComponents(new[] { (part.Id, 3) });
        var order = OrderOf((bundle.Id, int.MaxValue));

        var dto = await ServiceFor(order, part, bundle).PlanForOrderAsync(order.Id);

        Assert.NotNull(dto);
        Assert.Contains(dto!.Warnings!, w => w.Contains("begrenzt"));
        Assert.NotEmpty(dto.Cartons);
    }

    private sealed class FakeOrders : IOrderRepository
    {
        private readonly Order? _order;
        public FakeOrders(Order? order) => _order = order;

        public Task<Order?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_order is not null && _order.Id == id ? _order : null);
        public Task<IReadOnlyList<Order>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddAsync(Order entity, CancellationToken ct = default) => throw new NotSupportedException();
        public void Remove(Order entity) => throw new NotSupportedException();
        public Task<Order?> GetByNumberAsync(string orderNumber, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Order>> GetManyAsync(IEnumerable<Guid> ids, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeArticles : IArticleRepository
    {
        private readonly Dictionary<Guid, Article> _articles;
        public FakeArticles(IEnumerable<Article> articles) => _articles = articles.ToDictionary(a => a.Id);

        public Task<IReadOnlyDictionary<Guid, Article>> GetManyAsync(IEnumerable<Guid> ids, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, Article>>(ids.Where(_articles.ContainsKey).Distinct().ToDictionary(id => id, id => _articles[id]));

        public Task<Article?> GetAsync(Guid id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Article>> ListAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task AddAsync(Article entity, CancellationToken ct = default) => throw new NotSupportedException();
        public void Remove(Article entity) => throw new NotSupportedException();
        public Task<Article?> GetBySkuAsync(string sku, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
