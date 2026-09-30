using Lager.Domain.Warehouse;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lager.Infrastructure.Persistence.Configurations;

public class PickPointConfiguration : IEntityTypeConfiguration<PickPoint>
{
    public void Configure(EntityTypeBuilder<PickPoint> b)
    {
        b.ToTable("PickPoints");
        b.HasKey(x => x.Id);
        b.Property(x => x.Label).HasMaxLength(128).IsRequired();
        b.Property(x => x.Type).HasConversion<int>();

        b.OwnsOne(x => x.Position, p =>
        {
            p.Property(x => x.XMm).HasColumnName("PosXMm");
            p.Property(x => x.YMm).HasColumnName("PosYMm");
            p.Property(x => x.ZMm).HasColumnName("PosZMm");
        });

        // Fremdschlüssel nur für neu angelegte Datenbanken (siehe SchemaUpgrader: kein Tabellen-Rebuild für Legacy-DBs).
        b.HasOne<Lager.Domain.Warehouse.Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.WarehouseId);
    }
}
