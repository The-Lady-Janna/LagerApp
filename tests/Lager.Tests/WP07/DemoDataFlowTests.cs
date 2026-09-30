using Lager.Domain.Orders;
using Lager.Domain.PickLists;
using Lager.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Lager.Tests.WP07;

/// <summary>
/// Der ganze Kommissionierfluss auf den Demo-Daten (Wände, Pick-Points, mehrere Bins je Artikel): alle
/// Demo-Bestellungen in einer Pickliste, Picken, Packen. Bestand und Bestellstatus müssen danach stimmen.
/// </summary>
public class DemoDataFlowTests
{
    [Fact]
    public async Task Demo_orders_can_be_generated_picked_and_packed_end_to_end()
    {
        using var factory = new LagerApiFactory { Seed = true };
        var w = new PickWorld(factory);

        var orders = await w.DbAsync(db => db.Orders.AsNoTracking().Include(o => o.Lines).ToListAsync());
        Assert.NotEmpty(orders);
        var demand = orders.SelectMany(o => o.Lines).GroupBy(l => l.ArticleId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
        var stockBefore = await w.DbAsync(db => db.StockItems.AsNoTracking().GroupBy(s => s.ArticleId)
            .Select(g => new { g.Key, Total = g.Sum(s => s.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Total));

        async Task AssertAllOrdersAsync(OrderStatus expected)
        {
            foreach (var order in orders)
                Assert.Equal(expected, await w.OrderStatusAsync(order.Id));
        }

        var list = await w.GenerateAsync(orders.Select(o => o.Id).ToArray());

        // jede Bestellzeile ist mit ihrer vollen Menge auf der Liste - und kein Bin ist überbucht
        Assert.Equal(demand.Sum(d => d.Value), list.Items.Sum(i => i.Quantity));
        var stockRows = await w.DbAsync(db => db.StockItems.AsNoTracking().ToListAsync());
        foreach (var perBin in list.Items.GroupBy(i => (i.ArticleId, i.StorageLocationId)))
        {
            var available = stockRows
                .Where(s => s.ArticleId == perBin.Key.ArticleId && s.StorageLocationId == perBin.Key.StorageLocationId)
                .Sum(s => s.Quantity);
            Assert.True(perBin.Sum(i => i.Quantity) <= available);
        }
        await AssertAllOrdersAsync(OrderStatus.Picking);

        await w.MarkPickedAsync(list.Id);
        await AssertAllOrdersAsync(OrderStatus.Picked);

        await w.PackAllAsync(list);

        await AssertAllOrdersAsync(OrderStatus.Packed);
        Assert.Equal(PickListStatus.Completed, await w.PickListStatusAsync(list.Id));
        foreach (var (articleId, quantity) in demand)
            Assert.Equal(stockBefore[articleId] - quantity, await w.StockQuantityAsync(articleId));
    }
}
