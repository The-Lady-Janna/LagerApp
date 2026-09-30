using Lager.Application.PickLists;
using Lager.Contracts.PickLists;
using Lager.Domain.Articles;
using Lager.Domain.Orders;
using Lager.Domain.PickLists;
using Lager.Domain.Stock;
using Lager.Domain.Warehouse;
using Lager.Infrastructure.Persistence;
using Lager.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEntity = Lager.Domain.Warehouse.Warehouse;

namespace Lager.Tests.WP07;

/// <summary>
/// Testdaten und Service-Aufrufe für die WP07-Tests. Die Daten entstehen direkt über den DbContext
/// (die Factory hat Seed=false), jeder Service-Aufruf läuft wie ein Request in einem eigenen Scope -
/// so sieht der Test genau, was ein späterer Request aus der Datenbank liest.
/// </summary>
public sealed class PickWorld
{
    private readonly IServiceProvider _services;

    public PickWorld(LagerApiFactory factory)
    {
        // Startet den Host (Migration, Bootstrap) - erst danach ist die Datenbank benutzbar.
        _services = factory.Services;
    }

    public PickWorld(IServiceProvider services) => _services = services;

    public sealed record Site(Guid WarehouseId, Guid ShelfId, string Code);

    public sealed record Bin(Guid Id, string Code);

    // ---- Scopes ---------------------------------------------------------

    /// <summary>Ein offener Scope (wie ein laufender Request), z. B. um dieselbe DbContext-Instanz mehrfach zu benutzen.</summary>
    public sealed class ScopeHandle : IDisposable
    {
        private readonly IServiceScope _scope;
        public ScopeHandle(IServiceScope scope) => _scope = scope;
        public T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();
        public void Dispose() => _scope.Dispose();
    }

    public ScopeHandle NewScope() => new(_services.CreateScope());

