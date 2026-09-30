using Lager.Domain.Inbound;
using Lager.Domain.Inventory;
using Lager.Domain.Orders;
using Lager.Domain.PickLists;
using Lager.Domain.Purchasing;
using Lager.Domain.Returns;
using Lager.Domain.Shipping;
using Lager.Domain.Stock;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lager.Infrastructure.Persistence.Configurations;

/// <summary>
/// Marks the inherited <c>ConcurrencyToken</c> as an EF concurrency check column on the aggregates where
/// lost-update races actually hurt:
///  - Bestand und Bestandsbuchungen: StockItem;
///  - Belege mit Statuswechsel, der Bestand bucht oder Folgeobjekte erzeugt: Order, PickList, PickWave,
///    InboundShipment (Wareneingang), InventoryCount (Inventur), ReturnShipment (Retoure), PurchaseOrder,
///    Shipment (Versand), ReplenishmentTask (Nachschub).
/// Zwei parallele Requests lesen sonst beide den alten Status; nur das StockItem-Token schützte die Buchung, und das
/// greift nicht, wenn die Bestandszeile neu angelegt wird. Mit dem Token scheitert das zweite Status-UPDATE des Belegs
/// mit DbUpdateConcurrencyException (die Middleware macht daraus 409).
/// Stammdaten (Artikel, Kunden, Lagerplätze ...) tragen die Spalte weiter, erzwingen aber keine optimistische Sperre
/// (last write wins). Ein If-Match/ETag-Header wird bewusst nicht eingeführt: Der Server erkennt Konflikte, die
/// Antwort 409 kommt mit der Fehlerabbildung im API-Paket.
/// </summary>
public class StockItemConcurrencyConfiguration : IEntityTypeConfiguration<StockItem>
{
    public void Configure(EntityTypeBuilder<StockItem> b) =>
        b.Property(x => x.ConcurrencyToken).IsConcurrencyToken();
}

public class OrderConcurrencyConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> b) =>
        b.Property(x => x.ConcurrencyToken).IsConcurrencyToken();
}

public class PickListConcurrencyConfiguration : IEntityTypeConfiguration<PickList>
{
    public void Configure(EntityTypeBuilder<PickList> b) =>
        b.Property(x => x.ConcurrencyToken).IsConcurrencyToken();
}

public class PickWaveConcurrencyConfiguration : IEntityTypeConfiguration<PickWave>
{
    public void Configure(EntityTypeBuilder<PickWave> b) =>
        b.Property(x => x.ConcurrencyToken).IsConcurrencyToken();
}

public class InboundShipmentConcurrencyConfiguration : IEntityTypeConfiguration<InboundShipment>
{
    public void Configure(EntityTypeBuilder<InboundShipment> b) =>
        b.Property(x => x.ConcurrencyToken).IsConcurrencyToken();
}

public class InventoryCountConcurrencyConfiguration : IEntityTypeConfiguration<InventoryCount>
{
    public void Configure(EntityTypeBuilder<InventoryCount> b) =>
        b.Property(x => x.ConcurrencyToken).IsConcurrencyToken();
}

public class ReturnShipmentConcurrencyConfiguration : IEntityTypeConfiguration<ReturnShipment>
{
    public void Configure(EntityTypeBuilder<ReturnShipment> b) =>
        b.Property(x => x.ConcurrencyToken).IsConcurrencyToken();
}

public class PurchaseOrderConcurrencyConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> b) =>
        b.Property(x => x.ConcurrencyToken).IsConcurrencyToken();
}

public class ShipmentConcurrencyConfiguration : IEntityTypeConfiguration<Shipment>
{
    public void Configure(EntityTypeBuilder<Shipment> b) =>
        b.Property(x => x.ConcurrencyToken).IsConcurrencyToken();
}

public class ReplenishmentTaskConcurrencyConfiguration : IEntityTypeConfiguration<ReplenishmentTask>
{
    public void Configure(EntityTypeBuilder<ReplenishmentTask> b) =>
        b.Property(x => x.ConcurrencyToken).IsConcurrencyToken();
}
