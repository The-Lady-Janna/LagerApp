using Lager.Application.Abstractions;
using Lager.Contracts.Inbound;
using Lager.Contracts.Purchasing;
using Lager.Domain.Purchasing;
using Lager.Domain.Stock;
using Microsoft.EntityFrameworkCore;

namespace Lager.Tests.WP14;

/// <summary>
/// Einkauf: Bestellung und Wareneingang sind verknüpft. "Wareneingang aus Bestellung" legt einen Entwurf aus den
/// offenen Mengen an, sein Buchen bucht den Bestand und schreibt die Bestellzeilen fort (Bestellung wird Received).
/// Bestellvorschläge ziehen offene Bestellmengen ab. Die Bestellnummern kommen aus dem atomaren Nummernkreis.
/// </summary>
public class PurchasingFlowTests : IClassFixture<StockApiFixture>
{
    private readonly StockWorld _w;

    public PurchasingFlowTests(StockApiFixture fixture) => _w = fixture.World;

    private static readonly DateTime Expiry = StockWorld.InDays(300);

    [Fact]
    public async Task Purchase_order_to_inbound_to_receive_books_the_stock_and_closes_the_order()
    {
        var supplier = await _w.AddSupplierAsync();
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var articleA = await _w.AddArticleAsync(priceCents: 100);
        var articleB = await _w.AddArticleAsync(priceCents: 100);
        var po = await _w.SentPurchaseOrderAsync(supplier, (articleA, 10, 333), (articleB, 5, null));

        var draft = (await _w.InboundAsync(s => s.CreateFromPurchaseOrderAsync(po.Id, new CreateInboundFromPurchaseOrderRequest(bin.Id))))!;

        // Entwurf aus den offenen Mengen, mit Bestellbezug und dem Bestellpreis; Charge/MHD ergänzt der Empfänger.
        Assert.Equal("Draft", draft.Status);
        Assert.Equal(po.Id, draft.PurchaseOrderId);
        Assert.Equal(2, draft.Lines.Count);
        var lineA = draft.Lines.Single(l => l.ArticleId == articleA);
        Assert.Equal((10, 333), (lineA.Quantity, lineA.UnitCostCents));
        Assert.Equal(po.Lines.Single(l => l.ArticleId == articleA).Id, lineA.PurchaseOrderLineId);
        Assert.Equal(100, draft.Lines.Single(l => l.ArticleId == articleB).UnitCostCents);   // Preis der Bestellzeile = Artikelpreis

        // ein zweiter Entwurf zur selben Bestellung (Doppelklick) würde die offene Menge doppelt einplanen
        await ErrorAssert.ThrowsWithCodeAsync<InvalidOperationException>("inbound_draft_exists",
            () => _w.InboundAsync(s => s.CreateFromPurchaseOrderAsync(po.Id, new CreateInboundFromPurchaseOrderRequest(bin.Id))));

        // Bestand und Bestellung ändern sich erst mit dem Buchen des Wareneingangs
        Assert.Equal(0, await _w.QuantityAsync(articleA));
        Assert.Equal("Sent", (await _w.PurchasingAsync(s => s.GetAsync(po.Id)))!.Status);

        var received = (await _w.InboundAsync(s => s.ReceiveAsync(draft.Id)))!;

        Assert.Equal("Received", received.Status);
        Assert.Equal(10, await _w.QuantityAsync(articleA));
        Assert.Equal(5, await _w.QuantityAsync(articleB));
        var closed = (await _w.PurchasingAsync(s => s.GetAsync(po.Id)))!;
        Assert.Equal("Received", closed.Status);
        Assert.All(closed.Lines, l => Assert.Equal(l.OrderedQty, l.ReceivedQty));
        // der Bestellpreis ist der Kosten-Snapshot der Buchung
        Assert.Equal(333, (await _w.MovementsAsync(articleA)).Single().UnitCostCents);

        // die Bestellung ist erfüllt: keine weiteren offenen Mengen, kein weiterer Wareneingang
        await ErrorAssert.ThrowsWithCodeAsync<InvalidOperationException>("po_not_receivable",
            () => _w.InboundAsync(s => s.CreateFromPurchaseOrderAsync(po.Id, new CreateInboundFromPurchaseOrderRequest(bin.Id))));
    }

