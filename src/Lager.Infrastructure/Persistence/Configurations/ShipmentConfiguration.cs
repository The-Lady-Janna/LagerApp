using Lager.Domain.Shipping;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lager.Infrastructure.Persistence.Configurations;

public class ShipmentConfiguration : IEntityTypeConfiguration<Shipment>
{
    public void Configure(EntityTypeBuilder<Shipment> b)
    {
        b.ToTable("Shipments");
        b.HasKey(x => x.Id);
        b.Property(x => x.ShipmentNumber).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.ShipmentNumber).IsUnique();
        b.Property(x => x.CarrierCode).HasMaxLength(16);
        b.Property(x => x.TrackingNumber).HasMaxLength(128);
        b.Property(x => x.TrackingUrl).HasMaxLength(Shipment.MaxTrackingUrlLength);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.Status).HasConversion<int>();
        b.HasIndex(x => x.OrderId);
        b.HasIndex(x => new { x.Status, x.CreatedAt });
        // Fremdschlüssel nur für neu angelegte Datenbanken (kein Tabellen-Rebuild für Legacy-DBs). Die Pickliste ist optional
        // und lässt sich löschen (Picklisten zurücksetzen): die Sendung verliert dann nur die Verknüpfung (SET NULL).
        b.HasOne<Lager.Domain.Orders.Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Lager.Domain.PickLists.PickList>().WithMany().HasForeignKey(x => x.PickListId).OnDelete(DeleteBehavior.SetNull);
    }
}
