using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Articles;

namespace Lager.Tests.WP20;

/// <summary>
/// GTIN und SKU am Artikel über die API: gültige EAN-13 wird gespeichert, falsche Prüfziffer abgelehnt (400 mit Feldbezug),
/// dieselbe GTIN zweimal ist ein 409 mit Code (auch in gleichwertiger Länge), SKU-Eindeutigkeit ohne Rücksicht auf
/// Schreibweise und Leerraum (vorher 500). Änderung per PUT: fehlende GTIN = unverändert, leere GTIN = entfernen.
/// </summary>
public class ArticleGtinApiTests : IClassFixture<Wp20Fixture>
{
    private readonly Wp20Fixture _fx;

    public ArticleGtinApiTests(Wp20Fixture fixture) => _fx = fixture;

    [Fact]
    public async Task An_article_with_a_valid_ean13_is_saved_and_returned()
    {
        var admin = await _fx.AdminAsync();
        var gtin = Wp20Api.NewGtin13();

        var created = await admin.CreateArticleAsync(Wp20Api.Request(gtin: $" {gtin[..1]} {gtin[1..]} "));   // mit Leerraum getippt

        Assert.Equal(gtin, created.Gtin);
        Assert.Equal(gtin, (await admin.GetArticleAsync(created.Id)).Gtin);
        var listed = (await admin.GetFromJsonAsync<ArticleDto[]>("/api/articles"))!.Single(a => a.Id == created.Id);
        Assert.Equal(gtin, listed.Gtin);
    }

    [Theory]
    [InlineData("4006381333932", "Prüfziffer")]
    [InlineData("40063813339A1", "Ziffern")]
    [InlineData("12345", "Stellen")]
    public async Task An_invalid_gtin_is_a_400_with_the_field_and_the_reason_on_create_and_update(string gtin, string reason)
    {
        var admin = await _fx.AdminAsync();

        var create = await admin.PostAsJsonAsync("/api/articles", Wp20Api.Request(gtin: gtin));
        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Equal("validation_failed", await create.ErrorCodeAsync());
        var errors = (await create.JsonAsync()).GetProperty("errors");
        Assert.Contains(reason, errors.GetProperty("Gtin")[0].GetString());

        var existing = await admin.CreateArticleAsync(Wp20Api.Request());
        var update = await admin.PutAsJsonAsync($"/api/articles/{existing.Id}", Wp20Api.UpdateOf(existing, r => r with { Gtin = gtin }));
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        Assert.Contains(reason, (await update.JsonAsync()).GetProperty("errors").GetProperty("Gtin")[0].GetString());
        Assert.Null((await admin.GetArticleAsync(existing.Id)).Gtin);
    }

    [Fact]
    public async Task The_same_gtin_on_a_second_article_is_a_409_duplicate_gtin_and_creates_nothing()
    {
        var admin = await _fx.AdminAsync();
        var gtin = Wp20Api.NewGtin13();
        var first = await admin.CreateArticleAsync(Wp20Api.Request(gtin: gtin));
        var secondSku = Wp20Api.Unique("DUP");

        var response = await admin.PostAsJsonAsync("/api/articles", Wp20Api.Request(sku: secondSku, gtin: gtin));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("duplicate_gtin", await response.ErrorCodeAsync());
        Assert.Contains(first.Sku, (await response.JsonAsync()).GetProperty("detail").GetString());   // nennt den Besitzer
        var all = (await admin.GetFromJsonAsync<ArticleDto[]>("/api/articles"))!;
        Assert.DoesNotContain(all, a => a.Sku == secondSku);
    }

    [Fact]
    public async Task A_upc_a_and_its_ean13_form_are_the_same_gtin_for_the_uniqueness_check()
    {
        var admin = await _fx.AdminAsync();
        // Ein bekanntes Paar: UPC-A 036000291452 = EAN-13 0036000291452.
        var upc = await admin.CreateArticleAsync(Wp20Api.Request(gtin: "036000291452"));

        var asEan13 = await admin.PostAsJsonAsync("/api/articles", Wp20Api.Request(gtin: "0036000291452"));
        Assert.Equal(HttpStatusCode.Conflict, asEan13.StatusCode);
        Assert.Equal("duplicate_gtin", await asEan13.ErrorCodeAsync());

        // Der Artikel selbst darf seine GTIN in anderer Länge neu speichern (kein Konflikt mit sich selbst).
        var self = await admin.PutAsJsonAsync($"/api/articles/{upc.Id}", Wp20Api.UpdateOf(upc, r => r with { Gtin = "0036000291452" }));
        Assert.Equal(HttpStatusCode.OK, self.StatusCode);
    }

