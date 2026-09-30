using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.PickLists;
using Lager.Domain.Orders;
using Lager.Domain.PickLists;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP07;

/// <summary>
/// Dieselben Regeln über die HTTP-Schicht: Fehlerfälle liefern einen 4xx (kein 500). Welcher 4xx es genau ist
/// (409 Regelverstoß, 400 ungültige Eingabe, 404 nicht gefunden), bestimmt die zentrale Fehlerabbildung (WP12); Status,
/// Code und Format prüfen die Tests in WP32 (ControllerErrorFormatTests) - hier reicht die Klasse.
/// </summary>
public class PickListHttpTests : IClassFixture<PickApiFixture>
{
    private readonly PickApiFixture _api;
    private PickWorld W => _api.World;

    public PickListHttpTests(PickApiFixture fixture) => _api = fixture;

    private static void AssertClientError(HttpResponseMessage response) =>
        Assert.True((int)response.StatusCode is >= 400 and < 500,
            $"4xx erwartet, war {(int)response.StatusCode}");

    [Fact]
    public async Task Pack_endpoint_books_once_and_rejects_repeats_and_oversized_quantities()
    {
        var site = await W.AddWarehouseAsync();
        var bin = await W.AddBinAsync(site, PickWorld.Unique("BIN"));
        var article = await W.AddArticleAsync();
        await W.AddStockAsync(article, bin, 100);
        var order = await W.AddOrderAsync((article, 10));
        var admin = await _api.Factory.CreateClient().AsReadyAdminAsync();

        var generated = await admin.PostAsJsonAsync("/api/picklists/generate", new GeneratePickListRequest(new[] { order }));
        Assert.Equal(HttpStatusCode.Created, generated.StatusCode);
        var list = (await generated.Content.ReadFromJsonAsync<PickListDto>())!;
        var item = list.Items.Single();

        // Menge über Plan: abgelehnt, nichts gebucht
        var tooMany = await admin.PostAsJsonAsync($"/api/picklists/{list.Id}/pack",
            new PackPickListRequest(new[] { new ConfirmPackedItemRequest(item.Id, item.Quantity + 1) }));
        AssertClientError(tooMany);
        Assert.Equal(100, await W.StockQuantityAsync(article));

        // erster Aufruf: gebucht
        var body = new PackPickListRequest(new[] { new ConfirmPackedItemRequest(item.Id, item.Quantity) });
        var first = await admin.PostAsJsonAsync($"/api/picklists/{list.Id}/pack", body);
        first.EnsureSuccessStatusCode();
        Assert.Equal(90, await W.StockQuantityAsync(article));
        Assert.Single(await W.MovementsAsync(article));

        // zweiter Aufruf: abgelehnt, Bestand und Bewegungen unverändert
        var second = await admin.PostAsJsonAsync($"/api/picklists/{list.Id}/pack", body);
        AssertClientError(second);
        Assert.Equal(90, await W.StockQuantityAsync(article));
        Assert.Single(await W.MovementsAsync(article));

        // mark-picked auf der verpackten Liste und ein zweites generate für die verpackte Bestellung: ebenfalls 4xx
        AssertClientError(await admin.PostAsync($"/api/picklists/{list.Id}/mark-picked", null));
        AssertClientError(await admin.PostAsJsonAsync("/api/picklists/generate", new GeneratePickListRequest(new[] { order })));
        Assert.Equal(OrderStatus.Packed, await W.OrderStatusAsync(order));
    }

    [Fact]
    public async Task Generate_endpoint_reports_a_shortage_as_a_client_error()
    {
        var site = await W.AddWarehouseAsync();
        var bin = await W.AddBinAsync(site, PickWorld.Unique("BIN"));
        var article = await W.AddArticleAsync();
        await W.AddStockAsync(article, bin, 10);
        var o1 = await W.AddOrderAsync((article, 8));
        var o2 = await W.AddOrderAsync((article, 8));
        var admin = await _api.Factory.CreateClient().AsReadyAdminAsync();

        var response = await admin.PostAsJsonAsync("/api/picklists/generate", new GeneratePickListRequest(new[] { o1, o2 }));

        AssertClientError(response);
        Assert.Contains("Fehlmenge", await response.Content.ReadAsStringAsync());
        Assert.Equal(OrderStatus.New, await W.OrderStatusAsync(o1));
    }

    [Fact]
    public async Task Recalculate_endpoint_returns_the_route_of_an_open_list_and_rejects_a_picked_one()
    {
        var site = await W.AddWarehouseAsync();
        var article = await W.AddArticleAsync();
        for (var i = 0; i < 2; i++)
        {
            var bin = await W.AddBinAsync(site, PickWorld.Unique("BIN"), x: 1_000 + i * 1_500);
            await W.AddStockAsync(article, bin, 10);
        }
        var order = await W.AddOrderAsync((article, 15));
        var admin = await _api.Factory.CreateClient().AsReadyAdminAsync();
        var generated = await admin.PostAsJsonAsync("/api/picklists/generate", new GeneratePickListRequest(new[] { order }));
        var list = (await generated.Content.ReadFromJsonAsync<PickListDto>())!;

        var recalculated = await admin.PostAsJsonAsync($"/api/picklists/{list.Id}/recalculate", new RecalculatePickListRequest());
        recalculated.EnsureSuccessStatusCode();
        var dto = (await recalculated.Content.ReadFromJsonAsync<PickListDto>())!;
        Assert.Equal(list.Items.Count, dto.Items.Count);
        Assert.Equal(15, dto.Items.Sum(i => i.Quantity));

        (await admin.PostAsync($"/api/picklists/{list.Id}/mark-picked", null)).EnsureSuccessStatusCode();
        AssertClientError(await admin.PostAsJsonAsync($"/api/picklists/{list.Id}/recalculate", new RecalculatePickListRequest()));
        Assert.Equal(list.Items.Count, (await W.PickItemsAsync(list.Id)).Count);
    }

    [Fact]
    public async Task Wave_release_endpoint_rejects_a_second_release()
    {
        var site = await W.AddWarehouseAsync();
        var bin = await W.AddBinAsync(site, PickWorld.Unique("BIN"));
        var article = await W.AddArticleAsync();
        await W.AddStockAsync(article, bin, 100);
        var order = await W.AddOrderAsync((article, 3));
        var admin = await _api.Factory.CreateClient().AsReadyAdminAsync();

        var created = await admin.PostAsJsonAsync("/api/pick-waves", new CreatePickWaveRequest("HTTP", null, new[] { order }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var wave = (await created.Content.ReadFromJsonAsync<PickWaveDto>())!;

        var first = await admin.PostAsJsonAsync($"/api/pick-waves/{wave.Id}/release", new ReleaseWaveRequest());
        first.EnsureSuccessStatusCode();
        var second = await admin.PostAsJsonAsync($"/api/pick-waves/{wave.Id}/release", new ReleaseWaveRequest());
        AssertClientError(second);

        Assert.Equal(1, await W.PickItemCountForOrderAsync(order));
        var listId = (await first.Content.ReadFromJsonAsync<PickWaveDto>())!.PickListIds.Single();
        Assert.Equal(PickListStatus.Pending, await W.PickListStatusAsync(listId));

        // Abbruch nach Release: Liste storniert, Bestellung frei; danach ist die Welle endgültig
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/pick-waves/{wave.Id}/cancel", null)).StatusCode);
        Assert.Equal(OrderStatus.New, await W.OrderStatusAsync(order));
        AssertClientError(await admin.PostAsync($"/api/pick-waves/{wave.Id}/cancel", null));
    }
}
