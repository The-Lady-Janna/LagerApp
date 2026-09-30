using System.Net;
using System.Net.Http.Json;
using Lager.Api.Security;
using Lager.Contracts.Auth;
using Lager.Domain.Auth;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP01;

/// <summary>
/// Ein ausgestelltes Token überlebt weder Deaktivierung noch Rollenänderung noch Passwort-Reset
/// (der Passwortwechsel selbst ist in PasswordChangeEnforcementTests abgedeckt).
/// </summary>
public class TokenRevocationTests
{
    [Fact]
    public async Task Deactivating_a_user_revokes_the_token_immediately()
    {
        using var factory = new LagerApiFactory();
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        var user = await admin.CreateUserAsync(new[] { "Picker" });
        var client = factory.CreateClient().WithToken((await factory.CreateClient().LoginOkAsync(user.Username, user.Password)).Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/articles")).StatusCode);

        var deactivate = await admin.PostAsync($"/api/users/{user.Id}/deactivate", null);

        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/articles")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
        // Auch ein neuer Login ist nicht mehr möglich.
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().TryLoginAsync(user.Username, user.Password)).StatusCode);
    }

    [Fact]
    public async Task Changing_roles_revokes_the_old_token_and_a_new_login_carries_the_new_roles()
    {
        using var factory = new LagerApiFactory();
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        var user = await admin.CreateUserAsync(new[] { "Picker" });
        var client = factory.CreateClient().WithToken((await factory.CreateClient().LoginOkAsync(user.Username, user.Password)).Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/articles")).StatusCode);

        var update = await admin.PutAsJsonAsync($"/api/users/{user.Id}", new UpdateUserRequest(new[] { "Picker", "Packer" }));

        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/articles")).StatusCode);
        var relogin = await factory.CreateClient().LoginOkAsync(user.Username, user.Password);
        Assert.Contains("Packer", relogin.User.Roles);
        Assert.Equal(HttpStatusCode.OK, (await factory.CreateClient().WithToken(relogin.Token).GetAsync("/api/articles")).StatusCode);
    }

    [Fact]
    public async Task Admin_password_reset_revokes_the_old_token()
    {
        using var factory = new LagerApiFactory();
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        var user = await admin.CreateUserAsync(new[] { "Picker" });
        var client = factory.CreateClient().WithToken((await factory.CreateClient().LoginOkAsync(user.Username, user.Password)).Token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/articles")).StatusCode);

        var reset = await admin.PostAsJsonAsync($"/api/users/{user.Id}/reset-password",
            new AdminResetPasswordRequest("Vom-Admin-Gesetzt-2025!", MustChangeOnNextLogin: true));

        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/articles")).StatusCode);
        // Das neue Passwort gilt, verlangt aber den Wechsel (serverseitig erzwungen).
        var relogin = await factory.CreateClient().LoginOkAsync(user.Username, "Vom-Admin-Gesetzt-2025!");
        Assert.True(relogin.User.MustChangePassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateClient().WithToken(relogin.Token).GetAsync("/api/articles")).StatusCode);
    }

    [Fact]
    public void Token_check_rejects_missing_or_stale_stamp_unknown_user_and_changed_roles()
    {
        // Reine Vergleichslogik des Token-Widerrufs (Tokens älterer Versionen tragen keinen Stamp).
        var user = new User("stamp-user", "hash:1", Role.Picker);
        var roles = new[] { "Picker" };
        var oldStamp = user.GetSecurityStamp();

        Assert.Null(AuthenticatedUserValidator.Check(user, oldStamp, roles));
        Assert.NotNull(AuthenticatedUserValidator.Check(user, null, roles));
        Assert.NotNull(AuthenticatedUserValidator.Check(null, oldStamp, roles));
        Assert.NotNull(AuthenticatedUserValidator.Check(user, oldStamp, new[] { "Picker", "Admin" }));

        user.SetPasswordHash("hash:2");
        Assert.NotEqual(oldStamp, user.GetSecurityStamp());
        Assert.NotNull(AuthenticatedUserValidator.Check(user, oldStamp, roles));
        Assert.Null(AuthenticatedUserValidator.Check(user, user.GetSecurityStamp(), roles));
    }
}
