using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Inbound;
using Lager.Contracts.Inventory;
using Lager.Contracts.PickLists;
using Lager.Contracts.Returns;
using Lager.Contracts.Stock;
using Lager.Domain.Stock;
using Lager.Domain.Warehouse;
using Lager.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Lager.Tests.WP21;

/// <summary>
/// Die Bestandsinvarianten nach einer zufälligen, aber reproduzierbaren Buchungsfolge (fester Seed, 200 Vorgänge über
/// Wareneingang, Korrektur, Packen, Retoure, Nachschub und Inventur - alles über HTTP). Gefordert ist nach jedem Abschnitt und am Ende:
/// je Artikel Summe(Movement-Deltas) = Summe(Bestandsmengen) (sogar je Platz und Charge), kein negativer Bestand, keine
/// Bestandszeile doppelt je (Artikel, Platz, Charge) - und kein einziger Vorgang endet mit einem Serverfehler (5xx).
/// Abgelehnte Vorgänge (409, z. B. zu wenig Bestand) gehören dazu: sie dürfen nichts hinterlassen.
///
/// Reproduzierbarkeit (WP33): Die Folge hängt NUR vom Seed ab. Die Testwelt benutzt feste Codes und SKUs statt der zufälligen
/// <see cref="WorldBuilder.Unique"/>-Namen, denn der Server sortiert bei Gleichstand nach Platzcode und SKU (Bestandsvergabe,
/// Nachschub-Scan): zufällige Namen ergäben bei gleichem Seed eine andere Reihenfolge der Nachschub-Aufgaben und damit eine andere
/// Folge von Zufallszahlen je Vorgang. Jeder Fehlschlag nennt Seed, Schritt und die letzten Serveraufrufe; die Folge lässt sich mit
/// der Umgebungsvariablen <c>LAGER_LEDGER_SEED</c> nachspielen (oder mit einem anderen Seed erkunden).
/// </summary>
public class LedgerInvariantTests
{
    private const int Operations = 200;
    private const int DefaultSeed = 21;

    /// <summary>Der feste Seed des Laufs; <c>LAGER_LEDGER_SEED</c> überschreibt ihn (zum Nachspielen eines gemeldeten Fehlschlags).</summary>
    private static readonly int Seed = int.TryParse(Environment.GetEnvironmentVariable("LAGER_LEDGER_SEED"), out var fromEnvironment)
        ? fromEnvironment
        : DefaultSeed;

    private readonly ITestOutputHelper _output;

    public LedgerInvariantTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task After_a_random_sequence_of_200_bookings_the_ledger_matches_the_stock_for_every_article()
    {
        using var factory = new LagerApiFactory();
        var w = new WorldBuilder(factory);
        var driver = await Driver.CreateAsync(w, Seed);

        for (var i = 1; i <= Operations; i++)
        {
            await driver.RunOneAsync();
            if (i % 50 != 0) continue;
            await AssertConsistentAsync(w, driver, $"Zwischenprüfung nach {i} Vorgängen");
        }

        _output.WriteLine(driver.Summary());
        await AssertConsistentAsync(w, driver, "Schlussprüfung");

        // Die Folge hat wirklich alle Buchungswege durchlaufen (sonst wäre die Invariante nur für einen Teil geprüft). Das gilt für
        // den festen Seed; eine andere Folge (LAGER_LEDGER_SEED) darf einen Weg auslassen - der Nachschub-Scan legt nur dann
        // Aufgaben an, wenn ein Artikel im Hot-Pick-Platz unter die Schwelle fällt, während ein Reserve-Platz ihn noch hat, und das
        // hängt vom Zufall ab (mehrere Seeds lassen den Nachschub aus) -, die Invarianten oben gelten für jede Folge.
        if (Seed == DefaultSeed)
        {
            foreach (var operation in new[] { "inbound", "adjust", "pack", "return", "replenishment", "inventory" })
                Assert.True(driver.Succeeded.GetValueOrDefault(operation) >= 1, $"Vorgang '{operation}' ist nie gelungen (Seed {Seed}): {driver.Summary()}");
            var reasons = await w.DbAsync(db => db.StockMovements.Select(m => m.Reason).Distinct().ToListAsync());
            Assert.Contains(StockMovementReason.Inbound, reasons);
            Assert.Contains(StockMovementReason.Adjust, reasons);
            Assert.Contains(StockMovementReason.Pick, reasons);
            Assert.Contains(StockMovementReason.Return, reasons);
            Assert.Contains(StockMovementReason.ReplenishmentOut, reasons);
            Assert.Contains(StockMovementReason.Inventory, reasons);
        }

        // Die API sieht denselben Bestand wie die Datenbank
        var http = await w.AdminAsync();
        var apiTotal = (await (await http.GetAsync("/api/stock")).ExpectAsync<List<StockItemDto>>()).Sum(s => (long)s.Quantity);
        var dbTotal = await w.DbAsync(db => db.StockItems.SumAsync(s => (long)s.Quantity));
        Assert.True(dbTotal == apiTotal, $"API {apiTotal} statt Datenbank {dbTotal} (Seed {Seed}, nach {Operations} Vorgängen)");
    }

