using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.PickLists;
using Lager.Contracts.Stock;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP21;

/// <summary>
/// Szenario 9 (Rollen-Stichprobe an den Grenzen des Ablaufs) und Szenario 10 (einheitliches Fehlerformat an echten Endpunkten).
/// Die vollständige Rollenmatrix aller Endpunkte steht in WP02; hier geht es darum, dass im laufenden Ablauf jede Rolle genau
/// das tun darf, was sie im Lager tut - und nichts von dem, was die nächste Rolle tut.
/// </summary>
public class RoleAndErrorScenarioTests : IClassFixture<WorkflowFixture>
{
    private readonly WorkflowFixture _fx;
    private WorldBuilder W => _fx.W;
    private WorldBuilder.World Site => _fx.Warehouse;

    public RoleAndErrorScenarioTests(WorkflowFixture fixture) => _fx = fixture;

    private static readonly string Unknown = Guid.NewGuid().ToString();

    /// <summary>Sammelt Abweichungen von der erwarteten Rollenmatrix, damit ein Fehlschlag alle Verstöße auf einmal zeigt.</summary>
    private sealed class Matrix
    {
        public List<string> Failures { get; } = new();

        public async Task ExpectAsync(string who, HttpClient client, HttpMethod method, string url, object? body, params HttpStatusCode[] allowed)
        {
            using var request = new HttpRequestMessage(method, url);
            if (body is not null) request.Content = JsonContent.Create(body);
            using var response = await client.SendAsync(request);
            if (!allowed.Contains(response.StatusCode))
                Failures.Add($"{who}: {method} {url} -> {(int)response.StatusCode}, erwartet {string.Join("/", allowed.Select(s => (int)s))}");
        }

        /// <summary>Verboten: 403.</summary>
        public Task DeniedAsync(string who, HttpClient client, HttpMethod method, string url, object? body = null) =>
            ExpectAsync(who, client, method, url, body, HttpStatusCode.Forbidden);

