using Lager.Domain.Inbound;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lager.Infrastructure.Persistence.Configurations;

public class InboundShipmentConfiguration : IEntityTypeConfiguration<InboundShipment>
{
    public void Configure(EntityTypeBuilder<InboundShipment> b)
    {
        b.ToTable("InboundShipments");
        b.HasKey(x => x.Id);
        b.Property(x => x.ShipmentNumber).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.ShipmentNumber).IsUnique();
        b.Property(x => x.SupplierReference).HasMaxLength(128);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.Status).HasConversion<int>();

        b.HasMany(x => x.Lines)
            .WithOne()
            .HasForeignKey(l => l.InboundShipmentId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Lines).Metadata.SetField("_lines");
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Bestellbezug (WP14): eigene Detailtabelle statt neuer Spalte, siehe InboundShipmentLinkConfiguration.
        b.HasOne(x => x.PurchaseOrderLink)
            .WithOne()
            .HasForeignKey<InboundShipmentLink>(l => l.InboundShipmentId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.PurchaseOrderLink).AutoInclude();
        b.Ignore(x => x.PurchaseOrderId);

        b.HasIndex(x => new { x.Status, x.CreatedAt });
    }
}

/// <summary>
/// Tabelle InboundShipmentLinks (eine Zeile je Lieferung mit Bestellbezug). Die Tabelle InboundShipments hat Legacy-DDL im
/// SchemaUpgrader (Baseline), das dem Modell entsprechen muss (Drift-Test); neue Felder kommen deshalb als eigene Tabelle
/// über PurchaseOrderInboundLinkStep dazu. Kein Fremdschlüssel auf die Bestellung (wie bei den anderen Bezügen nur für neu
/// angelegte Datenbanken möglich); die Bestellung lädt das Repository beim Buchen.
/// </summary>
public class InboundShipmentLinkConfiguration : IEntityTypeConfiguration<InboundShipmentLink>
{
    public void Configure(EntityTypeBuilder<InboundShipmentLink> b)
    {
        b.ToTable("InboundShipmentLinks");
        b.HasKey(x => x.InboundShipmentId);
        b.Property(x => x.InboundShipmentId).ValueGeneratedNever();
        b.HasIndex(x => x.PurchaseOrderId); // Suche nach dem offenen Wareneingang einer Bestellung
    }
}

public class InboundLineConfiguration : IEntityTypeConfiguration<InboundLine>
{
    public void Configure(EntityTypeBuilder<InboundLine> b)
    {
        b.ToTable("InboundLines");
        b.HasKey(x => x.Id);
        // Die Id vergibt die Domain (Guid.NewGuid). Ohne ValueGeneratedNever hält EF eine neue Zeile, die an einer schon
        // geladenen Lieferung hängt, für vorhanden (Modified) und schreibt ein UPDATE ins Leere (DbUpdateConcurrencyException).
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.LotNumber).HasMaxLength(64);
        b.HasOne<Lager.Domain.Articles.Article>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Lager.Domain.Warehouse.StorageLocation>().WithMany().HasForeignKey(x => x.TargetBinId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.InboundShipmentId);

        // Bestellzeile und Einkaufspreis (WP14): eigene Detailtabelle statt neuer Spalten, siehe InboundLineLinkConfiguration.
        b.HasOne(x => x.Link)
            .WithOne()
            .HasForeignKey<InboundLineLink>(l => l.InboundLineId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Link).AutoInclude();
        b.Ignore(x => x.PurchaseOrderLineId);
        b.Ignore(x => x.UnitCostCents);
    }
}

/// <summary>Tabelle InboundLineLinks (eine Zeile je Wareneingangszeile mit Bestellzeile oder Einkaufspreis), siehe InboundShipmentLinkConfiguration.</summary>
public class InboundLineLinkConfiguration : IEntityTypeConfiguration<InboundLineLink>
{
    public void Configure(EntityTypeBuilder<InboundLineLink> b)
    {
        b.ToTable("InboundLineLinks");
        b.HasKey(x => x.InboundLineId);
        b.Property(x => x.InboundLineId).ValueGeneratedNever();
    }
}