    [Fact]
    public async Task The_same_seed_replays_the_same_sequence_of_server_calls_in_two_separate_hosts()
    {
        // Ohne diese Eigenschaft wäre "fester Seed" wertlos: ein Fehlschlag ließe sich nicht nachspielen. Zwei Hosts, dieselbe
        // Folge: gleiche Vorgänge, gleiche Antworten (Statuscodes), gleicher Endbestand je Artikel.
        var first = await RunAsync(operations: 60);
        var second = await RunAsync(operations: 60);

        Assert.Equal(first.Trace, second.Trace);
        Assert.Equal(first.Stock, second.Stock);
        Assert.True(first.Trace.Count > 60, "Die Folge hat kaum Serveraufrufe ausgelöst.");

        static async Task<(List<string> Trace, List<string> Stock)> RunAsync(int operations)
        {
            using var factory = new LagerApiFactory();
            var w = new WorldBuilder(factory);
            var driver = await Driver.CreateAsync(w, DefaultSeed);
            for (var i = 0; i < operations; i++) await driver.RunOneAsync();
            await AssertConsistentAsync(w, driver, "Wiederholungslauf");

            var stock = await w.DbAsync(async db => (await db.StockItems.AsNoTracking()
                    .Join(db.Articles, s => s.ArticleId, a => a.Id, (s, a) => new { a.Sku, s.Quantity, s.LotNumber, s.StorageLocationId })
                    .Join(db.StorageLocations, x => x.StorageLocationId, b => b.Id, (x, b) => new { x.Sku, BinCode = b.Code, x.LotNumber, x.Quantity })
                    .ToListAsync())
                .Select(x => $"{x.Sku} @ {x.BinCode} [{x.LotNumber ?? "-"}] = {x.Quantity}")
                .Order(StringComparer.Ordinal)
                .ToList());
            return (driver.Trace, stock);
        }
    }

    /// <summary>
    /// Prüft die Invarianten und bricht bei einem Verstoß oder einem 5xx mit einem Bericht ab, der Seed, Schritt und die letzten
    /// Serveraufrufe nennt - damit der Fehlschlag mit <c>LAGER_LEDGER_SEED</c> nachgespielt werden kann.
    /// </summary>
    private static async Task AssertConsistentAsync(WorldBuilder w, Driver driver, string moment)
    {
        var violations = await w.LedgerViolationsAsync();
        if (violations.Count == 0 && driver.ServerErrors.Count == 0) return;

        var report = new System.Text.StringBuilder()
            .AppendLine($"Bestandsinvariante verletzt ({moment}): Seed {driver.Seed}, Schritt {driver.Step} von {Operations}, zuletzt '{driver.Current}'.")
            .AppendLine($"Nachspielen: Umgebungsvariable LAGER_LEDGER_SEED={driver.Seed} setzen und diesen Test allein ausführen.");
        if (violations.Count > 0)
            report.AppendLine("Verstöße:").AppendLine(string.Join(Environment.NewLine, violations.Select(v => "  - " + v)));
        if (driver.ServerErrors.Count > 0)
            report.AppendLine("Serverfehler (5xx):").AppendLine(string.Join(Environment.NewLine, driver.ServerErrors.Select(e => "  - " + e)));
        report.AppendLine("Letzte Serveraufrufe (Schritt/Vorgang: Status):").AppendLine(driver.RecentTrace(30));
        Assert.Fail(report.ToString());
    }

