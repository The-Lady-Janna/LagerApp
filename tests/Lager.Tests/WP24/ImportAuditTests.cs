using System.Net;
using System.Net.Http.Json;
using Lager.Application.ImportExport;
using Lager.Contracts.Articles;
using Lager.Infrastructure.Persistence;
using Lager.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using static Lager.Tests.WP24.Wp24Support;

namespace Lager.Tests.WP24;

/// <summary>
/// Der Audit-Trail beim Import: EIN Sammel-Eintrag statt einer Zeile je Datensatz (AuditingInterceptor im Sammelmodus), das normale
/// Auditing bleibt unverändert, und die Übernahme ist atomar (scheitert das Ende, bleibt nichts zurück).
/// </summary>
public class ImportAuditTests : IClassFixture<ImportExportFixture>
{
    private readonly ImportExportFixture _fx;

    public ImportAuditTests(ImportExportFixture fixture) => _fx = fixture;

    [Fact]
    public async Task An_import_of_1000_articles_writes_exactly_one_audit_entry()
    {
        var prefix = WorldBuilder.Unique("BULK");
        var lines = new[] { "Sku;Name;LengthMm;WidthMm;HeightMm" }
            .Concat(Enumerable.Range(1, 1000).Select(i => $"{prefix}-{i:D4};Artikel {i};10;10;10")).ToArray();
        var auditBefore = await AuditCountAsync(_fx.W);
        var articleAuditBefore = await AuditCountAsync(_fx.W, "Article");

        var result = await ImportAsync(_fx.Admin, "articles", Csv(lines), dryRun: false);

        Assert.True(result.Applied);
        Assert.Equal((1000, 0), (result.Created, result.ErrorCount));
        Assert.Equal(1000, await ArticleCountAsync(_fx.W, prefix));

        // genau ein neuer Eintrag im Audit-Trail, und keiner davon ist eine Einzelzeile eines Artikels
        Assert.Equal(auditBefore + 1, await AuditCountAsync(_fx.W));
        Assert.Equal(articleAuditBefore, await AuditCountAsync(_fx.W, "Article"));
        var entry = Assert.Single(await AuditEntriesAsync(_fx.W, ImportService.AuditEntityType), e => e.EntityId == result.ImportId.ToString());
        Assert.Equal((ImportService.AuditOperation, "admin"), (entry.Operation, entry.User));
        Assert.Contains("CSV-Import: 1000 Artikel, Nutzer admin", entry.ChangesJson);
        Assert.Contains("\"Article\":{\"Added\":1000}", entry.ChangesJson);
        Assert.Contains("\"file\":\"import.csv\"", entry.ChangesJson);
    }

    [Fact]
    public async Task Stock_and_order_imports_write_one_summary_entry_each_with_their_own_unit()
    {
        var a = await _fx.W.AddArticleAsync(WorldBuilder.Unique("AUD"));
        var b = await _fx.W.AddArticleAsync(WorldBuilder.Unique("AUD"));
        var bin = _fx.Warehouse.PickA;
        var before = await AuditCountAsync(_fx.W);

        var stock = await ImportAsync(_fx.Admin, "stock", Csv("Sku;Location;Quantity", $"{a.Sku};{bin.Code};5", $"{b.Sku};{bin.Code};6", $"{a.Sku};{_fx.Warehouse.PickB.Code};7"), dryRun: false);
        var orderPrefix = WorldBuilder.Unique("AUDO");
        var orders = await ImportAsync(_fx.Admin, "orders", Csv("OrderNumber;Sku;Quantity", $"{orderPrefix}-1;{a.Sku};1", $"{orderPrefix}-2;{b.Sku};1"), dryRun: false);

        Assert.Equal(before + 2, await AuditCountAsync(_fx.W));
        var entries = await AuditEntriesAsync(_fx.W, ImportService.AuditEntityType);
        var stockEntry = Assert.Single(entries, e => e.EntityId == stock.ImportId.ToString());
        var orderEntry = Assert.Single(entries, e => e.EntityId == orders.ImportId.ToString());
        Assert.Contains("CSV-Import: 3 Bestandszeilen, Nutzer admin", stockEntry.ChangesJson);
        Assert.Contains("\"StockItem\":{\"Added\":3}", stockEntry.ChangesJson);
        Assert.Contains("CSV-Import: 2 Bestellungen, Nutzer admin", orderEntry.ChangesJson);
        Assert.Contains("\"Order\":{\"Added\":2}", orderEntry.ChangesJson);
    }

    [Fact]
    public async Task A_dry_run_and_an_import_without_changes_write_no_audit_entry()
    {
        var sku = WorldBuilder.Unique("NOAUD");
        var csv = Csv("Sku;Name;LengthMm;WidthMm;HeightMm", $"{sku};Einer;10;10;10");
        var before = await AuditCountAsync(_fx.W);

        await ImportAsync(_fx.Admin, "articles", csv, dryRun: true);
        Assert.Equal(before, await AuditCountAsync(_fx.W));

        await ImportAsync(_fx.Admin, "articles", csv, dryRun: false);
        Assert.Equal(before + 1, await AuditCountAsync(_fx.W));

        await ImportAsync(_fx.Admin, "articles", csv, dryRun: false);                            // schon auf dem Stand der Datei
        Assert.Equal(before + 1, await AuditCountAsync(_fx.W));
    }

