using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lager.Contracts.PickLists;
using Lager.Domain.Orders;
using Lager.Domain.PickLists;
using Lager.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Lager.Tests.WP07;

/// <summary>
/// DELETE /api/picklists (ResetAll) löscht nur Picklisten ohne Buchungswirkung. Jeder Test bekommt eine eigene
/// Factory, weil "alle Picklisten" globaler Zustand ist.
/// </summary>
public class PickListResetTests
{
    private static async Task<(PickWorld World, Guid Article, PickWorld.Bin Bin)> SetupAsync(LagerApiFactory factory, int stock = 1_000)
    {
        var world = new PickWorld(factory);
        var site = await world.AddWarehouseAsync();
        var bin = await world.AddBinAsync(site, PickWorld.Unique("BIN"));
        var article = await world.AddArticleAsync();
        await world.AddStockAsync(article, bin, stock);
        return (world, article, bin);
    }

    [Fact]
    public async Task Reset_keeps_completed_lists_and_packed_orders_and_frees_the_open_ones()
    {
        using var factory = new LagerApiFactory();
        var (w, article, _) = await SetupAsync(factory);

        var packedOrder = await w.AddOrderAsync((article, 10));
        var pendingOrder = await w.AddOrderAsync((article, 20));
        var pickedOrder = await w.AddOrderAsync((article, 30));
        var completed = await w.GenerateAsync(packedOrder);
        await w.GenerateAsync(pendingOrder);
        var picked = await w.GenerateAsync(pickedOrder);
        await w.MarkPickedAsync(picked.Id);
        await w.PackAllAsync(completed);
        Assert.Equal(990, await w.StockQuantityAsync(article));
        var movementsBefore = (await w.MovementsAsync(article)).Count;

        var result = await w.PickListsAsync(s => s.ResetAllAsync());

        Assert.Equal(new ResetPickListsResult(Deleted: 2, SkippedCompleted: 1), result);
        // die verpackte Liste samt bestätigter Menge bleibt ...
        Assert.Equal(PickListStatus.Completed, await w.PickListStatusAsync(completed.Id));
        Assert.Equal(10, (await w.PickItemsAsync(completed.Id)).Single().ConfirmedQuantity);
        Assert.Equal(1, await w.PickListCountAsync());
        // ... die verpackte Bestellung bleibt Packed, die offenen gehen zurück auf New
        Assert.Equal(OrderStatus.Packed, await w.OrderStatusAsync(packedOrder));
        Assert.Equal(OrderStatus.New, await w.OrderStatusAsync(pendingOrder));
        Assert.Equal(OrderStatus.New, await w.OrderStatusAsync(pickedOrder));
        // und der Bestand wurde nicht angefasst
        Assert.Equal(990, await w.StockQuantityAsync(article));
        Assert.Equal(movementsBefore, (await w.MovementsAsync(article)).Count);
        Assert.Equal(0, await w.PickItemCountForOrderAsync(pendingOrder) + await w.PickItemCountForOrderAsync(pickedOrder));
    }

    [Fact]
    public async Task Reset_does_not_reuse_numbers_while_a_completed_list_remains()
    {
        using var factory = new LagerApiFactory();
        var (w, article, _) = await SetupAsync(factory);
        var packedOrder = await w.AddOrderAsync((article, 1));
        var completed = await w.GenerateAsync(packedOrder);
        await w.PackAllAsync(completed);
        var openOrder = await w.AddOrderAsync((article, 1));
        await w.GenerateAsync(openOrder);

        await w.PickListsAsync(s => s.ResetAllAsync());
        var next = await w.GenerateAsync(openOrder);

        // Die Nummer geht weiter (Liste 1 = verpackt, 2 = gelöscht, 3 = neu) statt wieder bei 1 anzufangen.
        Assert.EndsWith("-00003", next.PickListNumber);
        Assert.NotEqual(completed.PickListNumber, next.PickListNumber);
    }

    [Fact]
    public async Task Reset_restarts_the_numbering_only_when_no_list_remains()
    {
        using var factory = new LagerApiFactory();
        var (w, article, _) = await SetupAsync(factory);
        var o1 = await w.AddOrderAsync((article, 1));
        var o2 = await w.AddOrderAsync((article, 1));
        var first = await w.GenerateAsync(o1);
        await w.GenerateAsync(o2);
        Assert.EndsWith("-00001", first.PickListNumber);

        var result = await w.PickListsAsync(s => s.ResetAllAsync());

        Assert.Equal(new ResetPickListsResult(2, 0), result);
        Assert.Equal(0, await w.PickListCountAsync());
        var again = await w.GenerateAsync(o1);
        Assert.EndsWith("-00001", again.PickListNumber);
    }

    [Fact]
    public async Task Reset_leaves_orders_alone_that_are_still_on_a_completed_list()
    {
        using var factory = new LagerApiFactory();
        var (w, article, _) = await SetupAsync(factory);
        var order = await w.AddOrderAsync((article, 5));
        var completed = await w.GenerateAsync(order);
        await w.PackAllAsync(completed);

        // Altlast: dieselbe Bestellung steht noch auf einer zweiten, offenen Liste (früher ohne Status-Prüfung möglich)
        // und steht selbst noch auf Picking, weil sie vor der Statusmaschine gepackt wurde.
        await w.DbAsync(async db =>
        {
            await db.Orders.Where(o => o.Id == order).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Picking));
            var item = await db.PickItems.AsNoTracking().SingleAsync(i => i.PickListId == completed.Id);
            var legacy = new PickList("PL-LEGACY-1", new[]
            {
                new PickItem(1, item.OrderId, item.OrderLineId, item.ArticleId, item.StorageLocationId, item.Quantity),
            }, 0);
            db.PickLists.Add(legacy);
            await db.SaveChangesAsync();
        });

        var result = await w.PickListsAsync(s => s.ResetAllAsync());

        Assert.Equal(new ResetPickListsResult(1, 1), result);
        // steht noch auf der verpackten Liste -> darf NICHT zurück auf New (sonst würde sie erneut gepickt)
        Assert.Equal(OrderStatus.Picking, await w.OrderStatusAsync(order));
    }

    [Fact]
    public async Task Delete_endpoint_reports_deleted_and_skippedCompleted_and_needs_the_admin_role()
    {
        using var factory = new LagerApiFactory();
        var (w, article, _) = await SetupAsync(factory);
        var packedOrder = await w.AddOrderAsync((article, 3));
        var openOrder = await w.AddOrderAsync((article, 4));
        var completed = await w.GenerateAsync(packedOrder);
        await w.PackAllAsync(completed);
        await w.GenerateAsync(openOrder);

        var manager = await factory.CreateClientWithRolesAsync("Manager");
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.DeleteAsync("/api/picklists")).StatusCode);
        Assert.Equal(2, await w.PickListCountAsync());

        var admin = await factory.CreateClient().AsReadyAdminAsync();
        var response = await admin.DeleteAsync("/api/picklists");
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(1, json.RootElement.GetProperty("deleted").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("skippedCompleted").GetInt32());

        Assert.Equal(1, await w.PickListCountAsync());
        Assert.Equal(PickListStatus.Completed, await w.PickListStatusAsync(completed.Id));
        Assert.Equal(OrderStatus.Packed, await w.OrderStatusAsync(packedOrder));
        Assert.Equal(OrderStatus.New, await w.OrderStatusAsync(openOrder));
    }
}
