using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lager.Contracts.Articles;
using Lager.Domain.Articles;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP20;

/// <summary>
/// Eine API-Instanz (eigene SQLite-Datei, Seed=false) pro Testklasse; die Tests legen ihre Artikel mit eindeutigen SKUs und
/// GTINs an und stören sich deshalb nicht.
/// </summary>
public sealed class Wp20Fixture : IDisposable
{
    public LagerApiFactory Factory { get; } = new();

    /// <summary>Ein als einsatzbereiter Admin eingeloggter HTTP-Client.</summary>
    public Task<HttpClient> AdminAsync() => Factory.CreateClient().AsReadyAdminAsync();

    public void Dispose() => Factory.Dispose();
}

internal static class Wp20Api
{
    private static int _counter;

    public static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>Eine gültige, pro Aufruf andere EAN-13 (Präfix 200 = interne Nutzung, Prüfziffer nach GS1).</summary>
    public static string NewGtin13()
    {
        var body = "200" + Interlocked.Increment(ref _counter).ToString("D9");
        return body + Gtin.ComputeCheckDigit(body);
    }

    public static CreateArticleRequest Request(
        string? sku = null, string? gtin = null, string[]? alternativeSkus = null,
        DateTime? validFrom = null, DateTime? validUntil = null,
        IReadOnlyList<CreateBundleComponentRequest>? components = null, string? name = null) =>
        new(sku ?? Unique("ART"), name ?? "Testartikel", null, new DimensionsDto(100, 100, 100), 100,
            new StackingInfoDto(false, "Z", 0, null),
            AlternativeSkus: alternativeSkus, ValidFrom: validFrom, ValidUntil: validUntil,
            BundleComponents: components, Gtin: gtin);

    /// <summary>Legt einen Artikel an und erwartet 201.</summary>
    public static async Task<ArticleDto> CreateArticleAsync(this HttpClient admin, CreateArticleRequest request)
    {
        var response = await admin.PostAsJsonAsync("/api/articles", request);
        Assert.True(response.StatusCode == HttpStatusCode.Created,
            $"Anlegen von {request.Sku} scheiterte: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ArticleDto>())!;
    }

    /// <summary>Der Änderungs-Request zu einem Artikel (alle Felder unverändert übernommen), mit Anpassung.</summary>
    public static UpdateArticleRequest UpdateOf(ArticleDto a, Func<UpdateArticleRequest, UpdateArticleRequest>? change = null)
    {
        var request = new UpdateArticleRequest(a.Name, a.Description, a.Dimensions, a.WeightGrams, a.Stacking,
            a.MinStock, a.ReorderPoint, a.MaxStock, a.PrimarySupplierId, a.PurchasePriceCents,
            a.AlternativeSkus, a.ValidFrom, a.ValidUntil,
            a.BundleComponents?.Select(c => new CreateBundleComponentRequest(c.ComponentArticleId, c.Quantity)).ToList(),
            a.Gtin);
        return change is null ? request : change(request);
    }

    public static async Task<ArticleDto> GetArticleAsync(this HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<ArticleDto>($"/api/articles/{id}"))!;

    /// <summary>Der maschinenlesbare Fehlercode einer Antwort (Feld <c>code</c>) oder null.</summary>
    public static async Task<string?> ErrorCodeAsync(this HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.Clone();
    }
}
