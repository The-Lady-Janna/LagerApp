using Lager.Api.Seeding;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Lager.Tests.WP27;

/// <summary>
/// Die Konfiguration des Demo-Modus: <c>Demo:Enabled</c> (Standard aus), die Kurzform <c>LAGER_DEMO</c> und die Sperre in
/// Production ohne <c>Demo:AllowInProduction</c>. Reine Logik ohne Host: die Umgebungsvariable wird über eine Funktion
/// gelesen, damit kein Test den Prozess verändert (parallel laufende Test-Hosts würden sonst mit Demo-Daten starten).
/// </summary>
public class DemoSettingsTests
{
    private sealed class FakeEnvironment : IHostEnvironment
    {
        public FakeEnvironment(string name) => EnvironmentName = name;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "Lager.Api";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static ConfigurationManager Config(params (string Key, string? Value)[] values)
    {
        var config = new ConfigurationManager();
        config.AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)));
        return config;
    }

    private static Func<string, string?> Env(string? lagerDemo) =>
        name => name == "LAGER_DEMO" ? lagerDemo : null;

    // ---- Standard ------------------------------------------------------------------------------------------------------

    [Fact]
    public void Der_Demo_Modus_ist_ohne_Angabe_aus_in_jeder_Umgebung()
    {
        foreach (var environment in new[] { "Development", "Testing", "Staging", "Production" })
        {
            var settings = DemoSettings.From(Config(), new FakeEnvironment(environment));

            Assert.False(settings.Enabled);
            Assert.False(settings.LegacySeed);
        }
    }

    [Fact]
    public void Database_Seed_bleibt_als_veralteter_Schalter_erkennbar_und_schaltet_den_Demo_Modus_nicht_ein()
    {
        var settings = DemoSettings.From(Config(("Database:Seed", "true")), new FakeEnvironment("Development"));

        Assert.True(settings.LegacySeed);
        Assert.False(settings.Enabled);
    }

    // ---- LAGER_DEMO ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("TRUE")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData(" 1 ")]
    public void LAGER_DEMO_schaltet_Demo_Enabled_ein(string value)
    {
        var config = Config(("Demo:Enabled", "false"));

        var applied = DemoSettings.ApplyEnvironmentShortcut(config, Env(value));

        Assert.True(applied);
        Assert.True(config.GetValue<bool>("Demo:Enabled"));
        Assert.True(DemoSettings.From(config, new FakeEnvironment("Development")).Enabled);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("no")]
    [InlineData("off")]
    public void LAGER_DEMO_aus_schlaegt_die_Konfigurationsdatei(string value)
    {
        var config = Config(("Demo:Enabled", "true"));

        var applied = DemoSettings.ApplyEnvironmentShortcut(config, Env(value));

        Assert.False(applied);
        Assert.False(config.GetValue<bool>("Demo:Enabled"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("vielleicht")]
    public void Fehlendes_oder_unbekanntes_LAGER_DEMO_aendert_nichts(string? value)
    {
        var config = Config(("Demo:Enabled", "true"));

        var applied = DemoSettings.ApplyEnvironmentShortcut(config, Env(value));

        Assert.Null(applied);
        Assert.True(config.GetValue<bool>("Demo:Enabled"));
    }

    [Fact]
    public void LAGER_DEMO_liest_genau_die_Variable_LAGER_DEMO()
    {
        var asked = new List<string>();
        var config = Config();

        DemoSettings.ApplyEnvironmentShortcut(config, name => { asked.Add(name); return null; });

        Assert.Equal(new[] { "LAGER_DEMO" }, asked);
    }

    // ---- Production ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Production_mit_Demo_Enabled_bricht_mit_klarer_Meldung_ab()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            DemoSettings.From(Config(("Demo:Enabled", "true")), new FakeEnvironment("Production")));

        Assert.Contains("Demo:Enabled", ex.Message);
        Assert.Contains("Production", ex.Message);
        Assert.Contains("Demo:AllowInProduction", ex.Message);
    }

    [Fact]
    public void Auch_LAGER_DEMO_1_ist_in_Production_gesperrt()
    {
        var config = Config();
        DemoSettings.ApplyEnvironmentShortcut(config, Env("1"));

        Assert.Throws<InvalidOperationException>(() => DemoSettings.From(config, new FakeEnvironment("Production")));
    }

    [Fact]
    public void Production_mit_ausdruecklicher_Freigabe_darf_starten()
    {
        var settings = DemoSettings.From(
            Config(("Demo:Enabled", "true"), ("Demo:AllowInProduction", "true")), new FakeEnvironment("Production"));

        Assert.True(settings.Enabled);
    }

    [Fact]
    public void Die_Freigabe_allein_schaltet_nichts_ein_und_Production_ohne_Demo_startet_normal()
    {
        Assert.False(DemoSettings.From(Config(("Demo:AllowInProduction", "true")), new FakeEnvironment("Production")).Enabled);
        Assert.False(DemoSettings.From(Config(), new FakeEnvironment("Production")).Enabled);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    public void Ausserhalb_von_Production_braucht_der_Demo_Modus_keine_Freigabe(string environment)
    {
        Assert.True(DemoSettings.From(Config(("Demo:Enabled", "true")), new FakeEnvironment(environment)).Enabled);
    }

    // ---- Standardwerte in der Datei ------------------------------------------------------------------------------------

    [Fact]
    public void Die_appsettings_json_liefert_den_Demo_Modus_und_den_alten_Schalter_standardmaessig_aus()
    {
        var file = Path.Combine(RepoRoot.Path, "src", "Lager.Api", "appsettings.json");
        var config = new ConfigurationBuilder().AddJsonFile(file, optional: false).Build();

        Assert.False(config.GetValue<bool>("Demo:Enabled"));
        Assert.False(config.GetValue<bool>("Demo:AllowInProduction"));
        Assert.False(config.GetValue<bool>("Database:Seed"));
        Assert.NotNull(config["Demo:Enabled"]);   // der Schlüssel steht ausdrücklich in der Datei, nicht nur als Code-Standard
    }
}

/// <summary>Findet das Repository-Root (Lager.sln) für Tests, die Quelldateien und Konfiguration lesen.</summary>
internal static class RepoRoot
{
    public static readonly string Path = Find();

    private static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(System.IO.Path.Combine(dir.FullName, "Lager.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository-Root (Lager.sln) nicht gefunden.");
    }
}
