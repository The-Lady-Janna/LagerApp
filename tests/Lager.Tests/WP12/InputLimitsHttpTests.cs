using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lager.Contracts.Articles;
using Lager.Contracts.PickLists;
using Lager.Contracts.Stock;
using Lager.Contracts.Warehouse;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP12;

/// <summary>Ein Host mit Demodaten (Lager, Regale, Bestand) + eingeloggter Admin für die Eingabe-Grenzen-Tests.</summary>
public sealed class SeededErrorApiFixture : IAsyncLifetime
{
    public ErrorApiFactory Factory { get; } = new(seed: true);
    public HttpClient Admin { get; private set; } = null!;

    public async Task InitializeAsync() => Admin = await Factory.CreateClient().AsReadyAdminAsync();

    public Task DisposeAsync()
    {
        Admin.Dispose();
        Factory.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Obergrenzen und Pflichtangaben der Requests über HTTP: zu große Zahlen/Listen werden mit 400 und Feldfehlern
/// (<c>errors</c>) abgewiesen, BEVOR ein Service läuft (der DoS über InitialBinCount und Wand-Punkte).
/// </summary>
public class InputLimitsHttpTests : IClassFixture<SeededErrorApiFixture>
{
    private readonly SeededErrorApiFixture _api;
    public InputLimitsHttpTests(SeededErrorApiFixture api) => _api = api;

    private HttpClient Admin => _api.Admin;

    private static readonly PositionDto Origin = new(0, 0, 0);

    private async Task<Guid> AnyAisleIdAsync()
    {
        var layout = (await Admin.GetFromJsonAsync<List<WarehouseDto>>("/api/warehouse/layout"))!;
        return layout.SelectMany(w => w.Zones).SelectMany(z => z.Aisles).First().Id;
    }

    private async Task<Guid> AnyWarehouseIdAsync() =>
        (await Admin.GetFromJsonAsync<List<WarehouseDto>>("/api/warehouse/layout"))!.First().Id;

    private static async Task<JsonElement> ErrorsAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var body = await ErrorApiFactory.JsonAsync(response);
        Assert.Equal("validation_failed", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("correlationId").GetString()));
        // detail fasst die erste Meldung zusammen (das Frontend zeigt nur diesen Text).
        Assert.StartsWith("Die Eingabe ist ungültig", body.GetProperty("detail").GetString());
        return body.GetProperty("errors");
    }

    private static CreateShelfRequest Shelf(Guid aisleId, string code, int bins) =>
        new(aisleId, code, Origin, 2000, 600, 2000, bins, 400, 600, 300, 20_000);

    // ---- Regal: InitialBinCount ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_shelf_with_100000_initial_bins_is_a_400_with_field_errors_and_returns_immediately()
    {
        var aisle = await AnyAisleIdAsync();

        // Vorher lief hier eine quadratische Schleife über 100000 Fächer (Minuten). Eine Zeitmessung mit harter Grenze taugt dafür
        // nicht (auf einem ausgelasteten Rechner kippt sie ohne Fehler im Code); der Test prüft stattdessen das, was "sofort" fachlich
        // heißt: Die Abweisung kommt vor dem Service, also gibt es danach kein Regal. WaitAsync schützt nur davor, dass die alte
        // Schleife den Testlauf Minuten festhält - die 60 s liegen weit über jeder echten Antwortzeit (Millisekunden).
        var response = await Admin.PostAsJsonAsync("/api/warehouse/shelves", Shelf(aisle, "WP12-DOS", 100_000))
            .WaitAsync(TimeSpan.FromSeconds(60));
        var errors = await ErrorsAsync(response);

        Assert.True(errors.TryGetProperty("InitialBinCount", out var messages));
        Assert.Contains("500", messages[0].GetString());

        var layout = (await Admin.GetFromJsonAsync<List<WarehouseDto>>("/api/warehouse/layout"))!;
        Assert.DoesNotContain(layout.SelectMany(w => w.Zones).SelectMany(z => z.Aisles).SelectMany(a => a.Shelves), s => s.Code == "WP12-DOS");
    }

