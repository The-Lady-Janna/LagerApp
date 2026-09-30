using Lager.Domain.PickLists;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lager.Infrastructure.Persistence.Configurations;

public class PickCartConfigConfiguration : IEntityTypeConfiguration<PickCartConfig>
{
    public void Configure(EntityTypeBuilder<PickCartConfig> b)
    {
        b.ToTable("PickCartConfigs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(128).IsRequired();
        b.HasIndex(x => x.Name).IsUnique();
        b.Property(x => x.LevelCount);
        b.Property(x => x.LevelHeightMm);
        b.Property(x => x.LevelWidthMm);
        b.Property(x => x.LevelDepthMm);
        b.Property(x => x.MaxWeightGrams);
        b.Ignore(x => x.TotalVolumeMm3);
    }
}
