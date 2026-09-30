using Lager.Contracts.PickLists;
using Lager.Domain.Orders;
using Lager.Domain.PickLists;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP07;

/// <summary>
/// Wagen-Pickliste (generate-cart): gemeinsame Bestandsreservierung, Kapazität nach Volumen UND Gewicht,
/// Bundles, ein einziger Commit. Jeder Test hat eine eigene Factory, weil generate-cart ALLE neuen
/// Bestellungen der Datenbank betrachtet.
/// </summary>
public class PickCartTests
{
    private static Task<PickListDto> CartAsync(PickWorld w, Guid cart, bool optimizeForBinReuse = false) =>
        w.PickListsAsync(s => s.GenerateCartAsync(new GenerateCartPickListRequest(cart, OptimizeForBinReuse: optimizeForBinReuse)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cart_reserves_stock_across_orders_and_skips_what_no_longer_fits(bool optimizeForBinReuse)
    {
        using var factory = new LagerApiFactory();
        var w = new PickWorld(factory);
        var site = await w.AddWarehouseAsync();
        var bin = await w.AddBinAsync(site, PickWorld.Unique("BIN"));
        var article = await w.AddArticleAsync();
        await w.AddStockAsync(article, bin, 10);
        var o1 = await w.AddOrderAsync((article, 8));
        var o2 = await w.AddOrderAsync((article, 8));    // passt nach o1 nicht mehr in den Bestand
        var o3 = await w.AddOrderAsync((article, 2));    // passt genau in den Rest
        var cart = await w.AddCartAsync();

        var dto = await CartAsync(w, cart, optimizeForBinReuse);

        Assert.Equal(cart, dto.PickCartConfigId);
        Assert.NotNull(dto.PickCartConfigName);
        Assert.Equal(10, dto.Items.Sum(i => i.Quantity));            // nie mehr als der Bestand
        Assert.Equal(OrderStatus.Picking, await w.OrderStatusAsync(o1));
        Assert.Equal(OrderStatus.New, await w.OrderStatusAsync(o2));
        Assert.Equal(OrderStatus.Picking, await w.OrderStatusAsync(o3));
        Assert.Equal(1, await w.PickListCountAsync());

        // Pickliste, Bestellstatus UND Wagen-Zuordnung sind gemeinsam gespeichert: so liest es die Datenbank
        var reread = await w.PickListsAsync(s => s.GetAsync(dto.Id));
        Assert.Equal(cart, reread!.PickCartConfigId);
    }

    [Fact]
    public async Task Cart_checks_weight_as_well_as_volume()
    {
        using var factory = new LagerApiFactory();
        var w = new PickWorld(factory);
        var site = await w.AddWarehouseAsync();
        var bin = await w.AddBinAsync(site, PickWorld.Unique("BIN"));
        var heavy = await w.AddArticleAsync(weightGrams: 400);       // Volumen ist winzig gegen das des Wagens
        await w.AddStockAsync(heavy, bin, 100);
        var o1 = await w.AddOrderAsync((heavy, 2));                  // 800 g
        var o2 = await w.AddOrderAsync((heavy, 1));                  // 400 g -> 1200 g > 1000 g
        var cart = await w.AddCartAsync(maxWeightGrams: 1_000);

        var dto = await CartAsync(w, cart);

        Assert.Equal(2, dto.Items.Sum(i => i.Quantity));
        Assert.Equal(OrderStatus.Picking, await w.OrderStatusAsync(o1));
        Assert.Equal(OrderStatus.New, await w.OrderStatusAsync(o2));
    }

    [Fact]
    public async Task Cart_checks_volume_as_well_as_weight()
    {
        using var factory = new LagerApiFactory();
        var w = new PickWorld(factory);
        var site = await w.AddWarehouseAsync();
        var bin = await w.AddBinAsync(site, PickWorld.Unique("BIN"));
        var bulky = await w.AddArticleAsync(weightGrams: 1, dimensions: new Lager.Domain.Articles.Dimensions(100, 100, 100));   // 1 dm³
        await w.AddStockAsync(bulky, bin, 1_000);
        var o1 = await w.AddOrderAsync((bulky, 60));                 // 60 dm³
        var o2 = await w.AddOrderAsync((bulky, 60));                 // 120 dm³ > 100 dm³
        var cart = await w.AddCartAsync(maxWeightGrams: 1_000_000, levels: 1, levelHeightMm: 100);   // 1 x 1000 x 1000 x 100 mm = 100 dm³

        var dto = await CartAsync(w, cart);

        Assert.Equal(60, dto.Items.Sum(i => i.Quantity));
        Assert.Equal(OrderStatus.Picking, await w.OrderStatusAsync(o1));
        Assert.Equal(OrderStatus.New, await w.OrderStatusAsync(o2));
    }

    [Fact]
    public async Task Cart_accepts_orders_with_bundle_lines_by_their_components()
    {
        using var factory = new LagerApiFactory();
        var w = new PickWorld(factory);
        var site = await w.AddWarehouseAsync();
        var bin = await w.AddBinAsync(site, PickWorld.Unique("BIN"));
        var component = await w.AddArticleAsync();
        await w.AddStockAsync(component, bin, 10);
        var bundle = await w.AddBundleAsync((component, 2));          // das Bundle selbst hat keinen Bestand
        var order = await w.AddOrderAsync((bundle, 3));               // = 6 Stück der Komponente
        var cart = await w.AddCartAsync();

        var dto = await CartAsync(w, cart);

        var item = Assert.Single(dto.Items);
        Assert.Equal(component, item.ArticleId);
        Assert.Equal(6, item.Quantity);
        Assert.Equal(OrderStatus.Picking, await w.OrderStatusAsync(order));
    }

    [Fact]
    public async Task Cart_counts_duplicate_lines_of_one_order_together()
    {
        using var factory = new LagerApiFactory();
        var w = new PickWorld(factory);
        var site = await w.AddWarehouseAsync();
        var bin = await w.AddBinAsync(site, PickWorld.Unique("BIN"));
        var article = await w.AddArticleAsync();
        await w.AddStockAsync(article, bin, 10);
        var order = await w.AddOrderAsync((article, 8), (article, 8));   // jede Zeile passt einzeln, zusammen nicht
        var cart = await w.AddCartAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => CartAsync(w, cart));

        Assert.Contains("Übersprungen", ex.Message);
        Assert.Contains("1 wegen Bestand", ex.Message);
        Assert.Equal(OrderStatus.New, await w.OrderStatusAsync(order));
        Assert.Equal(0, await w.PickListCountAsync());
    }

    [Fact]
    public async Task List_maps_articles_locations_and_carts_for_all_lists()
    {
        using var factory = new LagerApiFactory();
        var w = new PickWorld(factory);
        var site = await w.AddWarehouseAsync();
        var binA = await w.AddBinAsync(site, PickWorld.Unique("BIN"), x: 1_000);
        var binB = await w.AddBinAsync(site, PickWorld.Unique("BIN"), x: 3_000);
        var skuA = PickWorld.Unique("SKU");
        var skuB = PickWorld.Unique("SKU");
        var articleA = await w.AddArticleAsync(skuA);
        var articleB = await w.AddArticleAsync(skuB);
        await w.AddStockAsync(articleA, binA, 50);
        await w.AddStockAsync(articleB, binB, 50);
        var plain = await w.AddOrderAsync((articleA, 1));
        var plainList = await w.GenerateAsync(plain);
        var cartOrder = await w.AddOrderAsync((articleB, 2));
        var cart = await w.AddCartAsync();
        var cartList = await CartAsync(w, cart);

        var lists = await w.PickListsAsync(s => s.ListAsync());

        Assert.Equal(2, lists.Count);
        var plainDto = lists.Single(l => l.Id == plainList.Id);
        var item = Assert.Single(plainDto.Items);
        Assert.Equal(skuA, item.ArticleSku);
        Assert.Equal(binA.Code, item.StorageLocationCode);
        Assert.Null(plainDto.PickCartConfigName);

        var cartDto = lists.Single(l => l.Id == cartList.Id);
        Assert.Equal(skuB, Assert.Single(cartDto.Items).ArticleSku);
        Assert.Equal(cartList.PickCartConfigName, cartDto.PickCartConfigName);
        Assert.NotNull(cartDto.PickCartConfigName);
        Assert.Equal(OrderStatus.Picking, await w.OrderStatusAsync(cartOrder));
    }
}
