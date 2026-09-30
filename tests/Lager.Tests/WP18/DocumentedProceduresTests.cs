using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Articles;
using Lager.Contracts.Auth;
using Lager.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;

namespace Lager.Tests.WP18;

/// <summary>
/// Verfahren, die die Doku beschreibt und die deshalb wirklich funktionieren müssen:
///  - docs/TROUBLESHOOTING.md "Passwort verloren oder kein Admin mehr": Tabelle Users leeren, neu starten, neuer Admin,
///    alle übrigen Daten bleiben;
///  - docs/CONFIGURATION.md und TROUBLESHOOTING.md "AllowedHosts": ein Host-Header außerhalb der Liste bekommt 400.
/// </summary>
public class DocumentedProceduresTests
{
    private const string FirstAdminPassword = "Erster-Start-Pw-2025!";
    private const string ChangedAdminPassword = "Danach-Geaendert-2025!";
    private const string RecoveryPassword = "Notfall-Neustart-2025!";

    /// <summary>API auf einer festen SQLite-Datei: mehrere Instanzen nacheinander sehen dieselben Daten (ein "Neustart").</summary>
    private sealed class RestartableApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _dbPath;
        private readonly string _bootstrapPassword;
        private readonly string? _allowedHosts;

        public RestartableApiFactory(string dbPath, string bootstrapPassword, string? allowedHosts = null)
        {
            _dbPath = dbPath;
            _bootstrapPassword = bootstrapPassword;
            _allowedHosts = allowedHosts;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath}");
            builder.UseSetting("Database:Seed", "false");
            builder.UseSetting("Jwt:SigningKey", LagerApiFactory.SigningKey);
            builder.UseSetting("Auth:BootstrapAdminUsername", "admin");
            builder.UseSetting("Auth:BootstrapAdminPassword", _bootstrapPassword);
            if (_allowedHosts is not null) builder.UseSetting("AllowedHosts", _allowedHosts);
        }
    }

    private static string NewDbPath() => Path.Combine(Path.GetTempPath(), $"lager-wp18-{Guid.NewGuid():N}.db");

    private static void Cleanup(string dbPath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            try { File.Delete(dbPath + suffix); } catch (IOException) { /* Temp-Datei */ }
        }
    }

    private static async Task<HttpClient> LoginAsync(HttpClient client, string password)
    {
        var login = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", password));
        login.EnsureSuccessStatusCode();
        var response = (await login.Content.ReadFromJsonAsync<LoginResponse>())!;
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", response.Token);
        return client;
    }

    [Fact]
    public async Task Clearing_the_users_table_lets_the_next_start_create_a_new_bootstrap_admin_and_keeps_all_other_data()
    {
        var dbPath = NewDbPath();
        try
        {
            // Erster Betrieb: Admin meldet sich an, ändert das Startpasswort (Pflicht) und legt einen Artikel an.
            string sku;
            using (var first = new RestartableApiFactory(dbPath, FirstAdminPassword))
            {
                var client = await LoginAsync(first.CreateClient(), FirstAdminPassword);
                var change = await client.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest(FirstAdminPassword, ChangedAdminPassword));
                change.EnsureSuccessStatusCode();
                client = await LoginAsync(first.CreateClient(), ChangedAdminPassword);

                sku = "WP18-" + Guid.NewGuid().ToString("N")[..8];
                var article = await client.PostAsJsonAsync("/api/articles", new CreateArticleRequest(
                    sku, "Bleibt erhalten", null, new DimensionsDto(100, 100, 100), 100, new StackingInfoDto(false, "Z", 0, null)));
                Assert.Equal(HttpStatusCode.Created, article.StatusCode);
            }

            // Der Admin ist ausgesperrt (Passwort verloren): Anwendung gestoppt, Users leeren - genau wie in der Doku.
            SqliteConnection.ClearAllPools();
            await using (var connection = new SqliteConnection($"Data Source={dbPath}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM Users;";
                Assert.Equal(1, await command.ExecuteNonQueryAsync());
            }
            SqliteConnection.ClearAllPools();

            // Neustart mit gesetztem Startpasswort: der neue Admin funktioniert, das alte Passwort nicht mehr,
            // der Pflichtwechsel ist wieder aktiv, und der Artikel ist noch da.
            using var second = new RestartableApiFactory(dbPath, RecoveryPassword);
            var anonymous = second.CreateClient();
            Assert.Equal(HttpStatusCode.Unauthorized,
                (await anonymous.PostAsJsonAsync("/api/auth/login", new LoginRequest("admin", ChangedAdminPassword))).StatusCode);

            var recovered = await LoginAsync(second.CreateClient(), RecoveryPassword);
            var me = await recovered.GetFromJsonAsync<UserDto>("/api/auth/me");
            Assert.True(me!.MustChangePassword);
            Assert.Contains("Admin", me.Roles);
            Assert.Equal(HttpStatusCode.Forbidden, (await recovered.GetAsync("/api/articles")).StatusCode); // Pflichtwechsel sperrt alles andere

            var change2 = await recovered.PostAsJsonAsync("/api/auth/change-password", new ChangePasswordRequest(RecoveryPassword, ChangedAdminPassword));
            change2.EnsureSuccessStatusCode();
            var ready = await LoginAsync(second.CreateClient(), ChangedAdminPassword);

            var articles = await ready.GetFromJsonAsync<List<ArticleDto>>("/api/articles");
            Assert.Contains(articles!, a => a.Sku == sku);
            var users = await ready.GetFromJsonAsync<List<UserDto>>("/api/users");
            Assert.Single(users!);
        }
        finally
        {
            Cleanup(dbPath);
        }
    }

    [Fact]
    public async Task A_host_header_outside_AllowedHosts_gets_400_and_the_configured_host_is_served()
    {
        var dbPath = NewDbPath();
        try
        {
            // Standard aus appsettings.json: nur localhost, 127.0.0.1 und [::1].
            using (var standard = new RestartableApiFactory(dbPath, FirstAdminPassword))
            {
                Assert.Equal(HttpStatusCode.BadRequest, (await WithHost(standard.CreateClient(), "lager.example.com").GetAsync("/api/auth/me")).StatusCode);
                Assert.Equal(HttpStatusCode.Unauthorized, (await WithHost(standard.CreateClient(), "localhost").GetAsync("/api/auth/me")).StatusCode);
            }

            // Mit dem eigenen Hostnamen in AllowedHosts (so beschreibt es die Doku) geht genau dieser durch.
            using var configured = new RestartableApiFactory(dbPath, FirstAdminPassword, allowedHosts: "lager.example.com");
            Assert.Equal(HttpStatusCode.Unauthorized, (await WithHost(configured.CreateClient(), "lager.example.com").GetAsync("/api/auth/me")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await WithHost(configured.CreateClient(), "other.example.com").GetAsync("/api/auth/me")).StatusCode);
        }
        finally
        {
            Cleanup(dbPath);
        }

        static HttpClient WithHost(HttpClient client, string host)
        {
            client.DefaultRequestHeaders.Host = host;
            return client;
        }
    }
}
