using Lager.Domain.PickLists;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lager.Infrastructure.Persistence.Configurations;

public class PickWaveConfiguration : IEntityTypeConfiguration<PickWave>
{
    public void Configure(EntityTypeBuilder<PickWave> b)
    {
        b.ToTable("PickWaves");
        b.HasKey(x => x.Id);
        b.Property(x => x.WaveNumber).HasMaxLength(64).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.Property(x => x.Status).HasConversion<int>();
        // Lists persisted as CSV columns — keeps schema flat and avoids a
        // separate join table for what is essentially a small denormalised set.
        b.Property(x => x.OrderIdsCsv).HasColumnName("OrderIdsCsv");
        b.Property(x => x.PickListIdsCsv).HasColumnName("PickListIdsCsv");
        b.Ignore(x => x.OrderIds);
        b.Ignore(x => x.PickListIds);
        b.HasIndex(x => x.WaveNumber).IsUnique();
        b.HasIndex(x => new { x.Status, x.CreatedAt });
    }
}
