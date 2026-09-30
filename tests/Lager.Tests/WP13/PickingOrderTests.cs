using Lager.Application.PickLists;
using Lager.Contracts.Orders;
using Lager.Contracts.PickLists;
using Lager.Domain.Orders;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP13;

/// <summary>
/// Reihenfolge und Verfügbarkeit: das Wagen-Füllen nimmt Bestellungen nach Priorität und Fälligkeit vor dem Eingang, die
/// Bestandsampel kennt schon zugeteilte Bestellungen und löst Bundles in Komponenten auf, ein Picklisten-Reset lässt keine
/// verwaiste Welle zurück.
/// </summary>
public class PickingOrderTests
{
    private static OrderDto Find(IEnumerable<OrderDto> orders, Guid id) => orders.Single(o => o.Id == id);

    // ---- Wagen-Reihenfolge ------------------------------------------------

    [Fact]
    public async Task Cart_fill_takes_priority_first_then_due_date_then_arrival()
    {
        // Eigene Instanz: der Wagen bedient ALLE neuen Bestellungen der Datenbank.
        using var factory = new LagerApiFactory();
        var w = new Wp13World(factory.Services);
        var (article, _) = await w.AddStockedArticleAsync(100, weightGrams: 1000);
        var cart = await w.AddCartAsync(maxWeightGrams: 1500);   // je Bestellung 1000 g: genau eine passt in den Wagen

        var oldest = await w.AddOrderAsync(article, 1);
        await Task.Delay(20);   // deutlich verschiedene Eingangszeiten
        var dueLater = await w.AddOrderAsync(article, 1, dueDate: DateTime.UtcNow.AddDays(10));
        await Task.Delay(20);
        var dueSoon = await w.AddOrderAsync(article, 1, dueDate: DateTime.UtcNow.AddDays(1));
        await Task.Delay(20);
        var urgent = await w.AddOrderAsync(article, 1, priority: 2);

        var chosen = new List<Guid>();
        for (var i = 0; i < 4; i++)
        {
            var list = await w.PickListsAsync(s => s.GenerateCartAsync(new GenerateCartPickListRequest(cart)));
            chosen.Add(list.Items.Select(item => item.OrderId).Distinct().Single());
        }

        // Priorität schlägt alles, dann die frühere Fälligkeit (Bestellungen ohne Termin kommen nach denen mit Termin), dann der Eingang.
        Assert.Equal(new[] { urgent, dueSoon, dueLater, oldest }, chosen);
    }

    [Fact]
    public async Task Cart_fill_ignores_cancelled_orders()
    {
        using var factory = new LagerApiFactory();
        var w = new Wp13World(factory.Services);
        var (article, _) = await w.AddStockedArticleAsync(100);
        var cart = await w.AddCartAsync(maxWeightGrams: 100_000);
        var order = await w.AddOrderAsync(article, 1);
        await w.OrdersAsync(s => s.CancelAsync(order));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            w.PickListsAsync(s => s.GenerateCartAsync(new GenerateCartPickListRequest(cart))));

