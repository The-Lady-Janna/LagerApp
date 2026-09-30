using Lager.Application.Customers;
using Lager.Application.Orders;
using Lager.Application.PickLists;
using Lager.Application.Shipping;
using Lager.Contracts.Customers;
using Lager.Contracts.Orders;
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

namespace Lager.Tests.WP13;

/// <summary>
/// Eine API-Instanz (eigene SQLite-Datei, Seed=false) pro Testklasse. Die Tests einer Klasse legen ihre Daten mit
/// eindeutigen Namen an und stören sich deshalb nicht.
/// </summary>
public sealed class Wp13Fixture : IDisposable
{
    public LagerApiFactory Factory { get; } = new();
    public Wp13World World { get; }

    public Wp13Fixture() => World = new Wp13World(Factory.Services);

    /// <summary>Ein als einsatzbereiter Admin eingeloggter HTTP-Client.</summary>
    public Task<HttpClient> AdminAsync() => Factory.CreateClient().AsReadyAdminAsync();

    public void Dispose() => Factory.Dispose();
}

/// <summary>
/// Testdaten und Service-Aufrufe für die WP13-Tests. Die Daten entstehen direkt über den DbContext (Seed=false), jeder
/// Service-Aufruf läuft wie ein Request in einem eigenen Scope - so sieht der Test genau, was ein späterer Request
/// aus der Datenbank liest.
/// </summary>
public sealed class Wp13World
{
    private readonly IServiceProvider _services;

    public Wp13World(IServiceProvider services) => _services = services;

    public sealed record Bin(Guid Id, string Code);

    public static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    // ---- Zugriff --------------------------------------------------------