        /// <summary>Erlaubt: die Berechtigung greift (unbekannte Id -> 404 statt 403), es passiert nichts.</summary>
        public Task AllowedAsync(string who, HttpClient client, HttpMethod method, string url, object? body = null) =>
            ExpectAsync(who, client, method, url, body, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Every_role_may_do_exactly_its_step_of_the_workflow_and_not_the_next_ones()
    {
        var admin = await W.AdminAsync();
        var manager = await W.ClientAsync("Manager");
        var picker = await W.ClientAsync("Picker");
        var packer = await W.ClientAsync("Packer");
        var receiver = await W.ClientAsync("Receiver");
        var viewer = await W.ClientAsync("Viewer");
        var anonymous = W.Anonymous();
        var article = await W.AddArticleAsync();
        await manager.ReceiveAsync(new ApiCalls.Receipt(article.Id, Site.PickA, 30));

        var order = await manager.PlaceOrderAsync(article.Id, 4);
        var list = await manager.GenerateAsync(order.Id);
        var m = new Matrix();

        var adjust = new AdjustStockRequest(article.Id, Site.PickA.Id, 1, null, null);
        var newOrder = new Lager.Contracts.Orders.CreateOrderRequest("X", null, new[] { new Lager.Contracts.Orders.CreateOrderLineRequest(article.Id, 1) });
        var generate = new GeneratePickListRequest(new[] { order.Id });
        var inbound = new Lager.Contracts.Inbound.CreateInboundShipmentRequest("X", null, null);
        var shipment = new Lager.Contracts.Shipping.CreateShipmentRequest(order.Id, null, "MANUAL", 100, 100, 100, 500);

        // Viewer: lesen ja, nichts anlegen oder ändern
        foreach (var url in new[] { "/api/orders", "/api/stock", "/api/picklists", "/api/shipments", "/api/inbound", "/api/inventory" })
            await m.ExpectAsync("Viewer", viewer, HttpMethod.Get, url, null, HttpStatusCode.OK);
        await m.DeniedAsync("Viewer", viewer, HttpMethod.Post, "/api/orders/manual", newOrder);
        await m.DeniedAsync("Viewer", viewer, HttpMethod.Post, "/api/stock/adjust", adjust);
        await m.DeniedAsync("Viewer", viewer, HttpMethod.Post, "/api/picklists/generate", generate);
        await m.DeniedAsync("Viewer", viewer, HttpMethod.Post, $"/api/picklists/{Unknown}/mark-picked");
        await m.DeniedAsync("Viewer", viewer, HttpMethod.Post, $"/api/picklists/{Unknown}/pack", EmptyPack);
        await m.DeniedAsync("Viewer", viewer, HttpMethod.Post, "/api/inbound", inbound);
        await m.DeniedAsync("Viewer", viewer, HttpMethod.Post, "/api/shipments", shipment);
        await m.DeniedAsync("Viewer", viewer, HttpMethod.Post, $"/api/replenishment/{Unknown}/complete", new { actualQty = 1 });

        // Picker: meldet Picken fertig und schließt Nachschub ab - erzeugt und packt nichts
        await m.DeniedAsync("Picker", picker, HttpMethod.Post, "/api/picklists/generate", generate);
        await m.DeniedAsync("Picker", picker, HttpMethod.Post, $"/api/picklists/{Unknown}/pack", EmptyPack);
        await m.DeniedAsync("Picker", picker, HttpMethod.Post, "/api/shipments", shipment);
        await m.DeniedAsync("Picker", picker, HttpMethod.Post, "/api/stock/adjust", adjust);
        await m.DeniedAsync("Picker", picker, HttpMethod.Post, "/api/inbound", inbound);
        await m.DeniedAsync("Picker", picker, HttpMethod.Post, $"/api/orders/{Unknown}/cancel");
        await m.AllowedAsync("Picker", picker, HttpMethod.Post, $"/api/picklists/{Unknown}/mark-picked");
        await m.AllowedAsync("Picker", picker, HttpMethod.Post, $"/api/replenishment/{Unknown}/complete", new { actualQty = 1 });
        Assert.Empty(m.Failures);
        Assert.Equal("Picked", (await picker.MarkPickedAsync(list.Id)).Status);

        // Packer: packt und versendet - meldet kein Picken fertig, erzeugt keine Listen, bucht keinen Bestand
        await m.DeniedAsync("Packer", packer, HttpMethod.Post, $"/api/picklists/{Unknown}/mark-picked");
        await m.DeniedAsync("Packer", packer, HttpMethod.Post, "/api/picklists/generate", generate);
        await m.DeniedAsync("Packer", packer, HttpMethod.Post, "/api/inbound", inbound);
        await m.DeniedAsync("Packer", packer, HttpMethod.Post, "/api/stock/adjust", adjust);
        await m.DeniedAsync("Packer", packer, HttpMethod.Post, $"/api/shipments/{Unknown}/cancel");
        await m.AllowedAsync("Packer", packer, HttpMethod.Post, $"/api/picklists/{Unknown}/pack", EmptyPack);
        Assert.Empty(m.Failures);
        Assert.Equal("Completed", (await packer.PackAsync(list)).Status);
        Assert.Equal("Shipped", (await packer.ShipAsync(order.Id)).Status);

        // Receiver: nimmt Ware an und zählt - bucht keine Korrekturen, packt und gleicht keine Inventur ab
        await m.DeniedAsync("Receiver", receiver, HttpMethod.Post, "/api/stock/adjust", adjust);
        await m.DeniedAsync("Receiver", receiver, HttpMethod.Post, "/api/orders/manual", newOrder);
        await m.DeniedAsync("Receiver", receiver, HttpMethod.Post, $"/api/picklists/{Unknown}/pack", EmptyPack);
        await m.DeniedAsync("Receiver", receiver, HttpMethod.Post, $"/api/inventory/{Unknown}/reconcile");
        await m.DeniedAsync("Receiver", receiver, HttpMethod.Post, $"/api/inbound/{Unknown}/cancel");
        await m.AllowedAsync("Receiver", receiver, HttpMethod.Post, $"/api/inbound/{Unknown}/receive");
        await m.AllowedAsync("Receiver", receiver, HttpMethod.Put, $"/api/inventory/{Unknown}/lines/{Unknown}", new { countedQty = 1 });
        Assert.Equal("Draft", (await receiver.DraftInboundAsync(new ApiCalls.Receipt(article.Id, Site.PickA, 1))).Status);

        // Manager: alles Operative, aber nicht das Zurücksetzen und die Systemfunktionen des Admins
        await m.DeniedAsync("Manager", manager, HttpMethod.Delete, "/api/picklists");
        await m.DeniedAsync("Manager", manager, HttpMethod.Post, "/api/admin/backup");
        await m.DeniedAsync("Manager", manager, HttpMethod.Get, "/api/users");
        await m.ExpectAsync("Manager", manager, HttpMethod.Post, "/api/stock/adjust", adjust, HttpStatusCode.OK);

        // Admin: Benutzerverwaltung; ohne Anmeldung ist nichts erreichbar
        await m.ExpectAsync("Admin", admin, HttpMethod.Get, "/api/users", null, HttpStatusCode.OK);
        foreach (var (method, url) in new[] { (HttpMethod.Get, "/api/orders"), (HttpMethod.Get, "/api/stock"), (HttpMethod.Post, "/api/stock/adjust"), (HttpMethod.Post, "/api/picklists/generate") })
            await m.ExpectAsync("anonym", anonymous, method, url, method == HttpMethod.Post ? new { } : null, HttpStatusCode.Unauthorized);

        Assert.Empty(m.Failures);
    }

    /// <summary>Ein gültiger Pack-Body ohne Positionen: erreicht bei unbekannter Liste den Service (404), ohne etwas zu buchen.</summary>
    private static readonly object EmptyPack = new { items = Array.Empty<object>() };

    // ---- 10: Fehlerformat -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Error_responses_of_the_real_endpoints_are_problem_json_with_a_code_and_the_correlation_id_of_the_request()
    {
        var manager = await W.ClientAsync("Manager");
        var receiver = await W.ClientAsync("Receiver");
        var viewer = await W.ClientAsync("Viewer");
        var article = await W.AddArticleAsync();
        var inbound = await receiver.ReceiveAsync(new ApiCalls.Receipt(article.Id, Site.PickA, 5));

        var cases = new (string Name, HttpResponseMessage Response, HttpStatusCode Status, string Code)[]
        {
            ("nicht angemeldet", await W.Anonymous().GetAsync("/api/orders"), HttpStatusCode.Unauthorized, "unauthorized"),
            ("Rolle reicht nicht", await viewer.PostAsJsonAsync("/api/stock/adjust", new AdjustStockRequest(article.Id, Site.PickA.Id, 1, null, null)), HttpStatusCode.Forbidden, "forbidden"),
            ("Pickliste unbekannt", await manager.GetAsync($"/api/picklists/{Guid.NewGuid()}"), HttpStatusCode.NotFound, "not_found"),
            ("ungültige Bestellung", await manager.PostAsJsonAsync("/api/orders/manual",
                new Lager.Contracts.Orders.CreateOrderRequest("", null, Array.Empty<Lager.Contracts.Orders.CreateOrderLineRequest>())), HttpStatusCode.BadRequest, "validation_failed"),
            ("Wareneingang doppelt gebucht", await receiver.PostAsync($"/api/inbound/{inbound.Id}/receive", null), HttpStatusCode.Conflict, "conflict"),
        };

        foreach (var (name, response, status, code) in cases)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.True(response.StatusCode == status, $"{name}: {(int)response.StatusCode} statt {(int)status}: {text}");
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
            var body = System.Text.Json.JsonDocument.Parse(text).RootElement;
            Assert.Equal((int)status, body.GetProperty("status").GetInt32());
            Assert.Equal(code, body.GetProperty("code").GetString());
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("title").GetString()), name);
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("detail").GetString()), name);
            // Die Referenz-ID im Body ist die des Antwort-Headers (und damit der Logzeilen dieser Anfrage).
            var header = response.Headers.GetValues("X-Correlation-Id").Single();
            Assert.False(string.IsNullOrEmpty(header), name);
            Assert.Equal(header, body.GetProperty("correlationId").GetString());
            // Weder Stacktrace noch SQL noch Typnamen verlassen den Server.
            Assert.DoesNotContain("   at ", text);
            Assert.DoesNotContain("Exception", text);
            Assert.DoesNotContain("SELECT", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Domain_rule_conflicts_of_the_pick_and_order_endpoints_carry_a_readable_error_and_the_correlation_header()
    {
        // Ist-Verhalten (Abweichung vom WP12-Plan "überall problem+json"): PickLists-, Orders- und Shipments-Controller fangen
        // fachliche Fehler noch lokal und antworten mit { error } bzw. { code, error } als application/json. Der Test prüft nur,
        // was für Clients zählt und in beiden Formaten gilt - Status, lesbarer Text in "error" (so liest das Frontend ihn),
        // Korrelations-Header, kein Stacktrace. Wird der lokale Fang entfernt, bleibt er grün und prüft dann zusätzlich das
        // volle Format: kommt application/problem+json, müssen auch code und correlationId (= Antwort-Header) dabei sein.
        var manager = await W.ClientAsync("Manager");
        var packer = await W.ClientAsync("Packer");
        var article = await W.AddArticleAsync();
        await manager.ReceiveAsync(new ApiCalls.Receipt(article.Id, Site.PickA, 10));
        var (order, list) = await manager.PackedOrderAsync(article.Id, 2);

        var responses = new[]
        {
            await packer.PackRawAsync(list),                        // Pickliste schon verpackt
            await manager.GenerateRawAsync(order.Id),               // Bestellung nicht mehr New
            await manager.PostAsync($"/api/orders/{order.Id}/cancel", null),   // Bestellung schon gepackt
        };

        foreach (var response in responses)
        {
            var text = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            var body = System.Text.Json.JsonDocument.Parse(text).RootElement;
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()), text);
            var header = response.Headers.GetValues("X-Correlation-Id").Single();
            Assert.False(string.IsNullOrEmpty(header));
            if (response.Content.Headers.ContentType?.MediaType == "application/problem+json")
            {
                Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("code").GetString()), text);
                Assert.Equal(header, body.GetProperty("correlationId").GetString());
            }
            Assert.DoesNotContain("   at ", text);
        }
    }
}
