using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Articles;
using Lager.Contracts.Orders;

namespace Lager.Tests.WP20;

/// <summary>
/// Bundle-Komponenten beim Speichern (Existenz, keine Duplikate, keine Selbstreferenz, keine Zyklen über beliebig viele
/// Ebenen, Tiefe wie beim Kommissionieren), die Semantik "fehlend = unverändert" und der Roundtrip des Editors (PUT mit dem
/// gesamten geladenen Stand ändert nichts). Dazu das Saison-Fenster: der letzte Gültigkeitstag gilt noch.
/// </summary>
public class BundleAndSeasonApiTests : IClassFixture<Wp20Fixture>
{
    private readonly Wp20Fixture _fx;

    public BundleAndSeasonApiTests(Wp20Fixture fixture) => _fx = fixture;

    private static IReadOnlyList<CreateBundleComponentRequest> Parts(params (Guid Id, int Quantity)[] parts) =>
        parts.Select(p => new CreateBundleComponentRequest(p.Id, p.Quantity)).ToList();

    private static Task<HttpResponseMessage> SetComponents(HttpClient admin, ArticleDto article, params (Guid Id, int Quantity)[] parts) =>
        admin.PutAsJsonAsync($"/api/articles/{article.Id}", Wp20Api.UpdateOf(article, r => r with { BundleComponents = Parts(parts) }));

    private async Task<ArticleDto> PlainAsync(HttpClient admin, string prefix) =>
        await admin.CreateArticleAsync(Wp20Api.Request(sku: Wp20Api.Unique(prefix).ToUpperInvariant()));

    // ---- Zyklen und Selbstreferenz ---------------------------------------

    [Fact]
    public async Task A_bundle_that_contains_itself_is_rejected()
    {
        var admin = await _fx.AdminAsync();
        var article = await PlainAsync(admin, "SELF");

        var response = await SetComponents(admin, article, (article.Id, 1));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("bundle_cycle", await response.ErrorCodeAsync());
        Assert.False((await admin.GetArticleAsync(article.Id)).IsBundle);
    }

    [Fact]
    public async Task Bundle_A_containing_B_containing_A_is_rejected_and_nothing_changes()
    {
        var admin = await _fx.AdminAsync();
        var a = await PlainAsync(admin, "ZYK-A");
        var b = await PlainAsync(admin, "ZYK-B");
        Assert.Equal(HttpStatusCode.OK, (await SetComponents(admin, a, (b.Id, 1))).StatusCode);   // A enthält B: erlaubt

        var response = await SetComponents(admin, b, (a.Id, 1));                                 // B enthält A: Zyklus

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("bundle_cycle", await response.ErrorCodeAsync());
        var detail = (await response.JsonAsync()).GetProperty("detail").GetString();
        Assert.Contains(a.Sku, detail);
        Assert.Contains(b.Sku, detail);
        Assert.False((await admin.GetArticleAsync(b.Id)).IsBundle);   // B blieb unverändert
    }

