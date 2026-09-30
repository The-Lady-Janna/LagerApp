using Lager.Domain.PickLists;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lager.Infrastructure.Persistence.Configurations;

public class PickListConfiguration : IEntityTypeConfiguration<PickList>
{
    public void Configure(EntityTypeBuilder<PickList> b)
    {
        b.ToTable("PickLists");
        b.HasKey(x => x.Id);
        b.Property(x => x.PickListNumber).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.PickListNumber).IsUnique();
        b.Property(x => x.Status).HasConversion<int>();
        b.Property(x => x.AssignedTo).HasMaxLength(128);
        b.Property(x => x.WaypointsJson)
            .HasColumnName("WaypointsJson")
            .HasColumnType("TEXT")
            .IsRequired(false);
        b.Ignore(x => x.Waypoints);

        b.HasMany(x => x.Items)
            .WithOne()
            .HasForeignKey(i => i.PickListId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Navigation(x => x.Items).Metadata.SetField("_items");
        b.Navigation(x => x.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public class PickItemConfiguration : IEntityTypeConfiguration<PickItem>
{
    public void Configure(EntityTypeBuilder<PickItem> b)
    {
        b.ToTable("PickItems");
        b.HasKey(x => x.Id);
        b.HasOne<Lager.Domain.Orders.Order>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Lager.Domain.Orders.OrderLine>().WithMany().HasForeignKey(x => x.OrderLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Lager.Domain.Articles.Article>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Lager.Domain.Warehouse.StorageLocation>().WithMany().HasForeignKey(x => x.StorageLocationId).OnDelete(DeleteBehavior.Restrict);
        // ListAsync paths and join lookups.
        b.HasIndex(x => x.PickListId);
        b.HasIndex(x => x.OrderId);
    }
}

public class PickListSequenceConfiguration : IEntityTypeConfiguration<Lager.Infrastructure.Persistence.PickListSequence>
{
    public void Configure(EntityTypeBuilder<Lager.Infrastructure.Persistence.PickListSequence> b)
    {
        b.ToTable("PickListSequence");
        b.HasKey(x => x.Id);
        b.Property(x => x.NextValue);
    }
}
