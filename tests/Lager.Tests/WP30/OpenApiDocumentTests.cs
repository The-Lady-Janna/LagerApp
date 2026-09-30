using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Lager.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;

namespace Lager.Tests.WP30;

/// <summary>
/// Die OpenAPI-Beschreibung (<c>/swagger/v1/swagger.json</c>) ist ein Vertrag für Integratoren: Sie nennt für jeden Endpunkt die
/// Antworten samt Typen (inklusive 401/403 und ProblemDetails für jeden Fehler), trägt die XML-Kommentare der Controller und
/// beschreibt Anmeldung, Fehlerformat und Version. Die Tests prüfen das Dokument, nicht einzelne Controller: ein neuer Endpunkt ohne
/// Kommentar oder ohne Fehlerantwort fällt hier auf.
/// </summary>
public class OpenApiDocumentTests : IClassFixture<OpenApiFixture>
{
    private readonly OpenApiFixture _api;

    public OpenApiDocumentTests(OpenApiFixture api) => _api = api;

    [Fact]
    public void The_document_has_title_assembly_version_description_bearer_scheme_and_the_problem_format()
    {
        var info = _api.Document.GetProperty("info");
        Assert.Equal("Lager API", info.GetProperty("title").GetString());

        // Version aus der Assembly (ohne die Build-Metadaten hinter "+"), nicht ein fester Text
        var informational = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.Equal(informational.Split('+')[0], info.GetProperty("version").GetString());
        Assert.Equal(ApiVersionInfo.AppVersion, info.GetProperty("version").GetString());

        var description = info.GetProperty("description").GetString()!;
        Assert.Contains("Bearer", description);
        Assert.Contains("application/problem+json", description);

        var bearer = _api.Document.GetProperty("components").GetProperty("securitySchemes").GetProperty("Bearer");
        Assert.Equal("http", bearer.GetProperty("type").GetString());
        Assert.Equal("bearer", bearer.GetProperty("scheme").GetString());
        Assert.Equal("JWT", bearer.GetProperty("bearerFormat").GetString());

        // das Fehlerformat: die Felder, die jede Fehlerantwort der API trägt, stehen im Schema
        var schemas = _api.Document.GetProperty("components").GetProperty("schemas");
        var problem = schemas.GetProperty("ProblemDetails").GetProperty("properties");
        foreach (var field in new[] { "type", "title", "status", "detail", "code", "correlationId", "error" })
            Assert.True(problem.TryGetProperty(field, out _), $"ProblemDetails.{field} fehlt im Schema");
        Assert.True(schemas.GetProperty("ValidationProblemDetails").GetProperty("properties").TryGetProperty("errors", out _));
    }

    [Fact]
    public void Every_operation_has_a_summary_from_the_xml_comments()
    {
        Assert.True(_api.Operations.Count > 140, $"Nur {_api.Operations.Count} Operationen gefunden - das Dokument ist unvollständig");

        var undocumented = _api.Operations.Where(o => string.IsNullOrWhiteSpace(o.Summary)).Select(o => o.Name).Order().ToList();
        Assert.True(undocumented.Count == 0, "Operationen ohne <summary>-Kommentar: " + string.Join(", ", undocumented));

        // ein Beispiel, dass Kommentare wirklich ankommen: Summary und Parameterbeschreibung eines Controllers
        var article = _api.Find("GET", "/api/articles/{id}");
        Assert.Equal("Liefert einen Artikel.", article.Summary);
        var id = article.Json.GetProperty("parameters").EnumerateArray().Single(p => p.GetProperty("name").GetString() == "id");
        Assert.Equal("Id des Artikels.", id.GetProperty("description").GetString());
    }

