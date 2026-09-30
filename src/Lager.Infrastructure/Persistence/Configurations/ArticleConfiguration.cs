using Lager.Domain.Articles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lager.Infrastructure.Persistence.Configurations;

public class ArticleConfiguration : IEntityTypeConfiguration<Article>
{
    public void Configure(EntityTypeBuilder<Article> b)
    {
        b.ToTable("Articles");
        b.HasKey(x => x.Id);
        b.Property(x => x.Sku).HasMaxLength(64).IsRequired();
        b.HasIndex(x => x.Sku).IsUnique();
        // GTIN/EAN (WP20): nur Ziffern, höchstens 14 Stellen, optional. Eindeutig über alle Artikel; NULL (keine GTIN) kollidiert
        // weder bei SQLite noch bei MySQL, ein Filter (nur nicht-NULL-Werte) ist daher nicht nötig. Legacy-Datenbanken bekommen
        // Spalte und Index vom Schritt AddArticleGtinStep (Index gleichen Namens; bei SQLite dort als partieller Index).
        b.Property(x => x.Gtin).HasMaxLength(Gtin.MaxLength);
        b.HasIndex(x => x.Gtin).IsUnique().HasDatabaseName("IX_Articles_Gtin");
        b.Property(x => x.Name).HasMaxLength(256).IsRequired();
        b.Property(x => x.Description).HasMaxLength(2000);
        b.Property(x => x.WeightGrams);
        b.Property(x => x.MinStock);
        b.Property(x => x.ReorderPoint);
        b.Property(x => x.MaxStock);
        b.Property(x => x.CreatedAt);
        b.Property(x => x.UpdatedAt);

        b.OwnsOne(x => x.Dimensions, d =>
        {
            d.Property(p => p.LengthMm).HasColumnName("LengthMm");
            d.Property(p => p.WidthMm).HasColumnName("WidthMm");
            d.Property(p => p.HeightMm).HasColumnName("HeightMm");
        });

        b.OwnsOne(x => x.Stacking, s =>
        {
            s.Property(p => p.IsStackable).HasColumnName("IsStackable");
            s.Property(p => p.StackingAxis).HasColumnName("StackingAxis").HasConversion<int>();
            s.Property(p => p.StackingIncrementMm).HasColumnName("StackingIncrementMm");
            s.Property(p => p.MaxStackCount).HasColumnName("MaxStackCount");
        });

        // Welle 5: Bundle-Komponenten, Alternativen, Saison
        b.HasMany(x => x.BundleComponents)
            .WithOne()
            .HasForeignKey(c => c.BundleArticleId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.BundleComponents).Metadata.SetField("_bundleComponents");
        b.Navigation(x => x.BundleComponents).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Ignore(x => x.IsBundle);
        b.Ignore(x => x.AlternativeSkus);
        b.Property(x => x.AlternativeSkusCsv).HasMaxLength(1000);

        // Bevorzugter Lieferant (optional). Fremdschlüssel (wie alle unten) nur für neu angelegte Datenbanken: Tabellen, die
        // der SchemaUpgrader für Legacy-DBs angelegt hat, haben keine FKs, ein Rebuild findet bewusst nicht statt.
        b.HasOne<Lager.Domain.Suppliers.Supplier>().WithMany().HasForeignKey(x => x.PrimarySupplierId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class BundleComponentConfiguration : IEntityTypeConfiguration<BundleComponent>
{
    public void Configure(EntityTypeBuilder<BundleComponent> b)
    {
        b.ToTable("BundleComponents");
        b.HasKey(x => x.Id);
        // Die Id vergibt die Domain (Entity.Id = Guid.NewGuid()). Ohne ValueGeneratedNever hält EF eine Komponente, die beim
        // Ändern eines geladenen Artikels neu in die Liste kommt, wegen des gesetzten Schlüssels für bereits vorhanden und versucht
        // ein UPDATE statt eines INSERT (DbUpdateConcurrencyException, HTTP 409): neue Komponenten ließen sich nur beim Anlegen speichern.
        b.Property(x => x.Id).ValueGeneratedNever();
        b.HasIndex(x => x.BundleArticleId);
        // Die Komponente selbst (BundleArticleId ist die Cascade-Beziehung oben): ein verwendeter Artikel lässt sich nicht löschen.
        b.HasOne<Lager.Domain.Articles.Article>().WithMany().HasForeignKey(x => x.ComponentArticleId).OnDelete(DeleteBehavior.Restrict);
    }
}
