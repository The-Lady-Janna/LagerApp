using System.Net;
using Lager.Api.Middleware;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP01;

/// <summary>Sicherheits-Header und die Behandlung der von aussen kommenden Correlation-Id.</summary>
public class HttpHardeningTests
{
    [Fact]
    public async Task Every_response_carries_the_security_headers_even_a_401()
    {
        using var factory = new LagerApiFactory();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/articles"); // anonym -> 401 (Kurzschluss in der Autorisierung)

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("nosniff", Single(response, "X-Content-Type-Options"));
        Assert.Equal("DENY", Single(response, "X-Frame-Options"));
        Assert.Equal("no-referrer", Single(response, "Referrer-Policy"));
        Assert.Contains("camera=(self)", Single(response, "Permissions-Policy")); // Barcode-Scanner
        var csp = Single(response, "Content-Security-Policy");
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("base-uri 'self'", csp);
        Assert.Contains("img-src 'self' data: blob:", csp);
    }

    [Fact]
    public async Task CORS_preflight_still_works_although_the_api_is_closed_by_default()
    {
        using var factory = new LagerApiFactory();
        var client = factory.CreateClient();
        var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/articles");
        preflight.Headers.Add("Origin", "http://localhost:5173");
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");

        var response = await client.SendAsync(preflight);

        // Ein Browser schickt den Preflight ohne Token; die Fallback-Policy darf ihn nicht mit 401 abweisen.
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("http://localhost:5173", Single(response, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task A_valid_correlation_id_is_kept_and_an_invalid_one_is_replaced()
    {
        using var factory = new LagerApiFactory();
        var client = factory.CreateClient();

        var kept = await SendWithCorrelationId(client, "meine-id_1.2");
        var withSpaces = await SendWithCorrelationId(client, "boese id] [ERR] gefaelscht");
        var tooLong = await SendWithCorrelationId(client, new string('a', 65));
        var maxLength = await SendWithCorrelationId(client, new string('b', 64));

        Assert.Equal("meine-id_1.2", kept);
        Assert.NotEqual("boese id] [ERR] gefaelscht", withSpaces);
        Assert.True(CorrelationIdMiddleware.IsValid(withSpaces)); // neu erzeugt und selbst gültig
        Assert.NotEqual(new string('a', 65), tooLong);
        Assert.True(CorrelationIdMiddleware.IsValid(tooLong));
        Assert.Equal(new string('b', 64), maxLength);
    }

    [Theory]
    [InlineData("abc-DEF_123.x", true)]
    [InlineData("", false)]
    [InlineData("mit leerzeichen", false)]
    [InlineData("zeile\nzwei", false)]
    [InlineData("abc\n", false)] // "$" würde den abschliessenden Umbruch durchlassen
    [InlineData("klammer]", false)]
    [InlineData("ümlaut", false)]
    public void Correlation_id_format(string id, bool valid) => Assert.Equal(valid, CorrelationIdMiddleware.IsValid(id));

    private static string Single(HttpResponseMessage response, string header)
    {
        Assert.True(response.Headers.TryGetValues(header, out var values), $"Header {header} fehlt");
        return Assert.Single(values!);
    }

    private static async Task<string> SendWithCorrelationId(HttpClient client, string id)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/articles");
        request.Headers.TryAddWithoutValidation("X-Correlation-Id", id);
        using var response = await client.SendAsync(request);
        return Single(response, "X-Correlation-Id");
    }
}
