using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Lager.Contracts.Auth;

namespace Lager.Tests.Infrastructure;

public static class ApiClientExtensions
{
    /// <summary>
    /// Loggt als Bootstrap-Admin ein und setzt den Bearer-Header. Der Bootstrap-Admin muss sein Passwort
    /// beim ersten Login ändern (<c>MustChangePassword</c>) - das erledigt dieser Helfer beim ersten Aufruf
    /// pro Factory einmalig und meldet sich danach mit dem geänderten Passwort neu an.
    /// Idempotent: spätere Aufrufe (auch von anderen HttpClients derselben Factory) loggen direkt mit dem
    /// geänderten Passwort ein.
    /// </summary>
    public static async Task<HttpClient> AsAdminAsync(this HttpClient client)
    {
        var first = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(LagerApiFactory.AdminUser, LagerApiFactory.AdminPassword));

        // Passwort wurde in diesem Factory-Lauf schon geändert -> mit dem neuen Passwort anmelden.
        if (first.StatusCode == HttpStatusCode.Unauthorized)
            return await client.LoginAsync(LagerApiFactory.AdminUser, LagerApiFactory.ChangedAdminPassword);

        first.EnsureSuccessStatusCode();
        var login = await first.Content.ReadFromJsonAsync<LoginResponse>()
                    ?? throw new InvalidOperationException("Login-Antwort war leer");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        if (!login.User.MustChangePassword) return client;

        var change = await client.PostAsJsonAsync("/api/auth/change-password",
            new ChangePasswordRequest(LagerApiFactory.AdminPassword, LagerApiFactory.ChangedAdminPassword));
        change.EnsureSuccessStatusCode();
        return await client.LoginAsync(LagerApiFactory.AdminUser, LagerApiFactory.ChangedAdminPassword);
    }

    /// <summary>Loggt mit beliebigen Zugangsdaten ein (Fehler bei falschem Passwort) und setzt den Bearer-Header.</summary>
    public static async Task<HttpClient> LoginAsync(this HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, password));
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>()
                    ?? throw new InvalidOperationException("Login-Antwort war leer");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);
        return client;
    }
}