    [Fact]
    public async Task Normal_saves_outside_an_import_are_still_audited_per_entity_and_the_batch_mode_does_not_leak()
    {
        // Erst ein Import (Sammelmodus an und wieder aus) ...
        await ImportAsync(_fx.Admin, "articles", Csv("Sku;Name;LengthMm;WidthMm;HeightMm", $"{WorldBuilder.Unique("LEAK")};Import;10;10;10"), dryRun: false);
        var importsBefore = await AuditCountAsync(_fx.W, ImportService.AuditEntityType);

        // ... dann die gewöhnlichen Wege: jede Entität bekommt ihre eigene Zeile wie zuvor.
        var article = await CreateArticleAsync(_fx.Admin, NewArticle(WorldBuilder.Unique("AUDN")));
        var put = await _fx.Admin.PutAsJsonAsync($"/api/articles/{article.Id}",
            new UpdateArticleRequest("Umbenannt", null, article.Dimensions, article.WeightGrams, article.Stacking));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var entries = (await AuditEntriesAsync(_fx.W, "Article")).Where(e => e.EntityId == article.Id.ToString()).OrderBy(e => e.At).ToList();
        Assert.Equal(new[] { "Added", "Modified" }, entries.Select(e => e.Operation).ToArray());
        Assert.All(entries, e => Assert.Equal("admin", e.User));
        Assert.Contains("Umbenannt", entries[1].ChangesJson);
        Assert.Equal(importsBefore, await AuditCountAsync(_fx.W, ImportService.AuditEntityType));  // kein Sammel-Eintrag zusätzlich
    }

    [Fact]
    public async Task If_the_commit_fails_nothing_of_the_import_remains_not_the_articles_and_not_the_audit_entry()
    {
        var prefix = WorldBuilder.Unique("ATOM");
        var csv = Csv(new[] { "Sku;Name;LengthMm;WidthMm;HeightMm" }
            .Concat(Enumerable.Range(1, 120).Select(i => $"{prefix}-{i:D3};Artikel {i};10;10;10")).ToArray());   // über 100: auch der Zwischenstand wurde geschrieben
        var auditBefore = await AuditCountAsync(_fx.W);

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ImportService>();
            var db = scope.ServiceProvider.GetRequiredService<LagerDbContext>();

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(
                ImportKind.Articles, Utf8.GetBytes(csv), new ImportOptions(DryRun: false, SkipErrors: false, CsvFormat.Semicolon, "atom.csv"),
                new FailingCommitTransactions(db)));
            Assert.Contains("Commit-Fehler", ex.Message);
        }

        Assert.Equal(0, await ArticleCountAsync(_fx.W, prefix));
        Assert.Equal(auditBefore, await AuditCountAsync(_fx.W));

        // derselbe Import mit funktionierender Transaktion (wie über die API) übernimmt dagegen alles
        var result = await ImportAsync(_fx.Admin, "articles", csv, dryRun: false);
        Assert.Equal(120, result.Created);
        Assert.Equal(120, await ArticleCountAsync(_fx.W, prefix));
    }

    [Fact]
    public async Task If_the_commit_fails_after_the_stock_was_booked_the_ledger_keeps_nothing()
    {
        var existing = await _fx.W.AddArticleAsync(WorldBuilder.Unique("MID"));
        var bin = _fx.Warehouse.PickA;
        var fresh = await _fx.W.AddArticleAsync(WorldBuilder.Unique("MID"));
        var csv = Csv("Sku;Location;Quantity", $"{existing.Sku};{bin.Code};5", $"{fresh.Sku};{bin.Code};6");
        var before = await AuditCountAsync(_fx.W);

        using (var scope = _fx.Factory.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<ImportService>();
            var db = scope.ServiceProvider.GetRequiredService<LagerDbContext>();
            // Der Bestand der ersten Zeile wird bereits geschrieben, dann bricht der Commit ab: das Ledger darf nichts davon behalten.
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.ImportAsync(
                ImportKind.Stock, Utf8.GetBytes(csv), new ImportOptions(false, false, CsvFormat.Semicolon), new FailingCommitTransactions(db)));
        }

        Assert.Empty(await _fx.W.StockRowsAsync(existing.Id));
        Assert.Empty(await _fx.W.MovementsAsync(existing.Id));
        Assert.Empty(await _fx.W.MovementsAsync(fresh.Id));
        Assert.Equal(before, await AuditCountAsync(_fx.W));
    }

    /// <summary>Eine echte Datenbank-Transaktion, deren Commit scheitert (ohne zu committen): Dispose rollt alles zurück.</summary>
    private sealed class FailingCommitTransactions : IImportTransactionFactory
    {
        private readonly LagerDbContext _db;

        public FailingCommitTransactions(LagerDbContext db) => _db = db;

        public async Task<IImportTransaction> BeginAsync(CancellationToken ct) =>
            new Transaction(_db, await _db.Database.BeginTransactionAsync(ct));

        private sealed class Transaction : IImportTransaction
        {
            private readonly LagerDbContext _db;
            private readonly IDbContextTransaction _inner;

            public Transaction(LagerDbContext db, IDbContextTransaction inner)
            {
                _db = db;
                _inner = inner;
            }

            public Task CommitAsync(CancellationToken ct) => throw new InvalidOperationException("Commit-Fehler (Test)");

            public void ReleaseTrackedEntities() => _db.ChangeTracker.Clear();

            public ValueTask DisposeAsync() => _inner.DisposeAsync();
        }
    }
}
