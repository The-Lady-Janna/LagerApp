using Lager.Contracts.Returns;
using Lager.Domain.Orders;
using Lager.Domain.Returns;
using Lager.Domain.Stock;
using Microsoft.EntityFrameworkCore;

namespace Lager.Tests.WP14;

/// <summary>
/// Retouren: die Menge wird gegen die gelieferte Menge der Bestellung geprüft (frühere Retouren zählen mit), das
/// QC-Ergebnis steuert die Buchung - nur A-Ware erhöht den Verkaufsbestand, B-Ware und Defekt gehen als
/// Sperr-/Ausschussbuchung ins Ledger (netto 0) - und das QC-Ergebnis wird nur als Name akzeptiert.
/// </summary>
public class ReturnFlowTests : IClassFixture<StockApiFixture>
{
    private readonly StockWorld _w;

    public ReturnFlowTests(StockApiFixture fixture) => _w = fixture.World;

    private static readonly DateTime Expiry = StockWorld.InDays(120);

    private Task<ReturnShipmentDto> CreateAsync(Guid? orderId, params (Guid Article, int Quantity, string? Lot)[] lines) =>
        _w.ReturnsAsync(s => s.CreateAsync(new CreateReturnShipmentRequest(orderId, null, null,
            lines.Select(l => new CreateReturnLineRequest(l.Article, l.Quantity, l.Lot)).ToList())));

    [Fact]
    public async Task A_return_above_the_delivered_quantity_is_rejected_and_creates_nothing()
    {
        var article = await _w.AddArticleAsync();
        var order = await _w.AddOrderAsync(OrderStatus.Shipped, (article, 2));
        var before = await _w.DbAsync(db => db.ReturnShipments.CountAsync(r => r.OrderId == order));

        var ex = await ErrorAssert.ThrowsWithCodeAsync<InvalidOperationException>("return_quantity_exceeded",
            () => CreateAsync(order, (article, 100, null)));

        Assert.Contains("100", ex.Message);
        Assert.Equal(before, await _w.DbAsync(db => db.ReturnShipments.CountAsync(r => r.OrderId == order)));
        // genau die gelieferte Menge geht
        Assert.Equal("Draft", (await CreateAsync(order, (article, 2, null))).Status);
    }

    [Fact]
    public async Task Earlier_returns_of_the_same_order_count_against_the_delivered_quantity()
    {
        var article = await _w.AddArticleAsync();
        var order = await _w.AddOrderAsync(OrderStatus.Packed, (article, 5));
        var first = await CreateAsync(order, (article, 3, null));

        // 3 + 3 > 5; zusätzliche Zeilen derselben Retoure zählen ebenso
        await ErrorAssert.ThrowsWithCodeAsync<InvalidOperationException>("return_quantity_exceeded", () => CreateAsync(order, (article, 3, null)));
        await ErrorAssert.ThrowsWithCodeAsync<InvalidOperationException>("return_quantity_exceeded",
            () => _w.ReturnsAsync(s => s.AddLineAsync(first.Id, new AddReturnLineRequest(article, 3))));
        await _w.ReturnsAsync(s => s.AddLineAsync(first.Id, new AddReturnLineRequest(article, 2)));   // 3 + 2 = 5 ist erlaubt

        // eine stornierte Retoure gibt ihre Menge frei
        Assert.True(await _w.ReturnsAsync(s => s.CancelAsync(first.Id)));
        Assert.Equal("Draft", (await CreateAsync(order, (article, 5, null))).Status);
    }

    [Fact]
    public async Task The_return_lines_must_belong_to_a_delivered_order()
    {
        var article = await _w.AddArticleAsync();
        var foreign = await _w.AddArticleAsync();
        var delivered = await _w.AddOrderAsync(OrderStatus.Shipped, (article, 2));
        var notYet = await _w.AddOrderAsync(OrderStatus.New, (article, 2));

        await ErrorAssert.ThrowsWithCodeAsync<InvalidOperationException>("return_article_not_on_order", () => CreateAsync(delivered, (foreign, 1, null)));
        await ErrorAssert.ThrowsWithCodeAsync<InvalidOperationException>("return_order_not_delivered", () => CreateAsync(notYet, (article, 1, null)));
        await ErrorAssert.ThrowsWithCodeAsync<KeyNotFoundException>("order_not_found", () => CreateAsync(Guid.NewGuid(), (article, 1, null)));
        await ErrorAssert.ThrowsWithCodeAsync<KeyNotFoundException>("article_not_found", () => CreateAsync(delivered, (Guid.NewGuid(), 1, null)));
    }

