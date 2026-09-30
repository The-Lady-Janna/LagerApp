using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lager.Application.Abstractions;
using Lager.Contracts.Auth;
using Lager.Domain.Auth;
using Lager.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Lager.Tests.WP01;

/// <summary>
/// Eigene Factory für Tests, die Environment oder Konfiguration gezielt setzen müssen (Production ohne Key,
/// Rate-Limiting in "Staging", Key-Datei, abweichende Sperr-Schwelle). Wie <see cref="LagerApiFactory"/> mit eigener
/// SQLite-Datei; einen Signing-Key setzt sie NICHT von sich aus.
/// </summary>
public sealed class ConfigurableApiFactory : WebApplicationFactory<Program>
{
    private readonly string _environment;
    private readonly Dictionary<string, string?> _settings;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"lager-wp01-{Guid.NewGuid():N}.db");

    public ConfigurableApiFactory(string environment, IDictionary<string, string?>? settings = null)
    {
        _environment = environment;
        _settings = settings is null ? new() : new Dictionary<string, string?>(settings);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath}");
        builder.UseSetting("Database:Seed", "false");
        builder.UseSetting("Auth:BootstrapAdminUsername", LagerApiFactory.AdminUser);
        builder.UseSetting("Auth:BootstrapAdminPassword", LagerApiFactory.AdminPassword);
        foreach (var (key, value) in _settings)
            builder.UseSetting(key, value);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            try { File.Delete(_dbPath + suffix); } catch (IOException) { /* Temp-Datei */ }
        }
    }
}

/// <summary>Kleine Helfer, die nur die WP01-Tests brauchen (die geteilten stehen in AuthTestExtensions).</summary>
internal static class WP01Api
{
    public const string UserPassword = "Test-Nutzer-Pw-2025!";

    public sealed record TestUser(Guid Id, string Username, string Password);

    public static string NewUsername() => "u-" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>Legt als Admin einen Nutzer an (ohne Pflicht-Wechsel, sofern nicht verlangt) und liefert dessen Daten.</summary>
    public static async Task<TestUser> CreateUserAsync(
        this HttpClient admin, string[] roles, bool mustChangePassword = false, string? password = null)
    {
        var username = NewUsername();
        password ??= UserPassword;
        var response = await admin.PostAsJsonAsync("/api/users",
            new CreateUserRequest(username, password, roles, MustChangePassword: mustChangePassword));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var dto = await response.Content.ReadFromJsonAsync<UserDto>();
        return new TestUser(dto!.Id, username, password);
    }

    /// <summary>Roher Login ohne Assert; ohne Bearer-Header, damit ein alter Token nichts verfälscht.</summary>
    public static Task<HttpResponseMessage> TryLoginAsync(this HttpClient client, string username, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new LoginRequest(username, password));

    public static async Task<LoginResponse> LoginOkAsync(this HttpClient client, string username, string password)
    {
        var response = await client.TryLoginAsync(username, password);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    /// <summary>Neuer Client der Factory mit genau diesem Token.</summary>
    public static HttpClient WithToken(this HttpClient client, string token)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static async Task<string?> ErrorCodeAsync(this HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(text)) return null;
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
    }
}

// ---- Fakes für Service-Unit-Tests (ohne Host und ohne Datenbank) -------------------------------------------------

internal sealed class FakeUserRepository : IUserRepository
{
    public List<User> Users { get; } = new();

    /// <summary>Künstliche Wartezeit je Lesezugriff: erst dann überlappen gleichzeitige Aufrufe wie bei einer echten Datenbank.</summary>
    public TimeSpan Latency { get; set; }

    private Task SimulateDatabaseAsync(CancellationToken ct) => Latency > TimeSpan.Zero ? Task.Delay(Latency, ct) : Task.CompletedTask;

    public async Task<User?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await SimulateDatabaseAsync(ct);
        return Users.FirstOrDefault(u => u.Id == id);
    }

    public Task<User?> FindByUsernameAsync(string username, CancellationToken ct = default) =>
        Task.FromResult(Users.FirstOrDefault(u => u.Username == username));

    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken ct = default)
    {
        await SimulateDatabaseAsync(ct);
        return Users.ToList();
    }

    public Task<bool> AnyAsync(CancellationToken ct = default) => Task.FromResult(Users.Count > 0);
    public Task AddAsync(User entity, CancellationToken ct = default) { Users.Add(entity); return Task.CompletedTask; }
    public void Remove(User entity) => Users.Remove(entity);
}

internal sealed class FakeUnitOfWork : IUnitOfWork
{
    public int Saves { get; private set; }
    public Task<int> SaveChangesAsync(CancellationToken ct = default) { Saves++; return Task.FromResult(1); }
}

/// <summary>Billiger, deterministischer Hasher: "hash:" + Passwort. Zählt die Aufrufe für den Timing-Angleich.</summary>
internal sealed class FakePasswordHasher : IPasswordHasher
{
    public int DummyVerifications { get; private set; }
    public int Verifications { get; private set; }

    public string Hash(string plainPassword) => "hash:" + plainPassword;
    public bool Verify(string plainPassword, string hash) { Verifications++; return hash == "hash:" + plainPassword; }
    public bool VerifyDummy(string plainPassword) { DummyVerifications++; return false; }
}

internal sealed class FakeJwtTokenService : IJwtTokenService
{
    public (string Token, DateTime ExpiresAt) IssueToken(User user) => ("token-" + user.GetSecurityStamp(), DateTime.UtcNow.AddHours(1));
}

internal sealed class RecordingAudit : ISecurityAudit
{
    public List<(SecurityEvent Event, string? Username, string? Detail)> Events { get; } = new();

    public void Record(SecurityEvent securityEvent, string? username, Guid? userId = null, string? detail = null) =>
        Events.Add((securityEvent, username, detail));
}

internal sealed class FakeCurrentUser : ICurrentUser
{
    public FakeCurrentUser(Guid? userId, string? username = null, bool isSystemContext = false)
    {
        UserId = userId;
        Username = username;
        IsSystemContext = isSystemContext;
    }

    public bool IsAuthenticated => UserId is not null;
    public Guid? UserId { get; }
    public string? Username { get; }
    public Role Roles => Role.None;
    public bool IsSystemContext { get; }
}
