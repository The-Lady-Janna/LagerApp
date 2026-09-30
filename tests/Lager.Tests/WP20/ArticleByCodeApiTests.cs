using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Articles;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP20;

/// <summary>
/// GET /api/articles/by-code/{code}: löst gescannte Codes auf (GTIN, SKU exakt ohne Rücksicht auf Schreibweise, Alternativ-SKU).
/// Unbekannt = 404 mit Meldung, mehrdeutig = 409 mit der Kandidatenliste, lesen darf jeder Angemeldete.
/// </summary>
public class ArticleByCodeApiTests : IClassFixture<Wp20Fixture>
{
    private readonly Wp20Fixture _fx;

    public ArticleByCodeApiTests(Wp20Fixture fixture) => _fx = fixture;

    private static string Url(string code) => $"/api/articles/by-code/{Uri.EscapeDataString(code)}";

    [Fact]
    public async Task A_code_resolves_by_gtin_sku_and_alternative_sku()
    {
        var admin = await _fx.AdminAsync();
        var gtin = Wp20Api.NewGtin13();
        var sku = Wp20Api.Unique("Scan").ToUpperInvariant();
        var altSku = Wp20Api.Unique("Lieferant").ToUpperInvariant();
        var article = await admin.CreateArticleAsync(Wp20Api.Request(sku: sku, gtin: gtin, alternativeSkus: new[] { altSku }));

        foreach (var code in new[] { gtin, sku, altSku })
        {
            var response = await admin.GetAsync(Url(code));
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"'{code}' -> {(int)response.StatusCode}");
            var found = (await response.Content.ReadFromJsonAsync<ArticleDto>())!;
            Assert.Equal((article.Id, gtin), (found.Id, found.Gtin));
        }
    }

    [Fact]
    public async Task The_sku_lookup_ignores_case_and_surrounding_whitespace()
    {
        var admin = await _fx.AdminAsync();
        var sku = Wp20Api.Unique("Case").ToUpperInvariant();
        var article = await admin.CreateArticleAsync(Wp20Api.Request(sku: sku));

        var lower = await admin.GetFromJsonAsync<ArticleDto>(Url(sku.ToLowerInvariant()));
        var padded = await admin.GetFromJsonAsync<ArticleDto>(Url($"  {sku}\t"));

        Assert.Equal(article.Id, lower!.Id);
        Assert.Equal(article.Id, padded!.Id);
    }

    [Theory]
    [InlineData("REG/A-01")]         // Schrägstrich
    [InlineData("M8 x 40 (verz.)")]  // Leerzeichen und Klammern
    [InlineData("Ü-Blech+1%")]       // Umlaut, Plus- und Prozentzeichen
    public async Task Codes_with_special_characters_survive_the_url(string sku)
    {
        var admin = await _fx.AdminAsync();
        var unique = $"{sku}-{Wp20Api.Unique("X")}";
        var article = await admin.CreateArticleAsync(Wp20Api.Request(sku: unique));

        var response = await admin.GetAsync(Url(unique));

        Assert.True(response.StatusCode == HttpStatusCode.OK, $"'{unique}' -> {(int)response.StatusCode}");
        Assert.Equal(article.Id, (await response.Content.ReadFromJsonAsync<ArticleDto>())!.Id);
    }

    [Fact]
    public async Task A_upc_a_stored_on_the_article_is_found_by_its_ean13_scan_with_a_leading_zero()
    {
        var admin = await _fx.AdminAsync();
        var article = await admin.CreateArticleAsync(Wp20Api.Request(gtin: "012345678905"));   // UPC-A

        foreach (var scanned in new[] { "012345678905", "0012345678905", "00012345678905" })
        {
            var found = await admin.GetFromJsonAsync<ArticleDto>(Url(scanned));
            Assert.Equal(article.Id, found!.Id);
        }
    }

    [Fact]
    public async Task An_unknown_code_is_a_404_with_a_readable_message()
    {
        var admin = await _fx.AdminAsync();

        var response = await admin.GetAsync(Url("GIBT-ES-NICHT-4711"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", await response.ErrorCodeAsync());
        Assert.Contains("GIBT-ES-NICHT-4711", (await response.JsonAsync()).GetProperty("detail").GetString());
        // Auch eine gültige, aber nirgends vergebene GTIN: 404.
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(Url("4006381333931"))).StatusCode);
    }

    [Fact]
    public async Task The_alternative_sku_of_an_article_never_hides_the_article_that_owns_that_sku()
    {
        var admin = await _fx.AdminAsync();
        var target = await admin.CreateArticleAsync(Wp20Api.Request(sku: Wp20Api.Unique("Ziel").ToUpperInvariant()));
        // Ein anderer Artikel führt die SKU des Ziels als Ersatz (so ist die Alternativ-SKU gedacht).
        await admin.CreateArticleAsync(Wp20Api.Request(alternativeSkus: new[] { target.Sku }));

        var found = await admin.GetFromJsonAsync<ArticleDto>(Url(target.Sku));

        Assert.Equal(target.Id, found!.Id);
    }

    [Fact]
    public async Task A_code_that_matches_several_articles_is_a_409_with_the_candidates()
    {
        var admin = await _fx.AdminAsync();
        var shared = Wp20Api.Unique("Gemeinsam").ToUpperInvariant();
        var first = await admin.CreateArticleAsync(Wp20Api.Request(alternativeSkus: new[] { shared }));
        var second = await admin.CreateArticleAsync(Wp20Api.Request(alternativeSkus: new[] { shared.ToLowerInvariant() }));

        var response = await admin.GetAsync(Url(shared));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("ambiguous_code", await response.ErrorCodeAsync());
        var body = await response.JsonAsync();
        var candidates = body.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("id").GetGuid()).ToList();
        Assert.Equal(new[] { first.Id, second.Id }.Order(), candidates.Order());
        Assert.Contains(first.Sku, body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_sku_that_is_also_the_gtin_of_another_article_is_ambiguous()
    {
        var admin = await _fx.AdminAsync();
        var gtin = Wp20Api.NewGtin13();
        var byGtin = await admin.CreateArticleAsync(Wp20Api.Request(gtin: gtin));
        var bySku = await admin.CreateArticleAsync(Wp20Api.Request(sku: gtin));   // numerische SKU, zufällig gleich

        var response = await admin.GetAsync(Url(gtin));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var ids = (await response.JsonAsync()).GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("id").GetGuid());
        Assert.Equal(new[] { byGtin.Id, bySku.Id }.Order(), ids.Order());
    }

    [Fact]
    public async Task Everyone_signed_in_may_resolve_codes_but_an_anonymous_client_may_not()
    {
        var admin = await _fx.AdminAsync();
        var article = await admin.CreateArticleAsync(Wp20Api.Request());
        var viewer = await _fx.Factory.CreateClientWithRolesAsync("Viewer");
        var picker = await _fx.Factory.CreateClientWithRolesAsync("Picker");

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync(Url(article.Sku))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await picker.GetAsync(Url(article.Sku))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fx.Factory.CreateClient().GetAsync(Url(article.Sku))).StatusCode);
    }
}
