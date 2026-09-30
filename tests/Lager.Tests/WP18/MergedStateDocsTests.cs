using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lager.Api.Health;
using Lager.Contracts.Auth;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP18;

/// <summary>
/// Aussagen der Doku zu Health-Endpunkten, Frontend-Auslieferung und Fehlerformat, die anderswo im Verhalten getestet sind
/// (WP16, WP32), hier aber als Behauptung der Seiten geprüft werden: Die Doku nennt genau die Pfade und Ausnahmen des Codes.
/// Nur die Richtung "Doku -> Code".
/// </summary>
public class MergedStateDocsTests
{
    [Fact]
    public void Health_paths_and_the_frontend_fallback_exclusions_named_in_the_docs_are_the_code_constants()
    {
        Assert.Equal("/health/live", HealthEndpoints.LivePath);
        Assert.Equal("/health/ready", HealthEndpoints.ReadyPath);

        var configuration = DocsRepo.Read("docs/CONFIGURATION.md");
        var api = DocsRepo.Read("docs/API.md");
        foreach (var path in new[] { HealthEndpoints.LivePath, HealthEndpoints.ReadyPath })
        {
            Assert.Contains($"`GET {path}`", configuration);
            Assert.Contains($"| GET | {path} |", api);
        }

        // Die Doku sagt: /api, /health und /swagger fallen nicht auf index.html zurück.
        foreach (var excluded in new[] { "api", "health", "swagger" })
            Assert.Contains(excluded, FrontendHosting.FallbackPattern);
        Assert.Contains("`/api`, `/health` und `/swagger`", configuration);

        // Beide Cache-Regeln der Doku stehen im Code.
        Assert.Contains("max-age=31536000, immutable", configuration);
        Assert.Contains("no-cache", configuration);
    }

    [Fact]
    public async Task An_unknown_path_is_401_without_a_token_and_a_problem_json_404_with_one()
    {
        using var factory = new LagerApiFactory();
        var anonymous = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/gibt-es-nicht")).StatusCode);

        var admin = await factory.CreateClient().AsReadyAdminAsync();
        var response = await admin.GetAsync("/api/gibt-es-nicht");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("not_found", body.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_short_error_bodies_named_in_the_api_reference_carry_code_and_error()
    {
        using var factory = new LagerApiFactory();
        var client = factory.CreateClient();

        // 403 password_change_required: der frische Bootstrap-Admin muss sein Passwort zuerst ändern.
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(LagerApiFactory.AdminUser, LagerApiFactory.AdminPassword));
        login.EnsureSuccessStatusCode();
        using var loginBody = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", loginBody.RootElement.GetProperty("token").GetString());

        var blocked = await client.GetAsync("/api/articles");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        using var blockedBody = JsonDocument.Parse(await blocked.Content.ReadAsStringAsync());
        Assert.Equal("password_change_required", blockedBody.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(blockedBody.RootElement.GetProperty("error").GetString()));

        // 401 invalid_credentials: ProblemDetails mit code und error (die correlationId steht im Header).
        var failed = await factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new LoginRequest(LagerApiFactory.AdminUser, "falsch-falsch-1"));
        Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        Assert.True(failed.Headers.Contains("X-Correlation-Id"));
        using var failedBody = JsonDocument.Parse(await failed.Content.ReadAsStringAsync());
        Assert.Equal("invalid_credentials", failedBody.RootElement.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(failedBody.RootElement.GetProperty("error").GetString()));
    }
}
