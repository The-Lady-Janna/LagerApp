using System.Net.Http.Json;
using System.Text.Json;
using Lager.Domain.Articles;
using Lager.Domain.Orders;
using Lager.Domain.PickLists;
using Lager.Domain.Stock;
using Lager.Domain.Warehouse;
using Lager.Infrastructure.Persistence;
using Lager.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEntity = Lager.Domain.Warehouse.Warehouse;

namespace Lager.Tests.WP10;

/// <summary>
/// Ende-zu-Ende über die HTTP-API (echte DI, echte Fremdschlüssel): Reports und Vorschlagsdienste liefern die
/// korrigierten Werte im richtigen JSON.
/// </summary>
public class AnalyticsApiTests
{
    private sealed record Seeded(Guid ArticleId, string BinCode);

    /// <summary>Ein Artikel, ein Bin, eine fertig gepackte Pickliste (soll 10, gepackt 3), eine offene Pickliste (soll 100).</summary>
    private static async Task<Seeded> SeedAsync(LagerApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LagerDbContext>();

        var warehouse = new WarehouseEntity("W1", "Lager");
        var zone = new Zone(warehouse.Id, "Z1", "Zone", Position.Origin);
        var aisle = new Aisle(zone.Id, "A1", Position.Origin, new Position(5000, 0, 0), AisleOrientation.AlongX);
        var shelf = new Shelf(aisle.Id, "S1", new Position(1000, 0, 0), 5000, 600, 2000);
        var bin = shelf.AddLocation("BIN-API", 600, 600, 500, 0);
        var article = new Article("SKU-API", "Api-Artikel", new Dimensions(20, 6, 6), 10, StackingInfo.NotStackable);
        article.SetPurchasing(null, 250);
        var order = new Order("O-API", OrderSource.Manual, null, new[] { new OrderLine(article.Id, 10) });
        db.AddRange(warehouse, zone, aisle, shelf, article, order);
        await db.SaveChangesAsync();

        var line = order.Lines.Single();
        var done = new PickList("PL-API-1",
            new[] { new PickItem(1, order.Id, line.Id, article.Id, bin.Id, 10) }, 1500);
        done.Items.Single().ConfirmPacked(3);
        done.MarkPacked();
        var open = new PickList("PL-API-2",
            new[] { new PickItem(1, order.Id, line.Id, article.Id, bin.Id, 100) }, 1500);
        db.AddRange(done, open, new StockItem(article.Id, bin.Id, 20));
        await db.SaveChangesAsync();

        return new Seeded(article.Id, bin.Code);
    }

    [Fact]
    public async Task Abc_analysis_endpoint_counts_only_packed_quantities_and_classifies_a_single_article_as_A()
    {
        using var factory = new LagerApiFactory();
        var client = await factory.CreateClient().AsReadyAdminAsync();
        await SeedAsync(factory);

        var rows = await client.GetFromJsonAsync<JsonElement>("/api/reports/abc-analysis?range=30");

        var row = Assert.Single(rows.EnumerateArray().ToList());
        Assert.Equal("SKU-API", row.GetProperty("sku").GetString());
        Assert.Equal("A", row.GetProperty("class").GetString());
        Assert.Equal(3, row.GetProperty("totalQuantity").GetInt32()); // nicht 10 + 100
        Assert.Equal(100m, row.GetProperty("sharePercent").GetDecimal());

        var heat = await client.GetFromJsonAsync<JsonElement>("/api/reports/bin-heatmap?range=30");
        var hot = Assert.Single(heat.EnumerateArray().ToList());
        Assert.Equal("BIN-API", hot.GetProperty("binCode").GetString());
        Assert.Equal(1, hot.GetProperty("pickCount").GetInt32());
    }

    [Fact]
    public async Task Valuation_and_dead_stock_endpoints_expose_fallback_and_location_count()
    {
        using var factory = new LagerApiFactory();
        var client = await factory.CreateClient().AsReadyAdminAsync();
        await SeedAsync(factory);

        var valuation = await client.GetFromJsonAsync<JsonElement>("/api/reports/stock-valuation");
        Assert.Equal("EUR", valuation.GetProperty("currency").GetString());
        Assert.Equal(20 * 250, valuation.GetProperty("totalValueCents").GetInt64()); // Bestand ohne Ledger: Stammpreis
        Assert.Equal(20 * 250, valuation.GetProperty("fallbackValueCents").GetInt64());
        Assert.Contains("FIFO", valuation.GetProperty("basis").GetString());
        Assert.Equal(20, valuation.GetProperty("lines")[0].GetProperty("fallbackQuantity").GetInt32());

        var dead = await client.GetFromJsonAsync<JsonElement>("/api/reports/dead-stock?days=1");
        var row = Assert.Single(dead.EnumerateArray().ToList()); // nie bewegt (kein Ledger-Eintrag)
        Assert.Equal(1, row.GetProperty("locationCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("daysSinceLastMovement").ValueKind);
    }

    [Fact]
    public async Task Putaway_endpoint_ignores_non_positive_quantities_and_only_lists_fitting_bins()
    {
        using var factory = new LagerApiFactory();
        var client = await factory.CreateClient().AsReadyAdminAsync();
        var seeded = await SeedAsync(factory);

        var none = await client.GetFromJsonAsync<JsonElement>($"/api/slotting/putaway?articleId={seeded.ArticleId}&quantity=0");
        Assert.Empty(none.EnumerateArray().ToList());

        var ranking = await client.GetFromJsonAsync<JsonElement>($"/api/slotting/putaway?articleId={seeded.ArticleId}&quantity=5");
        var first = Assert.Single(ranking.EnumerateArray().ToList());
        Assert.Equal(seeded.BinCode, first.GetProperty("binCode").GetString());
        Assert.StartsWith("Konsolidieren", first.GetProperty("reason").GetString());

        var tooMany = await client.GetFromJsonAsync<JsonElement>($"/api/slotting/putaway?articleId={seeded.ArticleId}&quantity=100000000");
        Assert.Empty(tooMany.EnumerateArray().ToList()); // 100 Mio. Stück passen in keinen Bin (früher: Bin mit Score -1)
    }

    [Fact]
    public async Task Slotting_endpoint_answers_with_an_empty_list_when_nothing_is_worth_moving()
    {
        using var factory = new LagerApiFactory();
        var client = await factory.CreateClient().AsReadyAdminAsync();
        await SeedAsync(factory);

        var rows = await client.GetFromJsonAsync<JsonElement>("/api/slotting/suggestions?rangeDays=30&top=5");

        Assert.Empty(rows.EnumerateArray().ToList()); // ein einziger Bin: kein näherer Bin, kein Tausch
    }
}