    [Fact]
    public async Task The_bin_count_limit_is_500_inclusive()
    {
        var aisle = await AnyAisleIdAsync();

        var over = await Admin.PostAsJsonAsync("/api/warehouse/shelves", Shelf(aisle, "WP12-BINS-501", 501));
        var max = await Admin.PostAsJsonAsync("/api/warehouse/shelves", Shelf(aisle, "WP12-BINS-500", 500));

        await ErrorsAsync(over);
        Assert.Equal(HttpStatusCode.Created, max.StatusCode);
        var shelf = (await max.Content.ReadFromJsonAsync<ShelfDto>())!;
        Assert.Equal(500, shelf.Locations.Count);
    }

    [Fact]
    public async Task Shelf_dimensions_must_be_positive_and_the_coordinates_are_bounded()
    {
        var aisle = await AnyAisleIdAsync();
        var request = new CreateShelfRequest(aisle, "WP12-BAD", new PositionDto(2_000_000, 0, 0), 0, -5, 2000, 3, 400, 600, 300, 20_000);

        var errors = await ErrorsAsync(await Admin.PostAsJsonAsync("/api/warehouse/shelves", request));

        Assert.True(errors.TryGetProperty("Position.XMm", out _));
        Assert.True(errors.TryGetProperty("WidthMm", out _));
        Assert.True(errors.TryGetProperty("DepthMm", out _));
        Assert.False(errors.TryGetProperty("HeightMm", out _)); // 2000 ist gültig
    }

    [Fact]
    public async Task Bin_sizes_only_matter_when_bins_are_created()
    {
        var aisle = await AnyAisleIdAsync();

        var withoutBins = await Admin.PostAsJsonAsync("/api/warehouse/shelves",
            new CreateShelfRequest(aisle, "WP12-EMPTY", Origin, 2000, 600, 2000, 0, 0, 0, 0, 0));
        var withBins = await Admin.PostAsJsonAsync("/api/warehouse/shelves",
            new CreateShelfRequest(aisle, "WP12-ZERO", Origin, 2000, 600, 2000, 2, 0, 600, 300, 1000));

        Assert.Equal(HttpStatusCode.Created, withoutBins.StatusCode);
        Assert.True((await ErrorsAsync(withBins)).TryGetProperty("BinWidthMm", out _));
    }

    [Fact]
    public async Task A_shelf_code_too_long_for_the_generated_bin_codes_is_rejected()
    {
        var response = await Admin.PostAsJsonAsync("/api/warehouse/shelves", Shelf(await AnyAisleIdAsync(), new string('A', 62), 3));

        Assert.True((await ErrorsAsync(response)).TryGetProperty("Code", out _));
    }

    // ---- Wände ----------------------------------------------------------------------------------------------------

    private static IReadOnlyList<PositionDto> Points(int count) =>
        Enumerable.Range(0, count).Select(i => new PositionDto(i * 10, (i % 7) * 10, 0)).ToList();

    [Fact]
    public async Task A_wall_with_5000_points_is_a_400_with_field_errors()
    {
        var warehouse = await AnyWarehouseIdAsync();

        var response = await Admin.PostAsJsonAsync("/api/warehouse/walls", new CreateWallRequest(warehouse, "zu groß", Points(5000), 200));
        var errors = await ErrorsAsync(response);

        Assert.True(errors.TryGetProperty("Points", out var messages));
        Assert.Contains("500", messages[0].GetString());
        // Nichts wurde gespeichert, die Wegeberechnung bleibt unbeeinträchtigt.
        var walls = await Admin.GetFromJsonAsync<List<WallDto>>("/api/warehouse/walls");
        Assert.DoesNotContain(walls!, w => w.Label == "zu groß");
    }

    [Fact]
    public async Task Wall_point_limits_apply_to_create_and_update_alike()
    {
        var warehouse = await AnyWarehouseIdAsync();
        var created = await Admin.PostAsJsonAsync("/api/warehouse/walls", new CreateWallRequest(warehouse, "WP12-Wand", Points(500), 200));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode); // 500 Punkte sind erlaubt
        var wall = (await created.Content.ReadFromJsonAsync<WallDto>())!;

