using Lager.Domain.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lager.Infrastructure.Persistence.Configurations;

public class InventoryCountConfiguration : IEntityTypeConfiguration<InventoryCount>
{
    public void Configure(EntityTypeBuilder<InventoryCount> b)
    {
        b.ToTable("InventoryCounts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(128).IsRequired();
        b.Property(x => x.Status).HasConversion<int>();
        b.HasMany(x => x.Lines)
            .WithOne()
            .HasForeignKey(l => l.InventoryCountId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Lines).Metadata.SetField("_lines");
        b.Navigation(x => x.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.HasIndex(x => new { x.Status, x.CreatedAt });
    }
}

public class InventoryLineConfiguration : IEntityTypeConfiguration<InventoryLine>
{
    public void Configure(EntityTypeBuilder<InventoryLine> b)
    {
        b.ToTable("InventoryLines");
        b.HasKey(x => x.Id);
        b.Property(x => x.BinCode).HasMaxLength(64);
        b.Property(x => x.ArticleSku).HasMaxLength(64);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Ignore(x => x.Diff);
        // Charge und MHD der Zählzeile (WP14): eigene Detailtabelle statt neuer Spalten, siehe InventoryLineLotConfiguration.
        b.HasOne(x => x.Lot)
            .WithOne()
            .HasForeignKey<InventoryLineLot>(l => l.InventoryLineId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Lot).AutoInclude();
        b.Ignore(x => x.LotNumber);
        b.Ignore(x => x.ExpiryDate);
        b.HasIndex(x => x.InventoryCountId);
        // Fremdschlüssel nur für neu angelegte Datenbanken (kein Tabellen-Rebuild für Legacy-DBs). ArticleSku/BinCode
        // stehen zusätzlich als Text in der Zeile.
        b.HasOne<Lager.Domain.Articles.Article>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Lager.Domain.Warehouse.StorageLocation>().WithMany().HasForeignKey(x => x.BinId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// Tabelle InventoryLineLots (eine Zeile je Zählzeile von Ware mit Charge oder MHD). Die Tabelle InventoryLines hat Legacy-DDL
/// im SchemaUpgrader (Baseline), das dem Modell entsprechen muss (Drift-Test); neue Felder kommen deshalb als eigene Tabelle
/// über PurchaseOrderInboundLinkStep dazu.
/// </summary>
public class InventoryLineLotConfiguration : IEntityTypeConfiguration<InventoryLineLot>
{
    public void Configure(EntityTypeBuilder<InventoryLineLot> b)
    {
        b.ToTable("InventoryLineLots");
        b.HasKey(x => x.InventoryLineId);
        b.Property(x => x.InventoryLineId).ValueGeneratedNever();
        b.Property(x => x.LotNumber).HasMaxLength(64);
    }
}
