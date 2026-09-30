using Lager.Domain.Warehouse;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lager.Infrastructure.Persistence.Configurations;

public class ZoneConfiguration : IEntityTypeConfiguration<Zone>
{
    public void Configure(EntityTypeBuilder<Zone> b)
    {
        b.ToTable("Zones");
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(64).IsRequired();
        b.Property(x => x.Name).HasMaxLength(256).IsRequired();

        b.OwnsOne(x => x.Origin, o =>
        {
            o.Property(p => p.XMm).HasColumnName("OriginXMm");
            o.Property(p => p.YMm).HasColumnName("OriginYMm");
            o.Property(p => p.ZMm).HasColumnName("OriginZMm");
        });

        b.HasMany(x => x.Aisles)
            .WithOne()
            .HasForeignKey(a => a.ZoneId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Navigation(x => x.Aisles).Metadata.SetField("_aisles");
        b.Navigation(x => x.Aisles).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
