using System.Text.RegularExpressions;
using Lager.Tests.WP18;

namespace Lager.Tests.WP34;

/// <summary>
/// WP34: die <c>docker-compose.yml</c> reicht die Einstellungen durch, die die Doku und die <c>.env.example</c> nennen
/// (<c>Demo__Enabled</c>, <c>Backup__Schedule</c>, <c>Backup__RetentionCount</c>), und die Vorlage empfiehlt für den Demo-Modus den heutigen
/// Schalter statt des veralteten <c>Database__Seed</c>. Reine Dateitests, kein Docker nötig (<c>docker compose config</c> prüft die
/// Syntax und die aufgelösten Werte und wird beim Abnehmen von Hand ausgeführt).
/// </summary>
public class ComposePassThroughTests
{
    private static string Compose => DocsRepo.Read("docker-compose.yml");

    private static string EnvExample => DocsRepo.Read(".env.example");

    /// <summary>Durchgereichte Einstellung: ein Schlüssel ohne Wert (Compose übernimmt ihn aus der <c>.env</c> oder der Shell, sonst bleibt er ungesetzt).</summary>
    private static bool PassesThrough(string compose, string setting) =>
        Regex.IsMatch(compose, $@"(?m)^[ \t]+{Regex.Escape(setting)}:[ \t]*\r?$");

    [Theory]
    [InlineData("Demo__Enabled")]
    [InlineData("Backup__Schedule")]
    [InlineData("Backup__RetentionCount")]
    public void The_compose_file_passes_the_demo_and_backup_settings_through_with_an_empty_default(string setting)
    {
        // Muster der bestehenden Einträge (Auth__BootstrapAdminPassword, Backup__AllowRestore ...): Schlüssel ohne Wert. Ein Wert in der
        // Datei würde die Standards der Anwendung überschreiben (Zeitplan leer = aus, Aufbewahrung 14) und bei Secrets in die Datei gelangen.
        Assert.True(PassesThrough(Compose, setting), $"docker-compose.yml reicht {setting} nicht als Schlüssel ohne Wert durch");
        Assert.DoesNotMatch($@"(?m)^[ \t]+{Regex.Escape(setting)}:[ \t]*[^\s#]", Compose);
    }

    [Fact]
    public void The_demo_switch_and_its_production_release_are_both_passed_through_because_the_image_runs_as_Production()
    {
        Assert.True(PassesThrough(Compose, "Demo__Enabled"));
        Assert.True(PassesThrough(Compose, "Demo__AllowInProduction"));

        // Die Doku nennt beide für Docker; eine Zusatzdatei ist nicht mehr nötig.
        var gettingStarted = DocsRepo.Read("docs/GETTING_STARTED.md");
        var demo = DocsRepo.Read("docs/features/demo-modus.md");
        foreach (var text in new[] { gettingStarted, demo, EnvExample })
        {
            Assert.Contains("Demo__Enabled=true", text);
            Assert.Contains("Demo__AllowInProduction=true", text);
        }
    }

    [Fact]
    public void The_env_example_documents_the_demo_switch_and_the_backup_schedule_and_no_longer_recommends_the_legacy_seed()
    {
        var active = EnvExample.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        Assert.Empty(active); // alles auskommentiert: die Vorlage setzt nichts

        foreach (var setting in new[] { "#Demo__Enabled=true", "#Demo__AllowInProduction=true", "#Backup__Schedule=02:00", "#Backup__RetentionCount=14" })
            Assert.Contains(setting, EnvExample);

        // Der Abschnitt Demo-Daten nennt den heutigen Schalter und die Referenz, nicht mehr Database__Seed.
        Assert.DoesNotContain("Database__Seed", EnvExample);
        Assert.Contains("docs/features/demo-modus.md", EnvExample);
        Assert.Contains("docker compose logs lager", EnvExample);
    }

    [Fact]
    public void Every_application_setting_the_env_example_offers_is_passed_through_by_the_compose_file()
    {
        // Eine Einstellung der Vorlage (Schlüssel mit "__", auskommentiert), die die Compose-Datei nicht durchreicht, bliebe wirkungslos:
        // "Eine Einstellung in der .env wirkt nicht" (TROUBLESHOOTING.md). Die LAGER_*-Variablen und AllowedHosts sind Compose-Variablen
        // mit eigenem Eintrag und zählen hier nicht.
        var offered = Regex.Matches(EnvExample, @"(?m)^#([A-Z][A-Za-z]+(?:__[A-Za-z0-9]+)+)=")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .ToList();
        Assert.True(offered.Count >= 8, $"Nur {offered.Count} Einstellungen in der .env.example gefunden - die Auswertung greift nicht");

        var missing = offered.Where(s => !Regex.IsMatch(Compose, $@"(?m)^[ \t]+{Regex.Escape(s)}:")).ToList();
        Assert.True(missing.Count == 0, "docker-compose.yml reicht nicht durch: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_compose_documentation_lists_the_settings_the_compose_file_passes_through()
    {
        // docs/CONFIGURATION.md nennt die durchgereichten Einstellungen; jede davon steht in der Compose-Datei (und umgekehrt keine ohne Doku).
        var configuration = DocsRepo.Read("docs/CONFIGURATION.md");
        var section = configuration[configuration.IndexOf("**Was die `docker-compose.yml` an den Container durchreicht.**", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("**Nur für Compose**", StringComparison.Ordinal)];

        var passedThrough = Regex.Matches(Compose, @"(?m)^\s+([A-Z][A-Za-z]+(?:__[A-Za-z0-9]+)*):")
            .Select(m => m.Groups[1].Value)
            .Where(s => s != "LAGER_DOMAIN")
            .Distinct()
            .ToList();
        Assert.True(passedThrough.Count >= 12, $"Nur {passedThrough.Count} Einstellungen in docker-compose.yml gefunden");

        var undocumented = passedThrough.Where(s => !section.Contains($"`{s}`")).ToList();
        Assert.True(undocumented.Count == 0, "In docs/CONFIGURATION.md (Abschnitt Docker) fehlen: " + string.Join(", ", undocumented));
    }
}