    [Fact]
    public async Task A_partial_delivery_with_lot_and_expiry_leaves_the_order_partially_received_until_the_rest_arrives()
    {
        var supplier = await _w.AddSupplierAsync();
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync(priceCents: 100);
        var po = await _w.SentPurchaseOrderAsync(supplier, (article, 10, 200));
        var poLine = po.Lines.Single();

        // Der Empfänger ersetzt die vorgeschlagene Zeile durch eine mit Charge/MHD und weniger Menge - weiter mit Bestellbezug.
        var draft = (await _w.InboundAsync(s => s.CreateFromPurchaseOrderAsync(po.Id, new CreateInboundFromPurchaseOrderRequest(bin.Id))))!;
        await _w.InboundAsync(s => s.RemoveLineAsync(draft.Id, draft.Lines.Single().Id));
        await _w.InboundAsync(s => s.AddLineAsync(draft.Id, new AddInboundLineRequest(article, bin.Id, 4, "LOT-P1", Expiry, PurchaseOrderLineId: poLine.Id)));
        var first = (await _w.InboundAsync(s => s.ReceiveAsync(draft.Id)))!;

        Assert.Equal(200, first.Lines.Single().UnitCostCents);   // Preis kommt von der Bestellzeile
        var partial = (await _w.PurchasingAsync(s => s.GetAsync(po.Id)))!;
        Assert.Equal("PartiallyReceived", partial.Status);
        Assert.Equal(4, partial.Lines.Single().ReceivedQty);

        // Rest: neuer Entwurf mit der Restmenge, zweite Charge im selben Lagerplatz
        var rest = (await _w.InboundAsync(s => s.CreateFromPurchaseOrderAsync(po.Id, new CreateInboundFromPurchaseOrderRequest(bin.Id))))!;
        Assert.Equal(6, rest.Lines.Single().Quantity);
        Assert.EndsWith("-2", rest.ShipmentNumber);
        await _w.InboundAsync(s => s.RemoveLineAsync(rest.Id, rest.Lines.Single().Id));
        await _w.InboundAsync(s => s.AddLineAsync(rest.Id, new AddInboundLineRequest(article, bin.Id, 6, "LOT-P2", Expiry.AddDays(30), PurchaseOrderLineId: poLine.Id)));
        await _w.InboundAsync(s => s.ReceiveAsync(rest.Id));

        Assert.Equal("Received", (await _w.PurchasingAsync(s => s.GetAsync(po.Id)))!.Status);
        var rows = await _w.RowsAsync(article, bin);
        Assert.Equal(new[] { ("LOT-P1", 4), ("LOT-P2", 6) }, rows.Select(r => (r.LotNumber!, r.Quantity)));
    }

    [Fact]
    public async Task Receiving_more_than_the_open_order_quantity_is_rejected_and_books_nothing()
    {
        var supplier = await _w.AddSupplierAsync();
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync();
        var po = await _w.SentPurchaseOrderAsync(supplier, (article, 10, null));
        var draft = (await _w.InboundAsync(s => s.CreateFromPurchaseOrderAsync(po.Id, new CreateInboundFromPurchaseOrderRequest(bin.Id))))!;
        await _w.InboundAsync(s => s.AddLineAsync(draft.Id, new AddInboundLineRequest(article, bin.Id, 3, null, null, PurchaseOrderLineId: po.Lines.Single().Id)));

        // 10 (vorgeschlagen) + 3 = 13 > 10 bestellt
        await ErrorAssert.ThrowsWithCodeAsync<InvalidOperationException>("po_over_receipt",
            () => _w.InboundAsync(s => s.ReceiveAsync(draft.Id)));

        Assert.Equal(0, await _w.QuantityAsync(article));
        Assert.Equal("Sent", (await _w.PurchasingAsync(s => s.GetAsync(po.Id)))!.Status);
    }

