using Lager.Domain.Stock;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Lager.Infrastructure.Persistence.Configurations;

public class StockItemConfiguration : IEntityTypeConfiguration<StockItem>
{
    public void Configure(EntityTypeBuilder<StockItem> b)
    {
        b.ToTable("StockItems");
        b.HasKey(x => x.Id);
        b.Property(x => x.LotNumber).HasMaxLength(64);
        // Pro (Artikel, Lagerplatz, Charge) genau eine Bestandszeile. Legacy-Duplikate führt der Schritt
        // DedupeStockItemsStep vor dem Anlegen des Index zusammen. NULL-Chargen erfasst ein Unique-Index in SQLite und
        // MySQL nicht (NULLs gelten als verschieden): Die Bestandsbuchung muss leere Chargen auf null normalisieren
        // und eine vorhandene Zeile wiederverwenden (Find-or-Create).
        b.HasIndex(x => new { x.ArticleId, x.StorageLocationId, x.LotNumber }).IsUnique();
        b.HasIndex(x => x.ArticleId); // hot path: ListForArticleAsync
        b.HasIndex(x => x.StorageLocationId);
        b.HasIndex(x => x.ExpiryDate); // FEFO ordering
        b.HasOne<Lager.Domain.Articles.Article>().WithMany().HasForeignKey(x => x.ArticleId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Lager.Domain.Warehouse.StorageLocation>().WithMany().HasForeignKey(x => x.StorageLocationId).OnDelete(DeleteBehavior.Restrict);
    }
}