    [Fact]
    public async Task The_invariant_check_reports_a_stock_change_without_a_movement()
    {
        // Gegenprobe: ohne sie könnte der Test oben auch bestehen, weil die Prüfung nie etwas findet.
        using var factory = new LagerApiFactory();
        var w = new WorldBuilder(factory);
        var world = await w.BuildAsync();
        var manager = await w.ClientAsync("Manager");
        var article = await w.AddArticleAsync();
        await manager.ReceiveAsync(new ApiCalls.Receipt(article.Id, world.PickA, 10));
        Assert.Empty(await w.LedgerViolationsAsync(article.Id));

        // Bestand von Hand geändert (an der Buchung vorbei) -> Ledger und Bestand laufen auseinander
        await w.DbAsync(async db =>
        {
            var row = await db.StockItems.SingleAsync(s => s.ArticleId == article.Id);
            row.Add(3);
            await db.SaveChangesAsync();
        });
        Assert.Contains(await w.LedgerViolationsAsync(article.Id), v => v.StartsWith("Ledger passt nicht zum Bestand"));

        // Altbestand ohne Movement, negativer Bestand: ebenfalls gemeldet
        var legacy = await w.AddArticleAsync();
        await w.AddLegacyStockAsync(legacy.Id, world.PickB, 4);
        Assert.NotEmpty(await w.LedgerViolationsAsync(legacy.Id));
        await w.DbAsync(db => db.Database.ExecuteSqlRawAsync("UPDATE StockItems SET Quantity = -1 WHERE ArticleId = {0}", legacy.Id));
        Assert.Contains(await w.LedgerViolationsAsync(legacy.Id), v => v.StartsWith("Negativer Bestand"));
    }

    // ---- der Zufallsgenerator ---------------------------------------------------------------------------------------

    /// <summary>Führt zufällige Vorgänge über HTTP aus und merkt sich, was gelang, was abgelehnt wurde und was ein 5xx war.</summary>
    private sealed class Driver
    {
        private static readonly (string? Lot, DateTime? Expiry)[] Lots =
        {
            (null, null), ("LOT-1", WorldBuilder.InDays(30)), ("LOT-2", WorldBuilder.InDays(90)),
        };

        private readonly Random _random;
        private readonly HttpClient _http;
        private readonly List<WorldBuilder.ArticleRef> _articles = new();
        private readonly List<WorldBuilder.Bin> _bins = new();
        private readonly List<WorldBuilder.Bin> _sellableBins = new();
        private readonly Dictionary<Guid, HashSet<int>> _receivedLots = new();
        private readonly List<(Guid OrderId, Guid ArticleId, int Quantity)> _packed = new();
        private readonly Dictionary<Guid, int> _returned = new();

        public Dictionary<string, int> Succeeded { get; } = new();
        public Dictionary<string, int> Rejected { get; } = new();
        public List<string> ServerErrors { get; } = new();

        /// <summary>Nummer des laufenden Vorgangs (ab 1) und sein Name: für Fehlermeldungen, damit ein Fehlschlag ein Schritt mit Seed ist.</summary>
        public int Step { get; private set; }
        public string Current { get; private set; } = "(Aufbau)";

        /// <summary>Jeder Serveraufruf als "Schritt Vorgang: Status": die Spur, an der zwei Läufe mit demselben Seed verglichen werden.</summary>
        public List<string> Trace { get; } = new();

        public int Seed { get; }
        private int _serial;

        private Driver(int seed, HttpClient http)
        {
            Seed = seed;
            _random = new Random(seed);
            _http = http;
        }

        /// <summary>Die Testwelt mit FESTEN Codes und SKUs (siehe Klassenkommentar): dieselbe Welt bei jedem Lauf.</summary>
        public static async Task<Driver> CreateAsync(WorldBuilder w, int seed)
        {
            var driver = new Driver(seed, await w.ClientAsync("Manager"));
            var site = await w.AddSiteAsync("WH-LEDGER");
            driver._bins.Add(await w.AddBinAsync(site, x: 1_000, code: "LG-PICK-A"));
            driver._bins.Add(await w.AddBinAsync(site, x: 2_500, code: "LG-PICK-B"));
            driver._bins.Add(await w.AddBinAsync(site, BinType.HotPick, WorldBuilder.DefaultReplenishmentThreshold, x: 4_000, code: "LG-HOT"));
            driver._bins.Add(await w.AddBinAsync(site, BinType.Reserve, x: 5_500, code: "LG-RESERVE-1"));
            driver._bins.Add(await w.AddBinAsync(site, BinType.Reserve, x: 6_500, code: "LG-RESERVE-2"));
            driver._sellableBins.AddRange(driver._bins);
            for (var i = 0; i < 3; i++)
            {
                var article = await w.AddArticleAsync(sku: $"LG-SKU-{i + 1}", priceCents: 100 + i * 25);
                driver._articles.Add(article);
                driver._receivedLots[article.Id] = new HashSet<int> { 0 };
            }
            return driver;
        }