        var tooMany = await Admin.PutAsJsonAsync($"/api/warehouse/walls/{wall.Id}/points", new UpdateWallPointsRequest(Points(501)));
        var tooFew = await Admin.PutAsJsonAsync($"/api/warehouse/walls/{wall.Id}/points", new UpdateWallPointsRequest(Points(1)));
        var far = await Admin.PutAsJsonAsync($"/api/warehouse/walls/{wall.Id}/points",
            new UpdateWallPointsRequest(new[] { Origin, new PositionDto(0, 1_000_001, 0) }));
        var ok = await Admin.PutAsJsonAsync($"/api/warehouse/walls/{wall.Id}/points", new UpdateWallPointsRequest(Points(3)));

        await ErrorsAsync(tooMany);
        await ErrorsAsync(tooFew);
        Assert.Contains((await ErrorsAsync(far)).EnumerateObject(), e => e.Name.Contains("YMm"));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task A_wall_needs_a_positive_thickness()
    {
        var response = await Admin.PostAsJsonAsync("/api/warehouse/walls",
            new CreateWallRequest(await AnyWarehouseIdAsync(), null, Points(2), 0));

        Assert.True((await ErrorsAsync(response)).TryGetProperty("ThicknessMm", out _));
    }

    [Fact]
    public async Task Bin_type_and_pick_point_type_must_be_known_names_not_numbers()
    {
        var bins = await Admin.GetFromJsonAsync<List<StorageLocationDto>>("/api/warehouse/storage-locations");
        var bin = bins!.First();
        var warehouse = await AnyWarehouseIdAsync();

        var numericBinType = await Admin.PutAsJsonAsync($"/api/warehouse/storage-locations/{bin.Id}/bin-type", new SetBinTypeRequest("1", 0));
        var unknownBinType = await Admin.PutAsJsonAsync($"/api/warehouse/storage-locations/{bin.Id}/bin-type", new SetBinTypeRequest("Kühlhaus", 0));
        var hotPick = await Admin.PutAsJsonAsync($"/api/warehouse/storage-locations/{bin.Id}/bin-type", new SetBinTypeRequest("hotpick", 5));
        var pickPoint = await Admin.PostAsJsonAsync("/api/warehouse/pick-points", new CreatePickPointRequest(warehouse, "", "Mitte", Origin));

        await ErrorsAsync(numericBinType);
        await ErrorsAsync(unknownBinType);
        Assert.Equal(HttpStatusCode.OK, hotPick.StatusCode); // Groß-/Kleinschreibung egal, wie bisher
        var pickPointErrors = await ErrorsAsync(pickPoint);
        Assert.True(pickPointErrors.TryGetProperty("Label", out _));
        Assert.True(pickPointErrors.TryGetProperty("Type", out _));
    }

    // ---- Pickliste / Packen --------------------------------------------------------------------------------------

    [Fact]
    public async Task GeneratePickList_needs_one_to_200_distinct_orders()
    {
        var one = Guid.NewGuid();
        var empty = await Admin.PostAsJsonAsync("/api/picklists/generate", new GeneratePickListRequest(Array.Empty<Guid>()));
        var duplicates = await Admin.PostAsJsonAsync("/api/picklists/generate", new GeneratePickListRequest(new[] { one, one }));
        var tooMany = await Admin.PostAsJsonAsync("/api/picklists/generate",
            new GeneratePickListRequest(Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToList()));
        var missing = await Admin.PostAsJsonAsync("/api/picklists/generate", new { });

        await ErrorsAsync(empty);
        Assert.Contains("doppelte", (await ErrorsAsync(duplicates)).GetProperty("OrderIds")[0].GetString());
        Assert.Contains("200", (await ErrorsAsync(tooMany)).GetProperty("OrderIds")[0].GetString());
        await ErrorsAsync(missing);
    }

