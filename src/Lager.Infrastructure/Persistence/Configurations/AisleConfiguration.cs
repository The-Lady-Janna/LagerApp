using Lager.Domain.Warehouse;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lager.Infrastructure.Persistence.Configurations;

public class AisleConfiguration : IEntityTypeConfiguration<Aisle>
{
    public void Configure(EntityTypeBuilder<Aisle> b)
    {
        b.ToTable("Aisles");
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(64).IsRequired();
        b.Property(x => x.Orientation).HasConversion<int>();

        b.OwnsOne(x => x.StartPosition, p =>
        {
            p.Property(x => x.XMm).HasColumnName("StartXMm");
            p.Property(x => x.YMm).HasColumnName("StartYMm");
            p.Property(x => x.ZMm).HasColumnName("StartZMm");
        });
        b.OwnsOne(x => x.EndPosition, p =>
        {
            p.Property(x => x.XMm).HasColumnName("EndXMm");
            p.Property(x => x.YMm).HasColumnName("EndYMm");
            p.Property(x => x.ZMm).HasColumnName("EndZMm");
        });

        b.HasMany(x => x.Shelves)
            .WithOne()
            .HasForeignKey(s => s.AisleId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Navigation(x => x.Shelves).Metadata.SetField("_shelves");
        b.Navigation(x => x.Shelves).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
