using System.Net;
using System.Net.Http.Json;
using System.Text;
using Lager.Contracts.Orders;
using Lager.Domain.Articles;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP26;

/// <summary>
/// Eine API-Instanz (eigene SQLite-Datei, Seed=false) mit Standard-Lager und eingeloggtem Admin für alle Tests einer Klasse.
/// Die Tests legen ihre Daten mit eindeutigen Namen an und stören sich deshalb nicht.
/// </summary>
public sealed class LabelsFixture : IAsyncLifetime
{
    public LagerApiFactory Factory { get; } = new();
    public WorldBuilder W { get; }
    public HttpClient Admin { get; private set; } = null!;
    public WorldBuilder.World Warehouse { get; private set; } = null!;

    public LabelsFixture() => W = new WorldBuilder(Factory);

    public async Task InitializeAsync()
    {
        Admin = await W.AdminAsync();
        Warehouse = await W.BuildAsync();
    }

    public Task DisposeAsync()
    {
        Factory.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Die ZPL-Endpunkte <c>/api/labels/{bin|article|order}/{id}.zpl</c>: ein einheitliches Format (UTF-8-Text, Dateiname mit
/// <c>.zpl</c>, Code 128 als <c>^BC</c>), optional mit Kopienzahl. Die Rollen (Picker) prüft die Endpunkt-Matrix aus WP02.
/// </summary>
public class LabelsEndpointTests : IClassFixture<LabelsFixture>
{
    private readonly LabelsFixture _fx;

    public LabelsEndpointTests(LabelsFixture fixture) => _fx = fixture;

    [Fact]
    public async Task Bin_label_is_a_ZPL_download_with_a_Code128_barcode_of_the_bin_code()
    {
        var bin = _fx.Warehouse.PickA;

        var response = await _fx.Admin.GetAsync($"/api/labels/bin/{bin.Id}.zpl");
        var zpl = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertZplDownload(response, $"bin-{bin.Code}.zpl");
        Assert.StartsWith("^XA", zpl);
        Assert.Contains($"^BCN,80,Y,N,N^FD{bin.Code}^FS", zpl);
        Assert.EndsWith("^XZ", zpl.TrimEnd());
        Assert.DoesNotContain("^PQ", zpl);
    }

    [Fact]
    public async Task Article_label_carries_the_SKU_as_barcode_and_the_name_in_UTF8()
    {
        var sku = WorldBuilder.Unique("ART");
        var id = await _fx.W.DbAsync(async db =>
        {
            var article = new Article(sku, "Schraube Größe 8 äöü", new Dimensions(10, 10, 40), 12, StackingInfo.NotStackable);
            db.Articles.Add(article);
            await db.SaveChangesAsync();
            return article.Id;
        });

        var response = await _fx.Admin.GetAsync($"/api/labels/article/{id}.zpl");
        var bytes = await response.Content.ReadAsByteArrayAsync();
        var zpl = new UTF8Encoding(false).GetString(bytes);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertZplDownload(response, $"article-{sku}.zpl");
        Assert.Equal((byte)'^', bytes[0]);                                   // kein BOM vor dem ersten Befehl
        Assert.Contains($"^BCN,80,Y,N,N^FD{sku}^FS", zpl);
        Assert.Contains("^CI28", zpl);                                       // UTF-8 im Drucker einschalten
        Assert.Contains("Schraube Größe 8 äöü", zpl);
    }

    [Fact]
    public async Task Order_label_carries_the_order_number_and_the_customer_reference()
    {
        var article = await _fx.W.AddArticleAsync();
        var number = WorldBuilder.Unique("ORD");
        var created = await _fx.Admin.PostAsJsonAsync("/api/orders/manual",
            new CreateOrderRequest(number, "K-4711", new[] { new CreateOrderLineRequest(article.Id, 1) }));
        created.EnsureSuccessStatusCode();
        var order = (await created.Content.ReadFromJsonAsync<OrderDto>())!;

        var response = await _fx.Admin.GetAsync($"/api/labels/order/{order.Id}.zpl");
        var zpl = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        AssertZplDownload(response, $"order-{number}.zpl");
        Assert.Contains($"^BCN,70,Y,N,N^FD{number}^FS", zpl);
        Assert.Contains("Kunde: K-4711", zpl);
    }

    [Fact]
    public async Task Copies_are_printed_with_one_print_quantity_and_only_within_the_allowed_range()
    {
        var bin = _fx.Warehouse.PickB;

        var three = await (await _fx.Admin.GetAsync($"/api/labels/bin/{bin.Id}.zpl?copies=3")).Content.ReadAsStringAsync();
        Assert.Contains("^PQ3", three);
        Assert.Equal(1, three.Split("^XA").Length - 1);                      // eine Etikett-Beschreibung, der Drucker vervielfacht

        var article = await _fx.W.AddArticleAsync();
        Assert.Contains("^PQ25", await (await _fx.Admin.GetAsync($"/api/labels/article/{article.Id}.zpl?copies=25")).Content.ReadAsStringAsync());

        foreach (var invalid in new[] { "0", "-2", "501", "abc" })
        {
            var response = await _fx.Admin.GetAsync($"/api/labels/bin/{bin.Id}.zpl?copies={invalid}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        }
        // Grenzen sind erlaubt
        Assert.Equal(HttpStatusCode.OK, (await _fx.Admin.GetAsync($"/api/labels/bin/{bin.Id}.zpl?copies=500")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _fx.Admin.GetAsync($"/api/labels/bin/{bin.Id}.zpl?copies=1")).StatusCode);
    }

    [Fact]
    public async Task File_names_never_contain_path_characters_of_the_code()
    {
        var bin = await _fx.W.AddBinAsync(_fx.Warehouse.Site, code: "A/01 ..\\X:9");

        var response = await _fx.Admin.GetAsync($"/api/labels/bin/{bin.Id}.zpl");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var name = FileName(response);
        Assert.Equal("bin-A_01_.._X_9.zpl", name);
        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
        // Der Code selbst bleibt im Barcode unverändert (nur ^ und ~ fallen weg)
        Assert.Contains("^FDA/01 ..\\X:9^FS", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unknown_ids_are_404_and_anonymous_calls_are_401()
    {
        var unknown = Guid.NewGuid();
        foreach (var kind in new[] { "bin", "article", "order" })
            Assert.Equal(HttpStatusCode.NotFound, (await _fx.Admin.GetAsync($"/api/labels/{kind}/{unknown}.zpl")).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _fx.W.Anonymous().GetAsync($"/api/labels/bin/{_fx.Warehouse.PickA.Id}.zpl")).StatusCode);
    }

    [Fact]
    public async Task A_picker_may_download_but_a_viewer_may_not()
    {
        var bin = _fx.Warehouse.HotPick;
        var picker = await _fx.W.ClientAsync("Picker");
        var viewer = await _fx.W.ClientAsync("Viewer");

        Assert.Equal(HttpStatusCode.OK, (await picker.GetAsync($"/api/labels/bin/{bin.Id}.zpl?copies=2")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync($"/api/labels/bin/{bin.Id}.zpl")).StatusCode);
    }

    // ---- Hilfen ---------------------------------------------------------------------------------------------------

    private static void AssertZplDownload(HttpResponseMessage response, string expectedFileName)
    {
        Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType?.CharSet);
        Assert.Equal(expectedFileName, FileName(response));
        Assert.EndsWith(".zpl", FileName(response));
    }

    private static string FileName(HttpResponseMessage response)
    {
        var disposition = response.Content.Headers.ContentDisposition;
        Assert.NotNull(disposition);
        return (disposition.FileNameStar ?? disposition.FileName ?? "").Trim('"');
    }
}
