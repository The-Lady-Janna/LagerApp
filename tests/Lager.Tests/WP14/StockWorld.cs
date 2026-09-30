using Lager.Application.Inbound;
using Lager.Application.Inventory;
using Lager.Application.Purchasing;
using Lager.Application.Returns;
using Lager.Application.Stock;
using Lager.Contracts.Inbound;
using Lager.Contracts.Purchasing;
using Lager.Contracts.Returns;
using Lager.Domain.Articles;
using Lager.Domain.Orders;
using Lager.Domain.Stock;
using Lager.Domain.Suppliers;
using Lager.Domain.Warehouse;
using Lager.Infrastructure.Persistence;
using Lager.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using WarehouseEntity = Lager.Domain.Warehouse.Warehouse;

namespace Lager.Tests.WP14;

/// <summary>
/// Testdaten und Service-Aufrufe für die WP14-Tests. Die Daten entstehen direkt über den DbContext (die Factory hat
/// Seed=false), jeder Service-Aufruf läuft wie ein Request in einem eigenen Scope - der Test sieht so genau, was ein
/// späterer Request aus der Datenbank liest. Alle Namen sind eindeutig: mehrere Tests teilen sich eine Factory.
/// </summary>
public sealed class StockWorld
{
    private readonly IServiceProvider _services;

    public StockWorld(LagerApiFactory factory)
    {
        // Startet den Host (Migration, Bootstrap) - erst danach ist die Datenbank benutzbar.
        _services = factory.Services;
    }

    public sealed record Site(Guid ShelfId, string Code);

    public sealed record Bin(Guid Id, string Code);

    /// <summary>Ein Tag in der Zukunft/Vergangenheit relativ zu heute (UTC): die Tests hängen nicht an einem festen Datum.</summary>
    public static DateTime InDays(int days) => DateTime.UtcNow.Date.AddDays(days);

    public static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    // ---- Scopes -----------------------------------------------------------------------------------------------

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

    /// <summary>Ein Dienst-Aufruf in frischem Scope (wie ein Request).</summary>
    public async Task<T> CallAsync<TService, T>(Func<TService, Task<T>> action) where TService : notnull
    {
        using var scope = _services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public Task<T> InboundAsync<T>(Func<InboundService, Task<T>> action) => CallAsync(action);
    public Task<T> StockAsync<T>(Func<StockService, Task<T>> action) => CallAsync(action);
    public Task<T> InventoryAsync<T>(Func<InventoryService, Task<T>> action) => CallAsync(action);
    public Task<T> ReplenishmentAsync<T>(Func<ReplenishmentService, Task<T>> action) => CallAsync(action);
    public Task<T> ReturnsAsync<T>(Func<ReturnService, Task<T>> action) => CallAsync(action);
    public Task<T> PurchasingAsync<T>(Func<PurchaseOrderService, Task<T>> action) => CallAsync(action);

    // ---- Stammdaten -------------------------------------------------------------------------------------------

    public Task<Site> AddSiteAsync() => DbAsync(async db =>
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
        return new Site(shelf.Id, code);
    });

    public Task<Bin> AddBinAsync(Site site, string? code = null, BinType type = BinType.Standard, int threshold = 0) => DbAsync(async db =>
    {
        code ??= Unique("BIN");
        var bin = new StorageLocation(site.ShelfId, code, new Position(1_000, 200, 500), 600, 600, 500, 50_000);
        if (type != BinType.Standard || threshold > 0) bin.SetBinType(type, threshold);
        db.StorageLocations.Add(bin);
        await db.SaveChangesAsync();
        return new Bin(bin.Id, code);
    });

