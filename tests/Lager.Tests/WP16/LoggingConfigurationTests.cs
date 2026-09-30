using Microsoft.Extensions.Configuration;
using Serilog;

namespace Lager.Tests.WP16;

/// <summary>
/// Serilog wird aus der Konfiguration gebaut: Log-Verzeichnis, Aufbewahrung, Mindest-Level und Overrides lassen sich per
/// Umgebungsvariable/appsettings ändern (Docker: Log-Verzeichnis auf das Daten-Volume). Jeder Test baut seinen eigenen Logger -
/// der statische <c>Log.Logger</c> ist wegen parallel laufender Test-Hosts nicht verlässlich lesbar.
/// </summary>
public sealed class LoggingConfigurationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("lager-wp16-logs-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    /// <summary>Loggt über einen frisch gebauten Logger und liefert den Inhalt der Logdatei im Verzeichnis <c>Logging:Directory</c>.</summary>
    private string LogAndRead(IConfiguration config, Action<ILogger> write)
    {
        using (var logger = LagerLogging.Configure(new LoggerConfiguration(), config).CreateLogger())
            write(logger);

        var files = Directory.GetFiles(_dir, "lager-*.log");
        return files.Length == 0 ? "" : string.Concat(files.Select(File.ReadAllText));
    }

    [Fact]
    public void The_log_file_lands_in_the_configured_directory_with_the_daily_name()
    {
        var target = Path.Combine(_dir, "unterordner", "logs"); // wird angelegt (Docker: /data/logs)
        var config = Config(("Logging:Directory", target));

        using (var logger = LagerLogging.Configure(new LoggerConfiguration(), config).CreateLogger())
            logger.Information("Start {Wert}", 42);

        var file = Assert.Single(Directory.GetFiles(target));
        Assert.Matches(@"^lager-\d{8}\.log$", Path.GetFileName(file));
        Assert.Contains("Start 42", File.ReadAllText(file));
        Assert.Equal(target, LagerLogging.ResolveDirectory(config));
    }

    [Fact]
    public void Without_configuration_the_directory_is_logs_below_the_working_directory_and_a_relative_one_is_made_absolute()
    {
        Assert.Equal(Path.GetFullPath("logs"), LagerLogging.ResolveDirectory(Config()));
        Assert.Equal(Path.GetFullPath("logs"), LagerLogging.ResolveDirectory(Config(("Logging:Directory", "  "))));
        Assert.Equal(Path.GetFullPath(Path.Combine("var", "log", "lager")), LagerLogging.ResolveDirectory(Config(("Logging:Directory", "var/log/lager"))));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("viele")]
    public void An_invalid_retention_stops_the_start_instead_of_silently_falling_back(string value)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            LagerLogging.Configure(new LoggerConfiguration(), Config(("Logging:RetainedFileCount", value))));

        Assert.Contains("Logging:RetainedFileCount", ex.Message);
    }

    [Fact]
    public void Defaults_in_code_keep_the_framework_quiet_but_show_application_information()
    {
        var log = LogAndRead(Config(("Logging:Directory", _dir)), logger =>
        {
            logger.ForContext("SourceContext", "Lager.Api.Beispiel").Information("Anwendungs-Info");
            logger.ForContext("SourceContext", "Microsoft.AspNetCore.Hosting.Diagnostics").Information("Framework-Info");
            logger.ForContext("SourceContext", "Microsoft.AspNetCore.Hosting.Diagnostics").Warning("Framework-Warnung");
            logger.ForContext("SourceContext", "Microsoft.EntityFrameworkCore.Database.Command").Information("SQL-Info");
            // Der GlobalExceptionHandler schreibt den Fehler selbst; die Middleware von ASP.NET Core bleibt still.
            logger.ForContext("SourceContext", "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware").Error("Doppelter Fehler");
        });

        Assert.Contains("Anwendungs-Info", log);
        Assert.Contains("Framework-Warnung", log);
        Assert.DoesNotContain("Framework-Info", log);
        Assert.DoesNotContain("SQL-Info", log);
        Assert.DoesNotContain("Doppelter Fehler", log);
    }

    [Fact]
    public void The_serilog_section_overrides_the_defaults_in_code()
    {
        var config = Config(
            ("Logging:Directory", _dir),
            ("Serilog:MinimumLevel:Default", "Warning"),
            ("Serilog:MinimumLevel:Override:Microsoft.AspNetCore", "Debug"));

        var log = LogAndRead(config, logger =>
        {
            logger.ForContext("SourceContext", "Lager.Api.Beispiel").Information("Anwendungs-Info");
            logger.ForContext("SourceContext", "Lager.Api.Beispiel").Warning("Anwendungs-Warnung");
            logger.ForContext("SourceContext", "Microsoft.AspNetCore.Hosting.Diagnostics").Debug("Framework-Debug");
        });

        Assert.DoesNotContain("Anwendungs-Info", log); // Default von Information auf Warning gehoben
        Assert.Contains("Anwendungs-Warnung", log);
        Assert.Contains("Framework-Debug", log);       // Override (im Code Warning) per Konfiguration auf Debug gesenkt
    }
}
