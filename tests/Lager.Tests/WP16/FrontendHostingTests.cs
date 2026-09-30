using System.IO.Compression;
using System.Net;
using Lager.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lager.Tests.WP16;

/// <summary>
/// Das Backend liefert ein gebautes Frontend aus wwwroot mit aus (Docker-Image: ein Container, ein Port): Deep-Links bekommen
/// index.html, die API bleibt eine API, die Cache-Regeln stimmen, Antworten sind komprimiert. Ohne wwwroot bleibt es eine reine API.
/// </summary>
public sealed class FrontendHostingTests : IDisposable
{
    private const string IndexMarker = "<title>Lager-Testoberflaeche</title>";
    private static readonly string BundleScript = string.Concat(Enumerable.Repeat("console.log('Lager-Bundle');\n", 300));

    private readonly string _webRoot = Directory.CreateTempSubdirectory("lager-wp16-wwwroot-").FullName;
    private readonly LagerApiFactory _api = new();
    private readonly WebApplicationFactory<Program> _frontend;

    public FrontendHostingTests()
    {
        // So sieht ein Vite-Build aus: index.html mit Verweisen auf gehashte Bundles unter /assets/, dazu Service-Worker,
        // Manifest und Icon mit festem Namen.
        File.WriteAllText(Path.Combine(_webRoot, "index.html"),
            $"<!doctype html><html><head>{IndexMarker}<script type=\"module\" src=\"/assets/index-abc123.js\"></script></head><body><div id=\"root\"></div></body></html>");
        File.WriteAllText(Path.Combine(_webRoot, "sw.js"), "self.addEventListener('fetch', () => {});");
        File.WriteAllText(Path.Combine(_webRoot, "manifest.webmanifest"), """{"name":"Lager","start_url":"/"}""");
        File.WriteAllText(Path.Combine(_webRoot, "favicon.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        File.WriteAllText(Path.Combine(_webRoot, "assets", "index-abc123.js"), BundleScript);

        _frontend = WithWebRoot(_webRoot);
    }

    public void Dispose()
    {
        _api.Dispose(); // schließt auch die abgeleiteten Hosts
        try { Directory.Delete(_webRoot, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private WebApplicationFactory<Program> WithWebRoot(string path) => _api.WithWebHostBuilder(builder => builder.UseWebRoot(path));

    [Theory]
    [InlineData("/")]
    [InlineData("/orders")]
    [InlineData("/orders/3fa85f64-5717-4562-b3fc-2c963f66afa6")]
    [InlineData("/picklists/abc/pack")]
    public async Task Start_page_and_deep_links_get_index_html_without_a_login(string path)
    {
        var response = await _frontend.CreateClient().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(IndexMarker, await response.Content.ReadAsStringAsync());
        AssertRevalidated(response); // nach einem Update sofort die neue Oberfläche
        // Die CSP aus dem Sicherheits-Kern gilt auch für die ausgelieferte Oberfläche (keine Inline-Skripte, alles 'self').
        Assert.Contains("default-src 'self'", string.Join(";", response.Headers.GetValues("Content-Security-Policy")));
    }

    [Theory]
    [InlineData("/api/gibtsnicht")]
    [InlineData("/api/auth")]
    [InlineData("/API/orders/x/y")]
    [InlineData("/health/gibtsnicht")]
    [InlineData("/health")]
    [InlineData("/swagger/v1/swagger.json")]
    public async Task Backend_paths_never_fall_back_to_the_frontend(string path)
    {
        var admin = await _frontend.CreateClient().AsReadyAdminAsync();

        var response = await admin.GetAsync(path);

        Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.DoesNotContain(IndexMarker, await response.Content.ReadAsStringAsync());
        Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed,
            $"{path} -> {(int)response.StatusCode}");
    }

    [Fact]
    public async Task An_unknown_api_path_is_a_json_404_when_logged_in_and_a_json_401_when_not_never_html()
    {
        var anonymous = _frontend.CreateClient();
        var admin = await _frontend.CreateClient().AsReadyAdminAsync();

        var notFound = await admin.GetAsync("/api/gibtsnicht");
        var unauthorized = await anonymous.GetAsync("/api/gibtsnicht");

        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);
        Assert.Equal("application/problem+json", notFound.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.DoesNotContain(IndexMarker, await unauthorized.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_missing_file_is_not_answered_with_html_that_a_browser_would_run_as_javascript()
    {
        var admin = await _frontend.CreateClient().AsReadyAdminAsync();

        var response = await admin.GetAsync("/assets/index-fehlt.js");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(IndexMarker, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Hashed_bundles_are_immutable_everything_else_is_revalidated()
    {
        var client = _frontend.CreateClient();

        var bundle = await client.GetAsync("/assets/index-abc123.js");
        Assert.Equal(HttpStatusCode.OK, bundle.StatusCode);
        Assert.Contains("javascript", bundle.Content.Headers.ContentType?.MediaType);
        var cache = bundle.Headers.CacheControl!;
        Assert.True(cache.Public);
        Assert.Equal(TimeSpan.FromDays(365), cache.MaxAge);
        Assert.Contains(cache.Extensions, e => e.Name == "immutable");

        // Feste Namen: bei jeder Nutzung per ETag nachfragen, sonst bliebe nach einem Update der alte Service-Worker aktiv.
        foreach (var path in new[] { "/index.html", "/sw.js", "/manifest.webmanifest", "/favicon.svg" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            AssertRevalidated(response);
            Assert.NotNull(response.Headers.ETag);
        }
        Assert.Equal("application/manifest+json", (await client.GetAsync("/manifest.webmanifest")).Content.Headers.ContentType?.MediaType);

        // Die Neuprüfung ist billig: unveränderte Datei -> 304 ohne Body.
        var first = await client.GetAsync("/sw.js");
        var revalidate = new HttpRequestMessage(HttpMethod.Get, "/sw.js");
        revalidate.Headers.IfNoneMatch.Add(first.Headers.ETag!);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(revalidate)).StatusCode);
    }

    [Fact]
    public async Task Health_endpoints_and_login_still_work_next_to_the_frontend()
    {
        var client = _frontend.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        await client.AsReadyAdminAsync(); // /api/auth/login läuft durch den Fallback nicht hindurch
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Responses_are_compressed_with_brotli_preferred_and_gzip_as_fallback()
    {
        var client = _frontend.CreateClient();

        var brotli = await Send(client, "/assets/index-abc123.js", "br, gzip");
        Assert.Equal("br", Assert.Single(brotli.Content.Headers.ContentEncoding));
        Assert.Equal(BundleScript, Decode(await brotli.Content.ReadAsByteArrayAsync(), "br"));

        var gzip = await Send(client, "/assets/index-abc123.js", "gzip");
        Assert.Equal("gzip", Assert.Single(gzip.Content.Headers.ContentEncoding));
        Assert.Equal(BundleScript, Decode(await gzip.Content.ReadAsByteArrayAsync(), "gzip"));

        // Auch JSON aus der API (hier die Health-Antwort); ohne Accept-Encoding bleibt alles unverändert lesbar.
        var json = await Send(client, "/health/live", "gzip");
        Assert.Equal("""{"status":"Healthy"}""", Decode(await json.Content.ReadAsByteArrayAsync(), "gzip"));
        Assert.Contains("Accept-Encoding", json.Headers.Vary);

        var plain = await Send(client, "/assets/index-abc123.js", null);
        Assert.Empty(plain.Content.Headers.ContentEncoding);
        Assert.Equal(BundleScript, await plain.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Without_a_frontend_the_backend_stays_a_pure_api()
    {
        // Weder Standard-wwwroot (gibt es im Repo nicht) noch ein Ordner ohne index.html: kein Frontend, kein Fallback.
        var emptyRoot = Directory.CreateTempSubdirectory("lager-wp16-empty-").FullName;
        try
        {
            foreach (var factory in new[] { _api, WithWebRoot(emptyRoot) })
            {
                var admin = await factory.CreateClient().AsReadyAdminAsync();

                foreach (var path in new[] { "/", "/orders", "/index.html" })
                {
                    var response = await admin.GetAsync(path);
                    Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
                    Assert.NotEqual("text/html", response.Content.Headers.ContentType?.MediaType);
                }
                Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/auth/me")).StatusCode);
            }
        }
        finally
        {
            Directory.Delete(emptyRoot, recursive: true);
        }
    }

    /// <summary>no-cache: speichern erlaubt, aber vor jeder Verwendung nachfragen (kein max-age, kein immutable).</summary>
    private static void AssertRevalidated(HttpResponseMessage response)
    {
        var cache = response.Headers.CacheControl;
        Assert.NotNull(cache);
        Assert.True(cache.NoCache);
        Assert.Null(cache.MaxAge);
        Assert.DoesNotContain(cache.Extensions, e => e.Name == "immutable");
    }

    private static async Task<HttpResponseMessage> Send(HttpClient client, string path, string? acceptEncoding)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (acceptEncoding is not null) request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        return await client.SendAsync(request);
    }

    private static string Decode(byte[] bytes, string encoding)
    {
        using var input = new MemoryStream(bytes);
        using Stream decoder = encoding == "br"
            ? new BrotliStream(input, CompressionMode.Decompress)
            : new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(decoder);
        return reader.ReadToEnd();
    }
}
