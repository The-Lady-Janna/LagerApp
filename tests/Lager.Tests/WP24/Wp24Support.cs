using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lager.Application.ImportExport;
using Lager.Contracts.Articles;
using Lager.Domain.Auditing;
using Lager.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Lager.Tests.WP24;

/// <summary>
/// Eine API-Instanz (eigene SQLite-Datei, Seed=false) mit Standard-Lager und eingeloggtem Admin für alle Tests einer Klasse.
/// Die Tests legen ihre Daten mit eindeutigen Namen an und stören sich deshalb nicht.
/// </summary>
public sealed class ImportExportFixture : IAsyncLifetime
{
    public LagerApiFactory Factory { get; } = new();
    public WorldBuilder W { get; }
    public HttpClient Admin { get; private set; } = null!;
    public WorldBuilder.World Warehouse { get; private set; } = null!;

    public ImportExportFixture() => W = new WorldBuilder(Factory);

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

/// <summary>Hilfen der WP24-Tests: Upload als Multipart, Import-Aufrufe, Export lesen, Artikel über die API anlegen.</summary>
internal static class Wp24Support
{
    public static readonly UTF8Encoding Utf8 = new(false);

    // Gültige GTINs (GS1-Prüfziffer): EAN-13, EAN-13, EAN-8, UPC-A.
    public const string Gtin13A = "4006381333931";
    public const string Gtin13B = "5901234123457";
    public const string Gtin8 = "96385074";
    public const string GtinUpcA = "036000291452";

    private static int _gtinCounter = 100_000;

    /// <summary>Eine neue, gültige EAN-13 (eindeutig je Prozess): Tests, die sich eine Datenbank teilen, dürfen keine GTIN doppelt vergeben.</summary>
    public static string NextGtin13()
    {
        var body = "590" + Interlocked.Increment(ref _gtinCounter).ToString("D9");
        var sum = 0;
        for (var i = 0; i < body.Length; i++) sum += (body[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return body + (10 - sum % 10) % 10;
    }

    /// <summary>Liest das Ergebnis eines Import-Aufrufs aus der Antwort (ein HTTP-Fehler ist ein Testfehler).</summary>
    public static async Task<ImportResult> ReadResultAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode} {body}");
        return JsonSerializer.Deserialize<ImportResult>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    /// <summary>CSV-Text aus Zeilen (Zeilenende CRLF wie bei Excel).</summary>
    public static string Csv(params string[] lines) => string.Join("\r\n", lines) + "\r\n";

    public static MultipartFormDataContent Upload(byte[] bytes, string fileName = "import.csv")
    {
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("text/csv");
        return new MultipartFormDataContent { { file, "file", fileName } };
    }

    public static MultipartFormDataContent Upload(string csv, string fileName = "import.csv") => Upload(Utf8.GetBytes(csv), fileName);

    public static Task<HttpResponseMessage> PostImportAsync(HttpClient client, string kind, HttpContent content, bool? dryRun = null, bool skipErrors = false, string? delimiter = null)
    {
        var query = new List<string>();
        if (dryRun is { } d) query.Add("dryRun=" + (d ? "true" : "false"));
        if (skipErrors) query.Add("skipErrors=true");
        if (delimiter is not null) query.Add("delimiter=" + delimiter);
        return client.PostAsync($"/api/import/{kind}" + (query.Count > 0 ? "?" + string.Join("&", query) : ""), content);
    }

    /// <summary>Importiert (Trockenlauf oder Übernahme) und liest das Ergebnis; ein HTTP-Fehler ist ein Testfehler.</summary>
    public static async Task<ImportResult> ImportAsync(
        HttpClient client, string kind, string csv, bool dryRun, bool skipErrors = false, string? delimiter = null)
    {
        using var content = Upload(csv);
        var response = await PostImportAsync(client, kind, content, dryRun, skipErrors, delimiter);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"Import {kind}: {(int)response.StatusCode} {body}");
        return JsonSerializer.Deserialize<ImportResult>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    /// <summary>Fehlercode (Feld <c>code</c>) einer Problem-Antwort.</summary>
    public static async Task<string?> ProblemCodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    public static async Task<string> ProblemDetailAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("detail", out var detail) ? detail.GetString() ?? "" : "";
    }

    /// <summary>Die Bytes einer Exportdatei (ohne Prüfung des Inhalts).</summary>
    public static async Task<byte[]> ExportBytesAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsByteArrayAsync();
    }

    /// <summary>Die Exportdatei als Text (BOM entfernt).</summary>
    public static async Task<string> ExportTextAsync(HttpClient client, string path) =>
        CsvText.Decode(await ExportBytesAsync(client, path));

    /// <summary>Die Datenzeilen einer Exportdatei als Felder (ohne Kopfzeile).</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Rows(string csv, char delimiter = ';') =>
        CsvReader.Parse(csv, delimiter).Skip(1).Select(r => r.Fields).ToList();

    public static CreateArticleRequest NewArticle(
        string sku, string? name = null, string? gtin = null, string[]? alternatives = null, int priceCents = 0, Guid? supplierId = null,
        DateTime? validFrom = null, DateTime? validUntil = null, StackingInfoDto? stacking = null, string? description = null,
        int min = 0, int reorder = 0, int max = 0) =>
        new(sku, name ?? "Artikel " + sku, description, new DimensionsDto(120, 80, 40), 250,
            stacking ?? new StackingInfoDto(false, "Z", 0, null),
            min, reorder, max, supplierId, priceCents, alternatives, validFrom, validUntil, BundleComponents: null, Gtin: gtin);

    public static async Task<ArticleDto> CreateArticleAsync(HttpClient client, CreateArticleRequest request)
    {
        var response = await client.PostAsJsonAsync("/api/articles", request);
        Assert.True(response.StatusCode == HttpStatusCode.Created, $"Artikel {request.Sku}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<ArticleDto>())!;
    }

    public static async Task<List<ArticleDto>> ArticlesAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<List<ArticleDto>>("/api/articles"))!;

    // ---- Datenbankstand -------------------------------------------------------------------------------------

    public static Task<int> AuditCountAsync(WorldBuilder w, string? entityType = null) =>
        w.DbAsync(db => db.AuditEntries.CountAsync(a => entityType == null || a.EntityType == entityType));

    public static Task<List<AuditEntry>> AuditEntriesAsync(WorldBuilder w, string entityType) =>
        w.DbAsync(db => db.AuditEntries.AsNoTracking().Where(a => a.EntityType == entityType).ToListAsync());

    public static Task<int> ArticleCountAsync(WorldBuilder w, string skuPrefix) =>
        w.DbAsync(db => db.Articles.CountAsync(a => a.Sku.StartsWith(skuPrefix)));
}
