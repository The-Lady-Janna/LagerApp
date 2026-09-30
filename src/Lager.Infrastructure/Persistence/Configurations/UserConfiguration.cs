using Lager.Domain.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lager.Infrastructure.Persistence.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("Users");
        b.HasKey(x => x.Id);
        b.Property(x => x.Username).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.Username).IsUnique();
        b.Property(x => x.Email).HasMaxLength(256);
        b.Property(x => x.DisplayName).HasMaxLength(128);
        b.Property(x => x.PasswordHash).HasMaxLength(256).IsRequired();
        b.Property(x => x.Roles).HasConversion<int>();
    }
}
