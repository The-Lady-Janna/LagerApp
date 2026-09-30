using Lager.Domain.Warehouse;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lager.Infrastructure.Persistence.Configurations;

public class ShelfConfiguration : IEntityTypeConfiguration<Shelf>
{
    public void Configure(EntityTypeBuilder<Shelf> b)
    {
        b.ToTable("Shelves");
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(64).IsRequired();

        b.OwnsOne(x => x.Position, p =>
        {
            p.Property(x => x.XMm).HasColumnName("PosXMm");
            p.Property(x => x.YMm).HasColumnName("PosYMm");
            p.Property(x => x.ZMm).HasColumnName("PosZMm");
        });

        b.HasMany(x => x.Locations)
            .WithOne()
            .HasForeignKey(s => s.ShelfId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Navigation(x => x.Locations).Metadata.SetField("_locations");
        b.Navigation(x => x.Locations).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
