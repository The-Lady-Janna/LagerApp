using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lager.Contracts.Inbound;
using Lager.Contracts.Orders;
using Lager.Contracts.PickLists;
using Lager.Contracts.Stock;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP23;

/// <summary>
/// Eine API-Instanz (eigene SQLite-Datei, Seed=false) samt Standard-Lager pro Testklasse. Die Tests einer Klasse legen ihre
/// Artikel mit eindeutigen Namen an und stören sich deshalb nicht.
/// </summary>
public sealed class Wp23Fixture : IAsyncLifetime
{
    public LagerApiFactory Factory { get; } = new();
    public WorldBuilder W { get; }

    /// <summary>Das Standard-Lager (zwei Standard-Plätze, Hot-Pick, Reserve); erst nach <see cref="InitializeAsync"/> gesetzt.</summary>
    public WorldBuilder.World Warehouse { get; private set; } = null!;

    public Wp23Fixture() => W = new WorldBuilder(Factory);

    public async Task InitializeAsync() => Warehouse = await W.BuildAsync();

    public Task DisposeAsync()
    {
        Factory.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>Kleine HTTP-Helfer der WP23-Tests (bewusst eigenständig: keine Abhängigkeit von den Helfern anderer Testpakete).</summary>
internal static class Wp23Api
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Prüft den Statuscode (mit dem Body in der Fehlermeldung) und liest den Body als <typeparamref name="T"/>.</summary>
    public static async Task<T> ExpectAsync<T>(this HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Erwartet {(int)expected} {expected}, war {(int)response.StatusCode} {response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, Json) ?? throw new InvalidOperationException($"Leere Antwort: {body}");
    }

    /// <summary>Prüft den Statuscode und liefert den Fehler-Body (ProblemDetails: code, detail, ...).</summary>
    public static async Task<JsonElement> ExpectProblemAsync(this HttpResponseMessage response, HttpStatusCode expected, string? code = null)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Erwartet {(int)expected} {expected}, war {(int)response.StatusCode} {response.StatusCode}: {body}");
        var problem = JsonDocument.Parse(body).RootElement.Clone();
        if (code is not null)
            Assert.Equal(code, problem.TryGetProperty("code", out var c) ? c.GetString() : null);
        return problem;
    }

    public static string DetailOf(this JsonElement problem) =>
        problem.TryGetProperty("detail", out var detail) ? detail.GetString() ?? "" : "";

    /// <summary>Eine Wareneingangszeile: Artikel, Lagerplatz, Menge, Charge, MHD.</summary>
    public sealed record Line(Guid ArticleId, WorldBuilder.Bin Bin, int Quantity, string? Lot = null, DateTime? Expiry = null)
    {
        public AddInboundLineRequest ToRequest() => new(ArticleId, Bin.Id, Quantity, Lot, Expiry);
    }

    /// <summary>Lieferung mit Zeilen im Request anlegen (Entwurf) - der Weg der Erfassungsmaske.</summary>
    public static async Task<InboundShipmentDto> DraftWithLinesAsync(this HttpClient client, params Line[] lines) =>
        await (await client.PostAsJsonAsync("/api/inbound",
            new CreateInboundShipmentRequest(WorldBuilder.Unique("WE"), null, null, lines.Select(l => l.ToRequest()).ToList())))
            .ExpectAsync<InboundShipmentDto>(HttpStatusCode.Created);

    /// <summary>Wareneingang mit Zeilen im Request anlegen und buchen.</summary>
    public static async Task<InboundShipmentDto> ReceiveWithLinesAsync(this HttpClient client, params Line[] lines)
    {
        var draft = await client.DraftWithLinesAsync(lines);
        return await (await client.PostAsync($"/api/inbound/{draft.Id}/receive", null)).ExpectAsync<InboundShipmentDto>();
    }

    public static async Task<List<StockItemDto>> StockOfAsync(this HttpClient client, Guid articleId) =>
        await (await client.GetAsync($"/api/stock/article/{articleId}")).ExpectAsync<List<StockItemDto>>();

    /// <summary>Bestellung anlegen, kommissionieren, verpacken (bucht den Bestand ab, FEFO über die Chargen).</summary>
    public static async Task<OrderDto> PackedOrderAsync(this HttpClient client, Guid articleId, int quantity)
    {
        var order = await (await client.PostAsJsonAsync("/api/orders/manual", new CreateOrderRequest(
            WorldBuilder.Unique("ORD"), "Kunde WP23", new[] { new CreateOrderLineRequest(articleId, quantity) })))
            .ExpectAsync<OrderDto>(HttpStatusCode.Created);
        var list = await (await client.PostAsJsonAsync("/api/picklists/generate", new GeneratePickListRequest(new[] { order.Id })))
            .ExpectAsync<PickListDto>(HttpStatusCode.Created);
        await (await client.PostAsJsonAsync($"/api/picklists/{list.Id}/pack",
            new PackPickListRequest(list.Items.Select(i => new ConfirmPackedItemRequest(i.Id, i.Quantity)).ToList())))
            .ExpectAsync<PickListDto>();
        return order;
    }
}
