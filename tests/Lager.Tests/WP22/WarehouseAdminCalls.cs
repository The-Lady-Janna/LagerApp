using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lager.Contracts.Warehouse;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP22;

/// <summary>
/// Typisierte HTTP-Aufrufe für die Tests der Lager-Stammdaten. Die "Expect"-Varianten prüfen den Statuscode und zeigen bei einer
/// Abweichung den Body der Antwort; ein Fehler-Body wird als (Status, Code, Text) gelesen.
/// </summary>
internal static class WarehouseAdminCalls
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Ein durch die API aufgebautes Lager: Lager, Zone, Gang und Regal (mit Lagerplätzen).</summary>
    internal sealed record ApiSite(WarehouseDto Warehouse, ZoneDto Zone, AisleDto Aisle, ShelfDto Shelf)
    {
        public IReadOnlyList<StorageLocationDto> Bins => Shelf.Locations;
    }

    /// <summary>Fehlerantwort der API: Status, maschinenlesbarer Code und Text für Menschen.</summary>
    internal sealed record ApiError(HttpStatusCode Status, string? Code, string? Detail, JsonElement Body)
    {
        public bool HasFieldError(string field) =>
            Body.TryGetProperty("errors", out var errors) && errors.TryGetProperty(field, out _);
    }

    public static async Task<T> ExpectAsync<T>(this HttpResponseMessage response, HttpStatusCode expected)
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

    public static async Task<ApiError> ErrorAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Erwartet {(int)expected} {expected}, war {(int)response.StatusCode} {response.StatusCode}: {text}");
        var body = JsonDocument.Parse(text).RootElement.Clone();
        return new ApiError(
            response.StatusCode,
            body.TryGetProperty("code", out var code) ? code.GetString() : null,
            body.TryGetProperty("detail", out var detail) ? detail.GetString() : null,
            body);
    }

    // ---- Anlegen ---------------------------------------------------------------------------------------------------

    public static async Task<WarehouseDto> CreateWarehouseAsync(this HttpClient client, string? code = null, string name = "Testlager")
    {
        code ??= WorldBuilder.Unique("WH");
        return await (await client.PostAsJsonAsync("/api/warehouse", new CreateWarehouseRequest(code, name)))
            .ExpectAsync<WarehouseDto>(HttpStatusCode.Created);
    }

    public static async Task<ZoneDto> CreateZoneAsync(this HttpClient client, Guid warehouseId, string code = "Z-A", string name = "Zone A") =>
        await (await client.PostAsJsonAsync("/api/warehouse/zones", new CreateZoneRequest(warehouseId, code, name)))
            .ExpectAsync<ZoneDto>(HttpStatusCode.Created);

    public static async Task<AisleDto> CreateAisleAsync(this HttpClient client, Guid zoneId, string code = "A1") =>
        await (await client.PostAsJsonAsync("/api/warehouse/aisles", new CreateAisleRequest(zoneId, code)))
            .ExpectAsync<AisleDto>(HttpStatusCode.Created);

    public static CreateShelfRequest ShelfRequest(Guid aisleId, string code, int bins) =>
        new(aisleId, code, new PositionDto(0, 200, 0), 8_000, 600, 2_000, bins, 600, 600, 500, 50_000);

    public static async Task<ShelfDto> CreateShelfAsync(this HttpClient client, Guid aisleId, string code, int bins = 0) =>
        await (await client.PostAsJsonAsync("/api/warehouse/shelves", ShelfRequest(aisleId, code, bins)))
            .ExpectAsync<ShelfDto>(HttpStatusCode.Created);

    /// <summary>Baut Lager -> Zone -> Gang -> Regal (mit <paramref name="bins"/> Lagerplätzen) nur über die API auf. Alle Codes sind eindeutig (auch die der Lagerplätze).</summary>
    public static async Task<ApiSite> BuildSiteAsync(this HttpClient client, int bins = 4, string? code = null)
    {
        var warehouse = await client.CreateWarehouseAsync(code);
        var zone = await client.CreateZoneAsync(warehouse.Id);
        var aisle = await client.CreateAisleAsync(zone.Id);
        var shelf = await client.CreateShelfAsync(aisle.Id, "S-" + warehouse.Code, bins);
        return new ApiSite(warehouse, zone, aisle, shelf);
    }

    // ---- Lesen -----------------------------------------------------------------------------------------------------

    public static async Task<List<WarehouseDto>> LayoutAsync(this HttpClient client) =>
        await (await client.GetAsync("/api/warehouse/layout")).ExpectAsync<List<WarehouseDto>>(HttpStatusCode.OK);

    public static async Task<List<StorageLocationDto>> BinsAsync(this HttpClient client) =>
        await (await client.GetAsync("/api/warehouse/storage-locations")).ExpectAsync<List<StorageLocationDto>>(HttpStatusCode.OK);

    public static async Task<WarehouseDto?> FindWarehouseAsync(this HttpClient client, Guid id) =>
        (await client.LayoutAsync()).SingleOrDefault(w => w.Id == id);
}
