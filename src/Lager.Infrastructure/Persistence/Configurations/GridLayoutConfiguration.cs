using Lager.Domain.Warehouse;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lager.Infrastructure.Persistence.Configurations;

/// <summary>
/// Raster-Layout (einfacher Layout-Editor): eine Zeile je Lager. Felder und erzeugte Objekte stehen als JSON in
/// <c>CellsJson</c> und <c>ManagedJson</c> (SQLite TEXT, MySQL LONGTEXT; ein volles Raster mit 100 x 100 Feldern sprengt TEXT).
/// Bestandsdatenbanken bekommen die Tabelle über <see cref="SchemaSteps.AddGridLayoutStep"/>.
/// </summary>
public class GridLayoutConfiguration : IEntityTypeConfiguration<GridLayout>
{
    public void Configure(EntityTypeBuilder<GridLayout> b)
    {
        b.ToTable("GridLayouts");
        b.HasKey(x => x.Id);
        b.HasIndex(x => x.WarehouseId).IsUnique();
        b.Property(x => x.CellsJson).IsRequired();
        b.Property(x => x.ManagedJson).IsRequired();
        b.Ignore(x => x.Cells);
        b.Ignore(x => x.Managed);
        // Zwei Manager speichern dasselbe Raster: der zweite bekommt 409 statt die Felder des ersten zu überschreiben.
        b.Property(x => x.ConcurrencyToken).IsConcurrencyToken();
        b.HasOne<Lager.Domain.Warehouse.Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId).OnDelete(DeleteBehavior.Cascade);
    }
}
