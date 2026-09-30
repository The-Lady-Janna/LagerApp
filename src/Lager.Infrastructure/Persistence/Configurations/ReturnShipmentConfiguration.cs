using Lager.Domain.Returns;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lager.Infrastructure.Persistence.Configurations;

public class ReturnShipmentConfiguration : IEntityTypeConfiguration<ReturnShipment>
{
    public void Configure(EntityTypeBuilder<ReturnShipment> b)
    {
        b.ToTable("ReturnShipments");
        b.HasKey(x => x.Id);
        b.Property(x => x.RmaNumber).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.RmaNumber).IsUnique();
        b.Property(x => x.CustomerReference).HasMaxLength(128);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.Status).HasConversion<int>();
        b.HasMany(x => x.Lines)
            .WithOne()
            .HasForeignKey(l => l.ReturnShipmentId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Lines).Metadata.SetField("_lines");
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.HasIndex(x => new { x.Status, x.CreatedAt });
        // Optionaler Bezug zum Auftrag. Fremdschlüssel nur für neu angelegte Datenbanken (kein Tabellen-Rebuild für Legacy-DBs).
        b.HasOne<Lager.Domain.Orders.Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class ReturnLineConfiguration : IEntityTypeConfiguration<ReturnLine>
{
    public void Configure(EntityTypeBuilder<ReturnLine> b)
    {
        b.ToTable("ReturnLines");
        b.HasKey(x => x.Id);
        // Id kommt aus der Domain; ohne ValueGeneratedNever gilt eine neue Zeile an einer geladenen Retoure als vorhanden (UPDATE ins Leere).
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.ArticleSku).HasMaxLength(64);
        b.Property(x => x.LotNumber).HasMaxLength(64);
        b.Property(x => x.QcResult).HasConversion<int>();
        b.Property(x => x.QcNotes).HasMaxLength(500);
        b.HasIndex(x => x.ReturnShipmentId);
        b.HasOne<Lager.Domain.Articles.Article>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Lager.Domain.Warehouse.StorageLocation>().WithMany().HasForeignKey(x => x.TargetBinId).OnDelete(DeleteBehavior.Restrict);
    }
}
