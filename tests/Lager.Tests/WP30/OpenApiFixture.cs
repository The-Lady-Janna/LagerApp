using System.Net;
using System.Text.Json;
using Lager.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lager.Tests.WP30;

/// <summary>
/// Startet die API in der Umgebung Development (dort läuft Swagger) und lädt das OpenAPI-Dokument einmal für alle Tests der
/// Klasse. Die Erzeugung des Dokuments ist selbst schon ein Test: Swashbuckle bricht bei widersprüchlichen Schema-Ids, doppelten
/// Methode/Pfad-Kombinationen oder nicht darstellbaren Typen mit einer Ausnahme ab (dann antwortet der Endpunkt 500).
/// </summary>
public sealed class OpenApiFixture : IAsyncLifetime
{
    private LagerApiFactory? _baseFactory;
    private WebApplicationFactory<Program>? _factory;
    private JsonDocument? _document;

    /// <summary>Die Wurzel des OpenAPI-Dokuments (<c>/swagger/v1/swagger.json</c>).</summary>
    public JsonElement Document => _document?.RootElement ?? throw new InvalidOperationException("Das Dokument ist noch nicht geladen");

    /// <summary>Alle Operationen (Methode, Pfad, Beschreibung) des Dokuments.</summary>
    public IReadOnlyList<Operation> Operations { get; private set; } = Array.Empty<Operation>();

    public async Task InitializeAsync()
    {
        _baseFactory = new LagerApiFactory();
        _factory = _baseFactory.WithWebHostBuilder(builder => builder.UseEnvironment("Development"));

        var response = await _factory.CreateClient().GetAsync("/swagger/v1/swagger.json");
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK,
            $"swagger.json ließ sich nicht erzeugen: {(int)response.StatusCode} {body[..Math.Min(body.Length, 600)]}");

        _document = JsonDocument.Parse(body);
        Operations = Document.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Where(method => method.Name is "get" or "post" or "put" or "delete" or "patch")
                .Select(method => new Operation(method.Name.ToUpperInvariant(), path.Name, method.Value)))
            .ToList();
    }

    public Task DisposeAsync()
    {
        _document?.Dispose();
        _factory?.Dispose();
        _baseFactory?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Ein neuer Client der Development-Instanz (anonym; Anmeldung z. B. mit <c>AsReadyAdminAsync()</c>).</summary>
    public HttpClient CreateClient() => _factory?.CreateClient() ?? throw new InvalidOperationException("Die Factory ist noch nicht gestartet");

    public Operation Find(string method, string path) =>
        Operations.SingleOrDefault(o => o.Method == method && o.Path == path)
        ?? throw new InvalidOperationException($"{method} {path} steht nicht im OpenAPI-Dokument");

    /// <summary>Eine Operation des Dokuments.</summary>
    public sealed record Operation(string Method, string Path, JsonElement Json)
    {
        public string Name => $"{Method} {Path}";

        public JsonElement Responses => Json.GetProperty("responses");

        /// <summary>Die deklarierten Statuscodes ("200", "404" ...).</summary>
        public IReadOnlyList<string> Codes => Responses.EnumerateObject().Select(r => r.Name).ToList();

        public bool HasResponse(string code) => Responses.TryGetProperty(code, out _);

        public string? Summary => Json.TryGetProperty("summary", out var s) ? s.GetString() : null;

        public string? Description => Json.TryGetProperty("description", out var d) ? d.GetString() : null;

        public bool HasBearerRequirement => Json.TryGetProperty("security", out var security)
                                            && security.EnumerateArray().Any(r => r.TryGetProperty("Bearer", out _));

        /// <summary>Name des Schemas (<c>#/components/schemas/X</c> -> X) im Inhalt einer Antwort, sonst null.</summary>
        public static string? SchemaOf(JsonElement response, string contentType) =>
            response.TryGetProperty("content", out var content)
            && content.TryGetProperty(contentType, out var media)
            && media.TryGetProperty("schema", out var schema)
            && schema.TryGetProperty("$ref", out var reference)
                ? reference.GetString()!.Split('/')[^1]
                : null;
    }
}