    [Fact]
    public async Task A_pack_request_needs_sane_quantities_and_no_repeated_positions()
    {
        var item = Guid.NewGuid();
        var pickList = Guid.NewGuid();

        var huge = await Admin.PostAsJsonAsync($"/api/picklists/{pickList}/pack",
            new PackPickListRequest(new[] { new ConfirmPackedItemRequest(item, int.MaxValue) }));
        var negative = await Admin.PostAsJsonAsync($"/api/picklists/{pickList}/pack",
            new PackPickListRequest(new[] { new ConfirmPackedItemRequest(item, -1) }));
        var repeated = await Admin.PostAsJsonAsync($"/api/picklists/{pickList}/pack",
            new PackPickListRequest(new[] { new ConfirmPackedItemRequest(item, 1), new ConfirmPackedItemRequest(item, 2) }));
        var missing = await Admin.PostAsJsonAsync($"/api/picklists/{pickList}/pack", new { });

        Assert.Contains((await ErrorsAsync(huge)).EnumerateObject(), e => e.Name.Contains("ActualQuantity"));
        await ErrorsAsync(negative);
        await ErrorsAsync(repeated);
        await ErrorsAsync(missing);
    }

    [Fact]
    public async Task Wave_and_cart_requests_are_bounded()
    {
        var tooManyOrders = Enumerable.Range(0, 501).Select(_ => Guid.NewGuid()).ToList();

        var wave = await Admin.PostAsJsonAsync("/api/pick-waves", new CreatePickWaveRequest(new string('x', 501), null, tooManyOrders));
        var cart = await Admin.PostAsJsonAsync("/api/cart-configs", new CreatePickCartConfigRequest("", 0, 0, 20_000, 500, 0));

        var waveErrors = await ErrorsAsync(wave);
        Assert.True(waveErrors.TryGetProperty("Description", out _));
        Assert.True(waveErrors.TryGetProperty("OrderIds", out _));
        var cartErrors = await ErrorsAsync(cart);
        foreach (var field in new[] { "Name", "LevelCount", "LevelWidthMm", "LevelDepthMm", "MaxWeightGrams" })
            Assert.True(cartErrors.TryGetProperty(field, out _), field);
        Assert.False(cartErrors.TryGetProperty("LevelHeightMm", out _));
    }

    // ---- Bestandskorrektur / Artikel -----------------------------------------------------------------------------

    [Theory]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    [InlineData(1_000_001)]
    [InlineData(-1_000_001)]
    [InlineData(0)]
    public async Task Stock_adjustments_outside_1_to_1000000_are_rejected(int delta)
    {
        var response = await Admin.PostAsJsonAsync("/api/stock/adjust",
            new AdjustStockRequest(Guid.NewGuid(), Guid.NewGuid(), delta, null, null));

        Assert.True((await ErrorsAsync(response)).TryGetProperty("Delta", out _));
    }