    [Fact]
    public async Task For_a_bundle_order_the_components_can_be_returned_up_to_the_delivered_amount_but_not_the_bundle_itself()
    {
        var component = await _w.AddArticleAsync();
        var bundle = await _w.AddBundleAsync((component, 3));
        var order = await _w.AddOrderAsync(OrderStatus.Shipped, (bundle, 2));   // 2 Bundles = 6 Komponenten

        await ErrorAssert.ThrowsWithCodeAsync<InvalidOperationException>("return_quantity_exceeded", () => CreateAsync(order, (component, 7, null)));
        await ErrorAssert.ThrowsWithCodeAsync<InvalidOperationException>("return_bundle_not_allowed", () => CreateAsync(order, (bundle, 1, null)));
        Assert.Single((await CreateAsync(order, (component, 6, null))).Lines);
    }

    [Fact]
    public async Task Only_sellable_returns_raise_the_sales_stock_B_grade_and_defects_are_booked_as_blocked_and_scrap()
    {
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync(priceCents: 80);
        await _w.AddStockAsync(article, bin, 10);
        var ret = await CreateAsync(null, (article, 4, "LOT-R"), (article, 3, "LOT-R"), (article, 2, "LOT-R"), (article, 1, null));
        await _w.ReturnsAsync(s => s.AddLineAsync(ret.Id, new AddReturnLineRequest(article, 5, "LOT-E")));
        ret = (await _w.ReturnsAsync(s => s.GetAsync(ret.Id)))!;
        var byQuantity = ret.Lines.ToDictionary(l => l.Quantity);

        async Task QcAsync(int quantity, string result, Guid? targetBin = null) =>
            await _w.ReturnsAsync(s => s.SetQcAsync(ret.Id, byQuantity[quantity].Id, new SetQcRequest(result, targetBin, "geprüft")));
        await QcAsync(4, "Sellable", bin.Id);   // A-Ware -> zurück in den Bestand
        await QcAsync(3, "bgrade");             // B-Ware (Schreibweise egal)
        await QcAsync(2, "Defect");
        await QcAsync(1, "Destroy");
        await QcAsync(5, "Sellable", bin.Id);   // mit anderer Charge
        var stockBefore = await _w.QuantityAsync(article);

        var processed = (await _w.ReturnsAsync(s => s.ProcessAsync(ret.Id)))!;

        Assert.Equal("Processed", processed.Status);
        // Verkaufsbestand: nur die 4 + 5 A-Ware, nicht die 3 B-Ware und nicht die 2 + 1 Defekt/Vernichtung
        Assert.Equal(stockBefore + 4 + 5, await _w.QuantityAsync(article));

        var movements = await _w.MovementsAsync(article);
        Assert.Equal(2, movements.Count(m => m.Reason == StockMovementReason.Return && m.QuantityDelta > 0));
        var sellableLot = movements.Single(m => m.Reason == StockMovementReason.Return && m.LotNumber == "LOT-E");
        Assert.Equal((5, bin.Id, 80), (sellableLot.QuantityDelta, sellableLot.BinId, sellableLot.UnitCostCents));
        Assert.Equal("ReturnShipment", sellableLot.ReferenceType);

        // B-Ware: Zugang und Sperrung (ReturnB), Charge erhalten - netto 0
        var blocked = movements.Where(m => m.Reason == StockMovementReason.ReturnB).ToList();
        Assert.Equal(new[] { 3, -3 }, blocked.Select(m => m.QuantityDelta).OrderByDescending(d => d));
        Assert.All(blocked, m => Assert.Equal("LOT-R", m.LotNumber));
        // Defekt und Vernichtung: Ausschuss (ReturnScrap), je Zeile ein Zugang und ein Abgang
        var scrap = movements.Where(m => m.Reason == StockMovementReason.ReturnScrap).ToList();
        Assert.Equal(4, scrap.Count);
        Assert.Equal(0, scrap.Sum(m => m.QuantityDelta));
        Assert.Equal(0, blocked.Sum(m => m.QuantityDelta));
        Assert.All(movements.Where(m => m.Reason is StockMovementReason.ReturnB or StockMovementReason.ReturnScrap),
            m => Assert.Equal(ret.Id, m.ReferenceId));
    }

