using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lager.Api.Controllers;
using Lager.Api.Seeding;
using Lager.Application.ImportExport;
using Lager.Infrastructure.Backup;
using Lager.Tests.WP18;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Lager.Tests.WP31;

/// <summary>
/// Endabnahme der Doku (WP31): ergänzt die Doku-Tests aus WP18 (die nur README, TODO und die Seiten direkt unter docs/ prüfen) um
/// alles, was später dazukam: die Referenzen unter docs/features, die Screenshot-Anleitung, CHANGELOG, CONTRIBUTING und die
/// PR-Vorlage. Geprüft wird weiterhin nur die Richtung "Doku -> Code": was die Doku nennt (Links, Dateien, Routen, Umgebungsvariablen,
/// Benutzer, Grenzen), muss es geben; neue Funktionen anderer Pakete brechen diese Tests nicht.
/// </summary>
public class DocsFinalAcceptanceTests
{
    // ---------- welche Seiten ----------

    /// <summary>Alle Markdown-Seiten der Doku im weiteren Sinn, die WP18 nicht abdeckt.</summary>
    private static readonly string[] ExtraTopLevel = { "CHANGELOG.md", "CONTRIBUTING.md", ".github/PULL_REQUEST_TEMPLATE.md" };

    private static IEnumerable<string> AllMarkdown() =>
        DocsRepo.OwnedPages.Concat(ExtraMarkdown()).Distinct();

    private static IEnumerable<string> ExtraMarkdown() =>
        Directory.EnumerateFiles(DocsRepo.FullPath("docs"), "*.md", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(DocsRepo.Root, f).Replace('\\', '/'))
            .Where(p => !DocsRepo.OwnedPages.Contains(p))
            .Concat(ExtraTopLevel)
            .OrderBy(p => p, StringComparer.Ordinal);

    public static IEnumerable<object[]> ExtraPages() => ExtraMarkdown().Select(p => new object[] { p });

    [Fact]
    public void The_extra_pages_include_the_feature_references_and_the_screenshot_guide()
    {
        var pages = ExtraMarkdown().ToList();

        // Die sieben Referenzen je Bereich, die Screenshot-Anleitung und die Basisdateien gehören zur geprüften Menge.
        foreach (var expected in new[]
                 {
                     "docs/features/lager-stammdaten.md", "docs/features/artikel-gtin.md", "docs/features/chargen-mhd.md",
                     "docs/features/csv-import-export.md", "docs/features/backup-restore.md", "docs/features/etiketten.md",
                     "docs/features/demo-modus.md", "docs/screenshots/README.md", "CHANGELOG.md", "CONTRIBUTING.md",
                 })
            Assert.Contains(expected, pages);
    }

    // ---------- Links ----------

