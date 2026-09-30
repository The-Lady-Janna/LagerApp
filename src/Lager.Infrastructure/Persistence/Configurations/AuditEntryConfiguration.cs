using Lager.Domain.Auditing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lager.Infrastructure.Persistence.Configurations;

public class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> b)
    {
        b.ToTable("AuditEntries");
        b.HasKey(x => x.Id);
        b.Property(x => x.EntityType).HasMaxLength(64).IsRequired();
        b.Property(x => x.EntityId).HasMaxLength(64).IsRequired();
        b.Property(x => x.Operation).HasMaxLength(16).IsRequired();
        b.Property(x => x.User).HasMaxLength(128);
        // Kein fester Spaltentyp: SQLite bildet unbegrenzte Strings als TEXT ab, MySQL als LONGTEXT (ein "TEXT" wäre
        // dort auf 64 KB begrenzt, große Diffs ließen SaveChanges scheitern).
        b.Property(x => x.ChangesJson);
        b.HasIndex(x => new { x.EntityType, x.EntityId, x.At });
        b.HasIndex(x => x.At);
    }
}