    [Fact]
    public async Task Article_text_and_list_limits_are_enforced()
    {
        static object Article(string sku, Action<Dictionary<string, object?>>? change = null)
        {
            var article = new Dictionary<string, object?>
            {
                ["sku"] = sku, ["name"] = "Testartikel", ["description"] = null,
                ["dimensions"] = new { lengthMm = 10, widthMm = 10, heightMm = 10 }, ["weightGrams"] = 10,
                ["stacking"] = new { isStackable = false, stackingAxis = "Z", stackingIncrementMm = 0, maxStackCount = (int?)null },
            };
            change?.Invoke(article);
            return article;
        }

        var longDescription = await Admin.PostAsJsonAsync("/api/articles", Article("WP12-A1", a => a["description"] = new string('d', 2001)));
        var commaSku = await Admin.PostAsJsonAsync("/api/articles", Article("WP12-A2", a => a["alternativeSkus"] = new[] { "OK-1", "A,B" }));
        var manySkus = await Admin.PostAsJsonAsync("/api/articles", Article("WP12-A3", a => a["alternativeSkus"] = Enumerable.Range(0, 501).Select(i => $"S{i}").ToArray()));
        var badBundle = await Admin.PostAsJsonAsync("/api/articles", Article("WP12-A4", a => a["bundleComponents"] = new[] { new CreateBundleComponentRequest(Guid.NewGuid(), 0) }));
        var badAxis = await Admin.PostAsJsonAsync("/api/articles",
            Article("WP12-A5", a => a["stacking"] = new { isStackable = true, stackingAxis = "Q", stackingIncrementMm = 0, maxStackCount = (int?)null }));
        var hugeWeight = await Admin.PostAsJsonAsync("/api/articles", Article("WP12-A6", a => a["weightGrams"] = int.MaxValue));
        var seasonBackwards = await Admin.PostAsJsonAsync("/api/articles",
            Article("WP12-A7", a => { a["validFrom"] = new DateTime(2026, 6, 1); a["validUntil"] = new DateTime(2026, 5, 1); }));
        var fine = await Admin.PostAsJsonAsync("/api/articles", Article("WP12-A8", a => a["description"] = new string('d', 2000)));

        Assert.True((await ErrorsAsync(longDescription)).TryGetProperty("Description", out _));
        Assert.True((await ErrorsAsync(commaSku)).TryGetProperty("AlternativeSkus", out _));
        await ErrorsAsync(manySkus);
        Assert.True((await ErrorsAsync(badBundle)).TryGetProperty("BundleComponents", out _));
        Assert.Contains((await ErrorsAsync(badAxis)).EnumerateObject(), e => e.Name.Contains("StackingAxis"));
        Assert.True((await ErrorsAsync(hugeWeight)).TryGetProperty("WeightGrams", out _));
        Assert.True((await ErrorsAsync(seasonBackwards)).TryGetProperty("ValidUntil", out _));
        Assert.Equal(HttpStatusCode.Created, fine.StatusCode);
    }

    // ---- Query-Parameter --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/api/reports/dead-stock?days=100000", "days")]
    [InlineData("/api/reports/dead-stock?days=0", "days")]
    [InlineData("/api/reports/dashboard?range=-5", "range")]
    [InlineData("/api/reports/stock-trend/00000000-0000-4000-8000-000000000001?days=3661", "days")]
    [InlineData("/api/audit?take=5000", "take")]
    [InlineData("/api/slotting/suggestions?rangeDays=99999&top=10", "rangeDays")]
    [InlineData("/api/slotting/suggestions?top=0", "top")]
    public async Task Period_and_paging_parameters_beyond_their_limits_are_a_400(string url, string field)
    {
        var response = await Admin.GetAsync(url);

        Assert.True((await ErrorsAsync(response)).TryGetProperty(field, out _));
    }

    [Theory]
    [InlineData("/api/reports/dead-stock?days=3660")]
    [InlineData("/api/reports/dashboard?range=30")]
    [InlineData("/api/audit?take=1000")]
    [InlineData("/api/slotting/suggestions?rangeDays=30&top=5")]
    public async Task Parameters_within_their_limits_pass(string url)
    {
        var response = await Admin.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Datenbank-Fehler der echten Endpunkte ------------------------------------------------------------------

    [Fact]
    public async Task Deleting_a_bin_that_still_holds_stock_is_a_409_in_use_instead_of_a_500()
    {
        var stock = (await Admin.GetFromJsonAsync<List<StockItemDto>>("/api/stock"))!;
        var occupied = stock.First(s => s.Quantity > 0).StorageLocationId;

        var response = await Admin.DeleteAsync($"/api/warehouse/storage-locations/{occupied}");
        var body = await ErrorApiFactory.JsonAsync(response);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("in_use", body.GetProperty("code").GetString());
        Assert.Contains("verwendet", body.GetProperty("detail").GetString());
        // Der Lagerplatz existiert weiter.
        var bins = await Admin.GetFromJsonAsync<List<StorageLocationDto>>("/api/warehouse/storage-locations");
        Assert.Contains(bins!, b => b.Id == occupied);
    }
}