    [Fact]
    public void Every_operation_declares_a_success_and_an_error_response_and_every_error_is_problem_details()
    {
        var missing = new List<string>();
        var wrongError = new List<string>();

        foreach (var operation in _api.Operations)
        {
            var codes = operation.Codes;
            if (!codes.Any(c => c.StartsWith('2'))) missing.Add($"{operation.Name}: keine Erfolgsantwort ({string.Join(",", codes)})");
            if (!codes.Any(c => c.StartsWith('4'))) missing.Add($"{operation.Name}: keine Fehlerantwort ({string.Join(",", codes)})");

            foreach (var code in codes.Where(c => c.StartsWith('4') || c.StartsWith('5')))
            {
                var response = operation.Responses.GetProperty(code);
                var schema = OpenApiFixture.Operation.SchemaOf(response, "application/problem+json");
                if (schema is not ("ProblemDetails" or "ValidationProblemDetails"))
                    wrongError.Add($"{operation.Name} {code}: {schema ?? "kein application/problem+json"}");
            }
        }

        Assert.True(missing.Count == 0, string.Join("\n", missing));
        Assert.True(wrongError.Count == 0, string.Join("\n", wrongError));
    }

    [Fact]
    public void Protected_operations_declare_bearer_401_and_403_the_login_is_anonymous()
    {
        foreach (var operation in _api.Operations.Where(o => o.Path != "/api/auth/login"))
        {
            Assert.True(operation.HasBearerRequirement, $"{operation.Name}: Bearer-Sicherheitsanforderung fehlt");
            Assert.True(operation.HasResponse("401"), $"{operation.Name}: 401 fehlt");
        }

        // 403 (Rolle zu schwach oder Passwortwechsel offen) überall, nur die zwei Endpunkte des Passwortwechsels können es nicht liefern
        var without403 = _api.Operations.Where(o => !o.HasResponse("403")).Select(o => o.Name).Order().ToList();
        Assert.Equal(new[] { "GET /api/auth/me", "POST /api/auth/change-password", "POST /api/auth/login" }.Order().ToList(), without403);

        var login = _api.Find("POST", "/api/auth/login");
        Assert.False(login.HasBearerRequirement);
        Assert.True(login.HasResponse("401"));
        Assert.True(login.HasResponse("429"));
    }

    [Theory]
    [InlineData("GET", "/api/articles", "jeder angemeldete Benutzer")]
    [InlineData("POST", "/api/articles", "Rolle Manager, Admin")]
    [InlineData("POST", "/api/picklists/{id}/pack", "Rolle Packer, Manager, Admin")]
    [InlineData("POST", "/api/inbound", "Rolle Receiver, Manager, Admin")]
    [InlineData("GET", "/api/users", "Rolle Admin")]
    [InlineData("POST", "/api/admin/backups", "Rolle Admin")]
    public void The_minimum_role_is_the_first_line_of_the_description(string method, string path, string expected)
    {
        var description = _api.Find(method, path).Description;

        Assert.StartsWith($"**Berechtigung:** {expected}", description);
    }

    [Fact]
    public void Every_create_declares_201_with_a_Location_header()
    {
        var created = _api.Operations.Where(o => o.HasResponse("201")).ToList();
        Assert.True(created.Count >= 15, $"Nur {created.Count} Operationen mit 201");

        foreach (var operation in created)
        {
            var headers = operation.Responses.GetProperty("201").GetProperty("headers");
            Assert.True(headers.TryGetProperty("Location", out _), $"{operation.Name}: 201 ohne Location-Header");
        }

        // die Stichprobe der Akzeptanz
        foreach (var path in new[] { "/api/articles", "/api/orders", "/api/suppliers", "/api/customers" })
            Assert.True(_api.Find("POST", path).HasResponse("201"), $"POST {path} deklariert kein 201");
    }

