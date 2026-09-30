using Lager.Domain.Purchasing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lager.Infrastructure.Persistence.Configurations;

public class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> b)
    {
        b.ToTable("PurchaseOrders");
        b.HasKey(x => x.Id);
        b.Property(x => x.PoNumber).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.PoNumber).IsUnique();
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.Status).HasConversion<int>();
        b.Ignore(x => x.IsOpen);
        b.HasMany(x => x.Lines)
            .WithOne()
            .HasForeignKey(l => l.PurchaseOrderId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Lines).Metadata.SetField("_lines");
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.HasIndex(x => new { x.Status, x.CreatedAt });
        b.HasIndex(x => x.SupplierId);
        // Fremdschlüssel nur für neu angelegte Datenbanken (kein Tabellen-Rebuild für Legacy-DBs).
        b.HasOne<Lager.Domain.Suppliers.Supplier>().WithMany().HasForeignKey(x => x.SupplierId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class PurchaseOrderLineConfiguration : IEntityTypeConfiguration<PurchaseOrderLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> b)
    {
        b.ToTable("PurchaseOrderLines");
        b.HasKey(x => x.Id);
        // Id kommt aus der Domain; ohne ValueGeneratedNever gilt eine neue Zeile an einer geladenen Bestellung als vorhanden (UPDATE ins Leere).
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.ArticleSku).HasMaxLength(64);
        b.HasIndex(x => x.PurchaseOrderId);
        b.HasOne<Lager.Domain.Articles.Article>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Restrict);
    }
}
