using Lager.Application.Reports;
using Lager.Domain.PickLists;
using Lager.Domain.Stock;

namespace Lager.Tests.WP10;

/// <summary>
/// Berichts-Gateway und ReportService gegen das echte EF-Modell (In-Memory-SQLite): Auswertungen stützen sich
/// auf tatsächlich gepickte Mengen statt auf Planmengen, Dead-Stock kennt die Lagerplätze und ignoriert
/// Umlagerungen, und die Bewertung bleibt bei einer Umlagerung mit geändertem Preis gleich.
/// </summary>
public class ReportQueryGatewayTests
{
    private static readonly DateTime Now = DateTime.UtcNow;

    private sealed record Scene(Guid Bin1, Guid Bin2, Guid ArticleA, Guid ArticleB);

    /// <summary>
    /// Picklisten: PL-1 fertig (A: soll 10, gepackt 3; B: soll 5, gepackt 0), PL-2 "Picked" ohne Bestätigung (A: soll 2),
    /// PL-3 offen (A: soll 100), PL-4 in Arbeit (B: soll 50), PL-5 fertig, aber vor 60 Tagen (A: soll 7, gepackt 7).
    /// </summary>
    private static async Task<Scene> SeedPickListsAsync(Wp10Db db)
    {
        var bin1 = Wp10.NewBin("BIN-1", 1000, 0);
        var bin2 = Wp10.NewBin("BIN-2", 2000, 0);
        var a = Wp10.NewArticle("SKU-A");
        var b = Wp10.NewArticle("SKU-B");
        await db.SaveAsync(bin1, bin2, a, b);

        await db.AddPickListAsync(Wp10.NewPickList("PL-1", PickListStatus.Completed,
            (a.Id, bin1.Id, 10, 3), (b.Id, bin2.Id, 5, 0)), Now.AddMinutes(-10));
        await db.AddPickListAsync(Wp10.NewPickList("PL-2", PickListStatus.Picked,
            (a.Id, bin1.Id, 2, null)), Now.AddMinutes(-5));
        await db.AddPickListAsync(Wp10.NewPickList("PL-3", PickListStatus.Pending,
            (a.Id, bin1.Id, 100, null)), Now.AddMinutes(-4));
        await db.AddPickListAsync(Wp10.NewPickList("PL-4", PickListStatus.InProgress,
            (b.Id, bin2.Id, 50, null)), Now.AddMinutes(-3));
        await db.AddPickListAsync(Wp10.NewPickList("PL-5", PickListStatus.Completed,
            (a.Id, bin1.Id, 7, 7)), Now.AddDays(-60));
        return new Scene(bin1.Id, bin2.Id, a.Id, b.Id);
    }

    [Fact]
    public async Task Picked_items_contain_only_actual_picks_within_the_window()
    {
        using var db = new Wp10Db();
        var scene = await SeedPickListsAsync(db);

        var picked = await db.Gateway.PickedItemsInRangeAsync(Now.AddDays(-30), Now.AddMinutes(1), CancellationToken.None);

        // PL-1/A (Ist 3) und PL-2/A (Picked, ohne Bestätigung = Soll 2). Nicht: B mit Ist 0, offene/laufende Listen, alte Liste.
        Assert.Equal(2, picked.Count);
        Assert.All(picked, p => Assert.Equal(scene.ArticleA, p.ArticleId));
        Assert.Equal(new[] { 2, 3 }, picked.Select(p => p.Quantity).OrderBy(q => q).ToArray());
        Assert.All(picked, p => Assert.Equal(scene.Bin1, p.BinId));
    }

    [Fact]
    public async Task Abc_top_articles_and_heatmap_use_picked_quantities_not_planned_ones()
    {
        using var db = new Wp10Db();
        var scene = await SeedPickListsAsync(db);
        var service = new ReportService(db.Gateway);

        var abc = await service.AbcAnalysisAsync(30);
        var article = Assert.Single(abc); // B wurde nie tatsächlich gepickt (Ist 0 bzw. Liste nicht gepackt)
        Assert.Equal(scene.ArticleA, article.ArticleId);
        Assert.Equal(5, article.TotalQuantity); // 3 + 2, nicht 10 + 2 + 100
        Assert.Equal(2, article.PickCount);
        Assert.Equal("A", article.Class);
        Assert.Equal(100m, article.SharePercent);

        var heat = await service.BinHeatmapAsync(30);
        var hot = Assert.Single(heat);
        Assert.Equal("BIN-1", hot.BinCode);
        Assert.Equal(2, hot.PickCount);

        var dashboard = await service.DashboardAsync(30);
        var top = Assert.Single(dashboard.TopArticles);
        Assert.Equal("SKU-A", top.Sku);
        Assert.Equal(5, top.TotalQuantity);
        Assert.Equal(5, dashboard.PickListsPerDay.Sum(p => p.Count)); // Picklisten (Zeitraum) zählt nach Anlagedatum: alle 5, alle Status
    }