    [Fact]
    public async Task A_sellable_return_of_a_known_lot_uses_the_lot_row_with_its_expiry()
    {
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync();
        await _w.AddStockAsync(article, bin, 10, "LOT-K", Expiry);
        var ret = await CreateAsync(null, (article, 2, "LOT-K"));
        await _w.ReturnsAsync(s => s.SetQcAsync(ret.Id, ret.Lines.Single().Id, new SetQcRequest("Sellable", bin.Id, null)));

        await _w.ReturnsAsync(s => s.ProcessAsync(ret.Id));

        // MHD der Retoure unbekannt: die schon geführte Charge behält ihr MHD, keine zweite Zeile
        var row = Assert.Single(await _w.RowsAsync(article, bin));
        Assert.Equal((12, Expiry), (row.Quantity, row.ExpiryDate));
        Assert.Equal(Expiry, Assert.Single(await _w.MovementsAsync(article)).ExpiryDate);

        // Dieselbe Charge kommt in einen ANDEREN Lagerplatz zurück (dort noch keine Zeile) und als B-Ware: eine Charge
        // hat genau ein MHD - die neue Zeile und die Sperrbuchung tragen es, FEFO stellt die Ware nicht ans Ende.
        var other = await _w.AddBinAsync(await _w.AddSiteAsync());
        var second = await CreateAsync(null, (article, 4, "LOT-K"), (article, 1, "LOT-K"));
        await _w.ReturnsAsync(s => s.SetQcAsync(second.Id, second.Lines[0].Id, new SetQcRequest("Sellable", other.Id, null)));
        await _w.ReturnsAsync(s => s.SetQcAsync(second.Id, second.Lines[1].Id, new SetQcRequest("BGrade", null, null)));
        await _w.ReturnsAsync(s => s.ProcessAsync(second.Id));

        var moved = Assert.Single(await _w.RowsAsync(article, other));
        Assert.Equal((4, "LOT-K", Expiry), (moved.Quantity, moved.LotNumber, moved.ExpiryDate));
        Assert.All((await _w.MovementsAsync(article)).Where(m => m.LotNumber == "LOT-K"), m => Assert.Equal(Expiry, m.ExpiryDate));
    }

    [Fact]
    public async Task Blocked_and_scrap_bookings_find_a_ledger_bin_without_a_target_bin()
    {
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync();
        await _w.AddStockAsync(article, bin, 7);
        var soldOut = await _w.AddArticleAsync();           // kein Bestand irgendwo
        var ret = await CreateAsync(null, (article, 1, null), (soldOut, 1, null));
        foreach (var line in ret.Lines)
            await _w.ReturnsAsync(s => s.SetQcAsync(ret.Id, line.Id, new SetQcRequest("Defect", null, null)));

        await _w.ReturnsAsync(s => s.ProcessAsync(ret.Id));

        // Platz mit dem Bestand des Artikels; ohne Bestand irgendein vorhandener Lagerplatz - die Retoure lässt sich abschließen
        Assert.All(await _w.MovementsAsync(article), m => Assert.Equal(bin.Id, m.BinId));
        Assert.Equal(2, (await _w.MovementsAsync(soldOut)).Count);
        Assert.Equal(7, await _w.QuantityAsync(article));
        Assert.Equal(0, await _w.QuantityAsync(soldOut));
    }

    [Theory]
    [InlineData("99")]
    [InlineData("0x1")]
    [InlineData("")]
    [InlineData("Sellable,BGrade")]
    [InlineData("Unbekannt")]
    public async Task The_QC_result_is_accepted_only_as_a_name_never_as_a_number(string result)
    {
        var article = await _w.AddArticleAsync();
        var ret = await CreateAsync(null, (article, 1, null));

        await ErrorAssert.ThrowsWithCodeAsync<ArgumentException>("invalid_qc_result",
            () => _w.ReturnsAsync(s => s.SetQcAsync(ret.Id, ret.Lines.Single().Id, new SetQcRequest(result, null, null))));

        // die Zeile ist unverändert Pending: ein undefinierter Wert hätte sie still aus dem Prozess genommen
        var line = (await _w.ReturnsAsync(s => s.GetAsync(ret.Id)))!.Lines.Single();
        Assert.Equal(nameof(QcResult.Pending), line.QcResult);
    }

    [Fact]
    public async Task Processing_twice_or_with_pending_lines_books_nothing()
    {
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync();
        var ret = await CreateAsync(null, (article, 3, null));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _w.ReturnsAsync(s => s.ProcessAsync(ret.Id)));   // noch Pending
        await _w.ReturnsAsync(s => s.SetQcAsync(ret.Id, ret.Lines.Single().Id, new SetQcRequest("Sellable", bin.Id, null)));
        await _w.ReturnsAsync(s => s.ProcessAsync(ret.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _w.ReturnsAsync(s => s.ProcessAsync(ret.Id)));   // Doppelklick

        Assert.Equal(3, await _w.QuantityAsync(article));
        Assert.Single(await _w.MovementsAsync(article));
        Assert.Null(await _w.ReturnsAsync(s => s.ProcessAsync(Guid.NewGuid())));
    }
}
