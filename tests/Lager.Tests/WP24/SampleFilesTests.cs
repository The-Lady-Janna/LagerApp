using System.Net;
using Lager.Application.ImportExport;
using Lager.Tests.Infrastructure;
using static Lager.Tests.WP24.Wp24Support;

namespace Lager.Tests.WP24;

/// <summary>
/// Die Beispieldateien unter <c>docs/samples</c> sind keine Deko: sie müssen sich importieren lassen. Der Test spielt sie in der
/// dokumentierten Reihenfolge ein (Artikel, Bestand, Bestellungen) und prüft zum Schluss, dass sie - exportiert und wieder
/// importiert - nichts mehr ändern.
/// </summary>
public class SampleFilesTests : IAsyncLifetime
{
    private readonly LagerApiFactory _factory = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    private static byte[] Sample(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lager.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllBytes(Path.Combine(dir!.FullName, "docs", "samples", name));
    }

    [Fact]
    public async Task The_sample_files_import_in_the_documented_order_and_are_idempotent()
    {
        var w = new WorldBuilder(_factory);
        var admin = await w.AdminAsync();
        var site = await w.AddSiteAsync();
        foreach (var (code, x) in new[] { ("A-01-1", 500), ("A-01-2", 1500), ("A-01-3", 2500), ("B-02-1", 3500), ("B-02-2", 4500), ("B-02-3", 5500) })
            await w.AddBinAsync(site, code: code, x: x);

        // 1. Artikel
        var articles = Sample("articles.csv");
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, articles[..3]);                                 // die Beispieldateien sind wie der Export: UTF-8 mit BOM
        var firstArticles = await Run(admin, "articles", articles);
        Assert.Equal((5, 0, 0), (firstArticles.Created, firstArticles.Updated, firstArticles.ErrorCount));
        Assert.Empty(firstArticles.Warnings);
        var seasonal = (await ArticlesAsync(admin)).Single(a => a.Sku == "DEMO-1004");
        Assert.Equal((new DateTime(2026, 10, 1), new DateTime(2026, 12, 31)), (seasonal.ValidFrom!.Value.Date, seasonal.ValidUntil!.Value.Date));
        Assert.Equal("036000291452", seasonal.Gtin);

        // 2. Bestand: nur die Plätze, die es gibt (A-01-1 ... B-02-3)
        var stock = await Run(admin, "stock", Sample("stock.csv"));
        Assert.Equal((6, 0, 0), (stock.Created, stock.Updated, stock.ErrorCount));
        Assert.Empty(await w.LedgerViolationsAsync());

        // 3. Bestellungen
        var orders = await Run(admin, "orders", Sample("orders.csv"));
        Assert.Equal((3, 0, 0), (orders.Created, orders.Updated, orders.ErrorCount));

        // dieselben Dateien noch einmal: Artikel und Bestand ändern nichts
        var articlesAgain = await Run(admin, "articles", articles);
        Assert.Equal((0, 0, 5), (articlesAgain.Created, articlesAgain.Updated, articlesAgain.Unchanged));
        var stockAgain = await Run(admin, "stock", Sample("stock.csv"));
        Assert.Equal((0, 0, 6), (stockAgain.Created, stockAgain.Updated, stockAgain.Unchanged));

        // Bestellungen werden nie geändert: DEMO-B-1001 nennt dieselbe externe Referenz (eine Wiederholung: unverändert), die beiden
        // anderen Nummern gibt es schon (Fehler)
        using var upload = Upload(Sample("orders.csv"));
        var ordersAgain = await ReadResultAsync(await PostImportAsync(admin, "orders", upload, dryRun: false));
        Assert.Equal((0, 0, 1, 2), (ordersAgain.Created, ordersAgain.Updated, ordersAgain.Unchanged, ordersAgain.ErrorCount));
        Assert.All(ordersAgain.Errors, e => Assert.Equal("duplicate_order_number", e.Code));
    }

    /// <summary>Übernimmt eine Beispieldatei und verlangt, dass der Server sie annimmt.</summary>
    private static async Task<ImportResult> Run(HttpClient admin, string kind, byte[] file)
    {
        using var content = Upload(file, kind + ".csv");
        var response = await PostImportAsync(admin, kind, content, dryRun: false);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadResultAsync(response);
    }
}
