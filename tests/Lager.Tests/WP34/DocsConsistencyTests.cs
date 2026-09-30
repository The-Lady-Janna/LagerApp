using System.Text.Json;
using System.Text.RegularExpressions;
using Lager.Tests.WP18;

namespace Lager.Tests.WP34;

/// <summary>
/// Endabnahme-Nacharbeiten (WP34): die Doku und die Konfigurationsdateien stimmen nach WP29 (Mehrsprachigkeit), WP30 (Swagger/OpenAPI)
/// und WP31 (Doku) miteinander überein. Geprüft wird mit klaren Mustern, was früher falsch war und nicht wiederkommen darf
/// ("keine Mehrsprachigkeit", "Swagger nur in Development", ein Dockerfile, das erst entsteht ...), und dass die neuen Aussagen stehen.
/// </summary>
public class DocsConsistencyTests
{
    /// <summary>Alle Markdown-Seiten, die Aussagen über Sprache, Swagger oder Docker machen können.</summary>
    private static IEnumerable<string> AllPages()
    {
        var top = new[]
        {
            "README.md", "README.en.md", "TODO.md", "CHANGELOG.md", "CONTRIBUTING.md", "SECURITY.md",
            ".github/PULL_REQUEST_TEMPLATE.md", "frontend/lager-ui/README.md", "frontend/lager-ui/src/locales/README.md",
        };
        var docs = Directory.EnumerateFiles(DocsRepo.FullPath("docs"), "*.md", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(DocsRepo.Root, f).Replace('\\', '/'));
        return top.Concat(docs).Where(p => File.Exists(DocsRepo.FullPath(p))).Distinct().OrderBy(p => p, StringComparer.Ordinal);
    }

    // ---------- Mehrsprachigkeit (WP29) ----------

    /// <summary>Frühere Aussagen, die seit WP29 (Deutsch und Englisch, react-i18next) falsch sind.</summary>
    private static readonly (string Pattern, string Grund)[] VeralteteSprachAussagen =
    {
        (@"keine\s+Mehrsprachigkeit", "die Oberfläche ist zweisprachig"),
        (@"nur\s+Deutsch", "die Oberfläche gibt es auch auf Englisch"),
        (@"user interface is German only|UI is German only|German only", "the user interface is available in English"),
        (@"Oberfläche,\s+Meldungen\s+und\s+Doku\s+sind\s+deutsch", "die Oberfläche ist zweisprachig"),
        (@"Alle\s+Texte\s+der\s+Oberfläche[^\n]{0,40}sind\s+deutsch", "die Texte stehen in den Sprachdateien de und en"),
        (@"Übersetzungsschicht\s+\(i18n\)\s+gibt\s+es\s+\*\*nicht\*\*", "es gibt react-i18next"),
        (@"die\s+Oberfläche\s+ist\s+deutsch\b", "die Oberfläche ist zweisprachig"),
        (@"heute\s+sind\s+Oberfläche,\s+Meldungen\s+und\s+Doku\s+deutsch", "i18n der Oberfläche ist erledigt"),
    };

    [Fact]
    public void No_page_claims_anymore_that_the_ui_is_German_only_or_that_i18n_is_missing()
    {
        var found = new List<string>();
        foreach (var page in AllPages())
        {
            var text = DocsRepo.Read(page);
            foreach (var (pattern, reason) in VeralteteSprachAussagen)
                if (Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase))
                    found.Add($"{page}: '{pattern}' ({reason})");
        }

