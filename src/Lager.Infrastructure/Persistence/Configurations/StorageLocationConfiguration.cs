using Lager.Domain.Warehouse;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lager.Infrastructure.Persistence.Configurations;

public class StorageLocationConfiguration : IEntityTypeConfiguration<StorageLocation>
{
    public void Configure(EntityTypeBuilder<StorageLocation> b)
    {
        b.ToTable("StorageLocations");
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.Code).IsUnique();

        b.OwnsOne(x => x.Position, p =>
        {
            p.Property(x => x.XMm).HasColumnName("PosXMm");
            p.Property(x => x.YMm).HasColumnName("PosYMm");
            p.Property(x => x.ZMm).HasColumnName("PosZMm");
        });

        b.Property(x => x.BinType).HasConversion<int>();
        b.HasIndex(x => x.BinType);
    }
}

public class ReplenishmentTaskConfiguration : IEntityTypeConfiguration<Lager.Domain.Stock.ReplenishmentTask>
{
    public void Configure(EntityTypeBuilder<Lager.Domain.Stock.ReplenishmentTask> b)
    {
        b.ToTable("ReplenishmentTasks");
        b.HasKey(x => x.Id);
        b.Property(x => x.ArticleSku).HasMaxLength(64);
        b.Property(x => x.SourceBinCode).HasMaxLength(64);
        b.Property(x => x.TargetBinCode).HasMaxLength(64);
        b.Property(x => x.Status).HasConversion<int>();
        b.HasIndex(x => new { x.Status, x.CreatedAt });
        b.HasIndex(x => new { x.ArticleId, x.TargetBinId, x.Status });
        // Fremdschlüssel nur für neu angelegte Datenbanken (kein Tabellen-Rebuild für Legacy-DBs). ArticleSku/BinCode
        // stehen zusätzlich als Text im Datensatz.
        b.HasOne<Lager.Domain.Articles.Article>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StorageLocation>().WithMany().HasForeignKey(x => x.SourceBinId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<StorageLocation>().WithMany().HasForeignKey(x => x.TargetBinId).OnDelete(DeleteBehavior.Restrict);
    }
}
