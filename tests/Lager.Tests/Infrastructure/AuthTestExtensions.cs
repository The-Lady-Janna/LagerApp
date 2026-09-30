using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lager.Contracts.Auth;

namespace Lager.Tests.Infrastructure;

/// <summary>
/// Test-Helfer für eingeloggte Clients. Alle späteren Testpakete bauen darauf auf: die API ist standardmäßig
/// geschlossen, jeder Test braucht also einen angemeldeten Client.
/// </summary>
public static class AuthTestExtensions
{
    /// <summary>Passwort, das <see cref="AsReadyAdminAsync"/> dem Bootstrap-Admin gibt.</summary>
    public const string ReadyAdminPassword = LagerApiFactory.ChangedAdminPassword;

    /// <summary>Passwort aller Nutzer, die <see cref="CreateClientWithRolesAsync"/> anlegt.</summary>
    public const string RoleUserPassword = "Test-Rollen-Pw-2025!";

    /// <summary>
    /// Liefert den Client als "einsatzbereiten" Admin: Bearer-Token gesetzt, kein ausstehender Passwortwechsel.
    /// Beim ersten Aufruf pro Factory loggt der Helfer mit den Bootstrap-Zugangsdaten ein, stellt bei
    /// <c>MustChangePassword</c> sofort das Passwort auf <see cref="ReadyAdminPassword"/> um und loggt neu ein.
    /// Idempotent: ist das Passwort schon umgestellt (auch von einem anderen Client derselben Factory),
    /// meldet er sich direkt damit an. Aufrufe sollten pro Factory nacheinander erfolgen (ein paralleler erster
    /// Aufruf könnte am Passwortwechsel des anderen scheitern - der Helfer fängt das mit einem erneuten Login ab).
    /// </summary>
    public static async Task<HttpClient> AsReadyAdminAsync(this HttpClient c)
    {
        // Häufigster Fall: schon umgestellt. Spart späteren Aufrufen den Umweg über ein fehlschlagendes Login.
        var ready = await TryLoginAsync(c, LagerApiFactory.AdminUser, ReadyAdminPassword);
        if (ready is not null) return UseToken(c, ready);

        var bootstrap = await TryLoginAsync(c, LagerApiFactory.AdminUser, LagerApiFactory.AdminPassword);
        if (bootstrap is not null)
        {
            UseToken(c, bootstrap);
            if (!bootstrap.User.MustChangePassword) return c;

            // Schlägt der Wechsel fehl (ein paralleler Aufruf war schneller), gilt das neue Passwort schon -
            // der Login unten klärt es.
            await c.PostAsJsonAsync("/api/auth/change-password",
                new ChangePasswordRequest(LagerApiFactory.AdminPassword, ReadyAdminPassword));
            c.DefaultRequestHeaders.Authorization = null;
        }

        return await c.LoginAsync(LagerApiFactory.AdminUser, ReadyAdminPassword);
    }

    /// <summary>
    /// Legt (als einsatzbereiter Admin) einen Nutzer mit genau diesen Rollen an - ohne Passwortwechsel-Zwang,
    /// mit eindeutigem Benutzernamen - und liefert einen frischen, als dieser Nutzer eingeloggten Client.
    /// Rollennamen wie in <c>Lager.Domain.Auth.Role</c>: "Admin", "Manager", "Picker", "Packer", "Receiver", "Viewer".
    /// </summary>
    public static async Task<HttpClient> CreateClientWithRolesAsync(this LagerApiFactory f, params string[] roles)
    {
        var admin = await f.CreateClient().AsReadyAdminAsync();
        var username = "user-" + Guid.NewGuid().ToString("N")[..12];

        var created = await admin.PostAsJsonAsync("/api/users",
            new CreateUserRequest(username, RoleUserPassword, roles, MustChangePassword: false));
        if (created.StatusCode != HttpStatusCode.Created)
            throw new InvalidOperationException(
                $"Nutzer mit Rollen [{string.Join(", ", roles)}] konnte nicht angelegt werden: {(int)created.StatusCode} {await created.Content.ReadAsStringAsync()}");

        return await f.CreateClient().LoginAsync(username, RoleUserPassword);
    }

    private static async Task<LoginResponse?> TryLoginAsync(HttpClient c, string username, string password)
    {
        var response = await c.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, password));
        return response.IsSuccessStatusCode ? await response.Content.ReadFromJsonAsync<LoginResponse>() : null;
    }

    private static HttpClient UseToken(HttpClient c, LoginResponse login)
    {
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        return c;
    }
}
