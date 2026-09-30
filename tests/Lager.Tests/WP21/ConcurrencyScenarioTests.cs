using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Lager.Contracts.Purchasing;
using Lager.Contracts.Returns;
using Lager.Contracts.Shipping;
using Lager.Contracts.Stock;
using Lager.Domain.Stock;
using Lager.Domain.Warehouse;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP21;

/// <summary>
/// Parallelität über echte, gleichzeitige HTTP-Aufrufe: zwei Aufrufer gegen dieselbe Pickliste, dieselbe Bestellung, denselben
/// Wareneingang - genau einer gewinnt, der andere bekommt einen 409, gebucht wird einmal. Dazu die Nummernkreise: parallele
/// Vergabe ohne Duplikat und ohne Fehlschlag. Jede Wettlauf-Prüfung läuft mehrere Runden mit frischen Belegen, damit ein
/// zufällig serialisierter Ablauf den Fehler nicht versteckt.
/// </summary>
public class ConcurrencyScenarioTests : IClassFixture<WorkflowFixture>
{
    private const int Rounds = 5;

    private readonly WorkflowFixture _fx;
    private WorldBuilder W => _fx.W;
    private WorldBuilder.World Site => _fx.Warehouse;

    public ConcurrencyScenarioTests(WorkflowFixture fixture) => _fx = fixture;

