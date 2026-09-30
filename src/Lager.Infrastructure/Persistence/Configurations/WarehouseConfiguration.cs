using Lager.Domain.Warehouse;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WarehouseEntity = Lager.Domain.Warehouse.Warehouse;

namespace Lager.Infrastructure.Persistence.Configurations;

public class WarehouseConfiguration : IEntityTypeConfiguration<WarehouseEntity>
{
    public void Configure(EntityTypeBuilder<WarehouseEntity> b)
    {
        b.ToTable("Warehouses");
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.Name).HasMaxLength(256).IsRequired();

        b.HasMany(x => x.Zones)
            .WithOne()
            .HasForeignKey(z => z.WarehouseId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Navigation(x => x.Zones).Metadata.SetField("_zones");
        b.Navigation(x => x.Zones).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
