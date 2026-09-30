using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lager.Application.Abstractions;
using Lager.Contracts.Orders;
using Lager.Contracts.Shipping;
using Lager.Domain.Orders;
using Lager.Tests.Infrastructure;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Lager.Tests.WP13;

/// <summary>
/// Versand: Sendungen nur für gepackte Bestellungen, Versand setzt die Bestellung auf Shipped, nur konfigurierte
/// Carrier sind wählbar (und erzeugen ihr Label wirklich), Pflichtmaße, sichere Tracking-URL, atomare Nummern.
/// </summary>
public class ShipmentTests : IClassFixture<Wp13Fixture>
{
    private readonly Wp13Fixture _fx;
    private Wp13World W => _fx.World;

    public ShipmentTests(Wp13Fixture fixture) => _fx = fixture;

    private static CreateShipmentRequest ShipmentFor(Guid orderId, string carrier = "MANUAL") =>
        new(orderId, null, carrier, 300, 200, 100, 1500);

    private Task<ShipmentDto> CreateAsync(Guid orderId, string carrier = "MANUAL") =>
        W.ShipmentsAsync(s => s.CreateAsync(ShipmentFor(orderId, carrier)));

    /// <summary>Anlegen, Tracking zuweisen (manuell) - die Sendung ist danach Labeled.</summary>
    private async Task<ShipmentDto> LabeledAsync(Guid orderId)
    {
        var shipment = await CreateAsync(orderId);
        return (await W.ShipmentsAsync(s => s.AssignTrackingAsync(shipment.Id, new AssignTrackingRequest("TRACK-" + shipment.ShipmentNumber))))!;
    }

    private static void AssertRule(Exception ex, string code)
    {
        Assert.IsType<InvalidOperationException>(ex);
        Assert.Equal(code, ex.Data["code"]);
    }

    // ---- Kopplung an die Bestellung ---------------------------------------

    [Fact]
    public async Task A_shipment_can_only_be_created_for_a_packed_order()
    {
        var (article, _) = await W.AddStockedArticleAsync(20);
        var fresh = await W.AddOrderAsync(article, 1);
        var picking = await W.AddOrderAsync(article, 1);
        await W.GenerateAsync(picking);
        var cancelled = await W.AddOrderAsync(article, 1);
        await W.OrdersAsync(s => s.CancelAsync(cancelled));

        foreach (var order in new[] { fresh, picking, cancelled })
            AssertRule(await Assert.ThrowsAnyAsync<Exception>(() => CreateAsync(order)), "order_not_packed");

        var packed = await W.AddPackedOrderAsync(article, 1);
        Assert.Equal("Ready", (await CreateAsync(packed)).Status);
        var unknownOrder = await Assert.ThrowsAsync<ArgumentException>(() => CreateAsync(Guid.NewGuid()));
        Assert.Equal("unknown_order", unknownOrder.Data["code"]);
    }

    [Fact]
    public async Task Shipping_the_last_open_shipment_sets_the_order_to_Shipped()
    {
        var (article, _) = await W.AddStockedArticleAsync(20);
        var order = await W.AddPackedOrderAsync(article, 2);
        var first = await LabeledAsync(order);
        var second = await LabeledAsync(order);

        await W.ShipmentsAsync(s => s.MarkShippedAsync(first.Id));
        Assert.Equal(OrderStatus.Packed, await W.OrderStatusAsync(order));   // die zweite Sendung ist noch offen

        await W.ShipmentsAsync(s => s.MarkShippedAsync(second.Id));
        Assert.Equal(OrderStatus.Shipped, await W.OrderStatusAsync(order));

        // Zustellung ändert am Bestellstatus nichts mehr
        await W.ShipmentsAsync(s => s.MarkDeliveredAsync(first.Id));
        Assert.Equal(OrderStatus.Shipped, await W.OrderStatusAsync(order));
    }

