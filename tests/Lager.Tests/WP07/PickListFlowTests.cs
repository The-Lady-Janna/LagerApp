using Lager.Contracts.PickLists;
using Lager.Domain.Orders;
using Lager.Domain.PickLists;
using Lager.Domain.Stock;
using Microsoft.EntityFrameworkCore;

namespace Lager.Tests.WP07;

/// <summary>
/// Service-Tests gegen die echte Datenbank (SQLite in der Factory): Generieren, Packen, Bestellstatus,
/// Neuberechnen. Jeder Test legt sein eigenes Lager und seine eigenen Artikel an.
/// </summary>
public class PickListFlowTests : IClassFixture<PickApiFixture>
{
    private readonly PickWorld _w;

    public PickListFlowTests(PickApiFixture fixture) => _w = fixture.World;

    /// <summary>Ein Lager mit einem Bin, ein Artikel mit Bestand und eine Bestellung darauf.</summary>
    private async Task<(Guid Article, PickWorld.Bin Bin, Guid Order)> SimpleAsync(int stock, int orderQuantity)
    {
        var site = await _w.AddWarehouseAsync();
        var bin = await _w.AddBinAsync(site, PickWorld.Unique("BIN"));
        var article = await _w.AddArticleAsync();
        await _w.AddStockAsync(article, bin, stock);
        var order = await _w.AddOrderAsync((article, orderQuantity));
        return (article, bin, order);
    }

    // ---- Allokation ------------------------------------------------------

    [Fact]
    public async Task Generate_fails_with_a_shortage_instead_of_overallocating()
    {
        var site = await _w.AddWarehouseAsync();
        var bin = await _w.AddBinAsync(site, PickWorld.Unique("BIN"));
        var sku = PickWorld.Unique("SKU");
        var article = await _w.AddArticleAsync(sku);
        await _w.AddStockAsync(article, bin, 10);
        var o1 = await _w.AddOrderAsync((article, 8));
        var o2 = await _w.AddOrderAsync((article, 8));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _w.GenerateAsync(o1, o2));