    [Fact]
    public async Task Every_GET_by_id_that_documents_404_answers_an_unknown_id_with_404_problem_details()
    {
        // Die Beschreibung darf nicht lügen: was das Dokument als 404 verspricht, liefert der Server auch wirklich.
        const string unknown = "00000000-0000-4000-8000-0000000000a4";
        var admin = await _api.CreateClient().AsReadyAdminAsync();
        var checkedOperations = new List<string>();
        var wrong = new List<string>();

        foreach (var operation in _api.Operations.Where(o => o.Method == "GET" && o.HasResponse("404")))
        {
            var pathParameters = operation.Json.GetProperty("parameters").EnumerateArray()
                .Where(p => p.GetProperty("in").GetString() == "path").ToList();
            if (pathParameters.Count == 0 || !pathParameters.All(p => p.GetProperty("schema").TryGetProperty("format", out var f) && f.GetString() == "uuid"))
                continue; // Namen und Codes als Pfadwert (Backup, Charge, Artikelcode) haben eigene Tests

            var url = System.Text.RegularExpressions.Regex.Replace(operation.Path, @"\{\w+\}", unknown);
            var response = await admin.GetAsync(url);
            checkedOperations.Add(operation.Name);

            var problem = response.Content.Headers.ContentType?.MediaType == "application/problem+json"
                ? await response.Content.ReadFromJsonAsync<JsonElement>()
                : default;
            if (response.StatusCode != HttpStatusCode.NotFound || problem.ValueKind != JsonValueKind.Object
                || problem.GetProperty("code").GetString() != "not_found")
                wrong.Add($"{operation.Name}: {(int)response.StatusCode} {response.Content.Headers.ContentType?.MediaType}");
        }

        Assert.True(checkedOperations.Count >= 15, $"Nur {checkedOperations.Count} GET-Operationen mit 404 geprüft");
        Assert.True(wrong.Count == 0, "Dokumentiertes 404 wird nicht geliefert: " + string.Join("; ", wrong));
    }

    [Fact]
    public void The_version_endpoint_and_the_downloads_are_described()
    {
        var version = _api.Find("GET", "/api/version");
        Assert.Equal("VersionInfoDto", OpenApiFixture.Operation.SchemaOf(version.Responses.GetProperty("200"), "application/json"));

        // Dateidownloads sind Binärdaten im jeweiligen Format, kein JSON-Objekt
        foreach (var (path, contentType) in new[]
                 {
                     ("/api/picklists/{id}/shipping-label.pdf", "application/pdf"),
                     ("/api/export/articles.csv", "text/csv"),
                     ("/api/labels/bin/{binId}.zpl", "text/plain"),
                     ("/api/admin/backups/{name}", "application/octet-stream"),
                 })
        {
            var schema = _api.Find("GET", path).Responses.GetProperty("200").GetProperty("content").GetProperty(contentType).GetProperty("schema");
            Assert.Equal("binary", schema.GetProperty("format").GetString());
        }

        // Typisierte Antworten statt anonymer Objekte: Backup-Einstellungen, Waage
        Assert.Equal("BackupSettingsResponse",
            OpenApiFixture.Operation.SchemaOf(_api.Find("GET", "/api/admin/backup-settings").Responses.GetProperty("200"), "application/json"));
        Assert.Equal("ScaleStatusDto",
            OpenApiFixture.Operation.SchemaOf(_api.Find("GET", "/api/hardware/scale/status").Responses.GetProperty("200"), "application/json"));
    }

    [Theory]
    [InlineData("Testing", null, HttpStatusCode.NotFound)]          // Standard außerhalb von Development: aus
    [InlineData("Testing", "true", HttpStatusCode.OK)]              // per Konfiguration auch in Production einschaltbar
    [InlineData("Development", null, HttpStatusCode.OK)]            // Development: an
    [InlineData("Development", "false", HttpStatusCode.NotFound)]   // ausdrücklich abgeschaltet
    public async Task Swagger_is_on_in_Development_and_switchable_with_Swagger_Enabled(string environment, string? enabled, HttpStatusCode expected)
    {
        using var baseFactory = new LagerApiFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            if (enabled is not null) builder.UseSetting("Swagger:Enabled", enabled);
        });
        var admin = await factory.CreateClient().AsReadyAdminAsync();

        var response = await admin.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.OK)
            Assert.Equal("Lager API", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("info").GetProperty("title").GetString());
    }
}
