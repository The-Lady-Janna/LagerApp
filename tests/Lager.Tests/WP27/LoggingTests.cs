using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Lager.Tests.WP27;

/// <summary>
/// Das Log: Health-Probes (Compose-Healthcheck alle 30 Sekunden) füllen das Request-Log nicht mehr, Fehler bleiben sichtbar; die
/// einmaligen Demo-Zugangsdaten gelangen nie in die Logdatei.
/// </summary>
public sealed class LoggingTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lager-wp27-logs-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static HttpContext Context(string path, int status)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Response.StatusCode = status;
        return context;
    }

    // ---- Pegel je Anfrage --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("/health/live", 200, LogEventLevel.Debug)]
    [InlineData("/health/ready", 200, LogEventLevel.Debug)]
    [InlineData("/HEALTH/Live", 204, LogEventLevel.Debug)]
    [InlineData("/health/ready", 503, LogEventLevel.Warning)]
    [InlineData("/health/live", 500, LogEventLevel.Warning)]
    [InlineData("/api/orders", 200, LogEventLevel.Information)]
    [InlineData("/api/orders", 404, LogEventLevel.Information)]
    [InlineData("/api/orders", 500, LogEventLevel.Error)]
    [InlineData("/api/health", 200, LogEventLevel.Information)]     // kein Health-Endpunkt
    [InlineData("/healthy-food", 200, LogEventLevel.Information)]    // nur ganze Pfadsegmente zählen
    [InlineData("/", 200, LogEventLevel.Information)]
    public void Der_Pegel_der_Request_Zeile_daempft_nur_gesunde_Health_Probes(string path, int status, LogEventLevel expected)
    {
        Assert.Equal(expected, LagerLogging.GetRequestLogLevel(Context(path, status), 1.0, null));
    }

    [Fact]
    public void Eine_Ausnahme_bleibt_auch_bei_einer_Probe_sichtbar()
    {
        var boom = new InvalidOperationException("Datenbank weg");

        Assert.Equal(LogEventLevel.Warning, LagerLogging.GetRequestLogLevel(Context("/health/ready", 200), 1.0, boom));
        Assert.Equal(LogEventLevel.Error, LagerLogging.GetRequestLogLevel(Context("/api/stock", 200), 1.0, boom));
    }

    private sealed class ListSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) { lock (Events) Events.Add(logEvent); }
    }

    [Fact]
    public async Task Im_laufenden_Host_schreiben_gesunde_Probes_keine_Zeile_Fehler_und_andere_Anfragen_schon()
    {
        var sink = new ListSink();
        // Wie in Program.cs: Mindest-Level Information, das Framework selbst (Hosting-Zeilen "Request starting") gedämpft.
        var logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .WriteTo.Sink(sink).CreateLogger();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Host.UseSerilog(logger);
        await using var app = builder.Build();
        // Die Middleware schreibt sonst in das statische Log.Logger (in Program.cs ist das der konfigurierte Logger).
        app.UseSerilogRequestLogging(options =>
        {
            options.GetLevel = LagerLogging.GetRequestLogLevel;
            options.Logger = logger;
        });
        app.MapGet("/health/live", () => Results.Ok());
        app.MapGet("/health/ready", () => Results.StatusCode(503));
        app.MapGet("/api/ping", () => Results.Ok());
        app.MapGet("/api/boom", () => Results.StatusCode(500));
        await app.StartAsync();
        using var client = app.GetTestClient();

        for (var i = 0; i < 5; i++) await client.GetAsync("/health/live");   // 5 gesunde Probes
        await client.GetAsync("/health/ready");                              // eine fehlgeschlagene
        await client.GetAsync("/api/ping");
        await client.GetAsync("/api/boom");
        await app.StopAsync();

        string PathOf(LogEvent e) => e.Properties.TryGetValue("RequestPath", out var p) ? p.ToString().Trim('"') : "";
        var requests = sink.Events.Where(e => e.Properties.ContainsKey("RequestPath")
                                              && e.Properties.TryGetValue("SourceContext", out var source) && source.ToString().Contains("RequestLoggingMiddleware")).ToList();
        Assert.DoesNotContain(requests, e => PathOf(e) == "/health/live");
        Assert.Contains(requests, e => PathOf(e) == "/health/ready" && e.Level == LogEventLevel.Warning);
        Assert.Contains(requests, e => PathOf(e) == "/api/ping" && e.Level == LogEventLevel.Information);
        Assert.Contains(requests, e => PathOf(e) == "/api/boom" && e.Level == LogEventLevel.Error);
        Assert.Equal(3, requests.Count);
    }

    [Fact]
    public void Program_setzt_den_Pegel_fuer_das_Request_Logging()
    {
        var program = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "Lager.Api", "Program.cs"));

        Assert.Contains("app.UseSerilogRequestLogging(options => options.GetLevel = LagerLogging.GetRequestLogLevel);", program);
    }

    // ---- Demo-Zugangsdaten nie in die Logdatei ------------------------------------------------------------------------------

    [Fact]
    public void Eintraege_mit_der_Eigenschaft_DemoCredentials_landen_nicht_in_der_Logdatei()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:Directory"] = _dir }).Build();

        using (var logger = LagerLogging.Configure(new LoggerConfiguration(), config).CreateLogger())
        {
            logger.Warning("Demo-Zugangsdaten: {DemoCredentials}", "picker  Geheim-Passwort-4711");
            logger.Warning("Anderer Eintrag {Wert}", 5);
        }

        var text = string.Concat(Directory.GetFiles(_dir, "lager-*.log").Select(File.ReadAllText));
        Assert.Contains("Anderer Eintrag 5", text);
        Assert.DoesNotContain("Geheim-Passwort-4711", text);
        Assert.DoesNotContain("Demo-Zugangsdaten", text);
        Assert.Equal("DemoCredentials", LagerLogging.DemoCredentialsProperty);
    }

    [Fact]
    public void Der_Seeder_loggt_die_Zugangsdaten_unter_dem_Namen_der_gefilterten_Eigenschaft()
    {
        var seeder = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "Lager.Api", "Seeding", "DemoDataSeeder.cs"));

        Assert.Contains("{" + LagerLogging.DemoCredentialsProperty + "}", seeder);
    }
}
