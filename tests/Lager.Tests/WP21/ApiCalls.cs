using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lager.Contracts.Inbound;
using Lager.Contracts.Orders;
using Lager.Contracts.PickLists;
using Lager.Contracts.Shipping;
using Lager.Contracts.Stock;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP21;

/// <summary>
/// Typisierte HTTP-Aufrufe für die Workflow-Tests. Die "Ok"-Varianten prüfen den erwarteten Statuscode und zeigen bei einer
/// Abweichung den Body der Antwort (das erspart das Raten, warum ein Schritt mitten im Ablauf scheitert); die "Raw"-Varianten
/// liefern die Antwort unverändert, damit Tests Fehlerfälle prüfen können.
/// </summary>
internal static class ApiCalls
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- Antworten lesen ------------------------------------------------------------------------------------------

    public static async Task<T> ExpectAsync<T>(this HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Erwartet {(int)expected} {expected}, war {(int)response.StatusCode} {response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, Json) ?? throw new InvalidOperationException($"Leere Antwort: {body}");
    }

    public static async Task ExpectStatusAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Erwartet {(int)expected} {expected}, war {(int)response.StatusCode} {response.StatusCode}: {body}");
    }

    public static async Task<JsonElement> BodyAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    public static string? CodeOf(this JsonElement body) =>
        body.TryGetProperty("code", out var code) ? code.GetString() : null;

    public static string? ErrorOf(this JsonElement body) =>
        body.TryGetProperty("error", out var error) ? error.GetString() : null;

    // ---- Bestellung, Pickliste, Packen ----------------------------------------------------------------------------

    public static Task<HttpResponseMessage> PlaceOrderRawAsync(this HttpClient client, Guid articleId, int quantity, string? number = null) =>
        client.PostAsJsonAsync("/api/orders/manual", new CreateOrderRequest(
            number ?? WorldBuilder.Unique("ORD"), "E2E", new[] { new CreateOrderLineRequest(articleId, quantity) }));

    public static async Task<OrderDto> PlaceOrderAsync(this HttpClient client, Guid articleId, int quantity, string? number = null) =>
        await (await client.PlaceOrderRawAsync(articleId, quantity, number)).ExpectAsync<OrderDto>(HttpStatusCode.Created);

    public static Task<HttpResponseMessage> GenerateRawAsync(this HttpClient client, params Guid[] orderIds) =>
        client.PostAsJsonAsync("/api/picklists/generate", new GeneratePickListRequest(orderIds));

    public static async Task<PickListDto> GenerateAsync(this HttpClient client, params Guid[] orderIds) =>
        await (await client.GenerateRawAsync(orderIds)).ExpectAsync<PickListDto>(HttpStatusCode.Created);

    public static Task<HttpResponseMessage> MarkPickedRawAsync(this HttpClient client, Guid pickListId) =>
        client.PostAsync($"/api/picklists/{pickListId}/mark-picked", null);

    public static async Task<PickListDto> MarkPickedAsync(this HttpClient client, Guid pickListId) =>
        await (await client.MarkPickedRawAsync(pickListId)).ExpectAsync<PickListDto>();

    /// <summary>Packt die Liste; <paramref name="actual"/> bestimmt die gepackte Menge je Position (Standard: die Planmenge).</summary>
    public static Task<HttpResponseMessage> PackRawAsync(this HttpClient client, PickListDto list, Func<PickItemDto, int>? actual = null) =>
        client.PostAsJsonAsync($"/api/picklists/{list.Id}/pack",
            new PackPickListRequest(list.Items.Select(i => new ConfirmPackedItemRequest(i.Id, actual?.Invoke(i) ?? i.Quantity)).ToList()));

    public static async Task<PickListDto> PackAsync(this HttpClient client, PickListDto list, Func<PickItemDto, int>? actual = null) =>
        await (await client.PackRawAsync(list, actual)).ExpectAsync<PickListDto>();

    /// <summary>Bestellung anlegen, kommissionieren, verpacken: danach steht sie auf Packed.</summary>
    public static async Task<(OrderDto Order, PickListDto List)> PackedOrderAsync(this HttpClient client, Guid articleId, int quantity)
    {
        var order = await client.PlaceOrderAsync(articleId, quantity);
        var list = await client.GenerateAsync(order.Id);
        var packed = await client.PackAsync(list);
        return (order, packed);
    }

    public static async Task<OrderDto> GetOrderAsync(this HttpClient client, Guid orderId) =>
        await (await client.GetAsync($"/api/orders/{orderId}")).ExpectAsync<OrderDto>();

    public static async Task<PickListDto> GetPickListAsync(this HttpClient client, Guid pickListId) =>
        await (await client.GetAsync($"/api/picklists/{pickListId}")).ExpectAsync<PickListDto>();

    // ---- Wareneingang und Bestand ---------------------------------------------------------------------------------

    /// <summary>Eine Wareneingangszeile: Artikel, Lagerplatz, Menge, Charge, MHD.</summary>
    public sealed record Receipt(Guid ArticleId, WorldBuilder.Bin Bin, int Quantity, string? Lot = null, DateTime? Expiry = null);

    public static async Task<InboundShipmentDto> DraftInboundAsync(this HttpClient client, params Receipt[] lines)
    {
        var shipment = await (await client.PostAsJsonAsync("/api/inbound",
            new CreateInboundShipmentRequest(WorldBuilder.Unique("WE"), "Lieferant E2E", null))).ExpectAsync<InboundShipmentDto>(HttpStatusCode.Created);
        foreach (var line in lines)
            shipment = await (await client.PostAsJsonAsync($"/api/inbound/{shipment.Id}/lines",
                new AddInboundLineRequest(line.ArticleId, line.Bin.Id, line.Quantity, line.Lot, line.Expiry))).ExpectAsync<InboundShipmentDto>();
        return shipment;
    }

    /// <summary>Wareneingang anlegen, Zeilen ergänzen und buchen.</summary>
    public static async Task<InboundShipmentDto> ReceiveAsync(this HttpClient client, params Receipt[] lines)
    {
        var draft = await client.DraftInboundAsync(lines);
        return await (await client.PostAsync($"/api/inbound/{draft.Id}/receive", null)).ExpectAsync<InboundShipmentDto>();
    }

    public static async Task<List<StockItemDto>> StockOfAsync(this HttpClient client, Guid articleId) =>
        await (await client.GetAsync($"/api/stock/article/{articleId}")).ExpectAsync<List<StockItemDto>>();

    public static async Task<int> TotalStockAsync(this HttpClient client, Guid articleId) =>
        (await client.StockOfAsync(articleId)).Sum(s => s.Quantity);

    // ---- Versand --------------------------------------------------------------------------------------------------

    public static async Task<ShipmentDto> CreateShipmentAsync(this HttpClient client, Guid orderId) =>
        await (await client.PostAsJsonAsync("/api/shipments",
            new CreateShipmentRequest(orderId, null, "MANUAL", 300, 200, 100, 1500))).ExpectAsync<ShipmentDto>(HttpStatusCode.Created);

    /// <summary>Sendung anlegen, Tracking (manuell) zuweisen und versenden.</summary>
    public static async Task<ShipmentDto> ShipAsync(this HttpClient client, Guid orderId)
    {
        var shipment = await client.CreateShipmentAsync(orderId);
        await (await client.PostAsJsonAsync($"/api/shipments/{shipment.Id}/tracking",
            new AssignTrackingRequest("TRACK-" + shipment.ShipmentNumber))).ExpectAsync<ShipmentDto>();
        return await (await client.PostAsync($"/api/shipments/{shipment.Id}/ship", null)).ExpectAsync<ShipmentDto>();
    }
}
