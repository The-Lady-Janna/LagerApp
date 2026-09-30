using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lager.Application.PickLists;
using Lager.Contracts.Orders;
using Lager.Contracts.PickLists;
using Lager.Domain.Orders;
using Lager.Domain.PickLists;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP13;

/// <summary>
/// Storno (POST /api/orders/{id}/cancel): erlaubt aus New, Picking und Picked ohne Bestandseffekt (der Bestand wird erst
/// beim Verpacken abgebucht), abgelehnt aus Packed und Shipped.
/// </summary>
public class OrderCancelTests : IClassFixture<Wp13Fixture>
{
    private readonly Wp13Fixture _fx;
    private Wp13World W => _fx.World;

    public OrderCancelTests(Wp13Fixture fixture) => _fx = fixture;

    private static void AssertRule(Exception ex, string code)
    {
        var rule = Assert.IsType<InvalidOperationException>(ex);
        Assert.Equal(code, rule.Data["code"]);
    }

    [Fact]
    public async Task Cancel_from_Picking_takes_only_this_orders_items_off_the_list_and_books_nothing()
    {
        var (article, _) = await W.AddStockedArticleAsync(100);
        var a = await W.AddOrderAsync(article, 5);
        var b = await W.AddOrderAsync(article, 7);
        var list = await W.GenerateAsync(a, b);
        Assert.Equal(OrderStatus.Picking, await W.OrderStatusAsync(a));

        var cancelled = await W.OrdersAsync(s => s.CancelAsync(a));

        Assert.Equal("Cancelled", cancelled!.Status);
        Assert.Equal(OrderStatus.Cancelled, await W.OrderStatusAsync(a));
        // die Liste lebt für die zweite Bestellung weiter, ohne die Positionen der stornierten
        Assert.Equal(PickListStatus.Pending, await W.PickListStatusAsync(list.Id));
        var remaining = await W.DbAsync(db => Task.FromResult(db.PickItems.Where(i => i.PickListId == list.Id).Select(i => i.OrderId).Distinct().ToList()));
        Assert.Equal(new[] { b }, remaining);
        Assert.Equal(OrderStatus.Picking, await W.OrderStatusAsync(b));
        Assert.Equal(100, await W.StockQuantityAsync(article));

        // die zweite ebenfalls stornieren: die Liste ist leer und wird storniert
        await W.OrdersAsync(s => s.CancelAsync(b));
        Assert.Equal(0, await W.PickItemCountAsync(list.Id));
        Assert.Equal(PickListStatus.Cancelled, await W.PickListStatusAsync(list.Id));
        Assert.Equal(100, await W.StockQuantityAsync(article));
    }

    [Fact]
    public async Task Cancel_from_Picked_empties_and_cancels_the_list()
    {
        var (article, _) = await W.AddStockedArticleAsync(50);
        var order = await W.AddOrderAsync(article, 3);
        var list = await W.GenerateAsync(order);
        await W.MarkPickedAsync(list.Id);
        Assert.Equal(OrderStatus.Picked, await W.OrderStatusAsync(order));

        await W.OrdersAsync(s => s.CancelAsync(order));

        Assert.Equal(OrderStatus.Cancelled, await W.OrderStatusAsync(order));
        Assert.Equal(PickListStatus.Cancelled, await W.PickListStatusAsync(list.Id));
        Assert.Equal(0, await W.PickItemCountAsync(list.Id));
        Assert.Equal(50, await W.StockQuantityAsync(article));
    }

    [Fact]
    public async Task Cancel_from_Packed_is_rejected_and_changes_nothing()
    {
        var (article, _) = await W.AddStockedArticleAsync(50);
        var order = await W.AddPackedOrderAsync(article, 5);
        Assert.Equal(45, await W.StockQuantityAsync(article));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => W.OrdersAsync(s => s.CancelAsync(order)));

