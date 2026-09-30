using System.Net;
using Lager.Api.Health;
using Lager.Infrastructure;
using Lager.Tests.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Lager.Tests.WP16;

/// <summary>
/// /health/live und /health/ready: anonym erreichbar (die API ist sonst geschlossen), minimale Antwort, Bereitschaft
/// hängt an der Datenbank.
/// </summary>
public class HealthEndpointTests
{
    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Health_endpoints_answer_200_without_a_token_and_reveal_only_the_status(string path)
    {
        using var factory = new LagerApiFactory();
        var client = factory.CreateClient(); // kein Token

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("""{"status":"Healthy"}""", await response.Content.ReadAsStringAsync());
        // Probes dürfen nie aus einem Cache kommen, sonst meldet ein Proxy "gesund" für einen toten Dienst.
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Ready_is_503_when_the_database_file_is_gone_while_live_stays_200()
    {
        using var factory = new LagerApiFactory();
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);

        // Die Datenbank verschwindet im Betrieb (Volume weg, Datei gelöscht): kein DI-Trick, sondern der echte Fehlerfall.
        var databasePath = factory.Services.GetRequiredService<DatabaseSettings>().SqliteFilePath!;
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
            File.Delete(databasePath + suffix);

        var ready = await client.GetAsync("/health/ready");
        var live = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("""{"status":"Unhealthy"}""", await ready.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, live.StatusCode); // Liveness prüft nichts: ein DB-Problem darf den Container nicht neu starten lassen
    }

    [Fact]
    public async Task A_failing_check_never_leaks_its_error_into_the_response()
    {
        using var factory = new LagerApiFactory();
        using var failing = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.Configure<HealthCheckServiceOptions>(options =>
            {
                options.Registrations.Clear();
                options.Registrations.Add(new HealthCheckRegistration(
                    "database", _ => new ThrowingCheck(), HealthStatus.Unhealthy, new[] { HealthEndpoints.ReadyTag }));
            })));
        var client = failing.CreateClient();

        var ready = await client.GetAsync("/health/ready");
        var body = await ready.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal("""{"status":"Unhealthy"}""", body);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    private sealed class ThrowingCheck : IHealthCheck
    {
        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Server=intern.example;Password=geheim");
    }
}