    [Fact]
    public async Task The_sku_is_unique_regardless_of_case_and_surrounding_whitespace_and_never_a_500()
    {
        var admin = await _fx.AdminAsync();
        var sku = Wp20Api.Unique("Sku").ToUpperInvariant();
        await admin.CreateArticleAsync(Wp20Api.Request(sku: sku));

        foreach (var variant in new[] { sku.ToLowerInvariant(), $"  {sku}", $"{sku}  ", $" {sku.ToLowerInvariant()} " })
        {
            var response = await admin.PostAsJsonAsync("/api/articles", Wp20Api.Request(sku: variant));
            Assert.True(response.StatusCode == HttpStatusCode.Conflict, $"'{variant}' -> {(int)response.StatusCode}");
            Assert.Equal("conflict", await response.ErrorCodeAsync());
            Assert.Contains(variant.Trim(), (await response.JsonAsync()).GetProperty("detail").GetString());
        }

        // Der gespeicherte Wert ist getrimmt.
        var trimmed = await admin.CreateArticleAsync(Wp20Api.Request(sku: $"   {Wp20Api.Unique("TRIM")}  "));
        Assert.Equal(trimmed.Sku.Trim(), trimmed.Sku);
    }

    [Fact]
    public async Task On_update_a_missing_gtin_keeps_it_an_empty_one_removes_it_and_a_new_one_replaces_it()
    {
        var admin = await _fx.AdminAsync();
        var gtin = Wp20Api.NewGtin13();
        var article = await admin.CreateArticleAsync(Wp20Api.Request(gtin: gtin));

        // Ein Client ohne GTIN-Feld (z. B. ältere Integration) darf sie nicht löschen.
        var withoutField = await admin.PutAsJsonAsync($"/api/articles/{article.Id}",
            Wp20Api.UpdateOf(article, r => r with { Name = "Umbenannt", Gtin = null }));
        Assert.Equal(HttpStatusCode.OK, withoutField.StatusCode);
        var kept = await admin.GetArticleAsync(article.Id);
        Assert.Equal((gtin, "Umbenannt"), (kept.Gtin, kept.Name));

        var replacement = Wp20Api.NewGtin13();
        await admin.PutAsJsonAsync($"/api/articles/{article.Id}", Wp20Api.UpdateOf(kept, r => r with { Gtin = replacement }));
        Assert.Equal(replacement, (await admin.GetArticleAsync(article.Id)).Gtin);

        var cleared = await admin.PutAsJsonAsync($"/api/articles/{article.Id}", Wp20Api.UpdateOf(kept, r => r with { Gtin = "  " }));
        Assert.Equal(HttpStatusCode.OK, cleared.StatusCode);
        Assert.Null((await admin.GetArticleAsync(article.Id)).Gtin);

        // Die frei gewordene GTIN ist wieder vergebbar.
        await admin.CreateArticleAsync(Wp20Api.Request(gtin: gtin));
    }

    [Fact]
    public async Task Changing_to_a_gtin_of_another_article_is_a_409_and_leaves_the_article_unchanged()
    {
        var admin = await _fx.AdminAsync();
        var taken = Wp20Api.NewGtin13();
        await admin.CreateArticleAsync(Wp20Api.Request(gtin: taken));
        var other = await admin.CreateArticleAsync(Wp20Api.Request(gtin: Wp20Api.NewGtin13(), name: "Vorher"));

        var response = await admin.PutAsJsonAsync($"/api/articles/{other.Id}",
            Wp20Api.UpdateOf(other, r => r with { Name = "Nachher", Gtin = taken }));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("duplicate_gtin", await response.ErrorCodeAsync());
        var unchanged = await admin.GetArticleAsync(other.Id);
        Assert.Equal(("Vorher", other.Gtin), (unchanged.Name, unchanged.Gtin));
    }

    [Fact]
    public async Task Search_matches_name_sku_alternative_sku_and_gtin()
    {
        var admin = await _fx.AdminAsync();
        var token = Wp20Api.Unique("suche").Replace("-", "");
        var gtin = Wp20Api.NewGtin13();
        var byName = await admin.CreateArticleAsync(Wp20Api.Request(name: $"Namens-{token}-Treffer"));
        var bySku = await admin.CreateArticleAsync(Wp20Api.Request(sku: $"SKU-{token}".ToUpperInvariant()));
        var byAlt = await admin.CreateArticleAsync(Wp20Api.Request(alternativeSkus: new[] { $"Ersatz-{token}" }));
        var byGtin = await admin.CreateArticleAsync(Wp20Api.Request(gtin: gtin));
        var none = await admin.CreateArticleAsync(Wp20Api.Request());

        async Task<Guid[]> Search(string term) =>
            (await admin.GetFromJsonAsync<ArticleDto[]>($"/api/articles?search={Uri.EscapeDataString(term)}"))!.Select(a => a.Id).ToArray();

        Assert.Equal(new[] { byName.Id, bySku.Id, byAlt.Id }.Order(), (await Search(token.ToUpperInvariant())).Order());   // Schreibweise egal
        Assert.Equal(new[] { byGtin.Id }, await Search(gtin));
        Assert.Equal(new[] { byGtin.Id }, await Search($"{gtin[..6]} {gtin[6..]}"));   // mit Leerraum getippt, wie auf der Verpackung gruppiert
        Assert.DoesNotContain(none.Id, await Search(token));
        Assert.Empty(await Search("gibt-es-bestimmt-nicht-" + token));
        // Ohne Suchtext (oder nur Leerraum) kommen alle.
        var all = (await admin.GetFromJsonAsync<ArticleDto[]>("/api/articles?search=%20"))!;
        Assert.Contains(all, a => a.Id == none.Id);
    }
}
