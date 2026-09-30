using Lager.Domain.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lager.Infrastructure.Persistence.Configurations;

public class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> b)
    {
        b.ToTable("Suppliers");
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(32).IsRequired();
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.Name).HasMaxLength(256).IsRequired();
        b.Property(x => x.ContactEmail).HasMaxLength(256);
        b.Property(x => x.ContactPhone).HasMaxLength(64);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.Currency).HasMaxLength(3);
    }
}