    [Fact]
    public async Task A_cycle_over_three_levels_is_rejected()
    {
        var admin = await _fx.AdminAsync();
        var a = await PlainAsync(admin, "RING-A");
        var b = await PlainAsync(admin, "RING-B");
        var c = await PlainAsync(admin, "RING-C");
        Assert.Equal(HttpStatusCode.OK, (await SetComponents(admin, a, (b.Id, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetComponents(admin, b, (c.Id, 2))).StatusCode);

        var response = await SetComponents(admin, c, (a.Id, 1));   // C -> A -> B -> C

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("bundle_cycle", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task Nested_bundles_without_a_cycle_are_allowed_up_to_the_depth_the_picking_supports()
    {
        var admin = await _fx.AdminAsync();
        var chain = new List<ArticleDto>();
        for (var i = 0; i < 6; i++) chain.Add(await PlainAsync(admin, $"TIEF{i}"));

        // chain[0] ist ein normaler Artikel; chain[1] enthält ihn, chain[2] enthält chain[1] ... : 5 Bundle-Ebenen.
        for (var i = 1; i <= 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await SetComponents(admin, chain[i], (chain[i - 1].Id, 1))).StatusCode);
        Assert.True((await admin.GetArticleAsync(chain[5].Id)).IsBundle);

        // Ein weiteres Bundle obendrauf wäre die 6. Ebene: Picken und Packen könnten es nicht mehr auflösen.
        var top = await PlainAsync(admin, "TIEF-OBEN");
        var response = await SetComponents(admin, top, (chain[5].Id, 1));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("bundle_too_deep", await response.ErrorCodeAsync());
    }

    // ---- Existenz, Duplikate -----------------------------------------------

    [Fact]
    public async Task Unknown_and_repeated_components_are_a_400_with_a_code()
    {
        var admin = await _fx.AdminAsync();
        var bundle = await PlainAsync(admin, "PRUEF");
        var part = await PlainAsync(admin, "TEIL");

        var unknown = await SetComponents(admin, bundle, (Guid.NewGuid(), 1));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal("unknown_bundle_component", await unknown.ErrorCodeAsync());

        var repeated = await SetComponents(admin, bundle, (part.Id, 1), (part.Id, 2));
        Assert.Equal(HttpStatusCode.BadRequest, repeated.StatusCode);
        Assert.Equal("duplicate_bundle_component", await repeated.ErrorCodeAsync());

        Assert.False((await admin.GetArticleAsync(bundle.Id)).IsBundle);
    }

    [Fact]
    public async Task Create_validates_components_too_and_returns_the_component_skus()
    {
        var admin = await _fx.AdminAsync();
        var screw = await PlainAsync(admin, "SCHRAUBE");
        var nut = await PlainAsync(admin, "MUTTER");

        var bad = await admin.PostAsJsonAsync("/api/articles", Wp20Api.Request(components: Parts((Guid.NewGuid(), 1))));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var kit = await admin.CreateArticleAsync(Wp20Api.Request(components: Parts((screw.Id, 4), (nut.Id, 4))));

        Assert.True(kit.IsBundle);
        Assert.Equal(new[] { (screw.Sku, 4), (nut.Sku, 4) }.Order(),
            kit.BundleComponents!.Select(c => (c.ComponentSku, c.Quantity)).Order());   // SKU statt leerem Text
        var listed = (await admin.GetFromJsonAsync<ArticleDto[]>("/api/articles"))!.Single(a => a.Id == kit.Id);
        Assert.All(listed.BundleComponents!, c => Assert.NotEmpty(c.ComponentSku));
    }

    // ---- fehlend = unverändert; Roundtrip des Editors ------------------------

    [Fact]
    public async Task Missing_components_keep_the_bundle_and_an_empty_list_dissolves_it()
    {
        var admin = await _fx.AdminAsync();
        var part = await PlainAsync(admin, "TEIL");
        var bundle = await PlainAsync(admin, "KIT");
        await SetComponents(admin, bundle, (part.Id, 3));
        var loaded = await admin.GetArticleAsync(bundle.Id);

        // Ein Client ohne Bundle-Feld (z. B. der Editor eines Nicht-Bundles) lässt die Komponenten in Ruhe.
        var without = await admin.PutAsJsonAsync($"/api/articles/{bundle.Id}",
            Wp20Api.UpdateOf(loaded, r => r with { Name = "Kit neu", BundleComponents = null }));
        Assert.Equal(HttpStatusCode.OK, without.StatusCode);
        var kept = await admin.GetArticleAsync(bundle.Id);
        Assert.Equal(("Kit neu", true), (kept.Name, kept.IsBundle));
        Assert.Equal(3, Assert.Single(kept.BundleComponents!).Quantity);

        var dissolved = await SetComponents(admin, kept);
        Assert.Equal(HttpStatusCode.OK, dissolved.StatusCode);
        Assert.False((await admin.GetArticleAsync(bundle.Id)).IsBundle);
    }

    [Fact]
    public async Task The_components_of_a_saved_article_can_be_added_replaced_and_changed_in_quantity()
    {
        // Regression: neue Komponenten an einem GELADENEN Artikel wurden von EF als vorhanden behandelt (UPDATE statt INSERT) und
        // scheiterten mit 409; nur beim Anlegen ließen sich Komponenten speichern.
        var admin = await _fx.AdminAsync();
        var first = await PlainAsync(admin, "ERSTE");
        var second = await PlainAsync(admin, "ZWEITE");
        var kit = await PlainAsync(admin, "KIT");

        Assert.Equal(HttpStatusCode.OK, (await SetComponents(admin, kit, (first.Id, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await SetComponents(admin, kit, (first.Id, 5), (second.Id, 2))).StatusCode);
        var both = await admin.GetArticleAsync(kit.Id);
        Assert.Equal(new[] { (first.Id, 5), (second.Id, 2) }.Order(),
            both.BundleComponents!.Select(c => (c.ComponentArticleId, c.Quantity)).Order());

        Assert.Equal(HttpStatusCode.OK, (await SetComponents(admin, kit, (second.Id, 2))).StatusCode);
        var only = Assert.Single((await admin.GetArticleAsync(kit.Id)).BundleComponents!);
        Assert.Equal((second.Id, 2), (only.ComponentArticleId, only.Quantity));
    }

    [Fact]
    public async Task Saving_the_complete_loaded_state_changes_nothing_alternatives_season_bundle_and_gtin_survive()
    {
        var admin = await _fx.AdminAsync();
        var part = await PlainAsync(admin, "TEIL");
        var gtin = Wp20Api.NewGtin13();
        var created = await admin.CreateArticleAsync(Wp20Api.Request(
            sku: Wp20Api.Unique("VOLL").ToUpperInvariant(), gtin: gtin, alternativeSkus: new[] { "ALT-1", "ALT-2" },
            validFrom: new DateTime(2026, 3, 1), validUntil: new DateTime(2026, 9, 30), components: Parts((part.Id, 2))));
        var loaded = await admin.GetArticleAsync(created.Id);

        var put = await admin.PutAsJsonAsync($"/api/articles/{created.Id}", Wp20Api.UpdateOf(loaded, r => r with { Name = "Vollständig, umbenannt" }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var saved = await admin.GetArticleAsync(created.Id);
        Assert.Equal("Vollständig, umbenannt", saved.Name);
        Assert.Equal(new[] { "ALT-1", "ALT-2" }, saved.AlternativeSkus);
        Assert.Equal((loaded.ValidFrom, loaded.ValidUntil), (saved.ValidFrom, saved.ValidUntil));
        Assert.Equal(gtin, saved.Gtin);
        var component = Assert.Single(saved.BundleComponents!);
        Assert.Equal((part.Id, 2), (component.ComponentArticleId, component.Quantity));
    }

    // ---- Saison-Fenster ------------------------------------------------------

    [Fact]
    public async Task An_article_whose_season_ends_today_is_still_orderable_today_and_shown_as_active()
    {
        var admin = await _fx.AdminAsync();
        var today = DateTime.UtcNow.Date;   // ein Datum: heute 00:00 UTC ist der letzte Gültigkeitstag
        var article = await admin.CreateArticleAsync(Wp20Api.Request(validFrom: today.AddDays(-30), validUntil: today));
        Assert.True(article.IsCurrentlyActive);

        var order = await admin.PostAsJsonAsync("/api/orders/manual", new CreateOrderRequest(
            Wp20Api.Unique("SAISON"), "Testkunde", new[] { new CreateOrderLineRequest(article.Id, 1) }));

        Assert.Equal(HttpStatusCode.Created, order.StatusCode);
    }

    [Fact]
    public async Task An_article_whose_season_ended_yesterday_is_not_orderable_and_the_list_says_so()
    {
        var admin = await _fx.AdminAsync();
        var today = DateTime.UtcNow.Date;
        var over = await admin.CreateArticleAsync(Wp20Api.Request(validFrom: today.AddDays(-30), validUntil: today.AddDays(-1)));
        var notYet = await admin.CreateArticleAsync(Wp20Api.Request(validFrom: today.AddDays(1)));
        var always = await admin.CreateArticleAsync(Wp20Api.Request());

        Assert.False(over.IsCurrentlyActive);
        Assert.False(notYet.IsCurrentlyActive);
        Assert.True(always.IsCurrentlyActive);
        var order = await admin.PostAsJsonAsync("/api/orders/manual", new CreateOrderRequest(
            Wp20Api.Unique("SAISON"), "Testkunde", new[] { new CreateOrderLineRequest(over.Id, 1) }));
        Assert.Equal(HttpStatusCode.Conflict, order.StatusCode);
        Assert.Equal("article_not_orderable", await order.ErrorCodeAsync());

        var listed = (await admin.GetFromJsonAsync<ArticleDto[]>("/api/articles"))!.ToDictionary(a => a.Id);
        Assert.False(listed[over.Id].IsCurrentlyActive);
        Assert.False(listed[notYet.Id].IsCurrentlyActive);
    }

    [Fact]
    public async Task A_season_end_before_the_start_is_a_400_but_a_one_day_window_is_fine()
    {
        var admin = await _fx.AdminAsync();
        var day = new DateTime(2026, 5, 5);

        var bad = await admin.PostAsJsonAsync("/api/articles", Wp20Api.Request(validFrom: day, validUntil: day.AddDays(-1)));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.True((await bad.JsonAsync()).GetProperty("errors").TryGetProperty("ValidUntil", out _));

        var oneDay = await admin.PostAsJsonAsync("/api/articles", Wp20Api.Request(validFrom: day.AddHours(14), validUntil: day));
        Assert.Equal(HttpStatusCode.Created, oneDay.StatusCode);
    }
}
