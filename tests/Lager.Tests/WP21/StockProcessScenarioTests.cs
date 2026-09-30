using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Inventory;
using Lager.Contracts.Returns;
using Lager.Contracts.Stock;
using Lager.Domain.Stock;
using Lager.Domain.Warehouse;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP21;

/// <summary>
/// Szenarien 4 bis 6: Inventur mit Differenz und einer Bewegung während der Zählung, Retoure (A-Ware zurück in den Bestand,
/// B-Ware und Ausschuss nur im Ledger), Nachschub (Scan und Abschluss mit Charge). Jeweils mit der Rolle, die den Schritt ausführt,
/// und mit der Prüfung der Ledger-Invariante am Ende.
/// </summary>
public class StockProcessScenarioTests : IClassFixture<WorkflowFixture>
{
    private readonly WorkflowFixture _fx;
    private WorldBuilder W => _fx.W;
    private WorldBuilder.World Site => _fx.Warehouse;

    public StockProcessScenarioTests(WorkflowFixture fixture) => _fx = fixture;

    // ---- 4: Inventur ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Inventory_with_a_difference_and_a_pick_during_the_count_ends_with_the_counted_stock()
    {
        var manager = await W.ClientAsync("Manager");
        var receiver = await W.ClientAsync("Receiver");
        var packer = await W.ClientAsync("Packer");
        var bin = await W.AddBinAsync(Site.Site, x: 7_000);
        var x = await W.AddArticleAsync(priceCents: 300);
        var y = await W.AddArticleAsync();
        var expiry = WorldBuilder.InDays(50);
        await manager.ReceiveAsync(
            new ApiCalls.Receipt(x.Id, bin, 20, "L1", expiry),
            new ApiCalls.Receipt(y.Id, bin, 10));

        // Inventur nur für diesen Platz: ein Snapshot mit einer Zeile je Bestandszeile
        var count = await (await receiver.PostAsJsonAsync("/api/inventory/start",
            new StartInventoryRequest(WorldBuilder.Unique("Inventur"), bin.Id))).ExpectAsync<InventoryCountDto>(HttpStatusCode.Created);
        Assert.Equal("Open", count.Status);
        var lineX = Assert.Single(count.Lines, l => l.ArticleId == x.Id);
        var lineY = Assert.Single(count.Lines, l => l.ArticleId == y.Id);
        Assert.Equal((20, "L1", expiry), (lineX.ExpectedQty, lineX.LotNumber, lineX.ExpiryDate));

        // Während der Zählung werden 4 Stück von X gepackt: der Bestand ist jetzt 16, nicht mehr der Snapshot 20
        var order = await manager.PlaceOrderAsync(x.Id, 4);
        await packer.PackAsync(await manager.GenerateAsync(order.Id));
        Assert.Equal(16, await manager.TotalStockAsync(x.Id));

        // Abgleich vor vollständiger Zählung: abgelehnt, nichts gebucht
        await (await receiver.PutAsJsonAsync($"/api/inventory/{count.Id}/lines/{lineX.Id}", new SetCountRequest(15, "1 Stück fehlt"))).ExpectAsync<InventoryCountDto>();
        var early = await manager.PostAsync($"/api/inventory/{count.Id}/reconcile", null);
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Contains("noch nicht alle Positionen gezählt", (await early.BodyAsync()).GetProperty("detail").GetString());
        Assert.Equal(16, await manager.TotalStockAsync(x.Id));

        // Y: 2 mehr gefunden. Nur der Manager gleicht ab.
        await (await receiver.PutAsJsonAsync($"/api/inventory/{count.Id}/lines/{lineY.Id}", new SetCountRequest(12, null))).ExpectAsync<InventoryCountDto>();
        Assert.Equal(HttpStatusCode.Forbidden, (await receiver.PostAsync($"/api/inventory/{count.Id}/reconcile", null)).StatusCode);
        var reconciled = await (await manager.PostAsync($"/api/inventory/{count.Id}/reconcile", null)).ExpectAsync<InventoryCountDto>();
        Assert.Equal("Reconciled", reconciled.Status);

        // gebucht wird gezählt minus AKTUELL (15 - 16 = -1), nicht gezählt minus Snapshot (-5): die gepackten 4 zählen nicht doppelt
        Assert.Equal(15, await manager.TotalStockAsync(x.Id));
        Assert.Equal(12, await manager.TotalStockAsync(y.Id));
        var inventoryX = Assert.Single(await W.MovementsAsync(x.Id), m => m.Reason == StockMovementReason.Inventory);
        Assert.Equal((-1, "L1", 300, count.Id), (inventoryX.QuantityDelta, inventoryX.LotNumber, inventoryX.UnitCostCents, inventoryX.ReferenceId!.Value));
        Assert.Equal(2, Assert.Single(await W.MovementsAsync(y.Id), m => m.Reason == StockMovementReason.Inventory).QuantityDelta);

        // die Bewegung seit Zählbeginn steht an der Zeile, hinter der Bemerkung des Zählers
        var reason = reconciled.Lines.Single(l => l.ArticleId == x.Id).Reason!;
        Assert.StartsWith("1 Stück fehlt | ", reason);
        Assert.Contains("Snapshot 20, beim Abgleich 16, gezählt 15", reason);

        // ein zweiter Abgleich bucht nichts mehr
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsync($"/api/inventory/{count.Id}/reconcile", null)).StatusCode);
        Assert.Equal(15, await manager.TotalStockAsync(x.Id));
        Assert.Empty(await W.LedgerViolationsAsync(x.Id));
        Assert.Empty(await W.LedgerViolationsAsync(y.Id));
    }

    // ---- 5: Retoure -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Return_books_A_grade_back_into_stock_and_B_grade_and_scrap_only_as_blocked_movements()
    {
        var manager = await W.ClientAsync("Manager");
        var receiver = await W.ClientAsync("Receiver");
        var packer = await W.ClientAsync("Packer");
        var article = await W.AddArticleAsync(priceCents: 80);
        var expiry = WorldBuilder.InDays(75);
        await manager.ReceiveAsync(new ApiCalls.Receipt(article.Id, Site.PickA, 30, "LR", expiry));
        var (order, _) = await manager.PackedOrderAsync(article.Id, 10);
        await packer.ShipAsync(order.Id);
        Assert.Equal(20, await manager.TotalStockAsync(article.Id));

        // Retoure zur Bestellung: 4 A-Ware, 3 B-Ware, 1 Defekt
        var created = await (await receiver.PostAsJsonAsync("/api/returns", new CreateReturnShipmentRequest(order.Id, "E2E", null, new[]
        {
            new CreateReturnLineRequest(article.Id, 4, "LR"),
            new CreateReturnLineRequest(article.Id, 3, "LR"),
            new CreateReturnLineRequest(article.Id, 1),
        }))).ExpectAsync<ReturnShipmentDto>(HttpStatusCode.Created);
        Assert.Equal(("Draft", 3), (created.Status, created.Lines.Count));
        Assert.StartsWith("RMA-", created.RmaNumber);

        // Abschluss ohne QC: abgelehnt
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsync($"/api/returns/{created.Id}/process", null)).StatusCode);
        Assert.Equal(20, await manager.TotalStockAsync(article.Id));

        // QC je Zeile: A-Ware braucht einen Ziel-Lagerplatz, ein unbekanntes Ergebnis wird abgelehnt (auch als Zahl)
        var qcUrl = (Guid lineId) => $"/api/returns/{created.Id}/lines/{lineId}/qc";
        Assert.Equal(HttpStatusCode.BadRequest, (await manager.PutAsJsonAsync(qcUrl(created.Lines[0].Id), new SetQcRequest("99", null, null))).StatusCode);
        await (await manager.PutAsJsonAsync(qcUrl(created.Lines[0].Id), new SetQcRequest("Sellable", Site.PickB.Id, "einwandfrei"))).ExpectAsync<ReturnShipmentDto>();
        await (await manager.PutAsJsonAsync(qcUrl(created.Lines[1].Id), new SetQcRequest("BGrade", null, "Karton beschädigt"))).ExpectAsync<ReturnShipmentDto>();
        await (await manager.PutAsJsonAsync(qcUrl(created.Lines[2].Id), new SetQcRequest("Defect", null, "kaputt"))).ExpectAsync<ReturnShipmentDto>();

        var processed = await (await manager.PostAsync($"/api/returns/{created.Id}/process", null)).ExpectAsync<ReturnShipmentDto>();
        Assert.Equal("Processed", processed.Status);

        // Verkaufsbestand: nur die 4 A-Ware sind zurück (20 + 4), mit Charge und dem MHD dieser Charge
        Assert.Equal(24, await manager.TotalStockAsync(article.Id));
        var back = Assert.Single((await manager.StockOfAsync(article.Id)), s => s.StorageLocationId == Site.PickB.Id);
        Assert.Equal((4, "LR", expiry), (back.Quantity, back.LotNumber, back.ExpiryDate));

        // Ledger: Return +4; B-Ware und Ausschuss als Paar aus Zugang und Sperrung (netto 0)
        var movements = await W.MovementsAsync(article.Id);
        Assert.Equal(4, movements.Where(m => m.Reason == StockMovementReason.Return).Sum(m => m.QuantityDelta));
        Assert.Equal((2, 0), (movements.Count(m => m.Reason == StockMovementReason.ReturnB), movements.Where(m => m.Reason == StockMovementReason.ReturnB).Sum(m => m.QuantityDelta)));
        Assert.Equal((2, 0), (movements.Count(m => m.Reason == StockMovementReason.ReturnScrap), movements.Where(m => m.Reason == StockMovementReason.ReturnScrap).Sum(m => m.QuantityDelta)));
        Assert.Empty(await W.LedgerViolationsAsync(article.Id));

        // ein zweiter Abschluss bucht nichts mehr
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsync($"/api/returns/{created.Id}/process", null)).StatusCode);
        Assert.Equal(24, await manager.TotalStockAsync(article.Id));
    }

    [Fact]
    public async Task A_return_is_limited_to_delivered_orders_and_to_the_delivered_quantity()
    {
        var manager = await W.ClientAsync("Manager");
        var receiver = await W.ClientAsync("Receiver");
        var article = await W.AddArticleAsync();
        await manager.ReceiveAsync(new ApiCalls.Receipt(article.Id, Site.PickA, 30));

        // Bestellung noch nicht ausgeliefert: keine Retoure
        var open = await manager.PlaceOrderAsync(article.Id, 5);
        var early = await receiver.PostAsJsonAsync("/api/returns", new CreateReturnShipmentRequest(open.Id, null, null, new[] { new CreateReturnLineRequest(article.Id, 1) }));
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        Assert.Equal("return_order_not_delivered", (await early.BodyAsync()).CodeOf());

        // ausgeliefert: bis zur gelieferten Menge (5), darüber hinaus nicht - auch nicht verteilt auf zwei Retouren
        var (packed, _) = await manager.PackedOrderAsync(article.Id, 5);
        var first = await receiver.PostAsJsonAsync("/api/returns", new CreateReturnShipmentRequest(packed.Id, null, null, new[] { new CreateReturnLineRequest(article.Id, 4) }));
        await first.ExpectStatusAsync(HttpStatusCode.Created);
        var tooMuch = await receiver.PostAsJsonAsync("/api/returns", new CreateReturnShipmentRequest(packed.Id, null, null, new[] { new CreateReturnLineRequest(article.Id, 2) }));
        Assert.Equal(HttpStatusCode.Conflict, tooMuch.StatusCode);
        Assert.Equal("return_quantity_exceeded", (await tooMuch.BodyAsync()).CodeOf());

        // ein Artikel, der nicht zur Bestellung gehört
        var other = await W.AddArticleAsync();
        var foreign = await receiver.PostAsJsonAsync("/api/returns", new CreateReturnShipmentRequest(packed.Id, null, null, new[] { new CreateReturnLineRequest(other.Id, 1) }));
        Assert.Equal(HttpStatusCode.Conflict, foreign.StatusCode);
        Assert.Equal("return_article_not_on_order", (await foreign.BodyAsync()).CodeOf());
    }

    // ---- 6: Nachschub ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Replenishment_scan_and_complete_move_stock_from_reserve_to_hot_pick_and_keep_lot_and_expiry()
    {
        var manager = await W.ClientAsync("Manager");
        var picker = await W.ClientAsync("Picker");
        var hot = await W.AddBinAsync(Site.Site, BinType.HotPick, replenishmentThreshold: 10, x: 8_000);
        var reserve = await W.AddBinAsync(Site.Site, BinType.Reserve, x: 9_000);
        var article = await W.AddArticleAsync(priceCents: 40);
        var expiry = WorldBuilder.InDays(100);
        await manager.ReceiveAsync(
            new ApiCalls.Receipt(article.Id, reserve, 100, "LOT-H", expiry),
            new ApiCalls.Receipt(article.Id, hot, 3));

        // Scan: 3 im Hot-Pick-Platz liegen unter der Schwelle 10 -> eine Aufgabe bis zum Zielbestand 2 x 10 = 20 (Vorschlag 17)
        var tasks = await ScanAsync(manager, hot);
        var task = Assert.Single(tasks, t => t.ArticleId == article.Id);
        Assert.Equal((17, "Open", reserve.Id, hot.Id), (task.SuggestedQty, task.Status, task.SourceBinId, task.TargetBinId));

        // ein zweiter Scan legt keine zweite Aufgabe an (offene Aufgabe je Artikel und Ziel-Platz)
        Assert.DoesNotContain(await ScanAsync(manager, hot), t => t.ArticleId == article.Id);
        Assert.Single(await (await manager.GetAsync("/api/replenishment/open")).ExpectAsync<List<ReplenishmentTaskDto>>(), t => t.ArticleId == article.Id);

        // Abschluss: zwei Movements, die Charge zieht mit um, der Bestand insgesamt bleibt gleich
        var done = await (await picker.PostAsJsonAsync($"/api/replenishment/{task.Id}/complete", new CompleteReplenishmentRequest(17))).ExpectAsync<ReplenishmentTaskDto>();
        Assert.Equal(("Completed", 17), (done.Status, done.CompletedQty));
        Assert.Equal(103, await manager.TotalStockAsync(article.Id));
        Assert.Equal(83, await W.QuantityAsync(article.Id, reserve));
        Assert.Equal(20, await W.QuantityAsync(article.Id, hot));
        var moved = Assert.Single((await manager.StockOfAsync(article.Id)), s => s.StorageLocationId == hot.Id && s.LotNumber == "LOT-H");
        Assert.Equal((17, expiry), (moved.Quantity, moved.ExpiryDate));

        var movements = (await W.MovementsAsync(article.Id)).Where(m => m.ReferenceId == task.Id).ToList();
        Assert.Equal(2, movements.Count);
        var outMovement = Assert.Single(movements, m => m.Reason == StockMovementReason.ReplenishmentOut);
        var inMovement = Assert.Single(movements, m => m.Reason == StockMovementReason.ReplenishmentIn);
        Assert.Equal((-17, reserve.Id, "LOT-H"), (outMovement.QuantityDelta, outMovement.BinId, outMovement.LotNumber));
        Assert.Equal((17, hot.Id, "LOT-H"), (inMovement.QuantityDelta, inMovement.BinId, inMovement.LotNumber));

        // doppelt abgeschlossen oder nachträglich storniert: abgelehnt, nichts bewegt
        Assert.Equal(HttpStatusCode.Conflict, (await picker.PostAsJsonAsync($"/api/replenishment/{task.Id}/complete", new CompleteReplenishmentRequest(17))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await manager.PostAsync($"/api/replenishment/{task.Id}/cancel", null)).StatusCode);
        Assert.Equal(20, await W.QuantityAsync(article.Id, hot));

        // 20 liegen über der Schwelle: kein neuer Bedarf. Fällt der Platz wieder darunter, kommt eine neue Aufgabe - und ein
        // Teil-Abschluss (5 statt 12) ist erlaubt.
        Assert.DoesNotContain(await ScanAsync(manager, hot), t => t.ArticleId == article.Id);
        await (await manager.PostAsJsonAsync("/api/stock/adjust", new AdjustStockRequest(article.Id, hot.Id, -12, null, null))).ExpectAsync<StockItemDto>();
        var second = Assert.Single(await ScanAsync(manager, hot), t => t.ArticleId == article.Id);
        Assert.Equal(12, second.SuggestedQty);
        await (await picker.PostAsJsonAsync($"/api/replenishment/{second.Id}/complete", new CompleteReplenishmentRequest(5))).ExpectAsync<ReplenishmentTaskDto>();
        Assert.Equal(13, await W.QuantityAsync(article.Id, hot));
        Assert.Empty(await W.LedgerViolationsAsync(article.Id));
    }

    [Fact]
    public async Task Completing_a_replenishment_with_more_than_the_reserve_holds_is_rejected_and_books_nothing()
    {
        var manager = await W.ClientAsync("Manager");
        var picker = await W.ClientAsync("Picker");
        var hot = await W.AddBinAsync(Site.Site, BinType.HotPick, replenishmentThreshold: 10, x: 8_500);
        var reserve = await W.AddBinAsync(Site.Site, BinType.Reserve, x: 9_500);
        var article = await W.AddArticleAsync();
        await manager.ReceiveAsync(new ApiCalls.Receipt(article.Id, reserve, 6), new ApiCalls.Receipt(article.Id, hot, 2));
        var task = Assert.Single(await ScanAsync(manager, hot), t => t.ArticleId == article.Id);
        Assert.Equal(6, task.SuggestedQty);   // Zielbestand 20 wäre 18, die Reserve hat nur 6

        var response = await picker.PostAsJsonAsync($"/api/replenishment/{task.Id}/complete", new CompleteReplenishmentRequest(9));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal((6, 2), (await W.QuantityAsync(article.Id, reserve), await W.QuantityAsync(article.Id, hot)));
        Assert.Empty(await W.LedgerViolationsAsync(article.Id));
    }

    private static async Task<List<ReplenishmentTaskDto>> ScanAsync(HttpClient manager, WorldBuilder.Bin hot) =>
        await (await manager.PostAsJsonAsync("/api/replenishment/scan", new ScanReplenishmentRequest(hot.Id))).ExpectAsync<List<ReplenishmentTaskDto>>();
}
