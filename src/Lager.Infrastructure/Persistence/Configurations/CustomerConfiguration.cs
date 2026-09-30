using Lager.Domain.Customers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lager.Infrastructure.Persistence.Configurations;

public class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> b)
    {
        b.ToTable("Customers");
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(32).IsRequired();
        b.HasIndex(x => x.Code).IsUnique();
        b.Property(x => x.Name).HasMaxLength(256).IsRequired();
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.Phone).HasMaxLength(64);
        b.Property(x => x.Notes).HasMaxLength(1000);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.HasMany(x => x.Addresses)
            .WithOne()
            .HasForeignKey(a => a.CustomerId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Addresses).Metadata.SetField("_addresses");
        b.Navigation(x => x.Addresses).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public class CustomerAddressConfiguration : IEntityTypeConfiguration<CustomerAddress>
{
    public void Configure(EntityTypeBuilder<CustomerAddress> b)
    {
        b.ToTable("CustomerAddresses");
        b.HasKey(x => x.Id);
        b.Property(x => x.Kind).HasConversion<int>();
        b.Property(x => x.Label).HasMaxLength(64);
        b.Property(x => x.Street).HasMaxLength(256);
        b.Property(x => x.Street2).HasMaxLength(256);
        b.Property(x => x.Zip).HasMaxLength(16);
        b.Property(x => x.City).HasMaxLength(128);
        b.Property(x => x.Country).HasMaxLength(3);
        b.HasIndex(x => x.CustomerId);
    }
}