    [Fact]
    public async Task Cancelling_a_shipment_never_resets_the_order_but_may_complete_it()
    {
        var (article, _) = await W.AddStockedArticleAsync(20);

        // einzige Sendung storniert: die Bestellung bleibt Packed und bekommt eine neue Sendung
        var single = await W.AddPackedOrderAsync(article, 1);
        var only = await CreateAsync(single);
        Assert.True(await W.ShipmentsAsync(s => s.CancelAsync(only.Id)));
        Assert.Equal(OrderStatus.Packed, await W.OrderStatusAsync(single));
        var replacement = await LabeledAsync(single);
        await W.ShipmentsAsync(s => s.MarkShippedAsync(replacement.Id));
        Assert.Equal(OrderStatus.Shipped, await W.OrderStatusAsync(single));

        // zwei Sendungen: die erste ist raus, die zweite offene wird storniert -> alles, was raus sollte, ist raus
        var pair = await W.AddPackedOrderAsync(article, 1);
        var sent = await LabeledAsync(pair);
        var spare = await CreateAsync(pair);
        await W.ShipmentsAsync(s => s.MarkShippedAsync(sent.Id));
        Assert.Equal(OrderStatus.Packed, await W.OrderStatusAsync(pair));
        await W.ShipmentsAsync(s => s.CancelAsync(spare.Id));
        Assert.Equal(OrderStatus.Shipped, await W.OrderStatusAsync(pair));
    }

    // ---- Carrier ----------------------------------------------------------

    [Fact]
    public async Task Only_configured_carriers_can_be_chosen_and_the_stubs_are_shown_as_unavailable()
    {
        var (article, _) = await W.AddStockedArticleAsync(20);
        var order = await W.AddPackedOrderAsync(article, 1);

        var carriers = await W.ShipmentsAsync(s => Task.FromResult(s.ListCarriers()));

        Assert.True(carriers.Single(c => c.Code == "MANUAL").IsConfigured);
        foreach (var code in new[] { "DHL", "UPS" })
        {
            var carrier = carriers.Single(c => c.Code == code);
            Assert.False(carrier.IsConfigured);
            Assert.False(string.IsNullOrWhiteSpace(carrier.Note));
        }
        // kein Entwickler-Jargon in Anzeigenamen und Hinweisen
        Assert.DoesNotContain(carriers, c => (c.DisplayName + c.Note).Contains("TODO", StringComparison.OrdinalIgnoreCase)
                                             || (c.DisplayName + c.Note).Contains("Stub", StringComparison.OrdinalIgnoreCase));

        AssertRule(await Assert.ThrowsAnyAsync<Exception>(() => CreateAsync(order, "DHL")), "carrier_not_available");
        AssertRule(await Assert.ThrowsAnyAsync<Exception>(() => CreateAsync(order, "GIBTS-NICHT")), "unknown_carrier");

        var http = await _fx.AdminAsync();
        var listed = (await http.GetFromJsonAsync<List<CarrierDto>>("/api/shipments/carriers"))!;
        Assert.Equal(new[] { "DHL", "MANUAL", "UPS" }, listed.Select(c => c.Code).Order().ToArray());
        Assert.False(listed.Single(c => c.Code == "DHL").IsConfigured);
    }

    private sealed class FakeCarrier : ICarrierAdapter
    {
        public string CarrierCode => "FAKE";
        public string DisplayName => "Fake Express";
        public List<CreateLabelRequest> Labels { get; } = new();

        public Task<CarrierLabelResult> CreateLabelAsync(CreateLabelRequest req, CancellationToken ct = default)
        {
            Labels.Add(req);
            return Task.FromResult(new CarrierLabelResult(false, "FAKE-TRACK-1", "https://fake.example/t/FAKE-TRACK-1", 495));
        }
    }

