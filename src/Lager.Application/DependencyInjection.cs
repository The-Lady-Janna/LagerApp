using Lager.Application.Articles;
using Lager.Application.Auth;
using Lager.Application.Customers;
using Lager.Application.ImportExport;
using Lager.Application.Inbound;
using Lager.Application.Inventory;
using Lager.Application.Orders;
using Lager.Application.Packing;
using Lager.Application.PickLists;
using Lager.Application.Purchasing;
using Lager.Application.Reports;
using Lager.Application.Returns;
using Lager.Application.Shipping;
using Lager.Application.Stock;
using Lager.Application.Suppliers;
using Lager.Application.Warehouse;
using Microsoft.Extensions.DependencyInjection;

namespace Lager.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        services.AddScoped<ArticleService>();
        services.AddScoped<WarehouseService>();
        services.AddScoped<StockService>();
        services.AddScoped<OrderService>();
        services.AddScoped<PickListService>();
        services.AddScoped<PickCartConfigService>();
        services.AddScoped<PackingService>();
        services.AddScoped<InboundService>();
        services.AddScoped<InventoryService>();
        services.AddScoped<ReportService>();
        services.AddScoped<AuthService>();
        services.AddScoped<UserService>();
        services.AddScoped<SupplierService>();
        services.AddScoped<PurchaseOrderService>();
        services.AddScoped<ReturnService>();
        services.AddScoped<CustomerService>();
        services.AddScoped<ShipmentService>();
        services.AddScoped<PickWaveService>();
        services.AddScoped<ReplenishmentService>();
        services.AddScoped<PutawayService>();
        services.AddScoped<SlottingService>();
        // CSV-Import/-Export: der Sammelmodus des Audits ist je Request (Scope) derselbe für Import und AuditingInterceptor.
        services.AddScoped<IAuditBatch, AuditBatch>();
        services.AddScoped<ImportService>();
        services.AddSingleton<IPickRouteOptimizer, WallAwarePickRouteOptimizer>();
        services.AddSingleton<IPackingOptimizer, FirstFitDecreasingPackingOptimizer>();
        return services;
    }
}
