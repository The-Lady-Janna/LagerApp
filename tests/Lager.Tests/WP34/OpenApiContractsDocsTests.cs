using System.Net;
using System.Text.Json;
using Lager.Tests.Infrastructure;
using Lager.Tests.WP18;
using Lager.Tests.WP30;
using Microsoft.AspNetCore.Hosting;

namespace Lager.Tests.WP34;

/// <summary>
/// WP34: <c>Lager.Contracts</c> erzeugt seine XML-Dokumentation, Swagger bindet sie automatisch ein (Program.cs, LagerOpenApi), und die
/// Aussagen der Seite docs/features/openapi.md über den Zugriff auf die Beschreibung stimmen: ohne Anmeldung lesbar, die Endpunkte
/// selbst bleiben geschützt, ohne Schalter gibt es Swagger außerhalb von Development nicht.
/// </summary>
public class OpenApiContractsDocsTests : IClassFixture<OpenApiFixture>
{
    private readonly OpenApiFixture _api;

    public OpenApiContractsDocsTests(OpenApiFixture api) => _api = api;

    [Fact]
    public void The_contracts_project_generates_its_xml_documentation_without_requiring_comments()
    {
        var project = DocsRepo.Read("src/Lager.Contracts/Lager.Contracts.csproj");

        Assert.Matches(@"<GenerateDocumentationFile>\s*true\s*</GenerateDocumentationFile>", project);
        // Fehlende Kommentare (CS1591) dürfen einen LagerStrict-Build nicht brechen, sonst wäre jedes undokumentierte DTO ein Fehler.
        Assert.Matches(@"<NoWarn>[^<]*\bCS1591\b[^<]*</NoWarn>", project);

        // Program.cs liest die Datei aus dem Anwendungsverzeichnis: sie muss neben der Test-Assembly liegen (Kopie der Referenz).
        var xml = Path.Combine(AppContext.BaseDirectory, "Lager.Contracts.xml");
        Assert.True(File.Exists(xml), "Lager.Contracts.xml liegt nicht im Ausgabeverzeichnis - Swagger kann die DTO-Kommentare nicht lesen");
        Assert.Contains("T:Lager.Contracts.Articles.CreateArticleRequest", File.ReadAllText(xml));
    }

    [Fact]
    public void Swagger_shows_the_dto_comments_as_descriptions_of_schemas_and_fields()
    {
        var schemas = _api.Document.GetProperty("components").GetProperty("schemas");

        // Kommentar am Typ: Beschreibung des Schemas.
        var create = schemas.GetProperty("CreateArticleRequest");
        Assert.Contains("Neuer Artikel", create.GetProperty("description").GetString());

        // Kommentare an den Feldern (param-Tags am Record werden zu Eigenschaften-Kommentaren).
        var article = schemas.GetProperty("ArticleDto").GetProperty("properties");
        Assert.Contains("GTIN/EAN", article.GetProperty("gtin").GetProperty("description").GetString());
        Assert.Contains("Saison", article.GetProperty("isCurrentlyActive").GetProperty("description").GetString());
    }

    [Fact]
    public async Task The_description_is_readable_without_login_while_the_endpoints_stay_protected()
    {
        var anonymous = _api.CreateClient();

        // Die Beschreibung und die Oberfläche brauchen kein Token (docs/features/openapi.md, Abschnitt Sicherheit) ...
        var document = await anonymous.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        var ui = await anonymous.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);

        // ... die beschriebenen Endpunkte aber schon.
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/articles")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/version")).StatusCode);
    }

    [Fact]
    public async Task Without_the_switch_there_is_no_swagger_outside_Development_and_the_switch_overrides_the_environment_both_ways()
    {
        using var baseFactory = new LagerApiFactory();

        // Standard ausserhalb von Development (Docker-Image: Production): aus, auch der Pfad der Oberfläche.
        using (var off = baseFactory.WithWebHostBuilder(b => b.UseEnvironment("Production").UseSetting("Jwt:SigningKey", LagerApiFactory.SigningKey)))
        {
            var admin = await off.CreateClient().AsReadyAdminAsync();
            Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/swagger/v1/swagger.json")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/swagger/index.html")).StatusCode);
        }

        // Mit Swagger:Enabled=true läuft es auch in Production; die Beschreibung trägt dann den Titel der API.
        using var on = baseFactory.WithWebHostBuilder(b => b.UseEnvironment("Production")
            .UseSetting("Jwt:SigningKey", LagerApiFactory.SigningKey)
            .UseSetting("Swagger:Enabled", "true"));
        var response = await on.CreateClient().GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Lager API", json.RootElement.GetProperty("info").GetProperty("title").GetString());
    }
}
