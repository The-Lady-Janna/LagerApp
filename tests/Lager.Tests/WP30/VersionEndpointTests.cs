using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Lager.Contracts.Auth;
using Lager.Infrastructure.Persistence.SchemaSteps;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP30;

/// <summary>
/// <c>GET /api/version</c>: App-Version (aus der Assembly) und Schemastand der Datenbank (höchster angewendeter Schema-Schritt aus
/// <c>__LagerSchemaVersion</c>). Wie jeder Endpunkt außer dem Login verlangt er die Anmeldung und folgt dem Passwortwechsel-Zwang;
/// eine bestimmte Rolle braucht er nicht.
/// </summary>
public class VersionEndpointTests : IClassFixture<LagerApiFactory>
{
    private readonly LagerApiFactory _factory;

    public VersionEndpointTests(LagerApiFactory factory) => _factory = factory;

    [Fact]
    public async Task An_anonymous_caller_gets_401_as_problem_details()
    {
        var response = await _factory.CreateClient().GetAsync("/api/version");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Any_signed_in_user_gets_the_app_version_and_the_schema_state_of_the_database()
    {
        var admin = await _factory.CreateClient().AsReadyAdminAsync();
        var viewer = await _factory.CreateClientWithRolesAsync("Viewer");

        // der neueste Schritt, den diese App-Version kennt: eine frische Datenbank steht genau dort
        var newest = SchemaStepCatalog.Discover().OrderBy(s => s.Order).Last();
        var informational = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        foreach (var client in new[] { admin, viewer })
        {
            var response = await client.GetAsync("/api/version");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = (await response.Content.ReadFromJsonAsync<JsonElement>());
            Assert.Equal(informational.Split('+')[0], body.GetProperty("appVersion").GetString());
            Assert.Equal(newest.Order, body.GetProperty("schemaVersion").GetInt32());
            Assert.Equal(newest.Name, body.GetProperty("schemaStep").GetString());
        }
    }

    [Fact]
    public async Task A_pending_password_change_blocks_the_version_like_every_other_endpoint()
    {
        // eigene Factory: der Bootstrap-Admin muss noch sein Einmalpasswort haben (MustChangePassword)
        using var factory = new LagerApiFactory();
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(LagerApiFactory.AdminUser, LagerApiFactory.AdminPassword));
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!;
        Assert.True(token.User.MustChangePassword);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);

        var response = await client.GetAsync("/api/version");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("password_change_required", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
    }
}
