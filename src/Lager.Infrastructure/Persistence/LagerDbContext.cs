using Lager.Domain.Articles;
using Lager.Domain.Auditing;
using Lager.Domain.Auth;
using Lager.Domain.Customers;
using Lager.Domain.Inbound;
using Lager.Domain.Inventory;
using Lager.Domain.Orders;
using Lager.Domain.PickLists;
using Lager.Domain.Stock;
using Lager.Domain.Suppliers;
using Lager.Domain.Warehouse;
using Lager.Infrastructure.Persistence.Converters;
using Microsoft.EntityFrameworkCore;
using WarehouseEntity = Lager.Domain.Warehouse.Warehouse;

namespace Lager.Infrastructure.Persistence;

public class LagerDbContext : DbContext
{
    public LagerDbContext(DbContextOptions<LagerDbContext> options) : base(options) { }

    public DbSet<Article> Articles => Set<Article>();
    public DbSet<WarehouseEntity> Warehouses => Set<WarehouseEntity>();
    public DbSet<Zone> Zones => Set<Zone>();
    public DbSet<Aisle> Aisles => Set<Aisle>();
    public DbSet<Shelf> Shelves => Set<Shelf>();
    public DbSet<StorageLocation> StorageLocations => Set<StorageLocation>();
    public DbSet<Wall> Walls => Set<Wall>();
    public DbSet<PickPoint> PickPoints => Set<PickPoint>();
    public DbSet<StockItem> StockItems => Set<StockItem>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<PickList> PickLists => Set<PickList>();
    public DbSet<PickItem> PickItems => Set<PickItem>();
    public DbSet<PickListSequence> PickListSequences => Set<PickListSequence>();
    public DbSet<PickCartConfig> PickCartConfigs => Set<PickCartConfig>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<InboundShipment> InboundShipments => Set<InboundShipment>();
    public DbSet<InboundLine> InboundLines => Set<InboundLine>();
    public DbSet<InventoryCount> InventoryCounts => Set<InventoryCount>();
    public DbSet<InventoryLine> InventoryLines => Set<InventoryLine>();
    public DbSet<PickWave> PickWaves => Set<PickWave>();
    public DbSet<ReplenishmentTask> ReplenishmentTasks => Set<ReplenishmentTask>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<Lager.Domain.Purchasing.PurchaseOrder> PurchaseOrders => Set<Lager.Domain.Purchasing.PurchaseOrder>();
    public DbSet<Lager.Domain.Purchasing.PurchaseOrderLine> PurchaseOrderLines => Set<Lager.Domain.Purchasing.PurchaseOrderLine>();
    public DbSet<Lager.Domain.Returns.ReturnShipment> ReturnShipments => Set<Lager.Domain.Returns.ReturnShipment>();
    public DbSet<Lager.Domain.Returns.ReturnLine> ReturnLines => Set<Lager.Domain.Returns.ReturnLine>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<CustomerAddress> CustomerAddresses => Set<CustomerAddress>();
    public DbSet<BundleComponent> BundleComponents => Set<BundleComponent>();
    public DbSet<Lager.Domain.Shipping.Shipment> Shipments => Set<Lager.Domain.Shipping.Shipment>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();

    /// <summary>
    /// Alle DateTime-Properties (Entity.CreatedAt/UpdatedAt, StockMovement.At, ReceivedAt ...) werden als UTC gelesen und
    /// geschrieben, damit die JSON-Ausgabe ein "Z" trägt (siehe <see cref="UtcDateTimeConverter"/>). Die Konvention
    /// gilt für DateTime und DateTime? und für alle künftigen Properties.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<UtcNullableDateTimeConverter>();
        base.ConfigureConventions(configurationBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LagerDbContext).Assembly);
        if (Database.IsSqlite())
            ApplySqliteCollations(modelBuilder);
        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// SKU, Auftragsnummer und Benutzername sind fachlich case-insensitiv eindeutig. MySQL (Standard-Collation *_ci)
    /// verhält sich so, SQLite vergleicht Text binär: dort bekommen die Spalten (nur bei diesem Provider) die Collation
    /// NOCASE, und mit ihnen der Unique-Index und jeder Vergleich mit =. Legacy-Datenbanken ziehen den Index über
    /// den Schritt CaseInsensitiveUniqueIndexesStep nach (die Spalte selbst bleibt dort binär, siehe dort).
    /// </summary>
    private static void ApplySqliteCollations(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Article>().Property(x => x.Sku).UseCollation("NOCASE");
        modelBuilder.Entity<Order>().Property(x => x.OrderNumber).UseCollation("NOCASE");
        modelBuilder.Entity<User>().Property(x => x.Username).UseCollation("NOCASE");
    }
}