        public string Summary() =>
            "ok: " + string.Join(", ", Succeeded.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}")) +
            " | abgelehnt: " + string.Join(", ", Rejected.OrderBy(k => k.Key).Select(k => $"{k.Key}={k.Value}"));

        /// <summary>Die letzten <paramref name="count"/> Zeilen der Spur, eine je Zeile.</summary>
        public string RecentTrace(int count) => string.Join(Environment.NewLine, Trace.TakeLast(count).Select(t => "  " + t));

        public async Task RunOneAsync()
        {
            Step++;
            var roll = _random.Next(100);
            Current = roll < 30 ? "inbound" : roll < 50 ? "adjust" : roll < 75 ? "pack" : roll < 87 ? "return" : roll < 95 ? "replenishment" : "inventory";
            try
            {
                if (roll < 30) await InboundAsync();
                else if (roll < 50) await AdjustAsync();
                else if (roll < 75) await PackAsync();
                else if (roll < 87) await ReturnAsync();
                else if (roll < 95) await ReplenishmentAsync();
                else await InventoryAsync();
            }
            catch (Exception ex)
            {
                // Eine fehlgeschlagene Erwartung mitten im Vorgang (unerwarteter Status, leere Antwort) bekommt den Zusammenhang dazu.
                throw new InvalidOperationException(
                    $"Seed {Seed}, Schritt {Step} ('{Current}') ist fehlgeschlagen: {ex.Message}{Environment.NewLine}Letzte Serveraufrufe:{Environment.NewLine}{RecentTrace(15)}", ex);
            }
        }

        /// <summary>Ein eindeutiger, aber vom Zufall unabhängiger Name (Zähler statt Guid): dieselbe Folge trägt dieselben Namen.</summary>
        private string Name(string prefix) => $"{prefix}-LG-{++_serial:D4}";

        // ---- Vorgänge ----

        private async Task InboundAsync()
        {
            var article = Pick(_articles);
            var bin = Pick(_bins);
            var lotIndex = _random.Next(Lots.Length);
            var (lot, expiry) = Lots[lotIndex];

            var shipment = Track("inbound-draft", await _http.PostAsJsonAsync("/api/inbound",
                new CreateInboundShipmentRequest(Name("WE"), null, null)));
            if (shipment is null) return;
            var draft = await ReadAsync<InboundShipmentDto>(shipment);
            var line = await _http.PostAsJsonAsync($"/api/inbound/{draft.Id}/lines",
                new AddInboundLineRequest(article.Id, bin.Id, _random.Next(1, 26), lot, expiry));
            if (Track("inbound-line", line) is null) return;
            if (Track("inbound", await _http.PostAsync($"/api/inbound/{draft.Id}/receive", null)) is not null)
                _receivedLots[article.Id].Add(lotIndex);
        }

        private async Task AdjustAsync()
        {
            var article = Pick(_articles);
            var bin = Pick(_bins);
            var (lot, expiry) = Lots[_random.Next(Lots.Length)];
            var delta = _random.Next(-10, 16);
            if (delta == 0) delta = 1;
            Track("adjust", await _http.PostAsJsonAsync("/api/stock/adjust", new AdjustStockRequest(article.Id, bin.Id, delta, lot, expiry)));
        }

        private async Task PackAsync()
        {
            var article = Pick(_articles);
            var quantity = _random.Next(1, 7);
            var placed = Track("order", await PlaceAsync(article.Id, quantity));
            if (placed is null) return;
            var order = await ReadAsync<Lager.Contracts.Orders.OrderDto>(placed);

            var generated = Track("generate", await _http.PostAsJsonAsync("/api/picklists/generate", new GeneratePickListRequest(new[] { order.Id })));
            if (generated is null)
            {
                // zu wenig Bestand: die Bestellung wird storniert, damit sie keine spätere Liste blockiert
                Track("cancel", await _http.PostAsync($"/api/orders/{order.Id}/cancel", null));
                return;
            }

            var list = await ReadAsync<PickListDto>(generated);
            if (_random.Next(4) == 0) Track("mark-picked", await _http.PostAsync($"/api/picklists/{list.Id}/mark-picked", null));
            var partial = _random.Next(4) == 0;
            var pack = Track("pack", await _http.PostAsJsonAsync($"/api/picklists/{list.Id}/pack",
                new PackPickListRequest(list.Items.Select(i => new ConfirmPackedItemRequest(i.Id, partial ? _random.Next(0, i.Quantity + 1) : i.Quantity)).ToList())));
            if (pack is not null) _packed.Add((order.Id, article.Id, quantity));
        }