        Assert.Contains("Keine offenen Bestellungen", ex.Message);
    }

    // ---- Bestandsampel ----------------------------------------------------

    [Fact]
    public async Task The_stock_indicator_follows_priority_and_reserves_for_orders_already_being_picked()
    {
        using var fx = new Wp13Fixture();
        var w = fx.World;

        // Zwei neue Bestellungen wollen je 8 von 10: die dringendere bekommt den Bestand, obwohl sie später kam.
        var (contested, _) = await w.AddStockedArticleAsync(10);
        var earlier = await w.AddOrderAsync(contested, 8);
        var urgent = await w.AddOrderAsync(contested, 8, priority: 3);
        var list = await w.OrdersAsync(s => s.ListAsync());
        Assert.True(Find(list, urgent).HasStockAfterFifo);
        Assert.False(Find(list, earlier).HasStockAfterFifo);
        Assert.True(Find(list, earlier).HasStockNow);   // allein hätte sie genug

        // Eine Bestellung, die schon kommissioniert wird, hat ihren Bestand zugeteilt (abgebucht wird erst beim Verpacken).
        var (reserved, _) = await w.AddStockedArticleAsync(10);
        var picking = await w.AddOrderAsync(reserved, 10);
        var waiting = await w.AddOrderAsync(reserved, 5);
        await w.GenerateAsync(picking);
        list = await w.OrdersAsync(s => s.ListAsync());
        Assert.Equal("Picking", Find(list, picking).Status);
        Assert.True(Find(list, waiting).HasStockNow);
        Assert.False(Find(list, waiting).HasStockAfterFifo);

        // Nach dem Verpacken ist der Bestand tatsächlich weg, die verpackte Bestellung warnt nicht mehr.
        var packedList = await w.PickListsAsync(s => s.ListAsync());
        await w.PackAllAsync(packedList.Single(l => l.Items.Any(i => i.OrderId == picking)));
        list = await w.OrdersAsync(s => s.ListAsync());
        Assert.Equal("Packed", Find(list, picking).Status);
        Assert.True(Find(list, picking).HasStockNow);
        Assert.False(Find(list, waiting).HasStockNow);   // 0 übrig
    }

    [Fact]
    public async Task Bundle_orders_are_judged_by_the_stock_of_their_components()
    {
        using var fx = new Wp13Fixture();
        var w = fx.World;
        var (partA, bin) = await w.AddStockedArticleAsync(10);
        var partB = await w.AddArticleAsync();
        await w.AddStockAsync(partB, bin, 5);
        var kit = await w.AddBundleAsync((partA, 2), (partB, 1));   // 1 Kit = 2 x A + 1 x B; das Kit selbst hat keinen Bestand

        var fits = await w.AddOrderAsync(kit, 5);     // braucht A 10, B 5: genau der Bestand
        var second = await w.AddOrderAsync(kit, 1);   // allein möglich, nach der ersten nicht mehr
        var (emptyPart, _) = await w.AddStockedArticleAsync(0);
        var missing = await w.AddOrderAsync(await w.AddBundleAsync((emptyPart, 1)), 1);

        var list = await w.OrdersAsync(s => s.ListAsync());

        Assert.True(Find(list, fits).HasStockNow);
        Assert.True(Find(list, fits).HasStockAfterFifo);
        Assert.True(Find(list, second).HasStockNow);
        Assert.False(Find(list, second).HasStockAfterFifo);
        Assert.False(Find(list, missing).HasStockNow);
    }

    // ---- Reset und Wellen -------------------------------------------------

    [Fact]
    public async Task Reset_cancels_a_released_wave_whose_lists_were_deleted_and_frees_its_orders()
    {
        using var factory = new LagerApiFactory();
        var w = new Wp13World(factory.Services);
        var (article, _) = await w.AddStockedArticleAsync(100);
        var o1 = await w.AddOrderAsync(article, 1);
        var o2 = await w.AddOrderAsync(article, 1);
        var wave = await w.WithAsync<PickWaveService, PickWaveDto>(s =>
            s.CreateAsync(new CreatePickWaveRequest("WP13", null, new[] { o1, o2 })));
        var released = (await w.WithAsync<PickWaveService, PickWaveDto?>(s => s.ReleaseAsync(wave.Id, new ReleaseWaveRequest())))!;
        Assert.Equal("Released", released.Status);

        var result = await w.PickListsAsync(s => s.ResetAllAsync());

        Assert.Equal(new ResetPickListsResult(Deleted: 1, SkippedCompleted: 0), result);
        Assert.Equal("Cancelled", (await w.WithAsync<PickWaveService, PickWaveDto?>(s => s.GetAsync(wave.Id)))!.Status);
        Assert.Equal(OrderStatus.New, await w.OrderStatusAsync(o1));
        Assert.Equal(OrderStatus.New, await w.OrderStatusAsync(o2));
    }
}
