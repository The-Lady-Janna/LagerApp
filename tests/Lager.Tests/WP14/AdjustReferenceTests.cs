using Lager.Contracts.Stock;
using Microsoft.EntityFrameworkCore;

namespace Lager.Tests.WP14;

/// <summary>
/// Vertrag mit der zentralen Fehlerabbildung (WP12): eine Korrektur für einen unbekannten Artikel oder Lagerplatz wird
/// nicht vorab mit einem eigenen Fehler abgewiesen, sondern scheitert beim Speichern am Fremdschlüssel
/// (DbUpdateException) - die API macht daraus 409 invalid_reference (siehe die WP12-Tests der Fehlerantworten).
/// </summary>
public class AdjustReferenceTests : IClassFixture<StockApiFixture>
{
    private readonly StockWorld _w;

    public AdjustReferenceTests(StockApiFixture fixture) => _w = fixture.World;

    [Fact]
    public async Task An_adjustment_for_an_unknown_article_and_bin_fails_at_the_foreign_key_and_books_nothing()
    {
        var unknownArticle = Guid.NewGuid();

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            _w.StockAsync(s => s.AdjustAsync(new AdjustStockRequest(unknownArticle, Guid.NewGuid(), 5, null, null))));

        Assert.Equal(0, await _w.QuantityAsync(unknownArticle));
        Assert.Empty(await _w.MovementsAsync(unknownArticle));
    }
}
