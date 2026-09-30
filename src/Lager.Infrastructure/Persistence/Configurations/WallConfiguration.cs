using Lager.Domain.Warehouse;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lager.Infrastructure.Persistence.Configurations;

public class WallConfiguration : IEntityTypeConfiguration<Wall>
{
    public void Configure(EntityTypeBuilder<Wall> b)
    {
        b.ToTable("Walls");
        b.HasKey(x => x.Id);
        b.Property(x => x.Label).HasMaxLength(128);
        b.Property(x => x.ThicknessMm);

        // PointsJson is the authoritative storage column for the polyline.
        // Nullable so EF doesn't crash on legacy rows where backfill hasn't run.
        // Kein fester Spaltentyp: SQLite = TEXT, MySQL = LONGTEXT (TEXT wäre dort auf 64 KB begrenzt).
        b.Property(x => x.PointsJson)
            .HasColumnName("PointsJson")
            .IsRequired(false);

        // Points/Start/End are computed in the domain — don't map them.
        b.Ignore(x => x.Points);
        b.Ignore(x => x.Start);
        b.Ignore(x => x.End);

        // Fremdschlüssel nur für neu angelegte Datenbanken (siehe SchemaUpgrader: kein Tabellen-Rebuild für Legacy-DBs).
        b.HasOne<Lager.Domain.Warehouse.Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => x.WarehouseId);
    }
}