    public async Task<T> DbAsync<T>(Func<LagerDbContext, Task<T>> action)
    {
        using var scope = _services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<LagerDbContext>());
    }

    public Task DbAsync(Func<LagerDbContext, Task> action) =>
        DbAsync<int>(async db => { await action(db); return 0; });

    public async Task<T> PickListsAsync<T>(Func<PickListService, Task<T>> action)
    {
        using var scope = _services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<PickListService>());
    }

    public async Task<T> WavesAsync<T>(Func<PickWaveService, Task<T>> action)
    {
        using var scope = _services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<PickWaveService>());
    }

    // ---- Stammdaten -----------------------------------------------------

    public static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    public Task<Site> AddWarehouseAsync(string? code = null) => DbAsync(async db =>
    {
        code ??= Unique("WH");
        var warehouse = new WarehouseEntity(code, code);
        var zone = new Zone(warehouse.Id, "Z-" + code, "Zone", Position.Origin);
        var aisle = new Aisle(zone.Id, "A-" + code, Position.Origin, new Position(10_000, 0, 0), AisleOrientation.AlongX);
        var shelf = new Shelf(aisle.Id, "S-" + code, new Position(0, 200, 0), 8_000, 600, 2_000);
        db.Warehouses.Add(warehouse);
        db.Zones.Add(zone);
        db.Aisles.Add(aisle);
        db.Shelves.Add(shelf);
        await db.SaveChangesAsync();
        return new Site(warehouse.Id, shelf.Id, code);
    });

    public Task<Bin> AddBinAsync(Site site, string code, int x = 1_000, int y = 200, BinType type = BinType.Standard) => DbAsync(async db =>
    {
        var bin = new StorageLocation(site.ShelfId, code, new Position(x, y, 500), 600, 600, 500, 50_000);
        if (type != BinType.Standard) bin.SetBinType(type, 0);
        db.StorageLocations.Add(bin);
        await db.SaveChangesAsync();
        return new Bin(bin.Id, code);
    });

    public Task<Guid> AddWallAsync(Site site, Position from, Position to) => DbAsync(async db =>
    {
        var wall = new Wall(site.WarehouseId, new[] { from, to }, 100, "Wand " + site.Code);
        db.Walls.Add(wall);
        await db.SaveChangesAsync();
        return wall.Id;
    });

    public Task<Guid> AddPickPointAsync(Site site, string label, Position position, PickPointType type) => DbAsync(async db =>
    {
        var point = new PickPoint(site.WarehouseId, label, position, type);
        db.PickPoints.Add(point);
        await db.SaveChangesAsync();
        return point.Id;
    });

    public Task<Guid> AddArticleAsync(string? sku = null, int weightGrams = 100, Dimensions? dimensions = null) => DbAsync(async db =>
    {
        sku ??= Unique("SKU");
        var article = new Article(sku, "Artikel " + sku, dimensions ?? new Dimensions(100, 100, 100), weightGrams, StackingInfo.NotStackable);
        db.Articles.Add(article);
        await db.SaveChangesAsync();
        return article.Id;
    });

    public Task<Guid> AddBundleAsync(params (Guid ComponentId, int Quantity)[] components) => DbAsync(async db =>
    {
        var bundle = new Article(Unique("BUNDLE"), "Bundle", Dimensions.Zero, 0, StackingInfo.NotStackable);
        bundle.ReplaceBundleComponents(components);
        db.Articles.Add(bundle);
        await db.SaveChangesAsync();
        return bundle.Id;
    });

    public Task<Guid> AddStockAsync(Guid articleId, Bin bin, int quantity, string? lot = null, DateTime? expiry = null) => DbAsync(async db =>
    {
        var stock = new StockItem(articleId, bin.Id, quantity, lot, expiry);
        db.StockItems.Add(stock);
        await db.SaveChangesAsync();
        return stock.Id;
    });

    public Task<Guid> AddOrderAsync(params (Guid ArticleId, int Quantity)[] lines) => DbAsync(async db =>
    {
        var order = new Order(Unique("ORD"), OrderSource.Manual, null, lines.Select(l => new OrderLine(l.ArticleId, l.Quantity)));
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    });

    public Task<Guid> AddCartAsync(int maxWeightGrams = 100_000, int levels = 4, int levelHeightMm = 1_000) => DbAsync(async db =>
    {
        var cart = new PickCartConfig(Unique("Wagen"), levels, 1_000, 1_000, levelHeightMm, maxWeightGrams);
        db.PickCartConfigs.Add(cart);
        await db.SaveChangesAsync();
        return cart.Id;
    });

    // ---- Vorgänge über die Services -------------------------------------

    public Task<PickListDto> GenerateAsync(params Guid[] orderIds) =>
        PickListsAsync(s => s.GenerateAsync(new GeneratePickListRequest(orderIds)));

    public Task<PickListDto?> MarkPickedAsync(Guid pickListId) =>
        PickListsAsync(s => s.MarkPickingCompleteAsync(pickListId));

    /// <summary>Packt alle Positionen der Liste mit ihrer Planmenge.</summary>
    public Task<PickListDto?> PackAllAsync(PickListDto list) =>
        PackAsync(list.Id, list.Items.Select(i => new ConfirmPackedItemRequest(i.Id, i.Quantity)).ToArray());

    public Task<PickListDto?> PackAsync(Guid pickListId, params ConfirmPackedItemRequest[] items) =>
        PickListsAsync(s => s.PackAsync(pickListId, new PackPickListRequest(items)));

    // ---- Zustand lesen --------------------------------------------------

    public Task<OrderStatus> OrderStatusAsync(Guid orderId) =>
        DbAsync(db => db.Orders.Where(o => o.Id == orderId).Select(o => o.Status).SingleAsync());

    public Task<int> StockQuantityAsync(Guid articleId) =>
        DbAsync(db => db.StockItems.Where(s => s.ArticleId == articleId).SumAsync(s => s.Quantity));

    public Task<List<StockMovement>> MovementsAsync(Guid articleId) =>
        DbAsync(db => db.StockMovements.AsNoTracking().Where(m => m.ArticleId == articleId).OrderBy(m => m.At).ToListAsync());

    public Task<int> PickListCountAsync() => DbAsync(db => db.PickLists.CountAsync());

    /// <summary>Wie viele Pickpositionen (über alle Listen) gibt es für die Bestellung?</summary>
    public Task<int> PickItemCountForOrderAsync(Guid orderId) =>
        DbAsync(db => db.PickItems.CountAsync(i => i.OrderId == orderId));

    public Task<PickListStatus> PickListStatusAsync(Guid pickListId) =>
        DbAsync(db => db.PickLists.Where(p => p.Id == pickListId).Select(p => p.Status).SingleAsync());

    public Task<List<PickItem>> PickItemsAsync(Guid pickListId) =>
        DbAsync(db => db.PickItems.AsNoTracking().Where(i => i.PickListId == pickListId).OrderBy(i => i.SequenceNumber).ToListAsync());
}