    [Fact]
    public async Task Dead_stock_reports_location_count_and_ignores_replenishment_moves()
    {
        using var db = new Wp10Db();
        var bin1 = Wp10.NewBin("BIN-1", 1000, 0);
        var bin2 = Wp10.NewBin("BIN-2", 2000, 0);
        var dead = Wp10.NewArticle("SKU-DEAD");
        var alive = Wp10.NewArticle("SKU-ALIVE");
        var empty = Wp10.NewArticle("SKU-EMPTY");
        await db.SaveAsync(bin1, bin2, dead, alive, empty,
            new StockItem(dead.Id, bin1.Id, 6), new StockItem(dead.Id, bin2.Id, 4),
            new StockItem(alive.Id, bin1.Id, 5));

        // "Dead": letzter Wareneingang vor 100 Tagen, gestern nur ein Nachschub (Out+In) - das ist keine Bewegung.
        await db.AddMovementAsync(dead.Id, bin1.Id, 10, 100, StockMovementReason.Inbound, Now.AddDays(-100));
        await db.AddMovementAsync(dead.Id, bin1.Id, -4, 100, StockMovementReason.ReplenishmentOut, Now.AddDays(-1));
        await db.AddMovementAsync(dead.Id, bin2.Id, 4, 100, StockMovementReason.ReplenishmentIn, Now.AddDays(-1));
        // "Alive": alter Eingang, aber ein Pick gestern.
        await db.AddMovementAsync(alive.Id, bin1.Id, 8, 100, StockMovementReason.Inbound, Now.AddDays(-100));
        await db.AddMovementAsync(alive.Id, bin1.Id, -3, 100, StockMovementReason.Pick, Now.AddDays(-1));

        var result = await new ReportService(db.Gateway).DeadStockAsync(90);

        var row = Assert.Single(result); // "empty" hat keinen Bestand, "alive" bewegt sich
        Assert.Equal("SKU-DEAD", row.Sku);
        Assert.Equal(10, row.TotalQuantity);
        Assert.Equal(2, row.LocationCount); // war fest 1
        Assert.InRange(row.DaysSinceLastMovement!.Value, 99, 100);
    }

    [Fact]
    public async Task Valuation_is_unchanged_by_a_replenishment_after_a_price_change()
    {
        using var db = new Wp10Db();
        var reserve = Wp10.NewBin("RES", 1000, 0, type: Lager.Domain.Warehouse.BinType.Reserve);
        var hot = Wp10.NewBin("HOT", 2000, 0, type: Lager.Domain.Warehouse.BinType.HotPick);
        var article = Wp10.NewArticle("SKU-1", priceCents: 120); // Preis steigt von 1,00 auf 1,20 EUR
        var task = Guid.NewGuid();
        await db.SaveAsync(reserve, hot, article, new StockItem(article.Id, hot.Id, 10));
        await db.AddMovementAsync(article.Id, reserve.Id, 10, 100, StockMovementReason.Inbound, Now.AddDays(-10));
        await db.AddMovementAsync(article.Id, reserve.Id, -10, 120, StockMovementReason.ReplenishmentOut, Now.AddDays(-1), task);
        await db.AddMovementAsync(article.Id, hot.Id, 10, 120, StockMovementReason.ReplenishmentIn, Now.AddDays(-1), task);

        var valuation = await new ReportService(db.Gateway).StockValuationAsync();

        Assert.Equal(1000, valuation.TotalValueCents); // vorher 1200
        var line = Assert.Single(valuation.Lines);
        Assert.Equal(0, line.FallbackQuantity);
        Assert.Equal(0, valuation.FallbackValueCents);
        Assert.Equal("EUR", valuation.Currency);
    }

    [Fact]
    public async Task Valuation_flags_stock_without_ledger_history_as_fallback()
    {
        using var db = new Wp10Db();
        var bin = Wp10.NewBin("BIN-1", 1000, 0);
        var article = Wp10.NewArticle("SKU-1", priceCents: 250);
        await db.SaveAsync(bin, article, new StockItem(article.Id, bin.Id, 4));

        var valuation = await new ReportService(db.Gateway).StockValuationAsync();

        Assert.Equal(1000, valuation.TotalValueCents);
        Assert.Equal(1000, valuation.FallbackValueCents);
        Assert.Equal(4, Assert.Single(valuation.Lines).FallbackQuantity);
    }
}