    public Task<Guid> AddArticleAsync(int priceCents = 0, int reorderPoint = 0, int maxStock = 0, Guid? supplierId = null) => DbAsync(async db =>
    {
        var sku = Unique("SKU");
        var article = new Article(sku, "Artikel " + sku, new Dimensions(100, 100, 100), 100, StackingInfo.NotStackable);
        if (priceCents > 0 || supplierId is not null) article.SetPurchasing(supplierId, priceCents);
        if (reorderPoint > 0) article.SetStockThresholds(0, reorderPoint, maxStock);
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

    public Task<Guid> AddSupplierAsync(bool active = true) => DbAsync(async db =>
    {
        var supplier = new Supplier(Unique("SUP"), "Lieferant");
        if (!active) supplier.Deactivate();
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync();
        return supplier.Id;
    });

    /// <summary>Legt eine Bestandszeile direkt an (ohne Movement): "Altbestand" für Tests, die einen Ausgangsbestand brauchen.</summary>
    public Task<Guid> AddStockAsync(Guid articleId, Bin bin, int quantity, string? lot = null, DateTime? expiry = null) => DbAsync(async db =>
    {
        var stock = new StockItem(articleId, bin.Id, quantity, lot, expiry);
        db.StockItems.Add(stock);
        await db.SaveChangesAsync();
        return stock.Id;
    });

    /// <summary>Eine Bestellung im gewünschten Status (Bestellzeilen: Artikel x Menge).</summary>
    public Task<Guid> AddOrderAsync(OrderStatus status, params (Guid ArticleId, int Quantity)[] lines) => DbAsync(async db =>
    {
        var order = new Order(Unique("ORD"), OrderSource.Manual, null, lines.Select(l => new OrderLine(l.ArticleId, l.Quantity)));
        if (status is OrderStatus.Picking or OrderStatus.Picked or OrderStatus.Packed or OrderStatus.Shipped) order.MarkPicking();
        if (status is OrderStatus.Picked or OrderStatus.Packed or OrderStatus.Shipped) order.MarkPicked();
        if (status is OrderStatus.Packed or OrderStatus.Shipped) order.MarkPacked();
        if (status == OrderStatus.Shipped) order.Transition(OrderStatus.Shipped);
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order.Id;
    });

    // ---- Vorgänge ---------------------------------------------------------------------------------------------

    /// <summary>Legt eine Lieferung mit den Zeilen an (ohne sie zu buchen) und liefert ihre Id.</summary>
    public async Task<Guid> DraftInboundAsync(params (Guid ArticleId, Bin Bin, int Quantity, string? Lot, DateTime? Expiry)[] lines)
    {
        var shipment = await InboundAsync(s => s.CreateAsync(new CreateInboundShipmentRequest(Unique("WE"), null, null)));
        foreach (var l in lines)
            await InboundAsync(s => s.AddLineAsync(shipment.Id, new AddInboundLineRequest(l.ArticleId, l.Bin.Id, l.Quantity, l.Lot, l.Expiry)));
        return shipment.Id;
    }

    /// <summary>Wareneingang mit den Zeilen anlegen und sofort buchen.</summary>
    public async Task<InboundShipmentDto> ReceiveAsync(params (Guid ArticleId, Bin Bin, int Quantity, string? Lot, DateTime? Expiry)[] lines)
    {
        var id = await DraftInboundAsync(lines);
        return (await InboundAsync(s => s.ReceiveAsync(id)))!;
    }

    /// <summary>Bestellung mit Zeilen anlegen und versenden (Status Sent).</summary>
    public async Task<PurchaseOrderDto> SentPurchaseOrderAsync(Guid supplierId, params (Guid ArticleId, int Quantity, int? PriceCents)[] lines)
    {
        var po = await PurchasingAsync(s => s.CreateAsync(new CreatePurchaseOrderRequest(supplierId, null, null,
            lines.Select(l => new CreatePurchaseOrderLineRequest(l.ArticleId, l.Quantity, l.PriceCents)).ToList())));
        return (await PurchasingAsync(s => s.SendAsync(po.Id)))!;
    }

    // ---- Zustand lesen ----------------------------------------------------------------------------------------

    public Task<List<StockItem>> RowsAsync(Guid articleId) =>
        DbAsync(db => db.StockItems.AsNoTracking().Where(s => s.ArticleId == articleId).OrderBy(s => s.LotNumber).ToListAsync());

    public Task<List<StockItem>> RowsAsync(Guid articleId, Bin bin) =>
        DbAsync(db => db.StockItems.AsNoTracking()
            .Where(s => s.ArticleId == articleId && s.StorageLocationId == bin.Id).OrderBy(s => s.LotNumber).ToListAsync());

    public Task<int> QuantityAsync(Guid articleId) =>
        DbAsync(db => db.StockItems.Where(s => s.ArticleId == articleId).SumAsync(s => s.Quantity));

    public Task<List<StockMovement>> MovementsAsync(Guid articleId) =>
        DbAsync(async db => (await db.StockMovements.AsNoTracking().Where(m => m.ArticleId == articleId).ToListAsync())
            .OrderBy(m => m.At).ThenByDescending(m => m.QuantityDelta).ToList());
}
