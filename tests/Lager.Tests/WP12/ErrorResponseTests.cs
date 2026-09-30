using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lager.Contracts.Auth;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP12;

/// <summary>Ein Host + eingeloggter Admin für alle Fehlerformat-Tests dieser Klasse (Login und BCrypt nur einmal).</summary>
public sealed class ErrorApiFixture : IAsyncLifetime
{
    public ErrorApiFactory Factory { get; } = new();
    public HttpClient Admin { get; private set; } = null!;

    public async Task InitializeAsync() => Admin = await Factory.CreateClient().AsReadyAdminAsync();

    public Task DisposeAsync()
    {
        Admin.Dispose();
        Factory.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Das einheitliche Fehlerformat über HTTP: jede Exception wird zentral auf <c>application/problem+json</c> mit
/// <c>code</c> und <c>correlationId</c> abgebildet (kein 500 mehr für fachliche Fehler), auch die leeren
/// 401/404/405 der Pipeline. Ein Test-Controller im Host wirft gezielt die Exceptions.
/// </summary>
public class ErrorResponseTests : IClassFixture<ErrorApiFixture>
{
    private readonly ErrorApiFixture _api;
    public ErrorResponseTests(ErrorApiFixture api) => _api = api;

    private HttpClient Admin => _api.Admin;

    [Theory]
    [InlineData("conflict", 409, "conflict")]
    [InlineData("conflict-code", 409, "stock_insufficient")]
    [InlineData("not-found", 404, "not_found")]
    [InlineData("bad-argument", 400, "validation_failed")]
    [InlineData("out-of-range", 400, "validation_failed")]
    [InlineData("format", 400, "validation_failed")]
    [InlineData("invalid-credentials", 401, "invalid_credentials")]
    [InlineData("user-rule", 409, "user_rule_violation")]
    [InlineData("unknown-role", 400, "unknown_role")]
    [InlineData("concurrency", 409, "concurrency_conflict")]
    [InlineData("bad-request-body", 413, "payload_too_large")]
    public async Task Exceptions_become_problem_json_with_status_code_and_correlation_id(string route, int status, string code)
    {
        var response = await Admin.GetAsync("/__wp12/" + route);
        var body = await ErrorApiFactory.JsonAsync(response);

        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(status, body.GetProperty("status").GetInt32());
        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("detail").GetString()));
        Assert.StartsWith("http", body.GetProperty("type").GetString());
        // Rückwärtskompatibel: das Frontend liest bei Login/Passwortwechsel weiter "error".
        Assert.Equal(body.GetProperty("detail").GetString(), body.GetProperty("error").GetString());
        // Die Referenz-ID im Body ist genau die im Header (und damit die in den Log-Zeilen).
        var header = response.Headers.GetValues("X-Correlation-Id").Single();
        if (code == "invalid_credentials")
            Assert.False(body.TryGetProperty("correlationId", out _)); // Login-Antwort bleibt byte-identisch (siehe eigener Test)
        else
            Assert.Equal(header, body.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task Business_messages_reach_the_client_but_database_internals_do_not()
    {
        var conflict = await ErrorApiFactory.JsonAsync(await Admin.GetAsync("/__wp12/conflict"));
        var concurrency = await ErrorApiFactory.JsonAsync(await Admin.GetAsync("/__wp12/concurrency"));

        Assert.Equal("Insufficient stock for article ABC-1 (missing 3)", conflict.GetProperty("detail").GetString());
        Assert.DoesNotContain("SQLite", concurrency.ToString());
        Assert.DoesNotContain("UPDATE", concurrency.ToString());
    }

    [Fact]
    public async Task An_unknown_exception_is_a_500_without_message_or_stacktrace_but_with_the_correlation_id()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__wp12/boom");
        request.Headers.Add("X-Correlation-Id", "wp12-korrelation-1");

        var response = await Admin.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(text).RootElement;

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("internal_error", body.GetProperty("code").GetString());
        Assert.DoesNotContain("Geheimes Detail", text);
        Assert.DoesNotContain("Passwort", text);
        Assert.DoesNotContain("stackTrace", text);
        Assert.DoesNotContain("Lager.Tests", text);
        // Die vom Client mitgeschickte (gültige) ID bleibt erhalten: Header UND Body, obwohl der Exception-Handler die Header leert.
        Assert.Equal("wp12-korrelation-1", response.Headers.GetValues("X-Correlation-Id").Single());
        Assert.Equal("wp12-korrelation-1", body.GetProperty("correlationId").GetString());
    }

    [Fact]
    public async Task InvalidOperation_thrown_by_the_framework_is_not_disguised_as_a_conflict()
    {
        var response = await Admin.GetAsync("/__wp12/framework-conflict");

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("Sequence contains no elements", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Error_responses_keep_the_cors_and_security_headers()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__wp12/conflict");
        request.Headers.Add("Origin", "http://localhost:5173");

        var response = await Admin.SendAsync(request);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("http://localhost:5173", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }

    [Fact]
    public async Task The_error_response_is_json_even_when_the_client_only_accepts_something_else()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/__wp12/not-found");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var response = await Admin.SendAsync(request);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);

        // Dasselbe für die 400 bei ungültigem Body (MVC-Pfad statt Exception-Handler).
        using var invalid = new HttpRequestMessage(HttpMethod.Post, "/__wp12/enum") { Content = Json("{\"color\":7}") };
        invalid.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        var badRequest = await Admin.SendAsync(invalid);

        Assert.Equal(HttpStatusCode.BadRequest, badRequest.StatusCode);
        Assert.Equal("application/problem+json", badRequest.Content.Headers.ContentType!.MediaType);
    }

    [Fact]
    public async Task Development_adds_the_cause_to_the_500_body()
    {
        using var factory = new ErrorApiFactory("Development");
        var admin = await factory.CreateClient().AsReadyAdminAsync();

        var body = await ErrorApiFactory.JsonAsync(await admin.GetAsync("/__wp12/boom"));

        Assert.Contains("Geheimes Detail", body.GetProperty("detail").GetString());
        Assert.Equal("System.Exception", body.GetProperty("exception").GetString());
        Assert.Contains("ErrorProbeController", body.GetProperty("stackTrace").GetString());
    }

    // ---- Leere Antworten der Pipeline --------------------------------------------------------------------------------

    [Fact]
    public async Task Anonymous_access_is_a_401_with_a_problem_body()
    {
        var anonymous = _api.Factory.CreateClient();

        var response = await anonymous.GetAsync("/api/articles");
        var body = await ErrorApiFactory.JsonAsync(response);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("unauthorized", body.GetProperty("code").GetString());
        Assert.Equal(401, body.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("correlationId").GetString()));
        Assert.Contains("Bearer", response.Headers.WwwAuthenticate.ToString()); // die Challenge des JWT-Schemas bleibt
    }

    [Fact]
    public async Task A_role_that_is_too_low_gets_a_403_problem_body()
    {
        using var factory = new ErrorApiFactory();
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        var created = await admin.PostAsJsonAsync("/api/users",
            new CreateUserRequest("wp12-viewer", AuthTestExtensions.RoleUserPassword, new[] { "Viewer" }, MustChangePassword: false));
        created.EnsureSuccessStatusCode();
        var viewer = await factory.CreateClient().LoginAsync("wp12-viewer", AuthTestExtensions.RoleUserPassword);

        var response = await viewer.PostAsJsonAsync("/api/articles", new { });
        var body = await ErrorApiFactory.JsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("forbidden", body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Unknown_routes_and_wrong_methods_are_problem_bodies_too()
    {
        var unknown = await Admin.GetAsync("/api/gibt-es-nicht");
        var wrongMethod = await Admin.DeleteAsync("/api/articles");

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("not_found", (await ErrorApiFactory.JsonAsync(unknown)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.Equal("method_not_allowed", (await ErrorApiFactory.JsonAsync(wrongMethod)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_missing_row_via_NotFound_action_result_uses_the_same_format()
    {
        var response = await Admin.GetAsync($"/api/articles/{Guid.NewGuid()}");
        var body = await ErrorApiFactory.JsonAsync(response);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("not_found", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("correlationId").GetString()));
        Assert.False(body.TryGetProperty("traceId", out _));
    }

    [Fact]
    public async Task A_pending_password_change_still_answers_403_with_its_code()
    {
        // Frisch angelegter Bootstrap-Admin: MustChangePassword. Die Antwort trägt weiter code + error (Auth-Vertrag).
        using var factory = new ErrorApiFactory();
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(LagerApiFactory.AdminUser, LagerApiFactory.AdminPassword));
        var token = (await login.Content.ReadFromJsonAsync<LoginResponse>())!.Token;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/api/articles");
        var body = await ErrorApiFactory.JsonAsync(response);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("password_change_required", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
    }

    // ---- Auth-Vertrag: code + error bleiben -------------------------------------------------------------------------

    [Fact]
    public async Task Login_failure_is_a_401_with_code_and_error_and_an_identical_body_every_time()
    {
        var client = _api.Factory.CreateClient();

        var first = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("gibt-es-nicht", "falsch-falsch-1"));
        var second = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(LagerApiFactory.AdminUser, "falsch-falsch-2"));
        var body = await ErrorApiFactory.JsonAsync(first);

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal("application/problem+json", first.Content.Headers.ContentType!.MediaType);
        Assert.Equal("invalid_credentials", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("error").GetString()));
        // Kein Rückschluss auf das Konto: derselbe Body für unbekannten Nutzer und falsches Passwort (die ID steht nur im Header).
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.NotEqual(first.Headers.GetValues("X-Correlation-Id").Single(), second.Headers.GetValues("X-Correlation-Id").Single());
    }

    [Fact]
    public async Task An_absurdly_long_login_is_rejected_with_400_before_any_password_hashing()
    {
        var response = await _api.Factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new LoginRequest(LagerApiFactory.AdminUser, new string('p', 5000)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_failed", (await ErrorApiFactory.JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Password_change_errors_are_400_with_their_codes_and_never_a_401()
    {
        using var factory = new ErrorApiFactory();
        var admin = await factory.CreateClient().AsReadyAdminAsync();

        var wrongCurrent = await admin.PostAsJsonAsync("/api/auth/change-password",
            new ChangePasswordRequest("Falsches-Altpasswort-1!", "Ein-Neues-Passwort-2026!"));
        var weakNew = await admin.PostAsJsonAsync("/api/auth/change-password",
            new ChangePasswordRequest(AuthTestExtensions.ReadyAdminPassword, "kurz"));

        Assert.Equal(HttpStatusCode.BadRequest, wrongCurrent.StatusCode);
        var wrongBody = await ErrorApiFactory.JsonAsync(wrongCurrent);
        Assert.Equal("invalid_current_password", wrongBody.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(wrongBody.GetProperty("error").GetString()));
        Assert.Equal(HttpStatusCode.BadRequest, weakNew.StatusCode);
        Assert.Equal("password_policy", (await ErrorApiFactory.JsonAsync(weakNew)).GetProperty("code").GetString());
    }

    // ---- Fachliche Fehler der echten Endpunkte (früher 500 oder uneinheitlich) -------------------------------------

    [Fact]
    public async Task A_duplicate_sku_is_a_409_instead_of_a_500()
    {
        var article = new
        {
            sku = "WP12-DUP", name = "Doppelt", description = (string?)null,
            dimensions = new { lengthMm = 10, widthMm = 10, heightMm = 10 }, weightGrams = 10,
            stacking = new { isStackable = false, stackingAxis = "Z", stackingIncrementMm = 0, maxStackCount = (int?)null },
        };

        var first = await Admin.PostAsJsonAsync("/api/articles", article);
        var second = await Admin.PostAsJsonAsync("/api/articles", article);
        var body = await ErrorApiFactory.JsonAsync(second);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("conflict", body.GetProperty("code").GetString());
        Assert.Contains("WP12-DUP", body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Supplier_errors_use_the_central_mapping_after_the_local_try_catch_was_removed()
    {
        var created = await Admin.PostAsJsonAsync("/api/suppliers", new { code = "WP12-SUP", name = "Lieferant" });
        var duplicate = await Admin.PostAsJsonAsync("/api/suppliers", new { code = "WP12-SUP", name = "Nochmal" });
        var noName = await Admin.PostAsJsonAsync("/api/suppliers", new { code = "WP12-SUP-2", name = "" });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("application/problem+json", duplicate.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.BadRequest, noName.StatusCode); // vorher: ArgumentException -> 500
    }

    [Fact]
    public async Task Unique_violations_from_the_database_are_a_409_duplicate()
    {
        var response = await Admin.PostAsync("/__wp12/duplicate-sku", null);
        var body = await ErrorApiFactory.JsonAsync(response);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("duplicate", body.GetProperty("code").GetString());
        Assert.Contains("SKU", body.GetProperty("detail").GetString());
        Assert.DoesNotContain("constraint", body.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Stock_for_an_unknown_article_is_a_409_reference_error_instead_of_a_500()
    {
        // Ein Bestand ohne Artikel/Lagerplatz verletzt die Fremdschlüssel erst beim Speichern (DbUpdateException).
        var response = await Admin.PostAsJsonAsync("/api/stock/adjust",
            new { articleId = Guid.NewGuid(), storageLocationId = Guid.NewGuid(), delta = 5 });
        var body = await ErrorApiFactory.JsonAsync(response);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("invalid_reference", body.GetProperty("code").GetString());
        Assert.DoesNotContain("FOREIGN KEY", body.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    // ---- Enums ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_enum_sent_as_a_number_is_rejected_with_400_and_as_text_accepted()
    {
        var asNumber = await Admin.PostAsync("/__wp12/enum", Json("{\"color\":1}"));
        var asText = await Admin.PostAsync("/__wp12/enum", Json("{\"color\":\"Green\"}"));

        var numberBody = await ErrorApiFactory.JsonAsync(asNumber);
        Assert.Equal(HttpStatusCode.BadRequest, asNumber.StatusCode);
        Assert.Equal("validation_failed", numberBody.GetProperty("code").GetString());
        Assert.True(numberBody.TryGetProperty("errors", out var errors) && errors.EnumerateObject().Any());
        Assert.Equal(HttpStatusCode.OK, asText.StatusCode);
        // Auch die Antwort schreibt den Enum als Text.
        Assert.Equal("Green", (await ErrorApiFactory.JsonAsync(asText)).GetProperty("color").GetString());
    }

    [Fact]
    public async Task Invalid_json_is_a_400_in_the_same_format()
    {
        var response = await Admin.PostAsync("/__wp12/enum", Json("{kaputt"));
        var body = await ErrorApiFactory.JsonAsync(response);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("validation_failed", body.GetProperty("code").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("correlationId").GetString()));
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");
}
