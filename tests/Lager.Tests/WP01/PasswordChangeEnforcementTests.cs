using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Auth;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP01;

/// <summary>Der Pflicht-Passwortwechsel wird serverseitig erzwungen, nicht nur im Frontend.</summary>
public class PasswordChangeEnforcementTests
{
    private const string NewPassword = "Neues-Passwort-2025!";

    [Fact]
    public async Task User_with_pending_password_change_is_blocked_everywhere_except_me_and_change_password()
    {
        using var factory = new LagerApiFactory();
        var client = factory.CreateClient();
        var login = await client.LoginOkAsync(LagerApiFactory.AdminUser, LagerApiFactory.AdminPassword);
        Assert.True(login.User.MustChangePassword);
        client.WithToken(login.Token);

        // Fachliche Endpunkte: 403 mit maschinenlesbarem Code.
        var articles = await client.GetAsync("/api/articles");
        Assert.Equal(HttpStatusCode.Forbidden, articles.StatusCode);
        Assert.Equal("password_change_required", await articles.ErrorCodeAsync());

        // Auch Endpunkte mit [Authorize(Roles = "Admin")] (der Bootstrap-Admin IST Admin) sind gesperrt.
        var users = await client.GetAsync("/api/users");
        Assert.Equal(HttpStatusCode.Forbidden, users.StatusCode);
        Assert.Equal("password_change_required", await users.ErrorCodeAsync());
        var createUser = await client.PostAsJsonAsync("/api/users",
            new CreateUserRequest("eingeschleust", "Irgendein-Passwort-1!", new[] { "Admin" }));
        Assert.Equal(HttpStatusCode.Forbidden, createUser.StatusCode);

        // me und change-password bleiben erreichbar.
        var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var change = await client.PostAsJsonAsync("/api/auth/change-password",
            new ChangePasswordRequest(LagerApiFactory.AdminPassword, NewPassword));
        Assert.Equal(HttpStatusCode.OK, change.StatusCode);

        // Nach dem Wechsel liefert die Antwort ein neues Token, mit dem alles funktioniert.
        var changed = (await change.Content.ReadFromJsonAsync<LoginResponse>())!;
        Assert.False(changed.User.MustChangePassword);
        var fresh = factory.CreateClient().WithToken(changed.Token);
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/articles")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await fresh.GetAsync("/api/users")).StatusCode);
    }

    [Fact]
    public async Task Password_change_returns_a_new_working_token_and_revokes_the_old_one()
    {
        using var factory = new LagerApiFactory();
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        var user = await admin.CreateUserAsync(new[] { "Picker" });
        var login = await factory.CreateClient().LoginOkAsync(user.Username, user.Password);
        var oldClient = factory.CreateClient().WithToken(login.Token);
        Assert.Equal(HttpStatusCode.OK, (await oldClient.GetAsync("/api/articles")).StatusCode);

        var change = await oldClient.PostAsJsonAsync("/api/auth/change-password",
            new ChangePasswordRequest(user.Password, NewPassword));

        Assert.Equal(HttpStatusCode.OK, change.StatusCode);
        var changed = (await change.Content.ReadFromJsonAsync<LoginResponse>())!;
        Assert.NotEqual(login.Token, changed.Token);
        // Sitzung des Nutzers läuft mit dem neuen Token weiter ...
        var newClient = factory.CreateClient().WithToken(changed.Token);
        Assert.Equal(HttpStatusCode.OK, (await newClient.GetAsync("/api/articles")).StatusCode);
        // ... das alte Token ist widerrufen.
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldClient.GetAsync("/api/articles")).StatusCode);
    }

    [Fact]
    public async Task Weak_or_unchanged_new_password_is_rejected_with_400()
    {
        using var factory = new LagerApiFactory();
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        var user = await admin.CreateUserAsync(new[] { "Picker" });
        var client = factory.CreateClient().WithToken((await factory.CreateClient().LoginOkAsync(user.Username, user.Password)).Token);

        // zu kurz / gleich dem Benutzernamen (12 Zeichen) / gleich dem bisherigen Passwort
        foreach (var weak in new[] { "kurz1!", user.Username, user.Password })
        {
            var response = await client.PostAsJsonAsync("/api/auth/change-password",
                new ChangePasswordRequest(user.Password, weak));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("password_policy", await response.ErrorCodeAsync());
        }
        // Das Token bleibt gültig: eine abgelehnte Änderung meldet niemanden ab.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }
}
