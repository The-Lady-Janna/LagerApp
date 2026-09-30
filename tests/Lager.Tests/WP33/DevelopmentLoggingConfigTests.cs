using Microsoft.Extensions.Configuration;
using Serilog;

namespace Lager.Tests.WP33;

/// <summary>
/// Die Logging-Einstellungen der Entwicklung (<c>appsettings.Development.json</c>) stehen im Abschnitt <c>Serilog</c>: Serilog
/// ersetzt die Microsoft-Logging-Konfiguration, ein Abschnitt <c>Logging:LogLevel</c> wirkte dort nicht (er stammte aus der
/// Standardvorlage von ASP.NET Core). Der Test baut den Logger aus den echten Dateien (appsettings.json + Development) und prüft die
/// Wirkung, nicht nur den Text.
/// </summary>
public sealed class DevelopmentLoggingConfigTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lager-wp33-logs-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static string ApiFile(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lager.sln")))
            dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("Lager.sln oberhalb von " + AppContext.BaseDirectory + " nicht gefunden");
        return Path.Combine(root, "src", "Lager.Api", name);
    }

    private IConfiguration Config(bool withDevelopmentFile)
    {
        var builder = new ConfigurationBuilder().AddJsonFile(ApiFile("appsettings.json"), optional: false);
        if (withDevelopmentFile) builder.AddJsonFile(ApiFile("appsettings.Development.json"), optional: false);
        return builder.AddInMemoryCollection(new Dictionary<string, string?> { ["Logging:Directory"] = _dir }).Build();
    }

    /// <summary>Was bei dieser Konfiguration eines Ereignisses je Quelle in der Logdatei landet.</summary>
    private string Log(IConfiguration config)
    {
        using (var logger = LagerLogging.Configure(new LoggerConfiguration(), config).CreateLogger())
        {
            logger.ForContext("SourceContext", "Lager.Api.Beispiel").Information("Anwendungs-Info");
            logger.ForContext("SourceContext", "Microsoft.AspNetCore.Hosting.Diagnostics").Information("AspNetCore-Info");
            logger.ForContext("SourceContext", "Microsoft.EntityFrameworkCore.Database.Command").Information("SQL-Info");
            logger.ForContext("SourceContext", "Microsoft.EntityFrameworkCore.Database.Command").Warning("SQL-Warnung");
        }
        return string.Concat(Directory.GetFiles(_dir, "lager-*.log").Select(File.ReadAllText));
    }

    [Fact]
    public void The_development_file_has_no_ineffective_logging_section_but_a_serilog_minimum_level()
    {
        var development = new ConfigurationBuilder().AddJsonFile(ApiFile("appsettings.Development.json"), optional: false).Build();

        Assert.Empty(development.GetSection("Logging:LogLevel").GetChildren());
        Assert.Equal("Information", development["Serilog:MinimumLevel:Default"]);
        Assert.Equal("Warning", development["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore.Database.Command"]);
    }

    [Fact]
    public void The_development_minimum_level_takes_effect_and_production_stays_quieter()
    {
        var production = Log(Config(withDevelopmentFile: false));
        foreach (var file in Directory.GetFiles(_dir, "lager-*.log")) File.Delete(file);
        var development = Log(Config(withDevelopmentFile: true));

        // Ohne die Entwicklungsdatei bleibt das Framework still (Vorgabe im Code: Warning) ...
        Assert.Contains("Anwendungs-Info", production);
        Assert.DoesNotContain("AspNetCore-Info", production);
        // ... mit ihr zeigt die Entwicklung die Information von ASP.NET Core, weiter ohne SQL-Kommandos unterhalb von Warning.
        Assert.Contains("Anwendungs-Info", development);
        Assert.Contains("AspNetCore-Info", development);
        Assert.DoesNotContain("SQL-Info", development);
        Assert.Contains("SQL-Warnung", development);
    }
}
