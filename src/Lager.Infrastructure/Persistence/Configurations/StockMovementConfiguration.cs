using Lager.Domain.Stock;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lager.Infrastructure.Persistence.Configurations;

public class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> b)
    {
        b.ToTable("StockMovements");
        b.HasKey(x => x.Id);
        b.Property(x => x.Reason).HasConversion<int>();
        b.Property(x => x.ReferenceType).HasMaxLength(64);
        b.Property(x => x.LotNumber).HasMaxLength(64);
        b.HasIndex(x => new { x.ArticleId, x.At });
        b.HasIndex(x => x.LotNumber);
        b.HasIndex(x => new { x.ReferenceType, x.ReferenceId });
    }
}
