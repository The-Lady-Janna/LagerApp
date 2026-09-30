using System.Net;
using System.Text.Json;
using Lager.Tests.Infrastructure;
using Lager.Tests.WP13;
using Lager.Tests.WP14;

namespace Lager.Tests.WP32;

/// <summary>
/// Eine API-Instanz (eigene SQLite-Datei, Seed=false) mit eingeloggtem Admin und beiden Test-Welten (Bestellungen/Versand
/// aus WP13, Bestand/Einkauf/Retouren aus WP14) für alle Tests einer Klasse. Die Tests legen ihre Daten mit eindeutigen
/// Namen an und stören sich deshalb nicht.
/// </summary>
public sealed class Wp32Fixture : IAsyncLifetime
{
    public LagerApiFactory Factory { get; } = new();
    public HttpClient Admin { get; private set; } = null!;
    public Wp13World Orders { get; private set; } = null!;
    public StockWorld Stock { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Admin = await Factory.CreateClient().AsReadyAdminAsync();
        Orders = new Wp13World(Factory.Services);
        Stock = new StockWorld(Factory);
    }

    public Task DisposeAsync()
    {
        Admin.Dispose();
        Factory.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>Prüft die einheitliche Fehlerantwort der API (RFC 7807 samt <c>code</c>, <c>correlationId</c> und <c>error</c>).</summary>
public static class ProblemAssert
{
    /// <summary>
    /// Die Antwort hat den erwarteten Status und ist ein <c>application/problem+json</c> mit den Feldern
    /// <c>type, title, status, detail, code, correlationId</c> und dem Kompatibilitätsfeld <c>error</c> (= detail).
    /// Die Referenz-ID im Body ist die aus dem Header <c>X-Correlation-Id</c>. Liefert den Body zurück.
    /// </summary>
    public static async Task<JsonElement> HasAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == status, $"{(int)status} erwartet, war {(int)response.StatusCode}: {text}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = JsonDocument.Parse(text).RootElement.Clone();
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
        Assert.StartsWith("http", body.GetProperty("type").GetString());
        foreach (var field in new[] { "title", "detail", "code", "correlationId", "error" })
            Assert.False(string.IsNullOrWhiteSpace(body.GetProperty(field).GetString()), $"Feld '{field}' fehlt oder ist leer: {text}");

        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal(body.GetProperty("detail").GetString(), body.GetProperty("error").GetString());
        Assert.Equal(response.Headers.GetValues("X-Correlation-Id").Single(), body.GetProperty("correlationId").GetString());
        return body;
    }
}
