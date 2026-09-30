using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Lager.Contracts.Auth;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP02;

/// <summary>
/// Helfer für Rollen-Tests: legt pro Rolle einen Testnutzer mit GENAU dieser einen Rolle an
/// und liefert einen eingeloggten <see cref="HttpClient"/>. Die Rolle Admin stellt der Bootstrap-Admin.
/// </summary>
public static class RoleClient
{
    public const string Password = "Wp02-Test-Passwort-2025!";

    public static string UsernameFor(string role) => $"wp02-{role.ToLowerInvariant()}";

    /// <summary>
    /// Legt per <c>POST /api/users</c> (als <paramref name="admin"/>) einen Nutzer mit genau der Rolle
    /// <paramref name="role"/> an und liefert einen eingeloggten Client für ihn.
    /// </summary>
    public static async Task<HttpClient> CreateAsync(LagerApiFactory factory, HttpClient admin, string role)
    {
        var create = await admin.PostAsJsonAsync("/api/users",
            new CreateUserRequest(UsernameFor(role), Password, new[] { role }, DisplayName: $"WP02 {role}", MustChangePassword: false));
        create.EnsureSuccessStatusCode();

        return await SignInAsync(factory.CreateClient(), UsernameFor(role), Password);
    }

    /// <summary>
    /// Loggt ein und setzt den Bearer-Header. Erzwingt der Server einen Passwortwechsel
    /// (<c>MustChangePassword</c>), wird er zuerst durchgeführt und danach neu eingeloggt.
    /// </summary>
    public static async Task<HttpClient> SignInAsync(HttpClient client, string username, string password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, password));
        response.EnsureSuccessStatusCode();
        var login = await response.Content.ReadFromJsonAsync<LoginResponse>()
                    ?? throw new InvalidOperationException("Login-Antwort war leer");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.Token);

        if (!login.User.MustChangePassword) return client;

        var changed = password + "-neu";
        var change = await client.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest(password, changed));
        change.EnsureSuccessStatusCode();
        return await client.LoginAsync(username, changed);
    }
}

/// <summary>
/// Gemeinsamer Testkontext einer Testklasse: EINE API-Instanz (eigene SQLite-DB) und je Rolle ein
/// eingeloggter Client. Wird einmal pro Klasse aufgebaut, damit nicht jeder Test neu einloggt.
/// </summary>
public sealed class RoleClientsFixture : IAsyncLifetime
{
    private readonly Dictionary<string, HttpClient> _clients = new();

    public LagerApiFactory Factory { get; } = new();

    public HttpClient Client(string role) => _clients[role];

    public async Task InitializeAsync()
    {
        // Bootstrap-Admin (Rolle Admin): AsAdminAsync erledigt den erzwungenen Passwortwechsel.
        var admin = await Factory.CreateClient().AsAdminAsync();
        _clients["Admin"] = admin;
        foreach (var role in EndpointMatrix.Roles.Where(r => r != "Admin"))
            _clients[role] = await RoleClient.CreateAsync(Factory, admin, role);
    }

    public Task DisposeAsync()
    {
        foreach (var client in _clients.Values) client.Dispose();
        Factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Sendet eine Anfrage als <paramref name="role"/> und liefert nur den Statuscode. POST/PUT bekommen einen
    /// leeren JSON-Body. Wirft die Action erst nach der Autorisierung (z. B. wegen einer unbekannten Id), bricht
    /// der TestServer den Aufruf mit der Exception ab; das zählt als 500, denn es beweist, dass die
    /// Autorisierung durchgelassen hat. 401/403 kommen nie als Exception.
    /// </summary>
    public async Task<HttpStatusCode> SendAsync(string role, string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method is "POST" or "PUT")
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        try
        {
            using var response = await Client(role).SendAsync(request);
            return response.StatusCode;
        }
        catch (Exception)
        {
            return HttpStatusCode.InternalServerError;
        }
    }
}