    [Fact]
    public async Task A_configured_carrier_really_creates_the_label_with_the_recipient()
    {
        var fake = new FakeCarrier();
        using var host = _fx.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<ICarrierAdapter>(fake)));
        var w = new Wp13World(host.Services);
        var (article, _) = await w.AddStockedArticleAsync(20);
        var (customer, address, _) = await w.AddCustomerAsync();
        var order = await w.AddPackedOrderAsync(article, 1, customer, address);

        var shipment = await w.ShipmentsAsync(s => s.CreateAsync(ShipmentFor(order, "FAKE")));
        // ohne eigene Tracking-Nummer: der Adapter erzeugt sie
        var labeled = (await w.ShipmentsAsync(s => s.AssignTrackingAsync(shipment.Id, new AssignTrackingRequest())))!;

        Assert.Equal("Labeled", labeled.Status);
        Assert.Equal(("FAKE-TRACK-1", "https://fake.example/t/FAKE-TRACK-1", 495), (labeled.TrackingNumber, labeled.TrackingUrl, labeled.CostCents));
        var label = Assert.Single(fake.Labels);
        Assert.Equal((shipment.Id, shipment.ShipmentNumber, 1500, 300, 200, 100), (label.ShipmentId, label.ShipmentNumber, label.WeightGrams, label.LengthMm, label.WidthMm, label.HeightMm));
        Assert.Equal(("Hafenstr. 5", "Halle 2", "20457", "Hamburg", "DE"),
            (label.RecipientStreet, label.RecipientStreet2, label.RecipientZip, label.RecipientCity, label.RecipientCountry));
        Assert.StartsWith("Kunde ", label.RecipientName);
    }

    [Fact]
    public async Task No_label_is_ordered_for_a_shipment_that_is_cancelled_or_already_shipped()
    {
        var fake = new FakeCarrier();
        using var host = _fx.Factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<ICarrierAdapter>(fake)));
        var w = new Wp13World(host.Services);
        var (article, _) = await w.AddStockedArticleAsync(20);
        var order = await w.AddPackedOrderAsync(article, 2);
        var cancelled = await w.ShipmentsAsync(s => s.CreateAsync(ShipmentFor(order, "FAKE")));
        var sent = await w.ShipmentsAsync(s => s.CreateAsync(ShipmentFor(order, "FAKE")));
        await w.ShipmentsAsync(s => s.CancelAsync(cancelled.Id));
        await w.ShipmentsAsync(s => s.AssignTrackingAsync(sent.Id, new AssignTrackingRequest()));
        await w.ShipmentsAsync(s => s.MarkShippedAsync(sent.Id));
        Assert.Single(fake.Labels);   // das eine gewollte Label

        // Weder die stornierte noch die versendete Sendung darf beim Carrier ein weiteres (kostenpflichtiges) Label auslösen.
        foreach (var shipment in new[] { cancelled, sent })
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                w.ShipmentsAsync(s => s.AssignTrackingAsync(shipment.Id, new AssignTrackingRequest())));
        Assert.Single(fake.Labels);
    }

    [Fact]
    public async Task A_manual_carrier_needs_the_tracking_number_from_the_user()
    {
        var (article, _) = await W.AddStockedArticleAsync(20);
        var shipment = await CreateAsync(await W.AddPackedOrderAsync(article, 1));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            W.ShipmentsAsync(s => s.AssignTrackingAsync(shipment.Id, new AssignTrackingRequest("  "))));

        Assert.Equal("tracking_number_required", ex.Data["code"]);
    }

    // ---- Eingaben über HTTP -----------------------------------------------

    [Fact]
    public async Task Missing_or_placeholder_dimensions_are_rejected_and_a_dangerous_tracking_url_too()
    {
        var (article, _) = await W.AddStockedArticleAsync(20);
        var order = await W.AddPackedOrderAsync(article, 1);
        var admin = await _fx.AdminAsync();

        foreach (var request in new[]
                 {
                     new CreateShipmentRequest(order, null, "MANUAL", 0, 200, 100, 1500),
                     new CreateShipmentRequest(order, null, "MANUAL", 300, 200, 100, 0),
                     new CreateShipmentRequest(order, null, "MANUAL", 300, 200, 100, 1_000_001),
                     new CreateShipmentRequest(order, null, "", 300, 200, 100, 1500),
                 })
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/shipments", request)).StatusCode);

        var created = await admin.PostAsJsonAsync("/api/shipments", ShipmentFor(order));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var shipment = (await created.Content.ReadFromJsonAsync<ShipmentDto>())!;

        foreach (var url in new[] { "javascript:alert(1)", "data:text/html,x", "ftp://x.example/1", "kein-link", "https://x.example/" + new string('a', 500) })
        {
            var response = await admin.PostAsJsonAsync($"/api/shipments/{shipment.Id}/tracking", new AssignTrackingRequest("T-1", url));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var ok = await admin.PostAsJsonAsync($"/api/shipments/{shipment.Id}/tracking", new AssignTrackingRequest("T-1", "https://t.example/T-1", 350));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal("https://t.example/T-1", (await ok.Content.ReadFromJsonAsync<ShipmentDto>())!.TrackingUrl);
    }

    // ---- Nummern ----------------------------------------------------------

    [Fact]
    public async Task Shipment_numbers_come_from_the_atomic_counter_and_never_repeat_under_parallel_requests()
    {
        // Eigene Instanz: die Zähler-Zeile existiert noch nicht, auch das gleichzeitige Erst-Anlegen muss funktionieren.
        using var factory = new LagerApiFactory();
        var world = new Wp13World(factory.Services);
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 10).Select(_ => Task.Run(async () =>
        {
            await gate.Task;
            return await world.WithAsync<IShipmentRepository, long>(r => r.NextSequenceAsync());
        })).ToArray();
        gate.SetResult();

        var numbers = await Task.WhenAll(tasks);

        Assert.Equal(Enumerable.Range(1, 10).Select(n => (long)n), numbers.Order());
    }

    // ---- Kompletter Ablauf über die API -----------------------------------

    [Fact]
    public async Task An_order_runs_from_New_to_Shipped_through_the_API()
    {
        var (article, _) = await W.AddStockedArticleAsync(20);
        var admin = await _fx.AdminAsync();
        var number = Wp13World.Unique("FLOW");

        var created = await admin.PostAsJsonAsync("/api/orders/manual", Wp13World.OrderRequest(number, article, 4));
        var order = (await created.Content.ReadFromJsonAsync<OrderDto>())!;
        Assert.Equal("New", order.Status);
        async Task<string> StatusAsync() => (await admin.GetFromJsonAsync<OrderDto>($"/api/orders/{order.Id}"))!.Status;

        // noch nicht gepackt: keine Sendung
        var early = await admin.PostAsJsonAsync("/api/shipments", ShipmentFor(order.Id));
        Assert.Equal(HttpStatusCode.Conflict, early.StatusCode);
        using (var body = JsonDocument.Parse(await early.Content.ReadAsStringAsync()))
            Assert.Equal("order_not_packed", body.RootElement.GetProperty("code").GetString());

        var generated = await admin.PostAsJsonAsync("/api/picklists/generate", new Lager.Contracts.PickLists.GeneratePickListRequest(new[] { order.Id }));
        var list = (await generated.Content.ReadFromJsonAsync<Lager.Contracts.PickLists.PickListDto>())!;
        Assert.Equal("Picking", await StatusAsync());

        (await admin.PostAsync($"/api/picklists/{list.Id}/mark-picked", null)).EnsureSuccessStatusCode();
        Assert.Equal("Picked", await StatusAsync());

        (await admin.PostAsJsonAsync($"/api/picklists/{list.Id}/pack", new Lager.Contracts.PickLists.PackPickListRequest(
            list.Items.Select(i => new Lager.Contracts.PickLists.ConfirmPackedItemRequest(i.Id, i.Quantity)).ToList()))).EnsureSuccessStatusCode();
        Assert.Equal("Packed", await StatusAsync());
        Assert.Equal(16, await W.StockQuantityAsync(article));

        var shipmentResponse = await admin.PostAsJsonAsync("/api/shipments", ShipmentFor(order.Id));
        Assert.Equal(HttpStatusCode.Created, shipmentResponse.StatusCode);
        var shipment = (await shipmentResponse.Content.ReadFromJsonAsync<ShipmentDto>())!;
        Assert.Equal("Packed", await StatusAsync());

        (await admin.PostAsJsonAsync($"/api/shipments/{shipment.Id}/tracking", new AssignTrackingRequest("T-4711", "https://t.example/4711"))).EnsureSuccessStatusCode();
        (await admin.PostAsync($"/api/shipments/{shipment.Id}/ship", null)).EnsureSuccessStatusCode();

        Assert.Equal("Shipped", await StatusAsync());
        Assert.Equal(OrderStatus.Shipped, await W.OrderStatusAsync(order.Id));
    }
}