        Assert.True(found.Count == 0, string.Join(Environment.NewLine, found));
    }

    [Fact]
    public void The_status_note_and_the_changelog_name_the_bilingual_ui_and_its_limits()
    {
        var readme = DocsRepo.Read("README.md");
        var readmeEn = DocsRepo.Read("README.en.md");
        var changelog = Release010(DocsRepo.Read("CHANGELOG.md"));

        Assert.Contains("Deutsch und Englisch", readme);
        Assert.Contains("German and English", readmeEn);
        Assert.Matches(@"Mehrsprachige Oberfläche \(Deutsch und Englisch\)", changelog);
        Assert.Contains("docs/features/i18n.md", changelog);

        // Das TODO führt den Ausbau (nicht mehr die Einführung) als offenen Punkt; die Einführung steht bei "Was steht".
        var todo = DocsRepo.Read("TODO.md");
        Assert.Matches(@"\[x\] \*\*Mehrsprachige Oberfläche \(DE/EN\)", todo);
        Assert.Matches(@"\[ \] \*\*i18n, Ausbau:\*\*", todo);
        Assert.DoesNotMatch(@"\[ \] \*\*i18n\*\* \(DE/EN\)", todo);
    }

    [Fact]
    public void The_i18n_page_names_the_limits_of_the_wp29_review_and_how_to_add_a_language()
    {
        var page = DocsRepo.Read("docs/features/i18n.md");

        // Grenzen (nur dokumentiert, nicht umgebaut)
        Assert.Contains("StatusPills", page);
        Assert.Contains("validation_failed", page);
        Assert.Contains("not_found", page);
        Assert.Contains("<Trans>", page);
        Assert.Contains("ErrorBoundary", page);
        Assert.Contains("Backend-Meldungen bleiben deutsch", page);

        // Nutzung, neue Sprache, Prüfskript
        Assert.Contains("lager.lang", page);
        Assert.Contains("npm run i18n:check", page);
        Assert.Contains("SUPPORTED_LANGUAGES", page);

        // Die genannte Zahl der Namespaces ist die der deutschen Sprachdateien.
        var namespaces = Directory.EnumerateFiles(DocsRepo.FullPath("frontend/lager-ui/src/locales/de"), "*.json").Count();
        Assert.Contains($"**{namespaces} Namespaces**", page);
        foreach (var file in Directory.EnumerateFiles(DocsRepo.FullPath("frontend/lager-ui/src/locales/de"), "*.json"))
            Assert.Contains($"`{Path.GetFileNameWithoutExtension(file)}`", page);
    }

    // ---------- Swagger/OpenAPI (WP30) ----------

    [Fact]
    public void No_page_says_anymore_that_swagger_runs_only_in_Development()
    {
        // "Swagger nur in Development", "läuft nur in der Umgebung `Development`", "nur mit ASPNETCORE_ENVIRONMENT=Development" ...
        var onlyDevelopment = new Regex(@"\b(nur|ausschließlich|only)\b\s+(in\s+)?(der\s+)?(Umgebung\s+)?(mit\s+)?`?(ASPNETCORE_ENVIRONMENT=)?Development`?", RegexOptions.IgnoreCase);
        var found = new List<string>();

        foreach (var page in AllPages())
        {
            var lines = DocsRepo.Read(page).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!line.Contains("swagger", StringComparison.OrdinalIgnoreCase)) continue;
                // Eine Zeile, die den Schalter nennt, sagt schon, dass Swagger auch anders läuft.
                if (line.Contains("Swagger:Enabled") || line.Contains("Swagger__Enabled")) continue;
                if (onlyDevelopment.IsMatch(line)) found.Add($"{page}:{i + 1}: {line.Trim()[..Math.Min(line.Trim().Length, 140)]}");
            }

            var text = DocsRepo.Read(page);
            if (text.Contains("Swagger im Entwicklungsmodus")) found.Add($"{page}: Überschrift 'Swagger im Entwicklungsmodus'");
            if (Regex.IsMatch(text, @"Swagger\*{0,2}\s+hat\s+keinen\s+eigenen\s+Konfigurationsschlüssel")) found.Add($"{page}: 'Swagger hat keinen eigenen Konfigurationsschlüssel'");
        }

        Assert.True(found.Count == 0, string.Join(Environment.NewLine, found));
    }

    [Fact]
    public void Every_page_that_describes_when_swagger_runs_names_the_switch_and_the_configuration_reference_lists_it()
    {
        // Die Konfigurationsreferenz hat den Schlüssel des Codes samt Umgebungsvariable in einer Tabellenzeile.
        var configuration = DocsRepo.Read("docs/CONFIGURATION.md");
        var row = DocsRepo.TableRows(configuration, l => l.StartsWith($"| `{LagerOpenApi.EnabledKey}`", StringComparison.Ordinal)).SingleOrDefault();
        Assert.NotNull(row);
        Assert.Contains("`Swagger__Enabled`", row![1]);
        Assert.Contains("Development", row[2]);

        // Die Seiten, die Swagger für die Umgebung beschreiben, nennen den Schalter (mit true und false).
        foreach (var page in new[] { "docs/API.md", "docs/GETTING_STARTED.md", "docs/TROUBLESHOOTING.md", "docs/features/openapi.md" })
        {
            var text = DocsRepo.Read(page);
            Assert.True(text.Contains("Swagger:Enabled=true") || text.Contains("Swagger__Enabled=true"), $"{page} nennt Swagger:Enabled=true nicht");
        }

        Assert.Contains("Swagger:Enabled=false", DocsRepo.Read("docs/features/openapi.md"));
        Assert.Contains("Swagger:Enabled=false", DocsRepo.Read("docs/API.md"));
    }

    [Fact]
    public void The_api_reference_lists_the_version_endpoint_and_no_longer_says_it_does_not_exist()
    {
        var api = DocsRepo.Read("docs/API.md");

        Assert.DoesNotMatch(@"Versionsendpunkt[^\n]{0,30}existiert nicht", api);
        Assert.Matches(@"(?m)^\| GET \| /api/version \| angemeldet \|", api);
        Assert.Matches(@"\[ \] \*\*Versionsanzeige:\*\*[^\n]*GET /api/version", DocsRepo.Read("TODO.md"));
    }

    // ---------- CI-Kommentare und Docker (WP16/WP17) ----------

    [Theory]
    [InlineData(".github/workflows/docker.yml")]
    [InlineData(".github/dependabot.yml")]
    public void The_ci_files_no_longer_claim_that_the_dockerfile_is_yet_to_come(string file)
    {
        var text = DocsRepo.Read(file);

        Assert.True(File.Exists(DocsRepo.FullPath("Dockerfile")), "das Dockerfile liegt im Repository-Root");
        Assert.DoesNotMatch(@"entsteht\s+(erst\s+)?mit\s+dem\s+Docker-Quickstart", text);
        Assert.DoesNotContain("(WP16)", text);
        Assert.DoesNotMatch(@"Dockerfile\s+entsteht", text);
    }

    [Fact]
    public void The_readmes_do_not_promise_a_single_command_docker_run_and_name_that_the_container_build_was_never_tested()
    {
        var readme = DocsRepo.Read("README.md");
        var readmeEn = DocsRepo.Read("README.en.md");

        Assert.DoesNotContain("Läuft mit einem Befehl in Docker", readme);
        Assert.DoesNotContain("Runs in Docker with a single command", readmeEn);

        // Kopfsatz: der Quickstart ist da, getestet ist er nicht.
        Assert.Matches(@"Docker-Quickstart vorhanden \(Dockerfile, Compose, MySQL-Variante, optional Caddy[^)]*\)", readme);
        Assert.Matches(@"Docker quick start is included \(Dockerfile, Compose, MySQL variant, optional Caddy[^)]*\)", readmeEn);

        // Projektstatus / bekannte Grenzen: der Build wurde nie praktisch gestartet und sollte vor dem ersten Release getestet werden.
        Assert.Contains("nie praktisch gebaut und gestartet", readme);
        Assert.Contains("vor dem ersten Release", readme);
        Assert.Contains("never been built and started in practice", readmeEn);
        Assert.Contains("before the first release", readmeEn);

        var changelog = Release010(DocsRepo.Read("CHANGELOG.md"));
        Assert.Contains("nie praktisch gebaut und gestartet", changelog);
        Assert.Matches(@"\[ \] \*\*Docker praktisch testen:\*\*", DocsRepo.Read("TODO.md"));
    }

    [Fact]
    public void No_page_tells_to_create_a_docker_compose_demo_file_anymore()
    {
        // Die docker-compose.yml reicht Demo__Enabled und Demo__AllowInProduction aus der .env durch: die Zusatzdatei ist überflüssig.
        var found = AllPages().Where(p => DocsRepo.Read(p).Contains("docker-compose.demo.yml")).ToList();

        Assert.True(found.Count == 0, "docker-compose.demo.yml wird noch genannt in: " + string.Join(", ", found));
        Assert.False(File.Exists(DocsRepo.FullPath("docker-compose.demo.yml")));
    }

    // ---------- CHANGELOG ----------

    [Fact]
    public void The_first_release_lists_i18n_the_swagger_improvements_and_the_fix_of_POST_shelf_bins_with_its_regression_test()
    {
        var release = Release010(DocsRepo.Read("CHANGELOG.md"));

        Assert.Contains("Mehrsprachige Oberfläche", release);
        Assert.Contains("react-i18next", release);

        Assert.Contains("Swagger/OpenAPI", release);
        Assert.Contains("Swagger:Enabled", release);
        Assert.Contains("Lager.Contracts", release);

        Assert.Contains("POST /api/warehouse/shelves/{id}/bins", release);
        Assert.Contains("409", release);
        Assert.Contains("tests/Lager.Tests/WP34/AddBinToShelfTests.cs", release);
        Assert.True(File.Exists(DocsRepo.FullPath("tests/Lager.Tests/WP34/AddBinToShelfTests.cs")));

        // Die Fehlerbehebung steht im Abschnitt "Behoben", nicht nur irgendwo.
        var fixedSection = release[release.IndexOf("### Behoben", StringComparison.Ordinal)..release.IndexOf("### Entfernt", StringComparison.Ordinal)];
        Assert.Contains("shelves/{id}/bins", fixedSection);
    }

    // ---------- Test-Hack in der Oberfläche (WP04/WP29) ----------

    [Fact]
    public void The_login_page_has_no_text_comment_that_only_exists_for_a_test_and_the_hint_text_lives_in_the_language_files()
    {
        var login = DocsRepo.Read("frontend/lager-ui/src/pages/LoginPage.tsx");

        Assert.DoesNotContain("Server-Konsole", login);
        Assert.Contains("t('auth:login.firstStart')", login);

        foreach (var (language, word) in new[] { ("de", "Server-Konsole"), ("en", "console") })
        {
            using var locale = JsonDocument.Parse(DocsRepo.Read($"frontend/lager-ui/src/locales/{language}/auth.json"));
            Assert.Contains(word, locale.RootElement.GetProperty("login").GetProperty("firstStart").GetString());
        }
    }

    [Fact]
    public void The_session_expired_notice_is_translated_when_it_is_needed_not_when_the_module_loads()
    {
        var watcher = DocsRepo.Read("frontend/lager-ui/src/lib/sessionWatcher.ts");

        // Eine Funktion übersetzt bei jedem Aufruf neu; der automatische Logout benutzt sie.
        Assert.Matches(@"export function sessionExpiredNotice\(\): string\s*\{\s*return t\('auth:sessionExpired'\)\s*\}", watcher);
        Assert.Contains("logout(sessionExpiredNotice())", watcher);
        Assert.Single(Regex.Matches(watcher, Regex.Escape("t('auth:sessionExpired')")));

        // Die Konstante aus dem Ladezeitpunkt darf nur noch als veraltet markierter Verweis für authStore.test.ts stehen. Sie darf auch
        // ganz fehlen: sobald dieser Test auf sessionExpiredNotice() umgestellt ist, wird sie gelöscht, ohne dass dieser Test im Weg steht.
        if (watcher.Contains("export const SESSION_EXPIRED_NOTICE", StringComparison.Ordinal))
            Assert.Matches(@"@deprecated[\s\S]*?\*/\s*export const SESSION_EXPIRED_NOTICE = sessionExpiredNotice\(\)", watcher);
    }

    // ---------- Helfer ----------

    /// <summary>Der Abschnitt der ersten Version (ab "## [0.1.0]" bis zum Ende).</summary>
    private static string Release010(string changelog)
    {
        var start = changelog.IndexOf("## [0.1.0]", StringComparison.Ordinal);
        Assert.True(start >= 0, "CHANGELOG.md hat keinen Abschnitt [0.1.0]");
        return changelog[start..];
    }
}