        private Task<HttpResponseMessage> PlaceAsync(Guid articleId, int quantity) => _http.PlaceOrderRawAsync(articleId, quantity, Name("ORD"));

        private async Task ReturnAsync()
        {
            var candidates = _packed.Where(p => p.Quantity - _returned.GetValueOrDefault(p.OrderId) > 0).ToList();
            if (candidates.Count == 0) return;
            var (orderId, articleId, quantity) = Pick(candidates);
            var open = quantity - _returned.GetValueOrDefault(orderId);
            var returnQuantity = _random.Next(1, Math.Min(open, 3) + 1);
            var lotIndex = Pick(_receivedLots[articleId].ToList());
            var lot = Lots[lotIndex].Lot;

            var created = Track("return-create", await _http.PostAsJsonAsync("/api/returns",
                new CreateReturnShipmentRequest(orderId, null, null, new[] { new CreateReturnLineRequest(articleId, returnQuantity, lot) })));
            if (created is null) return;
            _returned[orderId] = _returned.GetValueOrDefault(orderId) + returnQuantity;
            var returnShipment = await ReadAsync<ReturnShipmentDto>(created);

            var result = new[] { "Sellable", "Sellable", "BGrade", "Defect", "Destroy" }[_random.Next(5)];
            var bin = result == "Sellable" ? Pick(_sellableBins) : null;
            var qc = await _http.PutAsJsonAsync($"/api/returns/{returnShipment.Id}/lines/{returnShipment.Lines.Single().Id}/qc",
                new SetQcRequest(result, bin?.Id, null));
            if (Track("return-qc", qc) is null) return;
            Track("return", await _http.PostAsync($"/api/returns/{returnShipment.Id}/process", null));
        }

        private async Task ReplenishmentAsync()
        {
            var scan = Track("scan", await _http.PostAsJsonAsync("/api/replenishment/scan", new ScanReplenishmentRequest()));
            if (scan is null) return;
            foreach (var task in await ReadAsync<List<ReplenishmentTaskDto>>(scan))
            {
                var actual = _random.Next(1, task.SuggestedQty + 1);
                Track("replenishment", await _http.PostAsJsonAsync($"/api/replenishment/{task.Id}/complete", new CompleteReplenishmentRequest(actual)));
            }
        }

        private async Task InventoryAsync()
        {
            var bin = Pick(_bins);
            var started = Track("inventory-start", await _http.PostAsJsonAsync("/api/inventory/start",
                new StartInventoryRequest(Name("Inventur"), bin.Id)));
            if (started is null) return;
            var count = await ReadAsync<InventoryCountDto>(started);
            foreach (var line in count.Lines)
            {
                var counted = Math.Max(0, line.ExpectedQty + _random.Next(-2, 4));
                if (Track("inventory-count", await _http.PutAsJsonAsync($"/api/inventory/{count.Id}/lines/{line.Id}", new SetCountRequest(counted, null))) is null) return;
            }
            Track("inventory", await _http.PostAsync($"/api/inventory/{count.Id}/reconcile", null));
        }

        // ---- Hilfen ----

        private T Pick<T>(IReadOnlyList<T> items) => items[_random.Next(items.Count)];

        /// <summary>Zählt das Ergebnis; liefert die Antwort nur bei Erfolg (2xx). Ein 5xx wird als Fehler des Laufs gemerkt.</summary>
        private HttpResponseMessage? Track(string operation, HttpResponseMessage response)
        {
            Trace.Add($"{Step,3} {operation}: {(int)response.StatusCode}");
            if ((int)response.StatusCode >= 500)
            {
                ServerErrors.Add($"Schritt {Step}, {operation}: {(int)response.StatusCode} {response.Content.ReadAsStringAsync().GetAwaiter().GetResult()}");
                return null;
            }
            var bucket = response.IsSuccessStatusCode ? Succeeded : Rejected;
            bucket[operation] = bucket.GetValueOrDefault(operation) + 1;
            return response.IsSuccessStatusCode ? response : null;
        }

        private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
            await response.ExpectAsync<T>(response.StatusCode);
    }
}
