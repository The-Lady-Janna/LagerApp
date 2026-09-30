using System.Net;
using System.Net.Http.Json;
using Lager.Application.Auth;
using Lager.Contracts.Auth;
using Lager.Domain.Auth;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP01;

/// <summary>Login/Lockout/Passwortwechsel/Rate-Limit: einheitliche Antworten, Zähler, 400 statt 401, 429.</summary>
public class LoginHardeningTests
{
    private const string WrongPassword = "voellig-falsches-Passwort";

    [Fact]
    public async Task Unknown_user_wrong_password_and_locked_account_get_the_same_response()
    {
        using var factory = new ConfigurableApiFactory("Testing", new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = LagerApiFactory.SigningKey,
            ["Auth:MaxFailedAttempts"] = "3", // konfigurierbar: schon nach 3 statt 5 Versuchen gesperrt
        });
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        var user = await admin.CreateUserAsync(new[] { "Picker" });
        var other = await admin.CreateUserAsync(new[] { "Picker" });
        var client = factory.CreateClient();

        var unknown = await client.TryLoginAsync("gibt-es-nicht", WrongPassword);
        var wrong = await client.TryLoginAsync(user.Username, WrongPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        await client.TryLoginAsync(user.Username, WrongPassword);
        await client.TryLoginAsync(user.Username, WrongPassword); // 3. Fehlversuch -> gesperrt

        // Gesperrt: auch das RICHTIGE Passwort wird abgelehnt - mit derselben Antwort wie ein unbekannter Nutzer.
        var locked = await client.TryLoginAsync(user.Username, user.Password);
        Assert.Equal(HttpStatusCode.Unauthorized, locked.StatusCode);
        var unknownBody = await unknown.Content.ReadAsStringAsync();
        Assert.Equal(unknownBody, await wrong.Content.ReadAsStringAsync());
        Assert.Equal(unknownBody, await locked.Content.ReadAsStringAsync());
        Assert.DoesNotContain("gesperrt", unknownBody, StringComparison.OrdinalIgnoreCase);

        // Die Sperre trifft nur dieses Konto.
        Assert.Equal(HttpStatusCode.OK, (await client.TryLoginAsync(other.Username, other.Password)).StatusCode);
    }