    [Fact]
    public async Task A_purchase_order_line_must_belong_to_the_shipments_order_and_article()
    {
        var supplier = await _w.AddSupplierAsync();
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync();
        var other = await _w.AddArticleAsync();
        var po = await _w.SentPurchaseOrderAsync(supplier, (article, 10, null));
        var poLine = po.Lines.Single().Id;
        var free = await _w.InboundAsync(s => s.CreateAsync(new CreateInboundShipmentRequest(StockWorld.Unique("WE"), null, null)));
        var linked = (await _w.InboundAsync(s => s.CreateFromPurchaseOrderAsync(po.Id, new CreateInboundFromPurchaseOrderRequest(bin.Id))))!;

        // freier Wareneingang ohne Bestellbezug
        await ErrorAssert.ThrowsWithCodeAsync<InvalidOperationException>("inbound_without_po",
            () => _w.InboundAsync(s => s.AddLineAsync(free.Id, new AddInboundLineRequest(article, bin.Id, 1, null, null, PurchaseOrderLineId: poLine))));
        // anderer Artikel als die Bestellzeile
        await ErrorAssert.ThrowsWithCodeAsync<InvalidOperationException>("po_line_article_mismatch",
            () => _w.InboundAsync(s => s.AddLineAsync(linked.Id, new AddInboundLineRequest(other, bin.Id, 1, null, null, PurchaseOrderLineId: poLine))));
        // Bestellzeile einer fremden Bestellung
        await ErrorAssert.ThrowsWithCodeAsync<KeyNotFoundException>("po_line_not_found",
            () => _w.InboundAsync(s => s.AddLineAsync(linked.Id, new AddInboundLineRequest(article, bin.Id, 1, null, null, PurchaseOrderLineId: Guid.NewGuid()))));
    }

    [Fact]
    public async Task Create_inbound_needs_an_open_order_a_known_bin_and_reports_an_unknown_order_as_null()
    {
        var supplier = await _w.AddSupplierAsync();
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync();
        var draftOrder = await _w.PurchasingAsync(s => s.CreateAsync(new CreatePurchaseOrderRequest(supplier, null, null,
            new[] { new CreatePurchaseOrderLineRequest(article, 5) })));
        var sent = await _w.SentPurchaseOrderAsync(supplier, (article, 5, null));

        Assert.Null(await _w.InboundAsync(s => s.CreateFromPurchaseOrderAsync(Guid.NewGuid(), new CreateInboundFromPurchaseOrderRequest(bin.Id))));
        await ErrorAssert.ThrowsWithCodeAsync<InvalidOperationException>("po_not_receivable",
            () => _w.InboundAsync(s => s.CreateFromPurchaseOrderAsync(draftOrder.Id, new CreateInboundFromPurchaseOrderRequest(bin.Id))));
        await ErrorAssert.ThrowsWithCodeAsync<KeyNotFoundException>("bin_not_found",
            () => _w.InboundAsync(s => s.CreateFromPurchaseOrderAsync(sent.Id, new CreateInboundFromPurchaseOrderRequest(Guid.NewGuid()))));
    }

    [Fact]
    public async Task Marking_a_purchase_order_line_received_by_hand_books_no_stock()
    {
        var supplier = await _w.AddSupplierAsync();
        var article = await _w.AddArticleAsync();
        var po = await _w.SentPurchaseOrderAsync(supplier, (article, 10, null));

        var updated = (await _w.PurchasingAsync(s => s.ReceiveLineAsync(po.Id, po.Lines.Single().Id, new ReceivePurchaseOrderLineRequest(10))))!;

        Assert.Equal("Received", updated.Status);
        Assert.Equal(0, await _w.QuantityAsync(article));
        Assert.Empty(await _w.MovementsAsync(article));
    }