    /// <summary>Startet alle Aufgaben gleichzeitig (Startschuss), damit sie wirklich um dieselben Zeilen konkurrieren.</summary>
    private static async Task<T[]> InParallelAsync<T>(IEnumerable<Func<Task<T>>> work)
    {
        var gate = new TaskCompletionSource();
        var tasks = work.Select(w => Task.Run(async () =>
        {
            await gate.Task;
            return await w();
        })).ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    private static int[] Statuses(IEnumerable<HttpResponseMessage> responses) =>
        responses.Select(r => (int)r.StatusCode).Order().ToArray();

    [Fact]
    public async Task Two_parallel_pack_calls_on_the_same_list_book_the_stock_exactly_once()
    {
        var manager = await W.ClientAsync("Manager");
        var packerA = await W.ClientAsync("Packer");
        var packerB = await W.ClientAsync("Packer", "Viewer");   // ein zweiter Nutzer, sonst teilten sich beide einen Client
        var article = await W.AddArticleAsync();
        await manager.ReceiveAsync(new ApiCalls.Receipt(article.Id, Site.PickA, 200));
        var lists = new List<Guid>();

        for (var round = 0; round < Rounds; round++)
        {
            var order = await manager.PlaceOrderAsync(article.Id, 5);
            var list = await manager.GenerateAsync(order.Id);
            lists.Add(list.Id);

            var responses = await InParallelAsync(new Func<Task<HttpResponseMessage>>[] { () => packerA.PackRawAsync(list), () => packerB.PackRawAsync(list) });

            Assert.True(Statuses(responses).SequenceEqual(new[] { 200, 409 }),
                $"Runde {round}: erwartet genau einen Erfolg (200) und einen Konflikt (409), war {string.Join("/", Statuses(responses))}");
            Assert.Equal("Completed", (await manager.GetPickListAsync(list.Id)).Status);
            Assert.Equal("Packed", (await manager.GetOrderAsync(order.Id)).Status);
        }

        // 5 Listen x 5 Stück: fünfmal abgebucht, nicht zehnmal
        Assert.Equal(200 - Rounds * 5, await manager.TotalStockAsync(article.Id));
        var picks = (await W.MovementsAsync(article.Id)).Where(m => m.Reason == StockMovementReason.Pick).ToList();
        Assert.Equal(Rounds, picks.Count);
        Assert.All(picks, m => Assert.Equal(-5, m.QuantityDelta));
        Assert.Equal(lists.Order(), picks.Select(m => m.ReferenceId!.Value).Order());
        Assert.Empty(await W.LedgerViolationsAsync(article.Id));
    }

    [Fact]
    public async Task Two_parallel_generate_calls_for_the_same_order_create_exactly_one_pick_list()
    {
        var manager = await W.ClientAsync("Manager");
        var managerB = await W.ClientAsync("Manager", "Viewer");
        var article = await W.AddArticleAsync();
        await manager.ReceiveAsync(new ApiCalls.Receipt(article.Id, Site.PickA, 100));

        for (var round = 0; round < Rounds; round++)
        {
            var order = await manager.PlaceOrderAsync(article.Id, 4);

            var responses = await InParallelAsync(new Func<Task<HttpResponseMessage>>[] { () => manager.GenerateRawAsync(order.Id), () => managerB.GenerateRawAsync(order.Id) });

            Assert.True(Statuses(responses).SequenceEqual(new[] { 201, 409 }),
                $"Runde {round}: erwartet genau eine Pickliste (201) und einen Konflikt (409), war {string.Join("/", Statuses(responses))}");
            var lists = await (await manager.GetAsync("/api/picklists")).ExpectAsync<List<Lager.Contracts.PickLists.PickListDto>>();
            var withOrder = lists.Where(l => l.Items.Any(i => i.OrderNumber == order.OrderNumber)).ToList();
            Assert.Equal(4, Assert.Single(withOrder).Items.Sum(i => i.Quantity));
            Assert.Equal("Picking", (await manager.GetOrderAsync(order.Id)).Status);
        }

        // gebucht wird erst beim Packen: der Bestand ist unberührt
        Assert.Equal(100, await manager.TotalStockAsync(article.Id));
        Assert.Empty(await W.LedgerViolationsAsync(article.Id));
    }

    [Fact]
    public async Task A_double_click_on_receive_process_and_complete_books_once()
    {
        var manager = await W.ClientAsync("Manager");
        var managerB = await W.ClientAsync("Manager", "Viewer");
        var hot = await W.AddBinAsync(Site.Site, BinType.HotPick, replenishmentThreshold: 10, x: 8_000);
        var reserve = await W.AddBinAsync(Site.Site, BinType.Reserve, x: 9_000);
        var article = await W.AddArticleAsync();
        await manager.ReceiveAsync(new ApiCalls.Receipt(article.Id, reserve, 100), new ApiCalls.Receipt(article.Id, hot, 2));

        // Wareneingang
        var draft = await manager.DraftInboundAsync(new ApiCalls.Receipt(article.Id, Site.PickA, 7));
        var receive = await InParallelAsync(new Func<Task<HttpResponseMessage>>[]
        {
            () => manager.PostAsync($"/api/inbound/{draft.Id}/receive", null), () => managerB.PostAsync($"/api/inbound/{draft.Id}/receive", null),
        });
        Assert.True(Statuses(receive).SequenceEqual(new[] { 200, 409 }), $"Wareneingang: {string.Join("/", Statuses(receive))}");
        Assert.Equal(7, await W.QuantityAsync(article.Id, Site.PickA));

        // Retoure abschließen (eine gepackte Bestellung, A-Ware zurück)
        var (order, _) = await manager.PackedOrderAsync(article.Id, 3);
        var created = await (await manager.PostAsJsonAsync("/api/returns", new CreateReturnShipmentRequest(order.Id, null, null,
            new[] { new CreateReturnLineRequest(article.Id, 2) }))).ExpectAsync<ReturnShipmentDto>(HttpStatusCode.Created);
        await (await manager.PutAsJsonAsync($"/api/returns/{created.Id}/lines/{created.Lines.Single().Id}/qc",
            new SetQcRequest("Sellable", Site.PickB.Id, null))).ExpectAsync<ReturnShipmentDto>();
        var process = await InParallelAsync(new Func<Task<HttpResponseMessage>>[]
        {
            () => manager.PostAsync($"/api/returns/{created.Id}/process", null), () => managerB.PostAsync($"/api/returns/{created.Id}/process", null),
        });
        Assert.True(Statuses(process).SequenceEqual(new[] { 200, 409 }), $"Retoure: {string.Join("/", Statuses(process))}");
        Assert.Equal(2, await W.QuantityAsync(article.Id, Site.PickB));

        // Nachschub abschließen (das Packen oben hat schon aus dem Hot-Pick-Platz genommen: der Stand davor ist der Bezugspunkt)
        var task = (await (await manager.PostAsJsonAsync("/api/replenishment/scan", new ScanReplenishmentRequest(hot.Id)))
            .ExpectAsync<List<ReplenishmentTaskDto>>()).Single(t => t.ArticleId == article.Id);
        var hotBefore = await W.QuantityAsync(article.Id, hot);
        var reserveBefore = await W.QuantityAsync(article.Id, reserve);
        var complete = await InParallelAsync(new Func<Task<HttpResponseMessage>>[]
        {
            () => manager.PostAsJsonAsync($"/api/replenishment/{task.Id}/complete", new CompleteReplenishmentRequest(task.SuggestedQty)),
            () => managerB.PostAsJsonAsync($"/api/replenishment/{task.Id}/complete", new CompleteReplenishmentRequest(task.SuggestedQty)),
        });
        Assert.True(Statuses(complete).SequenceEqual(new[] { 200, 409 }), $"Nachschub: {string.Join("/", Statuses(complete))}");
        Assert.Equal(hotBefore + task.SuggestedQty, await W.QuantityAsync(article.Id, hot));
        Assert.Equal(reserveBefore - task.SuggestedQty, await W.QuantityAsync(article.Id, reserve));

        // je Vorgang genau eine Buchungsgruppe im Ledger
        var movements = await W.MovementsAsync(article.Id);
        Assert.Equal(1, movements.Count(m => m.Reason == StockMovementReason.Return));
        Assert.Equal(1, movements.Count(m => m.Reason == StockMovementReason.ReplenishmentOut));
        Assert.Equal(1, movements.Count(m => m.Reason == StockMovementReason.ReplenishmentIn));
        Assert.Empty(await W.LedgerViolationsAsync(article.Id));
    }

    [Fact]
    public async Task Parallel_number_allocation_yields_distinct_numbers_in_every_circle_without_a_failure()
    {
        var manager = await W.ClientAsync("Manager");
        var supplierId = await W.AddSupplierAsync();
        var article = await W.AddArticleAsync(supplierId: supplierId);
        await manager.ReceiveAsync(new ApiCalls.Receipt(article.Id, Site.PickA, 500));

        // Vorlauf (sequenziell): Bestellungen für die Picklisten, gepackte Bestellungen für Sendungen und Retouren
        var toPick = new List<Guid>();
        for (var i = 0; i < 10; i++) toPick.Add((await manager.PlaceOrderAsync(article.Id, 1)).Id);
        var packedForShipments = new List<Guid>();
        for (var i = 0; i < 6; i++) packedForShipments.Add((await manager.PackedOrderAsync(article.Id, 1)).Order.Id);
        var packedForReturns = (await manager.PackedOrderAsync(article.Id, 10)).Order.Id;

        // Alles gleichzeitig: 10 Picklisten, 6 Sendungen, 6 Einkaufsbestellungen, 5 Retouren
        var work = new List<Func<Task<(string Circle, HttpResponseMessage Response)>>>();
        work.AddRange(toPick.Select(id => (Func<Task<(string, HttpResponseMessage)>>)(async () => ("PL", await manager.GenerateRawAsync(id)))));
        work.AddRange(packedForShipments.Select(id => (Func<Task<(string, HttpResponseMessage)>>)(async () =>
            ("SH", await manager.PostAsJsonAsync("/api/shipments", new CreateShipmentRequest(id, null, "MANUAL", 300, 200, 100, 1500))))));
        for (var i = 0; i < 6; i++)
            work.Add(async () => ("PO", await manager.PostAsJsonAsync("/api/purchase-orders",
                new CreatePurchaseOrderRequest(supplierId, null, null, new[] { new CreatePurchaseOrderLineRequest(article.Id, 5) }))));
        for (var i = 0; i < 5; i++)
            work.Add(async () => ("RMA", await manager.PostAsJsonAsync("/api/returns",
                new CreateReturnShipmentRequest(packedForReturns, null, null, new[] { new CreateReturnLineRequest(article.Id, 1) }))));

        var results = await InParallelAsync(work);

        var numbers = new Dictionary<string, List<string>>();
        foreach (var (circle, response) in results)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == HttpStatusCode.Created, $"{circle}: {(int)response.StatusCode} {text}");
            var body = System.Text.Json.JsonDocument.Parse(text).RootElement;
            var number = circle switch
            {
                "PL" => body.GetProperty("pickListNumber").GetString(),
                "SH" => body.GetProperty("shipmentNumber").GetString(),
                "PO" => body.GetProperty("poNumber").GetString(),
                _ => body.GetProperty("rmaNumber").GetString(),
            };
            if (!numbers.TryGetValue(circle, out var circleNumbers)) numbers[circle] = circleNumbers = new List<string>();
            circleNumbers.Add(number!);
        }

        foreach (var (circle, expected) in new[] { ("PL", 10), ("SH", 6), ("PO", 6), ("RMA", 5) })
        {
            Assert.Equal(expected, numbers[circle].Count);
            Assert.Equal(expected, numbers[circle].Distinct().Count());
            Assert.All(numbers[circle], n => Assert.Matches(new Regex($@"^{circle}-\d{{8}}-\d{{5}}$"), n));
        }
        Assert.Empty(await W.LedgerViolationsAsync(article.Id));
    }
}
