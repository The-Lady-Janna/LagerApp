using Lager.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lager.Infrastructure.Persistence.Configurations;

public class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> b)
    {
        b.ToTable("Orders");
        b.HasKey(x => x.Id);
        b.Property(x => x.OrderNumber).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.OrderNumber).IsUnique();
        b.Property(x => x.CustomerReference).HasMaxLength(128);
        // Kennung im Quellsystem (Idempotenz der externen Bestell-API): eindeutig, sofern gesetzt (NULLs kollidieren nicht).
        // Priorität 0..3 (Domain) und optionale Fälligkeit steuern die Kommissionier-Reihenfolge. Für Bestandsdatenbanken
        // legt der Schritt OrderLifecycleStep die Spalten und den Index an.
        b.Property(x => x.ExternalReference).HasMaxLength(Order.MaxExternalReferenceLength);
        b.HasIndex(x => x.ExternalReference).IsUnique();
        b.Property(x => x.Status).HasConversion<int>();
        b.Property(x => x.Source).HasConversion<int>();
        // Dashboard hot path: filter by Status, ordered by CreatedAt.
        b.HasIndex(x => new { x.Status, x.CreatedAt });

        // Optionale Verknüpfung zum Kundenstamm. Fremdschlüssel nur für neu angelegte Datenbanken (kein Tabellen-Rebuild
        // für Legacy-DBs). Eine Lieferadresse lässt sich entfernen (Kunde bearbeiten): der Auftrag verliert dann nur die
        // Verknüpfung (SET NULL), den Freitext CustomerReference behält er.
        b.HasOne<Lager.Domain.Customers.Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Lager.Domain.Customers.CustomerAddress>().WithMany().HasForeignKey(x => x.ShippingAddressId).OnDelete(DeleteBehavior.SetNull);

        b.HasMany(x => x.Lines)
            .WithOne()
            .HasForeignKey(l => l.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Navigation(x => x.Lines).Metadata.SetField("_lines");
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> b)
    {
        b.ToTable("OrderLines");
        b.HasKey(x => x.Id);
        b.HasOne<Lager.Domain.Articles.Article>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Restrict);
    }
}
