using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Lager.Contracts.Articles;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP11;

/// <summary>
/// Backend-Seite des Datenverlust-Fixes im Artikel-Editor (frontend#2): <c>UpdateAsync</c> setzt Alternativ-SKUs und
/// Saison-Fenster aus dem Request. Der Editor lädt sie deshalb mit und sendet sie beim Speichern UNVERÄNDERT zurück
/// (Payload wie in ArticleEditorPage.tsx). Diese Tests sichern ab, dass genau dieser Roundtrip nichts verändert - auch
/// das Datumsformat (ISO ohne Zeitzone, wie es der Server liefert) übersteht ihn. Das Frontend-Verhalten selbst prüft
/// der Vitest-Test src/tests/WP11/articleEditor.test.tsx.
/// </summary>
public class ArticleEditorPayloadContractTests
{
    private static readonly CreateArticleRequest WithAlternativesAndSeason = new(
        Sku: "WP11-ALT",
        Name: "Schraube M8",
        Description: "Beschreibung",
        Dimensions: new DimensionsDto(10, 10, 40),
        WeightGrams: 12,
        Stacking: new StackingInfoDto(false, "Z", 0, null),
        MinStock: 5,
        ReorderPoint: 10,
        MaxStock: 100,
        PurchasePriceCents: 250,
        AlternativeSkus: new[] { "ALT-1", "ALT-2" },
        ValidFrom: new DateTime(2026, 3, 1),
        ValidUntil: new DateTime(2026, 9, 30));

    /// <summary>Der Update-Payload des Editors: geladener Artikel, nur der Name geändert, Alternativen/Saison durchgereicht.</summary>
    private static JsonObject EditorPayload(JsonNode loaded, string newName) => new()
    {
        ["name"] = newName,
        ["description"] = loaded["description"]?.DeepClone(),
        ["dimensions"] = loaded["dimensions"]!.DeepClone(),
        ["weightGrams"] = loaded["weightGrams"]!.DeepClone(),
        ["stacking"] = loaded["stacking"]!.DeepClone(),
        ["minStock"] = loaded["minStock"]!.DeepClone(),
        ["reorderPoint"] = loaded["reorderPoint"]!.DeepClone(),
        ["maxStock"] = loaded["maxStock"]!.DeepClone(),
        ["primarySupplierId"] = loaded["primarySupplierId"]?.DeepClone(),
        ["purchasePriceCents"] = loaded["purchasePriceCents"]!.DeepClone(),
        // form.alternativeSkus ist im Editor immer ein Array (null -> []); validFrom/validUntil bleiben string | null.
        ["alternativeSkus"] = loaded["alternativeSkus"]?.DeepClone() ?? new JsonArray(),
        ["validFrom"] = loaded["validFrom"]?.DeepClone(),
        ["validUntil"] = loaded["validUntil"]?.DeepClone(),
    };

    private static async Task<JsonNode> GetArticleAsync(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<JsonNode>($"/api/articles/{id}"))!;

    [Fact]
    public async Task Speichern_im_Editor_laesst_Alternativ_SKUs_und_Saison_Fenster_unveraendert()
    {
        using var factory = new LagerApiFactory();
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        var created = await admin.PostAsJsonAsync("/api/articles", WithAlternativesAndSeason);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<ArticleDto>())!.Id;
        var loaded = await GetArticleAsync(admin, id);

        var put = await admin.PutAsJsonAsync($"/api/articles/{id}", EditorPayload(loaded, "Schraube M8 verzinkt"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var saved = await GetArticleAsync(admin, id);
        Assert.Equal("Schraube M8 verzinkt", (string?)saved["name"]);
        Assert.Equal(new[] { "ALT-1", "ALT-2" }, saved["alternativeSkus"]!.AsArray().Select(n => (string?)n));
        // Datumswerte sind Zeichenketten ohne Zeitzone; sie müssen den Roundtrip bitgleich überstehen.
        Assert.Equal((string?)loaded["validFrom"], (string?)saved["validFrom"]);
        Assert.Equal((string?)loaded["validUntil"], (string?)saved["validUntil"]);
        Assert.NotNull((string?)saved["validFrom"]);
        Assert.NotNull((string?)saved["validUntil"]);
    }

    [Fact]
    public async Task Editor_Payload_ohne_Alternativen_und_Saison_wird_akzeptiert_und_bleibt_leer()
    {
        using var factory = new LagerApiFactory();
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        var created = await admin.PostAsJsonAsync("/api/articles",
            WithAlternativesAndSeason with { Sku = "WP11-PLAIN", AlternativeSkus = null, ValidFrom = null, ValidUntil = null });
        var id = (await created.Content.ReadFromJsonAsync<ArticleDto>())!.Id;
        var loaded = await GetArticleAsync(admin, id);

        // Der Editor sendet für "keine Alternativen" ein leeres Array und für "kein Saison-Fenster" null.
        var put = await admin.PutAsJsonAsync($"/api/articles/{id}", EditorPayload(loaded, "Neuer Name"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var saved = await GetArticleAsync(admin, id);
        Assert.Equal("Neuer Name", (string?)saved["name"]);
        Assert.True(saved["alternativeSkus"] is null || saved["alternativeSkus"]!.AsArray().Count == 0);
        Assert.Null(saved["validFrom"]);
        Assert.Null(saved["validUntil"]);
    }
}
