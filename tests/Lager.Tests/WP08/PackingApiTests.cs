using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Articles;
using Lager.Contracts.Orders;
using Lager.Contracts.Packing;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP08;

/// <summary>
/// Endpunkt-Tests für <c>POST /api/packing/{orderId}/plan</c> gegen die echte API (eigene SQLite-DB je Factory).
/// </summary>
public class PackingApiTests
{
    private static CreateArticleRequest NewArticle(string sku, int l, int w, int h, int weightGrams,
        IReadOnlyList<CreateBundleComponentRequest>? bundle = null) => new(
        Sku: sku,
        Name: $"WP08 {sku}",
        Description: null,
        Dimensions: new DimensionsDto(l, w, h),
        WeightGrams: weightGrams,
        Stacking: new StackingInfoDto(false, "Z", 0, null),
        BundleComponents: bundle);

    private static async Task<ArticleDto> CreateArticleAsync(HttpClient admin, CreateArticleRequest request)
    {
        var response = await admin.PostAsJsonAsync("/api/articles", request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ArticleDto>())!;
    }

    [Fact]
    public async Task Unknown_order_returns_404_not_500()
    {
        using var factory = new LagerApiFactory();
        var admin = await factory.CreateClient().AsReadyAdminAsync();

        var response = await admin.PostAsync($"/api/packing/{Guid.NewGuid()}/plan", null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Order_with_a_bundle_and_an_oversized_article_is_planned_by_components_with_unpacked_items()
    {
        using var factory = new LagerApiFactory();
        var admin = await factory.CreateClient().AsReadyAdminAsync();

        var screw = await CreateArticleAsync(admin, NewArticle("WP08-SCREW", 100, 50, 20, 200));
        var plate = await CreateArticleAsync(admin, NewArticle("WP08-PLATE", 150, 100, 30, 400));
        var kit = await CreateArticleAsync(admin, NewArticle("WP08-KIT", 800, 700, 500, 90_000,
            new[] { new CreateBundleComponentRequest(screw.Id, 2), new CreateBundleComponentRequest(plate.Id, 1) }));
        var beam = await CreateArticleAsync(admin, NewArticle("WP08-BEAM", 3000, 100, 100, 5_000)); // länger als jeder Karton

        var created = await admin.PostAsJsonAsync("/api/orders/manual", new CreateOrderRequest("WP08-ORDER", null, new[]
        {
            new CreateOrderLineRequest(kit.Id, 2),
            new CreateOrderLineRequest(beam.Id, 1),
        }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var order = (await created.Content.ReadFromJsonAsync<OrderDto>())!;

        var response = await admin.PostAsync($"/api/packing/{order.Id}/plan", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var plan = (await response.Content.ReadFromJsonAsync<PackingPlanDto>())!;
        var packed = plan.Cartons.SelectMany(c => c.Allocations).ToList();
        Assert.DoesNotContain(packed, a => a.ArticleSku == "WP08-KIT");
        Assert.Equal(4, packed.Where(a => a.ArticleSku == "WP08-SCREW").Sum(a => a.Quantity)); // 2 Kits x 2
        Assert.Equal(2, packed.Where(a => a.ArticleSku == "WP08-PLATE").Sum(a => a.Quantity)); // 2 Kits x 1
        var unpacked = Assert.Single(plan.Unpacked);
        Assert.Equal("WP08-BEAM", unpacked.ArticleSku);
        Assert.All(plan.Cartons, c => Assert.InRange(c.FillRatio, 0.0, 1.0));
    }
}
