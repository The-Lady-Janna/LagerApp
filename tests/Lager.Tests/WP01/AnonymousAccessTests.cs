using System.Net;
using System.Text;
using Lager.Tests.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;

namespace Lager.Tests.WP01;

/// <summary>Die API ist standardmäßig geschlossen: anonym erreichbar ist nur POST /api/auth/login.</summary>
public class AnonymousAccessTests
{
    [Theory]
    [InlineData("GET", "/api/articles")]
    [InlineData("GET", "/api/orders")]
    [InlineData("GET", "/api/stock")]
    [InlineData("GET", "/api/audit")]
    [InlineData("GET", "/api/users")]
    [InlineData("DELETE", "/api/picklists")]
    public async Task Anonymous_business_requests_get_401(string method, string url)
    {
        using var factory = new LagerApiFactory();
        var client = factory.CreateClient();

        var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Only_login_is_anonymous_and_every_other_controller_route_answers_401_without_token()
    {
        using var factory = new LagerApiFactory();
        var client = factory.CreateClient();
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ControllerActionDescriptor>() is not null)
            .ToList();
        Assert.True(endpoints.Count > 100, $"Es wurden nur {endpoints.Count} Controller-Routen gefunden - Test würde nichts absichern");

        // 1) Metadaten: anonym ist genau POST api/auth/login, alle anderen tragen eine Autorisierung.
        var anonymous = endpoints
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(e => $"{string.Join("/", e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)} {e.RoutePattern.RawText}")
            .ToList();
        Assert.Equal(new[] { "POST api/auth/login" }, anonymous);
        Assert.All(
            endpoints.Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null),
            e => Assert.NotEmpty(e.Metadata.GetOrderedMetadata<IAuthorizeData>()));

        // 2) Wirklich anfragen: jede geschlossene Route liefert ohne Token 401 (die Autorisierung greift vor
        //    der Action, es wird also nichts ausgeführt).
        var notUnauthorized = new List<string>();
        foreach (var endpoint in endpoints.Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is null))
        {
            var path = BuildSamplePath(endpoint.RoutePattern);
            foreach (var method in endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)
            {
                var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path));
                if (response.StatusCode != HttpStatusCode.Unauthorized)
                    notUnauthorized.Add($"{method} {path} -> {(int)response.StatusCode}");
            }
        }
        Assert.Empty(notUnauthorized);
    }

    [Fact]
    public async Task Swagger_is_not_available_outside_development()
    {
        using var factory = new LagerApiFactory(); // Environment "Testing"
        var client = await factory.CreateClient().AsReadyAdminAsync();

        // Selbst angemeldet gibt es keine API-Beschreibung (anonym erreicht man nicht einmal ein 404: die
        // Fallback-Policy antwortet auch für unbekannte Pfade mit 401).
        var response = await client.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Setzt für jeden Routenparameter einen Wert ein, der dessen Constraint erfüllt (sonst wäre es 404 statt 401).</summary>
    private static string BuildSamplePath(RoutePattern pattern)
    {
        var sb = new StringBuilder();
        foreach (var segment in pattern.PathSegments)
        {
            sb.Append('/');
            foreach (var part in segment.Parts)
            {
                switch (part)
                {
                    case RoutePatternLiteralPart literal: sb.Append(literal.Content); break;
                    case RoutePatternSeparatorPart separator: sb.Append(separator.Content); break;
                    case RoutePatternParameterPart parameter: sb.Append(SampleValue(parameter)); break;
                }
            }
        }
        return sb.ToString();
    }

    private static string SampleValue(RoutePatternParameterPart parameter)
    {
        var policies = parameter.ParameterPolicies.Select(p => p.Content ?? "").ToList();
        if (policies.Contains("guid")) return Guid.NewGuid().ToString();
        if (policies.Any(p => p is "int" or "long" or "double" or "decimal" or "float")) return "1";
        if (policies.Contains("bool")) return "true";
        return "x";
    }
}