    public async Task<T> DbAsync<T>(Func<LagerDbContext, Task<T>> action)
    {
        using var scope = _services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<LagerDbContext>());
    }

    public Task DbAsync(Func<LagerDbContext, Task> action) =>
        DbAsync<int>(async db => { await action(db); return 0; });

    /// <summary>Ruft einen Service in einem eigenen Scope auf (wie ein Request).</summary>
    public async Task<T> WithAsync<TService, T>(Func<TService, Task<T>> action) where TService : notnull
    {
        using var scope = _services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public Task<T> OrdersAsync<T>(Func<OrderService, Task<T>> action) => WithAsync(action);
    public Task<T> PickListsAsync<T>(Func<PickListService, Task<T>> action) => WithAsync(action);
    public Task<T> ShipmentsAsync<T>(Func<ShipmentService, Task<T>> action) => WithAsync(action);

    // ---- Stammdaten -----------------------------------------------------

    /// <summary>Ein Lager mit einem Lagerplatz, ein Artikel mit Bestand darauf.</summary>
    public async Task<(Guid Article, Bin Bin)> AddStockedArticleAsync(int stock, int weightGrams = 100)
    {
        var bin = await AddBinAsync(await AddWarehouseAsync());
        var article = await AddArticleAsync(weightGrams: weightGrams);
        await AddStockAsync(article, bin, stock);
        return (article, bin);
    }

    public Task<Guid> AddWarehouseAsync() => DbAsync(async db =>
    {
        var code = Unique("WH");
        var warehouse = new WarehouseEntity(code, code);
        var zone = new Zone(warehouse.Id, "Z-" + code, "Zone", Position.Origin);
        var aisle = new Aisle(zone.Id, "A-" + code, Position.Origin, new Position(10_000, 0, 0), AisleOrientation.AlongX);
        var shelf = new Shelf(aisle.Id, "S-" + code, new Position(0, 200, 0), 8_000, 600, 2_000);
        db.Warehouses.Add(warehouse);
        db.Zones.Add(zone);
        db.Aisles.Add(aisle);
        db.Shelves.Add(shelf);
        await db.SaveChangesAsync();
        return shelf.Id;
    });

    public Task<Bin> AddBinAsync(Guid shelfId) => DbAsync(async db =>
    {
        var code = Unique("BIN");
        var bin = new StorageLocation(shelfId, code, new Position(1_000, 200, 500), 600, 600, 500, 50_000);
        db.StorageLocations.Add(bin);
        await db.SaveChangesAsync();
        return new Bin(bin.Id, code);
    });

    public Task<Guid> AddArticleAsync(string? sku = null, int weightGrams = 100,
        DateTime? validFrom = null, DateTime? validUntil = null) => DbAsync(async db =>
    {
        sku ??= Unique("SKU");
        var article = new Article(sku, "Artikel " + sku, new Dimensions(100, 100, 100), weightGrams, StackingInfo.NotStackable);
        if (validFrom is not null || validUntil is not null) article.SetSeasonWindow(validFrom, validUntil);
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

    public Task AddStockAsync(Guid articleId, Bin bin, int quantity) => DbAsync(async db =>
    {
        db.StockItems.Add(new StockItem(articleId, bin.Id, quantity, null, null));
        await db.SaveChangesAsync();
    });

    public Task<Guid> AddCartAsync(int maxWeightGrams) => DbAsync(async db =>
    {
        var cart = new PickCartConfig(Unique("Wagen"), 4, 1_000, 1_000, 1_000, maxWeightGrams);
        db.PickCartConfigs.Add(cart);
        await db.SaveChangesAsync();
        return cart.Id;
    });

    /// <summary>Kunde mit einer Lieferadresse (und optional einer reinen Rechnungsadresse).</summary>
    public async Task<(Guid Customer, Guid ShippingAddress, Guid BillingAddress)> AddCustomerAsync(bool active = true)
    {
        var dto = await WithAsync<CustomerService, CustomerDto>(async s =>
        {
            var created = await s.CreateAsync(new CreateCustomerRequest(Unique("K"), "Kunde " + Unique("N")));
            await s.AddAddressAsync(created.Id, new AddAddressRequest("Shipping", "Lager", "Hafenstr. 5", "Halle 2", "20457", "Hamburg", "de"));
            var withBoth = await s.AddAddressAsync(created.Id, new AddAddressRequest("Billing", "Buchhaltung", "Postfach 1", null, "10115", "Berlin", "DE"));
            if (!active) withBoth = await s.SetActiveAsync(created.Id, false);
            return withBoth!;
        });
        return (dto.Id, dto.Addresses.Single(a => a.Kind == "Shipping").Id, dto.Addresses.Single(a => a.Kind == "Billing").Id);
    }

    // ---- Vorgänge über die Services --------------------------------------

    public static CreateOrderRequest OrderRequest(string number, Guid articleId, int quantity, int priority = 0,
        DateTime? dueDate = null, Guid? customerId = null, Guid? addressId = null, string? externalReference = null) =>
        new(number, null, new[] { new CreateOrderLineRequest(articleId, quantity) },
            customerId, addressId, priority, dueDate, externalReference);

    public async Task<Guid> AddOrderAsync(Guid articleId, int quantity, int priority = 0, DateTime? dueDate = null) =>
        (await OrdersAsync(s => s.CreateAsync(OrderRequest(Unique("ORD"), articleId, quantity, priority, dueDate), OrderSource.Manual))).Id;

    public Task<PickListDto> GenerateAsync(params Guid[] orderIds) =>
        PickListsAsync(s => s.GenerateAsync(new GeneratePickListRequest(orderIds)));

    public Task<PickListDto?> MarkPickedAsync(Guid pickListId) =>
        PickListsAsync(s => s.MarkPickingCompleteAsync(pickListId));

    /// <summary>Packt alle Positionen der Liste mit ihrer Planmenge.</summary>
    public Task<PickListDto?> PackAllAsync(PickListDto list) =>
        PickListsAsync(s => s.PackAsync(list.Id,
            new PackPickListRequest(list.Items.Select(i => new ConfirmPackedItemRequest(i.Id, i.Quantity)).ToList())));

    /// <summary>Bestellung anlegen, kommissionieren und verpacken: danach steht sie auf Packed.</summary>
    public async Task<Guid> AddPackedOrderAsync(Guid articleId, int quantity, Guid? customerId = null, Guid? addressId = null)
    {
        var order = (await OrdersAsync(s => s.CreateAsync(
            OrderRequest(Unique("ORD"), articleId, quantity, customerId: customerId, addressId: addressId), OrderSource.Manual))).Id;
        var list = await GenerateAsync(order);
        await PackAllAsync(list);
        return order;
    }

    // ---- Zustand lesen --------------------------------------------------

    public Task<OrderStatus> OrderStatusAsync(Guid orderId) =>
        DbAsync(db => db.Orders.Where(o => o.Id == orderId).Select(o => o.Status).SingleAsync());

    public Task<int> StockQuantityAsync(Guid articleId) =>
        DbAsync(db => db.StockItems.Where(s => s.ArticleId == articleId).SumAsync(s => s.Quantity));

    public Task<int> OrderCountAsync(string orderNumber) =>
        DbAsync(db => db.Orders.CountAsync(o => o.OrderNumber == orderNumber));

    public Task<int> PickItemCountAsync(Guid pickListId) =>
        DbAsync(db => db.PickItems.CountAsync(i => i.PickListId == pickListId));

    public Task<PickListStatus> PickListStatusAsync(Guid pickListId) =>
        DbAsync(db => db.PickLists.Where(p => p.Id == pickListId).Select(p => p.Status).SingleAsync());
}
