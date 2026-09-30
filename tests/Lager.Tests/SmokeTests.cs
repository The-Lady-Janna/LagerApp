using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Auth;
using Lager.Tests.Infrastructure;

namespace Lager.Tests;

/// <summary>
/// Minimaler End-to-End-Test: die API startet, legt den Bootstrap-Admin an und stellt Tokens aus.
/// Jeder Test bekommt eine eigene Factory (eigene DB), weil <c>AsAdminAsync</c> das Admin-Passwort ändert.
/// </summary>
public class SmokeTests
{
    [Fact]
    public async Task Bootstrap_admin_can_log_in_and_must_change_password()
    {
        using var factory = new LagerApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(LagerApiFactory.AdminUser, LagerApiFactory.AdminPassword));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.NotNull(login);
        Assert.False(string.IsNullOrWhiteSpace(login!.Token));
        Assert.True(login.User.MustChangePassword);
    }

    [Fact]
    public async Task Login_with_wrong_password_is_rejected()
    {
        using var factory = new LagerApiFactory();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(LagerApiFactory.AdminUser, "falsches-passwort"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Authenticated_admin_can_read_current_user()
    {
        using var factory = new LagerApiFactory();
        var client = await factory.CreateClient().AsAdminAsync();

        var me = await client.GetFromJsonAsync<UserDto>("/api/auth/me");

        Assert.NotNull(me);
        Assert.Equal(LagerApiFactory.AdminUser, me!.Username);
        Assert.False(me.MustChangePassword);
    }

    [Fact]
    public async Task AsAdminAsync_can_be_called_repeatedly_on_the_same_factory()
    {
        using var factory = new LagerApiFactory();

        await factory.CreateClient().AsAdminAsync();
        var second = await factory.CreateClient().AsAdminAsync();

        var me = await second.GetFromJsonAsync<UserDto>("/api/auth/me");
        Assert.Equal(LagerApiFactory.AdminUser, me!.Username);
    }
}