        AssertRule(ex, "order_not_cancellable");
        Assert.Equal(OrderStatus.Packed, await W.OrderStatusAsync(order));
        Assert.Equal(45, await W.StockQuantityAsync(article));
    }

    [Fact]
    public async Task Cancelling_twice_and_cancelling_an_unknown_order_are_handled()
    {
        var (article, _) = await W.AddStockedArticleAsync(10);
        var order = await W.AddOrderAsync(article, 1);
        await W.OrdersAsync(s => s.CancelAsync(order));

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => W.OrdersAsync(s => s.CancelAsync(order)));

        AssertRule(ex, "order_not_cancellable");
        Assert.Null(await W.OrdersAsync(s => s.CancelAsync(Guid.NewGuid())));
    }

    [Fact]
    public async Task A_cancelled_order_cannot_be_picked_and_no_longer_competes_for_stock()
    {
        var (article, _) = await W.AddStockedArticleAsync(10);
        var older = await W.AddOrderAsync(article, 8);
        var newer = await W.AddOrderAsync(article, 8);

        // beide wollen 8 von 10: nur die ältere kommt durch
        var before = await W.OrdersAsync(s => s.ListAsync());
        Assert.False(before.Single(o => o.Id == newer).HasStockAfterFifo);

        await W.OrdersAsync(s => s.CancelAsync(older));

        var after = await W.OrdersAsync(s => s.ListAsync());
        Assert.True(after.Single(o => o.Id == newer).HasStockAfterFifo);
        Assert.Equal("Cancelled", after.Single(o => o.Id == older).Status);
        var pick = await Assert.ThrowsAnyAsync<Exception>(() => W.GenerateAsync(older));
        Assert.Contains("Nur neue Bestellungen", pick.Message);
    }

    [Fact]
    public async Task Cancel_removes_the_order_from_an_open_wave()
    {
        var (article, _) = await W.AddStockedArticleAsync(50);
        var keep = await W.AddOrderAsync(article, 1);
        var drop = await W.AddOrderAsync(article, 1);
        var wave = await W.WithAsync<PickWaveService, PickWaveDto>(s =>
            s.CreateAsync(new CreatePickWaveRequest("WP13", null, new[] { keep, drop })));

        await W.OrdersAsync(s => s.CancelAsync(drop));

        var reloaded = await W.WithAsync<PickWaveService, PickWaveDto?>(s => s.GetAsync(wave.Id));
        Assert.Equal(new[] { keep }, reloaded!.OrderIds);
        // und die Welle lässt sich freigeben (mit der stornierten Bestellung darin würde das scheitern)
        var released = await W.WithAsync<PickWaveService, PickWaveDto?>(s => s.ReleaseAsync(wave.Id, new ReleaseWaveRequest()));
        Assert.Equal("Released", released!.Status);
    }

    [Fact]
    public async Task Cancelling_the_only_order_of_a_released_wave_cancels_the_wave()
    {
        var (article, _) = await W.AddStockedArticleAsync(50);
        var order = await W.AddOrderAsync(article, 2);
        var wave = await W.WithAsync<PickWaveService, PickWaveDto>(s =>
            s.CreateAsync(new CreatePickWaveRequest("WP13", null, new[] { order })));
        var released = (await W.WithAsync<PickWaveService, PickWaveDto?>(s => s.ReleaseAsync(wave.Id, new ReleaseWaveRequest())))!;

        await W.OrdersAsync(s => s.CancelAsync(order));

        Assert.Equal("Cancelled", (await W.WithAsync<PickWaveService, PickWaveDto?>(s => s.GetAsync(wave.Id)))!.Status);
        Assert.Equal(PickListStatus.Cancelled, await W.PickListStatusAsync(released.PickListIds.Single()));
    }

    // ---- HTTP -----------------------------------------------------------

    [Fact]
    public async Task Cancel_endpoint_needs_the_manager_role_and_answers_with_clear_status_codes()
    {
        var (article, _) = await W.AddStockedArticleAsync(50);
        var open = await W.AddOrderAsync(article, 1);
        var packed = await W.AddPackedOrderAsync(article, 1);

        foreach (var role in new[] { "Viewer", "Picker", "Packer" })
        {
            var client = await _fx.Factory.CreateClientWithRolesAsync(role);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"/api/orders/{open}/cancel", null)).StatusCode);
        }
        Assert.Equal(OrderStatus.New, await W.OrderStatusAsync(open));

        var manager = await _fx.Factory.CreateClientWithRolesAsync("Manager");

        var ok = await manager.PostAsync($"/api/orders/{open}/cancel", null);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("Cancelled", (await ok.Content.ReadFromJsonAsync<OrderDto>())!.Status);

        var tooLate = await manager.PostAsync($"/api/orders/{packed}/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, tooLate.StatusCode);
        using var body = JsonDocument.Parse(await tooLate.Content.ReadAsStringAsync());
        Assert.Equal("order_not_cancellable", body.RootElement.GetProperty("code").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await manager.PostAsync($"/api/orders/{Guid.NewGuid()}/cancel", null)).StatusCode);
    }
}
