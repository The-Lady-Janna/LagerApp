using Lager.Application.Abstractions;
using Lager.Domain.Stock;
using Microsoft.EntityFrameworkCore;

namespace Lager.Tests.WP07;

/// <summary>
/// Lot-genaue Suche im StockRepository: ein (Artikel, Lagerplatz) kann mehrere Zeilen mit verschiedener Charge/MHD
/// haben. Die Suche ohne Charge darf sie nicht vermischen.
/// </summary>
public class StockRepositoryLotTests : IClassFixture<PickApiFixture>
{
    private readonly PickWorld _w;

    public StockRepositoryLotTests(PickApiFixture fixture) => _w = fixture.World;

    private static readonly DateTime ExpiryA = new(2027, 3, 1);
    private static readonly DateTime ExpiryB = new(2027, 6, 1);

    /// <summary>Ein Bin mit einer chargenlosen Zeile (5), Charge A (3) und Charge B (2).</summary>
    private async Task<(Guid Article, PickWorld.Bin Bin, Guid Plain, Guid LotA, Guid LotB)> ThreeRowsAsync()
    {
        var site = await _w.AddWarehouseAsync();
        var bin = await _w.AddBinAsync(site, PickWorld.Unique("BIN"));
        var article = await _w.AddArticleAsync();
        var plain = await _w.AddStockAsync(article, bin, 5);
        var lotB = await _w.AddStockAsync(article, bin, 2, "B", ExpiryB);
        var lotA = await _w.AddStockAsync(article, bin, 3, "A", ExpiryA);
        return (article, bin, plain, lotA, lotB);
    }

    [Fact]
    public async Task FindAsync_with_lot_finds_exactly_the_matching_row()
    {
        var (article, bin, plain, lotA, lotB) = await ThreeRowsAsync();
        using var scope = _w.NewScope();
        var repo = scope.Get<IStockRepository>();

        Assert.Equal(lotA, (await repo.FindAsync(article, bin.Id, "A", ExpiryA))!.Id);
        Assert.Equal(lotB, (await repo.FindAsync(article, bin.Id, "B", ExpiryB))!.Id);
        Assert.Equal(plain, (await repo.FindAsync(article, bin.Id, null, null))!.Id);

        // Charge wird normalisiert: leer und Leerraum sind "keine Charge", Ränder werden getrimmt
        Assert.Equal(plain, (await repo.FindAsync(article, bin.Id, "", null))!.Id);
        Assert.Equal(plain, (await repo.FindAsync(article, bin.Id, "   ", null))!.Id);
        Assert.Equal(lotA, (await repo.FindAsync(article, bin.Id, " A ", ExpiryA))!.Id);

        // "exakt": falsches MHD zur Charge, unbekannte Charge, fremder Lagerplatz -> keine Zeile
        Assert.Null(await repo.FindAsync(article, bin.Id, "A", ExpiryB));
        Assert.Null(await repo.FindAsync(article, bin.Id, "A", null));
        Assert.Null(await repo.FindAsync(article, bin.Id, "C", null));
        Assert.Null(await repo.FindAsync(article, Guid.NewGuid(), "A", ExpiryA));
    }

    [Fact]
    public async Task FindAsync_sees_a_row_that_was_added_but_not_saved_yet()
    {
        var site = await _w.AddWarehouseAsync();
        var bin = await _w.AddBinAsync(site, PickWorld.Unique("BIN"));
        var article = await _w.AddArticleAsync();
        using var scope = _w.NewScope();
        var repo = scope.Get<IStockRepository>();

        var added = new StockItem(article, bin.Id, 4, "NEU", ExpiryA);
        await repo.AddAsync(added);

        // Zwei Wareneingangszeilen derselben Charge in einer Lieferung dürfen keine Doppelzeile erzeugen.
        Assert.Same(added, await repo.FindAsync(article, bin.Id, "NEU", ExpiryA));
        Assert.Null(await repo.FindAsync(article, bin.Id, "ANDERE", ExpiryA));
    }

    [Fact]
    public async Task Legacy_FindAsync_prefers_the_row_without_lot_and_falls_back_to_any_row()
    {
        var (article, bin, plain, _, _) = await ThreeRowsAsync();
        using var scope = _w.NewScope();
        var repo = scope.Get<IStockRepository>();

        Assert.Equal(plain, (await repo.FindAsync(article, bin.Id))!.Id);

        // Ein Bin nur mit Chargenware: die Altlogik (Adjust, Nachschub, Inventur) findet weiter eine Zeile.
        var onlyLots = await _w.AddBinAsync(await _w.AddWarehouseAsync(), PickWorld.Unique("BIN"));
        var lotRow = await _w.AddStockAsync(article, onlyLots, 7, "X", ExpiryA);
        Assert.Equal(lotRow, (await repo.FindAsync(article, onlyLots.Id))!.Id);
        Assert.Null(await repo.FindAsync(article, Guid.NewGuid()));
    }

    [Fact]
    public async Task ListForBin_is_tracked_FEFO_sorted_and_skips_empty_rows()
    {
        var (article, bin, plain, lotA, lotB) = await ThreeRowsAsync();
        var empty = await _w.AddStockAsync(article, bin, 0, "LEER", new DateTime(2026, 1, 1));   // frühestes MHD, aber leer
        using var scope = _w.NewScope();
        var repo = scope.Get<IStockRepository>();

        var rows = await repo.ListForBinAsync(article, bin.Id);

        // frühestes MHD zuerst, ohne MHD zuletzt, leere Zeilen fehlen
        Assert.Equal(new[] { lotA, lotB, plain }, rows.Select(r => r.Id));
        Assert.DoesNotContain(rows, r => r.Id == empty);

        // tracked: eine Änderung wird beim SaveChanges geschrieben
        rows[0].Remove(1);
        await scope.Get<IUnitOfWork>().SaveChangesAsync();
        var reread = await _w.DbAsync(db => db.StockItems.AsNoTracking().SingleAsync(s => s.Id == lotA));
        Assert.Equal(2, reread.Quantity);
    }
}