    [Fact]
    public async Task Suggestions_subtract_the_open_ordered_quantity_so_the_same_demand_is_not_ordered_twice()
    {
        var supplier = await _w.AddSupplierAsync();
        var site = await _w.AddSiteAsync();
        var bin = await _w.AddBinAsync(site);
        var article = await _w.AddArticleAsync(reorderPoint: 100, maxStock: 100, supplierId: supplier);

        async Task<PurchaseSuggestionLineDto?> SuggestionAsync() =>
            (await _w.PurchasingAsync(s => s.SuggestionsAsync()))
            .SelectMany(g => g.Lines).SingleOrDefault(l => l.ArticleId == article);

        var before = await SuggestionAsync();
        Assert.NotNull(before);
        Assert.Equal((0, 0, 100), (before.CurrentStock, before.OpenOrderedQty, before.SuggestedOrderQty));

        // Ein Entwurf zählt noch nicht: erst die versendete Bestellung erwartet Ware.
        var po = await _w.PurchasingAsync(s => s.CreateAsync(new CreatePurchaseOrderRequest(supplier, null, null,
            new[] { new CreatePurchaseOrderLineRequest(article, 40) })));
        Assert.Equal(100, (await SuggestionAsync())!.SuggestedOrderQty);

        await _w.PurchasingAsync(s => s.SendAsync(po.Id));
        var afterSend = await SuggestionAsync();
        Assert.NotNull(afterSend);
        Assert.Equal((0, 40, 60), (afterSend.CurrentStock, afterSend.OpenOrderedQty, afterSend.SuggestedOrderQty));

        // Eine Teillieferung von 25 verschiebt Menge von "offen" nach "Bestand": Bedarf unverändert 60.
        var draft = (await _w.InboundAsync(s => s.CreateFromPurchaseOrderAsync(po.Id, new CreateInboundFromPurchaseOrderRequest(bin.Id))))!;
        await _w.InboundAsync(s => s.RemoveLineAsync(draft.Id, draft.Lines.Single().Id));
        await _w.InboundAsync(s => s.AddLineAsync(draft.Id, new AddInboundLineRequest(article, bin.Id, 25, null, null, PurchaseOrderLineId: po.Lines.Single().Id)));
        await _w.InboundAsync(s => s.ReceiveAsync(draft.Id));
        var partial = await SuggestionAsync();
        Assert.NotNull(partial);
        Assert.Equal((25, 15, 60), (partial.CurrentStock, partial.OpenOrderedQty, partial.SuggestedOrderQty));

        // Eine zweite versendete Bestellung über 60: Bestand 25 + offen 15 + 60 = 100, nicht mehr unter dem Meldebestand.
        await _w.SentPurchaseOrderAsync(supplier, (article, 60, null));
        Assert.Null(await SuggestionAsync());
    }

    [Fact]
    public async Task A_deactivated_supplier_gets_no_new_purchase_order()
    {
        var supplier = await _w.AddSupplierAsync(active: false);
        var article = await _w.AddArticleAsync();

        await ErrorAssert.ThrowsWithCodeAsync<InvalidOperationException>("supplier_inactive",
            () => _w.PurchasingAsync(s => s.CreateAsync(new CreatePurchaseOrderRequest(supplier, null, null,
                new[] { new CreatePurchaseOrderLineRequest(article, 1) }))));
        await ErrorAssert.ThrowsWithCodeAsync<KeyNotFoundException>("supplier_not_found",
            () => _w.PurchasingAsync(s => s.CreateAsync(new CreatePurchaseOrderRequest(Guid.NewGuid(), null, null,
                new[] { new CreatePurchaseOrderLineRequest(article, 1) }))));
    }

    [Fact]
    public async Task Purchase_order_and_return_numbers_come_from_the_atomic_sequence_without_duplicates()
    {
        // parallele Aufrufer bekommen nie dieselbe Nummer (früher: Lesen-Ändern-Schreiben mit fester Zeilen-Id)
        var gate = new TaskCompletionSource();
        Task<long[]> InParallel(Func<Task<long>> next) => Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await gate.Task;
            return await next();
        })));

        var poTask = InParallel(async () =>
        {
            using var scope = _w.NewScope();
            return await scope.Get<IPurchaseOrderRepository>().NextSequenceAsync();
        });
        var rmaTask = InParallel(async () =>
        {
            using var scope = _w.NewScope();
            return await scope.Get<IReturnRepository>().NextSequenceAsync();
        });
        gate.SetResult();
        var poNumbers = await poTask;
        var rmaNumbers = await rmaTask;

        Assert.Equal(8, poNumbers.Distinct().Count());
        Assert.Equal(8, rmaNumbers.Distinct().Count());
        // konsekutiv innerhalb des Kreises (die anderen Tests der Klasse ziehen ebenfalls Nummern, daher nur die Spannweite)
        Assert.Equal(7, poNumbers.Max() - poNumbers.Min());
        Assert.Equal(7, rmaNumbers.Max() - rmaNumbers.Min());
    }
}