    [Theory]
    [MemberData(nameof(ExtraPages))]
    public void Relative_links_and_anchors_of_the_extra_pages_resolve(string page)
    {
        var file = DocsRepo.FullPath(page);
        var dir = Path.GetDirectoryName(file)!;
        var problems = new List<string>();

        foreach (var link in DocsRepo.Links(File.ReadAllText(file)))
        {
            if (DocsRepo.IsExternal(link)) continue;

            var hash = link.IndexOf('#');
            var path = hash >= 0 ? link[..hash] : link;
            var anchor = hash >= 0 ? Uri.UnescapeDataString(link[(hash + 1)..]).ToLowerInvariant() : null;

            var target = path.Length == 0 ? file : Path.GetFullPath(Path.Combine(dir, Uri.UnescapeDataString(path)));
            if (!File.Exists(target) && !Directory.Exists(target))
            {
                problems.Add($"Link '{link}' zeigt auf eine nicht vorhandene Datei");
                continue;
            }

            if (!string.IsNullOrEmpty(anchor) && File.Exists(target) && target.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                && !DocsRepo.HeadingAnchors(File.ReadAllText(target)).Contains(anchor))
                problems.Add($"Link '{link}': Anker '#{anchor}' existiert in {Path.GetFileName(target)} nicht");
        }

        Assert.True(problems.Count == 0, $"{page}:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    [Fact]
    public void Every_feature_reference_is_linked_from_the_readmes_the_manual_and_the_getting_started_guide()
    {
        var features = Directory.EnumerateFiles(DocsRepo.FullPath("docs/features"), "*.md").Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.True(features.Count >= 7, $"Nur {features.Count} Referenzen unter docs/features");

        var readme = DocsRepo.Read("README.md");
        var readmeEn = DocsRepo.Read("README.en.md");
        var usage = DocsRepo.Read("docs/USAGE.md");
        var problems = new List<string>();

        foreach (var name in features)
        {
            if (!readme.Contains($"(docs/features/{name})")) problems.Add($"README.md verlinkt docs/features/{name} nicht");
            if (!readmeEn.Contains($"(docs/features/{name})")) problems.Add($"README.en.md verlinkt docs/features/{name} nicht");
            if (!usage.Contains($"(features/{name}")) problems.Add($"docs/USAGE.md verlinkt features/{name} nicht");
        }

        // Demo-Modus, Docker und Backup stehen zusätzlich in der Startanleitung; die Screenshot-Anleitung in beiden READMEs.
        var gettingStarted = DocsRepo.Read("docs/GETTING_STARTED.md");
        foreach (var name in new[] { "demo-modus.md", "backup-restore.md" })
            if (!gettingStarted.Contains($"(features/{name}")) problems.Add($"docs/GETTING_STARTED.md verlinkt features/{name} nicht");
        if (!gettingStarted.Contains("#schnellstart-mit-docker") && !gettingStarted.Contains("## Schnellstart mit Docker"))
            problems.Add("docs/GETTING_STARTED.md hat keinen Abschnitt 'Schnellstart mit Docker'");
        foreach (var page in new[] { readme, readmeEn })
            if (!page.Contains("(docs/screenshots/README.md)")) problems.Add("ein README verlinkt docs/screenshots/README.md nicht");

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // ---------- Dateipfade ----------

    [Theory]
    [MemberData(nameof(ExtraPages))]
    public void Repository_paths_named_in_backticks_exist_in_the_extra_pages(string page)
    {
        // Wie WP18, aber für die Seiten, die WP18 nicht liest. Laufzeitpfade, Platzhalter und das Branch-Namen-Beispiel der CONTRIBUTING.md sind ausgenommen.
        string[] runtimeOrExample = { "docs/screenshots", "/dist", "/node_modules", "/bin/", "/obj/", "/logs", ".db", ".env", "publish/", "/publish", "docs/getting-started" };
        var problems = new List<string>();

        foreach (Match m in Regex.Matches(DocsRepo.Read(page), "`([^`\\n]+)`"))
        {
            var span = m.Groups[1].Value.Trim();
            if (!Regex.IsMatch(span, @"^(src|tests|frontend|docs|\.github)/[A-Za-z0-9_./-]+$")
                && !Regex.IsMatch(span, @"^(README(\.en)?\.md|TODO\.md|CHANGELOG\.md|CONTRIBUTING\.md|SECURITY\.md|CODE_OF_CONDUCT\.md|global\.json|Lager\.sln|Directory\.Build\.props)$"))
                continue;
            if (runtimeOrExample.Any(x => span.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;

            var path = DocsRepo.FullPath(span.TrimEnd('/'));
            if (!File.Exists(path) && !Directory.Exists(path)) problems.Add($"`{span}` existiert nicht");
        }

        Assert.True(problems.Count == 0, $"{page}:{Environment.NewLine}{string.Join(Environment.NewLine, problems.Distinct())}");
    }

    // ---------- Routen ----------

    private static string Normalize(string path) =>
        Regex.Replace("/" + path.Trim('/'), @"\{\*{0,2}(\w+)(:[^}]*)?\}", "{x}").ToLowerInvariant();

    private static List<string> ActualRoutes()
    {
        var routes = new List<string>();
        foreach (var controller in typeof(AuthController).Assembly.GetTypes()
                     .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t)))
        {
            var prefix = controller.GetCustomAttribute<RouteAttribute>()?.Template ?? string.Empty;
            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                foreach (var http in method.GetCustomAttributes(true).OfType<HttpMethodAttribute>())
                    routes.Add(Normalize(string.IsNullOrEmpty(http.Template) ? prefix : $"{prefix}/{http.Template}"));
        }

        Assert.True(routes.Count >= 100, $"Nur {routes.Count} Routen gefunden - Reflection greift nicht");

        // GET /api/version ist bewusst kein Controller (Minimal-API in Program.cs, siehe ApiVersionInfo), die Doku nennt ihn als vorhanden.
        routes.Add(Normalize(ApiVersionInfo.Route));
        return routes;
    }

    /// <summary>Routen, die die Doku ausdrücklich als nicht vorhanden oder als geplant nennt.</summary>
    private static readonly string[] DocumentedAsAbsent = { "/api/admin/demo/reset" };

    [Fact]
    public void Every_api_route_named_anywhere_in_the_docs_exists_in_the_controllers()
    {
        var actual = ActualRoutes().Select(r => r.Split('/')).ToList();
        var problems = new List<string>();

        foreach (var page in AllMarkdown())
        {
            // Nur Routen mit vollem /api/-Pfad; "src/api/..." (Dateipfade im Frontend) zählt nicht (Zeichen davor).
            // In Markdown-Tabellen steht "|" als "\|": für die Auswertung zurückwandeln ({a\|b} = {a|b}).
            foreach (Match m in Regex.Matches(DocsRepo.Read(page).Replace("\\|", "|"), @"(?<![A-Za-z0-9_.])(/api/[A-Za-z0-9_\-/{}.<>*|:]+)"))
            {
                if (m.Groups[1].Value.Contains("...") || m.Groups[1].Value.Contains('*')) continue; // Abkürzungen wie /api/labels/...zpl oder /api/*
                var raw = m.Groups[1].Value.TrimEnd('.', ',', ';', ':', ')', '/');
                if (raw.Length <= "/api".Length) continue;
                // Platzhalter jeder Art ({id}, {a|b|c}, <id>) gelten als "ein beliebiges Segment".
                var doc = Normalize(Regex.Replace(Regex.Replace(raw, "<[^>]*>", "{x}"), @"\{[^}]*\}", "{x}"));
                if (DocumentedAsAbsent.Contains(doc)) continue;

                // "articles|stock|orders" (Alternativen ohne Klammern) gilt ebenfalls als ein beliebiges Segment.
                var segments = doc.Split('/').Select(s => s.Contains('|') ? "{x}" : s).ToArray();
                var found = actual.Any(route => route.Length == segments.Length
                    && route.Zip(segments).All(p => p.First == "{x}" || p.Second == "{x}" || p.First == p.Second));
                if (!found) problems.Add($"{page}: {raw} gibt es nicht");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Distinct()));
    }

    // ---------- Umgebungsvariablen und Konfigurationsschlüssel ----------

    [Fact]
    public void Every_double_underscore_setting_named_in_the_docs_is_a_real_configuration_key()
    {
        var source = string.Join('\n', Directory.EnumerateFiles(DocsRepo.FullPath("src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(File.ReadAllText));
        var appsettings = FlatKeys(DocsRepo.Read("src/Lager.Api/appsettings.json"))
            .Concat(FlatKeys(DocsRepo.Read("src/Lager.Api/appsettings.Development.json")))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var problems = new List<string>();

        foreach (var page in AllMarkdown())
        {
            foreach (Match m in Regex.Matches(DocsRepo.Read(page), @"(?<![A-Za-z0-9_])[A-Z][A-Za-z]+(?:__[A-Za-z0-9.]+)+"))
            {
                var parts = m.Value.Split("__").ToList();
                while (parts.Count > 1 && parts[^1].All(char.IsDigit)) parts.RemoveAt(parts.Count - 1); // Array-Index (__0)
                var key = string.Join(':', parts);

                // Serilog liest seinen Abschnitt selbst (ReadFrom.Configuration); Arrays stehen in der appsettings.json unter dem Elternschlüssel.
                if (key.StartsWith("Serilog:", StringComparison.Ordinal)) continue;
                if (source.Contains($"\"{key}\"") || appsettings.Contains(key) || appsettings.Any(k => k.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase)))
                    continue;
                problems.Add($"{page}: {m.Value} ({key}) liest der Code nicht");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Distinct()));
    }

    // ---------- genannte Fakten ----------

    [Fact]
    public void Demo_users_limits_and_confirmation_words_named_in_the_docs_are_the_ones_of_the_code()
    {
        var demo = DocsRepo.Read("docs/features/demo-modus.md");
        var gettingStarted = DocsRepo.Read("docs/GETTING_STARTED.md");
        var problems = new List<string>();

        // Demo-Benutzer: jeder Benutzer des Katalogs steht in der Referenz und in der Startanleitung.
        foreach (var user in DemoCatalog.Users)
        {
            if (!demo.Contains($"`{user.Username}`")) problems.Add($"demo-modus.md nennt den Demo-Benutzer {user.Username} nicht");
            if (!gettingStarted.Contains($"`{user.Username}`")) problems.Add($"GETTING_STARTED.md nennt den Demo-Benutzer {user.Username} nicht");
        }

        // Umgebungsvariable und Schlüssel des Demo-Modus.
        foreach (var name in new[] { DemoSettings.EnvironmentVariable, DemoSettings.EnabledKey, DemoSettings.AllowInProductionKey })
            if (!DocsRepo.Read("docs/CONFIGURATION.md").Contains($"`{name}`") && !DocsRepo.Read("docs/features/demo-modus.md").Contains($"`{name}`"))
                problems.Add($"{name} steht in keiner Konfigurationsseite");

        // Import-Grenzen (USAGE und Referenz nennen 5 MB, 20.000 Zeilen, 500 Fehler).
        var csv = DocsRepo.Read("docs/features/csv-import-export.md");
        var usage = DocsRepo.Read("docs/USAGE.md");
        Assert.Equal(5 * 1024 * 1024, ImportLimits.MaxFileBytes);
        Assert.Equal(20_000, ImportLimits.MaxRows);
        Assert.Equal(500, ImportLimits.MaxReportedErrors);
        foreach (var text in new[] { csv, usage })
        {
            if (!text.Contains("5 MB")) problems.Add("eine Seite nennt die Importgrenze 5 MB nicht");
            if (!text.Contains("20.000")) problems.Add("eine Seite nennt die Importgrenze 20.000 Zeilen nicht");
            if (!text.Contains("500")) problems.Add("eine Seite nennt die Grenze von 500 Einzelfehlern nicht");
        }

        // Backup: Standard der Aufbewahrung und das Bestätigungswort des Restores.
        var configuration = DocsRepo.Read("docs/CONFIGURATION.md");
        var retentionRow = DocsRepo.TableRows(configuration, l => l.StartsWith("| `Backup:RetentionCount`", StringComparison.Ordinal)).SingleOrDefault();
        if (retentionRow is null || !retentionRow.Any(c => c.Contains($"`{BackupOptions.DefaultRetentionCount}`")))
            problems.Add($"CONFIGURATION.md nennt nicht den Standard {BackupOptions.DefaultRetentionCount} für Backup:RetentionCount");
        foreach (var page in new[] { "docs/USAGE.md", "docs/GETTING_STARTED.md", "docs/TROUBLESHOOTING.md", "docs/features/backup-restore.md" })
            if (!DocsRepo.Read(page).Contains($"`{AdminController.RestoreConfirmation}`")) problems.Add($"{page} nennt das Bestätigungswort {AdminController.RestoreConfirmation} nicht");

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Compose_settings_the_docs_call_passed_through_are_in_the_compose_file()
    {
        var compose = DocsRepo.Read("docker-compose.yml");
        var mysql = DocsRepo.Read("docker-compose.mysql.yml");
        var envExample = DocsRepo.Read(".env.example");
        var configuration = DocsRepo.Read("docs/CONFIGURATION.md");
        var problems = new List<string>();

        // Die Tabelle "Was die docker-compose.yml an den Container durchreicht" nennt diese Einstellungen.
        foreach (var setting in new[]
                 {
                     "AllowedHosts", "Security__ForwardedHeaders__Enabled", "Security__ForwardedHeaders__KnownNetworks__0",
                     "Auth__BootstrapAdminPassword", "Jwt__SigningKey", "Cors__AllowedOrigins__0", "Backup__AllowRestore",
                     "Backup__Schedule", "Backup__RetentionCount", "Demo__Enabled",
                     "Database__Seed", "Demo__AllowInProduction", "Serilog__MinimumLevel__Default", "Logging__RetainedFileCount",
                 })
        {
            if (!configuration.Contains($"`{setting}`")) problems.Add($"CONFIGURATION.md nennt {setting} nicht");
            if (!Regex.IsMatch(compose, $@"(?m)^\s+{Regex.Escape(setting)}:")) problems.Add($"docker-compose.yml reicht {setting} nicht durch");
        }

        // Variablen nur für Compose: in den Compose-Dateien und der Vorlage vorhanden, in der Doku genannt.
        foreach (var variable in new[] { "LAGER_PORT", "LAGER_DOMAIN" })
            if (!compose.Contains(variable) || !envExample.Contains(variable) || !configuration.Contains($"`{variable}`")) problems.Add($"{variable}: Compose, .env.example und CONFIGURATION.md müssen übereinstimmen");
        foreach (var variable in new[] { "LAGER_DB_PASSWORD", "LAGER_DB_ROOT_PASSWORD" })
            if (!mysql.Contains(variable) || !envExample.Contains(variable) || !configuration.Contains(variable)) problems.Add($"{variable}: MySQL-Zusatzdatei, .env.example und CONFIGURATION.md müssen übereinstimmen");

        // Der Healthcheck und die Volumes, die die Doku nennt.
        Assert.Contains("/health/ready", compose);
        foreach (var volume in new[] { "lager-data", "mysql-data", "caddy-data" })
            if (!compose.Contains(volume) && !mysql.Contains(volume)) problems.Add($"Volume {volume} fehlt in den Compose-Dateien");

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // ---------- Veröffentlichungsreife ----------

    [Fact]
    public void The_ci_badge_is_active_with_the_real_repository_and_no_page_contains_the_former_default_password_or_a_real_contact()
    {
        foreach (var readme in new[] { "README.md", "README.en.md" })
        {
            var text = DocsRepo.Read(readme);
            var withoutComments = Regex.Replace(text, "<!--.*?-->", string.Empty, RegexOptions.Singleline);
            Assert.Contains("The-Lady-Janna/LagerApp/actions/workflows/ci.yml/badge.svg", withoutComments);   // aktives Badge mit echtem Pfad
            Assert.DoesNotContain("OWNER/REPO", text);
        }

        var emails = new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+\.[A-Za-z]{2,}");
        var problems = new List<string>();
        foreach (var page in AllMarkdown())
        {
            var text = DocsRepo.Read(page);
            if (text.Contains("ChangeMe", StringComparison.OrdinalIgnoreCase)) problems.Add($"{page}: nennt das frühere Standardpasswort");
            foreach (Match m in emails.Matches(text))
                if (!m.Value.EndsWith("@example.com", StringComparison.OrdinalIgnoreCase)) problems.Add($"{page}: echte E-Mail-Adresse {m.Value}");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void The_changelog_has_an_unreleased_section_and_the_first_release_with_a_date_placeholder_matching_the_frontend_version()
    {
        var changelog = DocsRepo.Read("CHANGELOG.md");

        Assert.Matches(@"(?m)^## \[Unreleased\]\s*$", changelog);
        Assert.Matches(@"(?m)^## \[0\.1\.0\] - JJJJ-MM-TT\s*$", changelog);

        // Die erste Version ist die des Frontends: stimmt die Versionsnummer nicht mehr, fehlt der Changelog-Eintrag.
        using var package = JsonDocument.Parse(DocsRepo.Read("frontend/lager-ui/package.json"));
        Assert.Equal("0.1.0", package.RootElement.GetProperty("version").GetString());

        // Die Bereiche, die der Eintrag beschreiben soll.
        foreach (var heading in new[] { "### Sicherheit", "### Hinzugefügt", "### Geändert", "### Entfernt" })
            Assert.Contains(heading, changelog);
        foreach (var topic in new[] { "Docker", "Demo-Modus", "Lagerstruktur", "CSV", "Backup", "Etiketten", "problem+json", "409", "UTC" })
            Assert.Contains(topic, changelog);
    }

    // ---------- Helfer ----------

    /// <summary>Alle Schlüsselpfade einer JSON-Konfiguration ("Section:Key"), Kommentare erlaubt.</summary>
    private static List<string> FlatKeys(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        var keys = new List<string>();
        Walk(doc.RootElement, "");
        return keys;

        void Walk(JsonElement element, string prefix)
        {
            if (element.ValueKind != JsonValueKind.Object) return;
            foreach (var property in element.EnumerateObject())
            {
                var key = prefix.Length == 0 ? property.Name : $"{prefix}:{property.Name}";
                keys.Add(key);
                Walk(property.Value, key);
            }
        }
    }
}