        Assert.Contains(sku, ex.Message);
        Assert.Contains("Fehlmenge 6", ex.Message);
        // nichts angelegt, nichts umgestellt
        Assert.Equal(0, await _w.PickItemCountForOrderAsync(o1) + await _w.PickItemCountForOrderAsync(o2));
        Assert.Equal(OrderStatus.New, await _w.OrderStatusAsync(o1));
        Assert.Equal(OrderStatus.New, await _w.OrderStatusAsync(o2));
    }

    [Fact]
    public async Task Generate_spreads_two_orders_over_bins_without_exceeding_any_bin()
    {
        var site = await _w.AddWarehouseAsync();
        var small = await _w.AddBinAsync(site, PickWorld.Unique("BIN"), x: 1_000);
        var big = await _w.AddBinAsync(site, PickWorld.Unique("BIN"), x: 3_000);
        var article = await _w.AddArticleAsync();
        await _w.AddStockAsync(article, small, 10);
        await _w.AddStockAsync(article, big, 100);
        var o1 = await _w.AddOrderAsync((article, 8));
        var o2 = await _w.AddOrderAsync((article, 8));

        var list = await _w.GenerateAsync(o1, o2);

        Assert.Equal(16, list.Items.Sum(i => i.Quantity));
        Assert.True(list.Items.Where(i => i.StorageLocationId == small.Id).Sum(i => i.Quantity) <= 10);
        Assert.Equal(new[] { 1, 2, 3 }, list.Items.Select(i => i.SequenceNumber).OrderBy(n => n).Take(3));
        Assert.Equal(OrderStatus.Picking, await _w.OrderStatusAsync(o1));
        Assert.Equal(OrderStatus.Picking, await _w.OrderStatusAsync(o2));
    }

    [Fact]
    public async Task Generate_resolves_bundles_into_their_components()
    {
        var site = await _w.AddWarehouseAsync();
        var bin = await _w.AddBinAsync(site, PickWorld.Unique("BIN"));
        var componentA = await _w.AddArticleAsync();
        var componentB = await _w.AddArticleAsync();
        await _w.AddStockAsync(componentA, bin, 100);
        await _w.AddStockAsync(componentB, bin, 100);
        var bundle = await _w.AddBundleAsync((componentA, 2), (componentB, 1));
        var order = await _w.AddOrderAsync((bundle, 3));

        var list = await _w.GenerateAsync(order);

        Assert.Equal(6, list.Items.Single(i => i.ArticleId == componentA).Quantity);
        Assert.Equal(3, list.Items.Single(i => i.ArticleId == componentB).Quantity);
        Assert.DoesNotContain(list.Items, i => i.ArticleId == bundle);
    }

    [Fact]
    public async Task Generate_never_picks_expired_lots()
    {
        var site = await _w.AddWarehouseAsync();
        var expiredBin = await _w.AddBinAsync(site, PickWorld.Unique("BIN"), x: 1_000);
        var freshBin = await _w.AddBinAsync(site, PickWorld.Unique("BIN"), x: 3_000);
        var article = await _w.AddArticleAsync();
        await _w.AddStockAsync(article, expiredBin, 50, "ALT", DateTime.UtcNow.Date.AddDays(-5));
        await _w.AddStockAsync(article, freshBin, 50, "NEU", DateTime.UtcNow.Date.AddDays(60));
        var order = await _w.AddOrderAsync((article, 20));

        var list = await _w.GenerateAsync(order);

        Assert.All(list.Items, i => Assert.Equal(freshBin.Id, i.StorageLocationId));
    }

    // ---- Order-Status beim Generieren -----------------------------------

    [Fact]
    public async Task Generate_rejects_orders_that_are_not_new_and_drops_duplicate_ids()
    {
        var (_, _, order) = await SimpleAsync(stock: 100, orderQuantity: 5);

        var list = await _w.GenerateAsync(order, order);        // doppelte Id wird verworfen
        Assert.Equal(5, Assert.Single(list.Items).Quantity);
        Assert.Equal(OrderStatus.Picking, await _w.OrderStatusAsync(order));

        // laufende Bestellung: keine zweite Pickliste
        await Assert.ThrowsAsync<InvalidOperationException>(() => _w.GenerateAsync(order));
        Assert.Equal(1, await _w.PickItemCountForOrderAsync(order));

        // auch verpackte Bestellungen nicht
        await _w.PackAllAsync(list);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _w.GenerateAsync(order));
        Assert.Equal(OrderStatus.Packed, await _w.OrderStatusAsync(order));
    }

    // ---- Packen: Idempotenz ---------------------------------------------

    [Fact]
    public async Task Pack_books_the_stock_once_and_a_second_pack_is_rejected()
    {
        var (article, _, order) = await SimpleAsync(stock: 100, orderQuantity: 10);
        var list = await _w.GenerateAsync(order);

        var packed = await _w.PackAllAsync(list);

        Assert.Equal("Completed", packed!.Status);
        Assert.Equal(10, Assert.Single(packed.Items).ConfirmedQuantity);
        Assert.Equal(90, await _w.StockQuantityAsync(article));
        var movement = Assert.Single(await _w.MovementsAsync(article));
        Assert.Equal(-10, movement.QuantityDelta);
        Assert.Equal(StockMovementReason.Pick, movement.Reason);
        Assert.Equal(list.Id, movement.ReferenceId);

        // zweiter Aufruf (Retry, zweiter Tab): abgelehnt, nichts ändert sich
        await Assert.ThrowsAsync<InvalidOperationException>(() => _w.PackAllAsync(list));
        Assert.Equal(90, await _w.StockQuantityAsync(article));
        Assert.Single(await _w.MovementsAsync(article));
        Assert.Equal(PickListStatus.Completed, await _w.PickListStatusAsync(list.Id));
    }

    [Fact]
    public async Task Pack_rejects_invalid_quantities_and_items_without_changing_anything()
    {
        var (article, _, order) = await SimpleAsync(stock: 100, orderQuantity: 10);
        var list = await _w.GenerateAsync(order);
        var item = list.Items.Single();

        // mehr als geplant (Obergrenze), negativ, doppelte Id, fremde Id, fehlende Liste
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _w.PackAsync(list.Id, new ConfirmPackedItemRequest(item.Id, 11)));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _w.PackAsync(list.Id, new ConfirmPackedItemRequest(item.Id, -1)));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _w.PackAsync(list.Id,
            new ConfirmPackedItemRequest(item.Id, 5), new ConfirmPackedItemRequest(item.Id, 5)));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _w.PackAsync(list.Id, new ConfirmPackedItemRequest(Guid.NewGuid(), 1)));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => _w.PickListsAsync(s => s.PackAsync(list.Id, new PackPickListRequest(null!))));

        Assert.Equal(100, await _w.StockQuantityAsync(article));
        Assert.Empty(await _w.MovementsAsync(article));
        Assert.Equal(PickListStatus.Pending, await _w.PickListStatusAsync(list.Id));
        Assert.Null((await _w.PickItemsAsync(list.Id)).Single().ConfirmedQuantity);

        // genau die Planmenge ist erlaubt
        await _w.PackAsync(list.Id, new ConfirmPackedItemRequest(item.Id, 10));
        Assert.Equal(90, await _w.StockQuantityAsync(article));
    }

    [Fact]
    public async Task Pack_of_an_unknown_list_returns_null()
    {
        Assert.Null(await _w.PackAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Pack_fails_on_missing_stock_instead_of_silently_capping()
    {
        var (article, _, order) = await SimpleAsync(stock: 10, orderQuantity: 8);
        var list = await _w.GenerateAsync(order);

        // in der Zwischenzeit hat jemand Ware entnommen: nur noch 5 im Bin
        await _w.DbAsync(db => db.StockItems.Where(s => s.ArticleId == article)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Quantity, 5)));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _w.PackAllAsync(list));

        Assert.Contains("3 Stück fehlen", ex.Message);
        Assert.Equal(5, await _w.StockQuantityAsync(article));              // nichts teilweise gebucht
        Assert.Empty(await _w.MovementsAsync(article));
        Assert.Equal(PickListStatus.Pending, await _w.PickListStatusAsync(list.Id));
        Assert.Equal(OrderStatus.Picking, await _w.OrderStatusAsync(order));
        Assert.Null((await _w.PickItemsAsync(list.Id)).Single().ConfirmedQuantity);
    }

    [Fact]
    public async Task Pack_books_two_lots_of_one_bin_FEFO_with_one_movement_per_lot()
    {
        var site = await _w.AddWarehouseAsync();
        var bin = await _w.AddBinAsync(site, PickWorld.Unique("BIN"));
        var article = await _w.AddArticleAsync();
        var early = DateTime.UtcNow.Date.AddDays(10);
        var late = DateTime.UtcNow.Date.AddDays(40);
        await _w.AddStockAsync(article, bin, 5, "SPAET", late);      // zuerst angelegt, aber später ablaufend
        await _w.AddStockAsync(article, bin, 5, "FRUEH", early);
        var order = await _w.AddOrderAsync((article, 8));
        var list = await _w.GenerateAsync(order);

        // ein Lagerplatz, zwei Chargen: eine Position (die Charge wird erst beim Buchen gewählt)
        Assert.Equal(8, Assert.Single(list.Items).Quantity);
        await _w.PackAllAsync(list);

        var movements = await _w.MovementsAsync(article);
        Assert.Equal(2, movements.Count);
        var fromEarly = Assert.Single(movements, m => m.LotNumber == "FRUEH");
        Assert.Equal(-5, fromEarly.QuantityDelta);
        Assert.Equal(early, fromEarly.ExpiryDate);
        var fromLate = Assert.Single(movements, m => m.LotNumber == "SPAET");
        Assert.Equal(-3, fromLate.QuantityDelta);
        Assert.Equal(late, fromLate.ExpiryDate);

        var rows = await _w.DbAsync(db => db.StockItems.AsNoTracking().Where(s => s.ArticleId == article).ToListAsync());
        Assert.Equal(0, rows.Single(s => s.LotNumber == "FRUEH").Quantity);
        Assert.Equal(2, rows.Single(s => s.LotNumber == "SPAET").Quantity);
    }

    [Fact]
    public async Task Pack_of_two_orders_on_the_same_bin_books_the_bin_once_per_lot()
    {
        var site = await _w.AddWarehouseAsync();
        var bin = await _w.AddBinAsync(site, PickWorld.Unique("BIN"));
        var article = await _w.AddArticleAsync();
        await _w.AddStockAsync(article, bin, 30);
        var o1 = await _w.AddOrderAsync((article, 10));
        var o2 = await _w.AddOrderAsync((article, 12));
        var list = await _w.GenerateAsync(o1, o2);

        await _w.PackAllAsync(list);

        Assert.Equal(8, await _w.StockQuantityAsync(article));
        Assert.Equal(-22, Assert.Single(await _w.MovementsAsync(article)).QuantityDelta);
        Assert.Equal(OrderStatus.Packed, await _w.OrderStatusAsync(o1));
        Assert.Equal(OrderStatus.Packed, await _w.OrderStatusAsync(o2));
    }

    // ---- Order-Grundstatus ----------------------------------------------

    [Fact]
    public async Task Order_becomes_Picked_after_mark_picked_and_Packed_after_pack()
    {
        var (_, _, order) = await SimpleAsync(stock: 100, orderQuantity: 4);
        var list = await _w.GenerateAsync(order);
        Assert.Equal(OrderStatus.Picking, await _w.OrderStatusAsync(order));

        var picked = await _w.MarkPickedAsync(list.Id);
        Assert.Equal("Picked", picked!.Status);
        Assert.Equal(OrderStatus.Picked, await _w.OrderStatusAsync(order));

        // mark-picked ist wiederholbar, ändert aber nichts mehr
        await _w.MarkPickedAsync(list.Id);
        Assert.Equal(OrderStatus.Picked, await _w.OrderStatusAsync(order));

        await _w.PackAllAsync(list);
        Assert.Equal(OrderStatus.Packed, await _w.OrderStatusAsync(order));

        // verpackte Liste: mark-picked wird abgelehnt
        await Assert.ThrowsAsync<InvalidOperationException>(() => _w.MarkPickedAsync(list.Id));
        Assert.Equal(PickListStatus.Completed, await _w.PickListStatusAsync(list.Id));
    }

    [Fact]
    public async Task Pack_without_mark_picked_and_with_a_short_pick_still_completes_the_order()
    {
        var (article, _, order) = await SimpleAsync(stock: 100, orderQuantity: 10);
        var list = await _w.GenerateAsync(order);

        var packed = await _w.PackAsync(list.Id, new ConfirmPackedItemRequest(list.Items.Single().Id, 4));   // Kurz-Pick

        Assert.Equal(4, packed!.Items.Single().ConfirmedQuantity);
        Assert.Equal(96, await _w.StockQuantityAsync(article));
        Assert.Equal(OrderStatus.Packed, await _w.OrderStatusAsync(order));
    }

    // ---- Neuberechnen ----------------------------------------------------

    [Fact]
    public async Task Recalculate_returns_the_route_with_all_items_of_an_open_list()
    {
        var site = await _w.AddWarehouseAsync();
        var article = await _w.AddArticleAsync();
        var bins = new List<PickWorld.Bin>();
        for (var i = 0; i < 3; i++)
        {
            var bin = await _w.AddBinAsync(site, PickWorld.Unique("BIN"), x: 1_000 + i * 1_500);
            await _w.AddStockAsync(article, bin, 10);
            bins.Add(bin);
        }
        var order = await _w.AddOrderAsync((article, 25));
        var list = await _w.GenerateAsync(order);
        Assert.Equal(3, list.Items.Count);

        var recalculated = await _w.PickListsAsync(s => s.RecalculateAsync(list.Id, new RecalculatePickListRequest()));

        Assert.NotNull(recalculated);
        Assert.Equal(3, recalculated!.Items.Count);
        Assert.Equal(25, recalculated.Items.Sum(i => i.Quantity));
        Assert.Equal(new[] { 1, 2, 3 }, recalculated.Items.Select(i => i.SequenceNumber).OrderBy(n => n));
        // in der Datenbank stehen genau diese drei Items (die alten sind ersetzt, nicht verdoppelt)
        Assert.Equal(3, (await _w.PickItemsAsync(list.Id)).Count);
    }

    [Theory]
    [InlineData("Picked")]
    [InlineData("Completed")]
    public async Task Recalculate_is_rejected_for_lists_that_are_no_longer_open(string state)
    {
        var (_, _, order) = await SimpleAsync(stock: 100, orderQuantity: 5);
        var list = await _w.GenerateAsync(order);
        await _w.MarkPickedAsync(list.Id);
        if (state == "Completed") await _w.PackAllAsync(list);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _w.PickListsAsync(s => s.RecalculateAsync(list.Id, new RecalculatePickListRequest())));

        // die bestätigten Mengen bleiben erhalten
        var items = await _w.PickItemsAsync(list.Id);
        Assert.Single(items);
        Assert.Equal(state == "Completed" ? 5 : (int?)null, items[0].ConfirmedQuantity);
    }

    [Fact]
    public async Task Recalculate_keeps_the_items_when_saving_fails()
    {
        var site = await _w.AddWarehouseAsync();
        var article = await _w.AddArticleAsync();
        for (var i = 0; i < 2; i++)
        {
            var bin = await _w.AddBinAsync(site, PickWorld.Unique("BIN"), x: 1_000 + i * 1_500);
            await _w.AddStockAsync(article, bin, 10);
        }
        var order = await _w.AddOrderAsync((article, 15));
        var list = await _w.GenerateAsync(order);
        var idsBefore = (await _w.PickItemsAsync(list.Id)).Select(i => i.Id).Order().ToList();

        using var scope = _w.NewScope();
        var repo = scope.Get<Lager.Application.Abstractions.IPickListRepository>();
        var service = scope.Get<Lager.Application.PickLists.PickListService>();

        // Der Request lädt die Liste (Concurrency-Token wird gemerkt) ...
        await repo.GetAsync(list.Id);
        // ... ein anderer Vorgang ändert sie zwischenzeitlich ...
        var foreignToken = Guid.NewGuid();
        await _w.DbAsync(db => db.PickLists.Where(p => p.Id == list.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ConcurrencyToken, foreignToken)));

        // ... und das Speichern scheitert am Concurrency-Konflikt
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
            service.RecalculateAsync(list.Id, new RecalculatePickListRequest()));

        // Vorher wurden die alten Items sofort per SQL gelöscht: die Liste blieb leer. Jetzt ist alles unverändert.
        var idsAfter = (await _w.PickItemsAsync(list.Id)).Select(i => i.Id).Order().ToList();
        Assert.Equal(idsBefore, idsAfter);
    }
}