    [Fact]
    public async Task Wrong_current_password_on_change_returns_400_keeps_the_session_and_counts_as_failed_attempt()
    {
        using var factory = new LagerApiFactory();
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        var user = await admin.CreateUserAsync(new[] { "Picker" });
        var login = await factory.CreateClient().LoginOkAsync(user.Username, user.Password);
        var client = factory.CreateClient().WithToken(login.Token);

        var first = await client.PostAsJsonAsync("/api/auth/change-password",
            new ChangePasswordRequest(WrongPassword, "Ein-Neues-Passwort-2025!"));

        // 400 mit Code (kein 401 - das Frontend meldet bei 401 ab), die Sitzung bleibt gültig.
        Assert.Equal(HttpStatusCode.BadRequest, first.StatusCode);
        Assert.Equal("invalid_current_password", await first.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);

        // Fehlversuche beim Wechsel zählen wie beim Login: nach 5 ist das Konto gesperrt.
        for (var i = 0; i < 4; i++)
            await client.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest(WrongPassword, "Ein-Neues-Passwort-2025!"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().TryLoginAsync(user.Username, user.Password)).StatusCode);
    }

    [Fact]
    public async Task Login_is_rate_limited_per_ip_to_10_per_minute_with_429_and_retry_after()
    {
        using var factory = new ConfigurableApiFactory("Staging", new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = LagerApiFactory.SigningKey,
        });
        var client = factory.CreateClient();

        // Leere Zugangsdaten werden sofort (ohne BCrypt) abgelehnt, zählen für das Limit aber genauso.
        for (var i = 1; i <= 10; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.TryLoginAsync("", "")).StatusCode);
        var limited = await client.TryLoginAsync("", "");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.TryGetValues("Retry-After", out var retryAfter));
        Assert.InRange(int.Parse(retryAfter!.First()), 1, 60);
        Assert.Equal("too_many_requests", await limited.ErrorCodeAsync());
    }

    [Fact]
    public async Task Rate_limiting_is_off_in_the_Testing_environment_so_parallel_tests_are_never_throttled()
    {
        using var factory = new LagerApiFactory(); // Environment "Testing"
        var client = factory.CreateClient();

        for (var i = 0; i < 40; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.TryLoginAsync("", "")).StatusCode);
    }

    // ---- Service-Ebene (ohne Host): Timing-Angleich, Lockout-Zähler, Audit -----------------------------------------

    [Fact]
    public async Task Login_pays_a_bcrypt_run_for_unknown_inactive_and_locked_users_but_not_more_than_one()
    {
        var users = new FakeUserRepository();
        var hasher = new FakePasswordHasher();
        var audit = new RecordingAudit();
        var service = new AuthService(users, hasher, new FakeJwtTokenService(), new FakeUnitOfWork(), audit);

        var inactive = new User("inaktiv", hasher.Hash("Richtiges-Passwort-1"), Role.Picker);
        inactive.Deactivate();
        var locked = new User("gesperrt", hasher.Hash("Richtiges-Passwort-1"), Role.Picker);
        for (var i = 0; i < 5; i++) locked.OnFailedLogin();
        users.Users.AddRange(new[] { inactive, locked });

        foreach (var name in new[] { "unbekannt", "inaktiv", "gesperrt" })
            await Assert.ThrowsAsync<InvalidCredentialsException>(() =>
                service.LoginAsync(new LoginRequest(name, "Richtiges-Passwort-1")));

        Assert.Equal(3, hasher.DummyVerifications);
        Assert.Equal(0, hasher.Verifications); // das echte Verify läuft nur für aktive, ungesperrte Konten
        // Der Grund steht nur im Sicherheits-Log, nie in der Antwort.
        Assert.Contains(audit.Events, e => e.Event == SecurityEvent.LoginBlocked && e.Detail == "Konto gesperrt");
        Assert.Contains(audit.Events, e => e.Event == SecurityEvent.LoginFailed && e.Detail == "unbekannter Benutzer");
    }

    [Fact]
    public async Task Failed_attempts_reset_on_success_and_lockout_is_recorded_in_the_security_log()
    {
        var users = new FakeUserRepository();
        var hasher = new FakePasswordHasher();
        var audit = new RecordingAudit();
        var service = new AuthService(users, hasher, new FakeJwtTokenService(), new FakeUnitOfWork(), audit,
            new LockoutPolicy(3, TimeSpan.FromMinutes(5)));
        var user = new User("anna", hasher.Hash("Richtiges-Passwort-1"), Role.Picker);
        users.Users.Add(user);

        for (var i = 0; i < 2; i++)
            await Assert.ThrowsAsync<InvalidCredentialsException>(() => service.LoginAsync(new LoginRequest("anna", "falsch")));
        Assert.Equal(2, user.FailedLoginAttempts);

        await service.LoginAsync(new LoginRequest("anna", "Richtiges-Passwort-1"));
        Assert.Equal(0, user.FailedLoginAttempts); // Erfolg setzt den Zähler zurück

        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<InvalidCredentialsException>(() => service.LoginAsync(new LoginRequest("anna", "falsch")));
        Assert.True(user.IsLocked());
        Assert.Equal(1, audit.Events.Count(e => e.Event == SecurityEvent.LoginLockedOut));
        Assert.Contains(audit.Events, e => e.Event == SecurityEvent.LoginSucceeded && e.Username == "anna");
        // Nirgends im Log steht ein Passwort.
        Assert.DoesNotContain(audit.Events, e => (e.Detail ?? "").Contains("Richtiges-Passwort") || (e.Detail ?? "").Contains("falsch"));
    }
}
