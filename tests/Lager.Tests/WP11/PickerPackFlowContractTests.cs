using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Articles;
using Lager.Contracts.Orders;
using Lager.Contracts.PickLists;
using Lager.Contracts.Stock;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP11;

/// <summary>
/// Backend-Vertrag hinter dem Mobile-Picker-Fix (stock-integrity#6): "mark-picked" nimmt keine Mengen entgegen und bucht
/// nichts; gebucht wird erst beim Packen, und zwar die im Pack-Request übergebene Ist-Menge (nicht die Soll-Menge). Deshalb
/// reicht der Mobile-Picker seine Zählstände als Vorbelegung an die Pack-Seite weiter, statt den Pack-Endpunkt selbst
/// aufzurufen (der würde ohne Prüfung durch den Packer buchen). Das Frontend-Verhalten prüft
/// src/tests/WP11/mobilePicker.test.tsx.
/// </summary>
public class PickerPackFlowContractTests
{
    private static async Task<int> StockOfAsync(HttpClient client, Guid articleId) =>
        (await client.GetFromJsonAsync<List<StockItemDto>>("/api/stock"))!.Where(s => s.ArticleId == articleId).Sum(s => s.Quantity);

    [Fact]
    public async Task Pack_bucht_die_uebergebene_Ist_Menge_und_mark_picked_bucht_nichts()
    {
        // Demo-Daten: SKU-003 liegt mit 120 Stück in genau einem Lagerplatz.
        using var factory = new LagerApiFactory { Seed = true };
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        var article = (await admin.GetFromJsonAsync<List<ArticleDto>>("/api/articles"))!.Single(a => a.Sku == "SKU-003");
        var stockBefore = await StockOfAsync(admin, article.Id);

        var orderResponse = await admin.PostAsJsonAsync("/api/orders/manual",
            new CreateOrderRequest("WP11-1", null, new[] { new CreateOrderLineRequest(article.Id, 5) }));
        Assert.Equal(HttpStatusCode.Created, orderResponse.StatusCode);
        var order = (await orderResponse.Content.ReadFromJsonAsync<OrderDto>())!;

        var generated = await admin.PostAsJsonAsync("/api/picklists/generate", new GeneratePickListRequest(new[] { order.Id }));
        Assert.Equal(HttpStatusCode.Created, generated.StatusCode);
        var pickList = (await generated.Content.ReadFromJsonAsync<PickListDto>())!;
        var item = Assert.Single(pickList.Items);
        Assert.Equal(5, item.Quantity);

        // Picker-Schritt (Mobile-Picker "Picking abschließen"): Status Picked, keine Mengen, keine Buchung.
        var marked = await admin.PostAsync($"/api/picklists/{pickList.Id}/mark-picked", content: null);
        Assert.Equal(HttpStatusCode.OK, marked.StatusCode);
        var afterMark = (await marked.Content.ReadFromJsonAsync<PickListDto>())!;
        Assert.Equal("Picked", afterMark.Status);
        Assert.Null(Assert.Single(afterMark.Items).ConfirmedQuantity);
        Assert.Equal(stockBefore, await StockOfAsync(admin, article.Id));

        // Pack-Schritt: der Picker hat 3 von 5 gezählt, die Pack-Seite sendet 3 - gebucht werden 3, nicht 5.
        var packed = await admin.PostAsJsonAsync($"/api/picklists/{pickList.Id}/pack",
            new PackPickListRequest(new[] { new ConfirmPackedItemRequest(item.Id, 3) }));
        Assert.Equal(HttpStatusCode.OK, packed.StatusCode);
        var afterPack = (await packed.Content.ReadFromJsonAsync<PickListDto>())!;
        Assert.Equal("Completed", afterPack.Status);
        Assert.Equal(3, Assert.Single(afterPack.Items).ConfirmedQuantity);
        Assert.Equal(stockBefore - 3, await StockOfAsync(admin, article.Id));
    }
}
