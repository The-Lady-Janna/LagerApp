using System.Text.Json;
using System.Text.RegularExpressions;
using Lager.Api.Security;
using Lager.Application.Auth;
using Lager.Domain.Auth;
using Lager.Infrastructure.Auth;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Lager.Tests.WP18;

/// <summary>
/// Die Doku darf nichts behaupten, was der Code nicht hält: veraltete Aussagen sind entfernt, das README enthält die
/// Pflichtabschnitte (Sicherheit, Lizenz-Platzhalter, Drittlizenz, englische Fassung), die genannten npm-Befehle und
/// Konfigurationsschlüssel gibt es, und die Standardwerte in der Konfigurationsreferenz sind die des Codes.
/// </summary>
public class DocsClaimTests
{
    public static IEnumerable<object[]> Pages() => DocsRepo.OwnedPageData();

    // ---------- veraltete Aussagen ----------

    /// <summary>Frühere Falschaussagen (siehe Doku-Findings): jede darf nirgends mehr vorkommen.</summary>
    private static readonly (string Pattern, string Grund)[] Verboten =
    {
        (@"ChangeMe", "das frühere öffentliche Standardpasswort existiert nicht mehr"),
        (@"3D-Pack-Optimierung", "der Pickwagen bündelt nur nach Volumen und Gewicht"),
        (@"3D-Pack-Vorschl", "der Packvorschlag ist eine Karton-Heuristik"),
        (@"voller Audit-Trail", "nur ausgewählte Typen werden auditiert"),
        (@"jede Mutation", "nur ausgewählte Typen werden auditiert"),
        (@"PickReserved|PickPicked|InventoryDiff", "diese Ledger-Gründe gibt es nicht"),
        (@"ConcurrencyExceptionMiddleware", "die Middleware wurde durch den globalen Fehler-Handler ersetzt"),
        (@"hasMigrations|Migrations sind eingerichtet|dotnet ef migrations add", "es gibt keine EF-Migrationen"),
        (@"<repo-url>", "Platzhalter im Klon-Befehl"),
        (@"SchemaUpgrader erstellt alle Tabellen", "das Schema legt EnsureCreated an"),
        (@"appsettings\.Production\.json oder Env", "die Datei existiert nicht, Geheimnisse gehören in Umgebungsvariablen"),
        (@"Picks/h \(", "Picks pro Stunde sind gepickte Positionen je Zeitspanne"),
        (@"Order-Throughput", "einen solchen Kennwert gibt es nicht"),
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public void Outdated_claims_are_gone(string page)
    {
        var text = DocsRepo.Read(page);
        var found = Verboten
            .Where(v => Regex.IsMatch(text, v.Pattern, RegexOptions.IgnoreCase))
            .Select(v => $"'{v.Pattern}' ({v.Grund})")
            .ToList();

        Assert.True(found.Count == 0, $"{page} enthält veraltete Aussagen: {string.Join("; ", found)}");
    }

    // ---------- README ----------

    [Fact]
    public void Readme_has_the_sections_a_public_repository_needs()
    {
        var readme = DocsRepo.Read("README.md");

        // Abschnitte (Überschriften dürfen umbenannt werden, solange das Thema eine eigene Überschrift hat)
        foreach (var topic in new[] { "Quick|Schnellstart", "Sicherheit", "Drittlizenzen", "Lizenz", "Mitwirken" })
            Assert.Matches($@"(?im)^##\s+.*({topic})", readme);

        // CI-Badge mit echtem Repository-Pfad, Lizenz-Verweis, QuestPDF-Hinweis, Verweise auf die neuen Seiten und die englische Fassung
        Assert.Contains("The-Lady-Janna/LagerApp/actions/workflows/ci.yml/badge.svg", readme);
        Assert.DoesNotContain("OWNER/REPO", readme);
        // Lizenz: das README verweist auf die LICENSE-Datei (MIT)
        Assert.Contains("(LICENSE)", readme);
        Assert.Contains("MIT", readme);
        Assert.Contains("QuestPDF", readme);
        Assert.Contains("1 Mio. USD", readme);
        Assert.Contains("kommerzielle QuestPDF-Lizenz", readme);
        Assert.Contains("(README.en.md)", readme);
        foreach (var link in new[] { "docs/CONFIGURATION.md", "docs/API.md", "docs/DATA_MODEL.md", "docs/TROUBLESHOOTING.md", "CONTRIBUTING.md", "SECURITY.md", "CODE_OF_CONDUCT.md", "CHANGELOG.md" })
            Assert.Contains($"({link}", readme);

        // Die Prüfbefehle des Beitrags-Abschnitts sind die echten
        foreach (var command in new[] { "dotnet build Lager.sln", "dotnet test tests/Lager.Tests", "npm ci", "npm run lint", "npm run typecheck", "npm test", "npm run build" })
            Assert.Contains(command, readme);
    }

    [Fact]
    public void English_readme_mirrors_the_german_one_and_points_back_to_it()
    {
        var de = DocsRepo.Read("README.md");
        var en = DocsRepo.Read("README.en.md");

        Assert.Contains("(README.md)", en);
        Assert.Contains("German", en);
        Assert.Contains("The-Lady-Janna/LagerApp/actions/workflows/ci.yml/badge.svg", en);
        Assert.DoesNotContain("OWNER/REPO", en);
        Assert.Contains("QuestPDF", en);
        Assert.Contains("1 million USD", en);
        Assert.Contains("commercial QuestPDF license", en);
        Assert.DoesNotContain("ChangeMe", en, StringComparison.OrdinalIgnoreCase);

        // Gleicher Aufbau: gleiche Zahl Abschnitte (##), gleiche Prüfbefehle, dieselben Verweise auf docs/
        Assert.Equal(Count(de, @"^## "), Count(en, @"^## "));
        foreach (var command in new[] { "dotnet build Lager.sln", "dotnet test tests/Lager.Tests", "npm ci", "npm run lint", "npm run typecheck", "npm test", "npm run build" })
            Assert.Contains(command, en);
        Assert.Equal(DocLinks(de), DocLinks(en));

        static int Count(string text, string pattern) => Regex.Matches(text, pattern, RegexOptions.Multiline).Count;
        static string[] DocLinks(string text) =>
            Regex.Matches(text, @"\]\((docs/[A-Z_]+\.md)").Select(m => m.Groups[1].Value).Distinct().Order().ToArray();
    }

    // ---------- npm-Befehle ----------

    [Theory]
    [MemberData(nameof(Pages))]
    public void Npm_scripts_named_in_the_docs_exist_in_package_json(string page)
    {
        using var package = JsonDocument.Parse(DocsRepo.Read("frontend/lager-ui/package.json"));
        var scripts = package.RootElement.GetProperty("scripts").EnumerateObject().Select(p => p.Name).ToHashSet();

        var named = Regex.Matches(DocsRepo.Read(page), @"npm run ([A-Za-z][\w:-]*)").Select(m => m.Groups[1].Value)
            .Concat(Regex.Matches(DocsRepo.Read(page), @"npm (test)\b").Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();

        var missing = named.Where(s => !scripts.Contains(s)).ToList();
        Assert.True(missing.Count == 0, $"{page} nennt npm-Scripts, die es nicht gibt: {string.Join(", ", missing)}");
    }

    // ---------- Konfiguration ----------

    /// <summary>Alle Schlüssel, die der Code beim Start liest (Stand dieses Pakets); die Referenz muss jeden nennen.</summary>
    private static readonly string[] ConfigKeys =
    {
        "Database:Provider", "Database:ConnectionString", "Database:MySqlServerVersion", "Database:Seed", "Demo:AllowInProduction",
        "Jwt:SigningKey", "Jwt:KeyFile", "Jwt:Issuer", "Jwt:Audience", "Jwt:LifetimeMinutes",
        "Auth:BootstrapAdminUsername", "Auth:BootstrapAdminPassword", "Auth:MaxFailedAttempts", "Auth:LockoutMinutes",
        "Cors:AllowedOrigins", "AllowedHosts",
        "Security:RequireHttps", "Security:HttpsPort", "Security:LoginRateLimitPerMinute", "Security:GlobalRateLimitPerMinute",
        "Security:RateLimiting:Enabled", "Security:ForwardedHeaders:Enabled", "Security:ForwardedHeaders:KnownProxies",
        "Security:ForwardedHeaders:KnownNetworks", "Backup:AllowRestore", "Backup:Directory",
        "ASPNETCORE_ENVIRONMENT", "ASPNETCORE_URLS", "ASPNETCORE_HTTPS_PORT",
    };

    [Fact]
    public void Configuration_reference_names_every_key_and_its_environment_variable()
    {
        var text = DocsRepo.Read("docs/CONFIGURATION.md");
        var missing = new List<string>();

        foreach (var key in ConfigKeys)
        {
            if (!text.Contains($"`{key}`")) missing.Add(key);
            // Umgebungsvariable: ":" wird zu "__" (nicht für die reinen Umgebungsvariablen und AllowedHosts)
            if (key.Contains(':') && key != "Cors:AllowedOrigins" && !text.Contains(key.Replace(":", "__")))
                missing.Add(key.Replace(":", "__"));
        }
        Assert.Contains("Cors__AllowedOrigins__0", text);

        Assert.True(missing.Count == 0, "In docs/CONFIGURATION.md fehlen: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_key_named_in_the_configuration_reference_is_read_by_the_code_or_is_an_environment_variable()
    {
        var text = DocsRepo.Read("docs/CONFIGURATION.md");
        var source = string.Join('\n', SourceFiles("src", "*.cs").Select(File.ReadAllText));
        var appsettings = FlatKeys(DocsRepo.Read("src/Lager.Api/appsettings.json"))
            .Concat(FlatKeys(DocsRepo.Read("src/Lager.Api/appsettings.Development.json")))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Schlüssel = erste Zelle jeder Tabellenzeile in Backticks, mit Doppelpunkt (z. B. `Jwt:SigningKey`)
        var documented = DocsRepo.TableRows(text, l => Regex.IsMatch(l, @"^\|\s*`[A-Za-z]+(:[A-Za-z]+)+`"))
            .Select(cells => Regex.Match(cells[0], @"`([^`]+)`").Groups[1].Value)
            .Distinct()
            .ToList();
        Assert.True(documented.Count >= 20, $"Nur {documented.Count} Schlüssel in der Tabelle gefunden - Auswertung greift nicht");

        // Der Abschnitt "Serilog" liest Serilog selbst (ReadFrom.Configuration): der Code nennt diese Schlüssel nicht einzeln.
        var serilogReadsItself = source.Contains(".ReadFrom.Configuration(");

        var unknown = documented
            .Where(k => !(serilogReadsItself && k.StartsWith("Serilog:", StringComparison.Ordinal)))
            .Where(k => !source.Contains($"\"{k}\"") && !appsettings.Contains(k) && !ArrayKeyInAppsettings(k, appsettings))
            .ToList();
        Assert.True(unknown.Count == 0, "Schlüssel in der Referenz, die der Code nicht liest: " + string.Join(", ", unknown));

        static bool ArrayKeyInAppsettings(string key, HashSet<string> keys) => keys.Any(k => k.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Documented_defaults_are_the_defaults_of_the_code()
    {
        // Nur Standardwerte, die im C#-Code festgelegt sind (nicht die der appsettings.json, die andere Pakete ändern dürfen).
        var text = DocsRepo.Read("docs/CONFIGURATION.md");
        var production = SecuritySettings.From(new ConfigurationBuilder().Build(), new FakeEnvironment("Production"));
        var jwt = new JwtSettings();

        var expected = new (string Key, string Default)[]
        {
            ("Auth:MaxFailedAttempts", LockoutPolicy.Default.MaxFailedAttempts.ToString()),
            ("Auth:LockoutMinutes", ((int)LockoutPolicy.Default.Duration.TotalMinutes).ToString()),
            ("Jwt:LifetimeMinutes", jwt.LifetimeMinutes.ToString()),
            ("Jwt:Issuer", jwt.Issuer),
            ("Jwt:Audience", jwt.Audience),
            ("Security:LoginRateLimitPerMinute", production.LoginRateLimitPerMinute.ToString()),
            ("Security:GlobalRateLimitPerMinute", production.GlobalRateLimitPerMinute.ToString()),
            ("Security:RateLimiting:Enabled", production.RateLimitingEnabled ? "true" : "false"),
            ("Security:RequireHttps", production.RequireHttps ? "true" : "false"),
            ("Security:ForwardedHeaders:Enabled", production.ForwardedHeadersEnabled ? "true" : "false"),
            ("Database:Provider", "Sqlite"), // DatabaseSettings.Create: "Sqlite", wenn der Provider fehlt
        };

        var wrong = new List<string>();
        foreach (var (key, value) in expected)
        {
            var rows = DocsRepo.TableRows(text, l => l.StartsWith($"| `{key}`", StringComparison.Ordinal)).ToList();
            if (rows.Count == 0) { wrong.Add($"{key}: keine Zeile in der Referenz"); continue; }
            if (!rows.Any(cells => cells.Any(c => c.Contains(value, StringComparison.Ordinal))))
                wrong.Add($"{key}: Standardwert '{value}' fehlt in der Zeile");
        }

        Assert.True(wrong.Count == 0, string.Join(Environment.NewLine, wrong));
    }

    [Fact]
    public void Documented_numeric_rules_are_the_rules_of_the_code()
    {
        // Passwortregeln und Key-Länge stehen in mehreren Seiten; sie müssen zum Code passen.
        Assert.Equal(10, PasswordPolicy.MinLength);
        Assert.Equal(72, PasswordPolicy.MaxBytes);
        Assert.Equal(32, JwtKeyGuard.MinKeyBytes);

        foreach (var page in new[] { "docs/CONFIGURATION.md", "docs/API.md", "docs/USAGE.md", "docs/GETTING_STARTED.md", "docs/TROUBLESHOOTING.md" })
            Assert.Contains($"{PasswordPolicy.MinLength} Zeichen", DocsRepo.Read(page));
        Assert.Contains($"{PasswordPolicy.MaxBytes} Bytes", DocsRepo.Read("docs/CONFIGURATION.md"));
        Assert.Contains($"{JwtKeyGuard.MinKeyBytes} Bytes", DocsRepo.Read("docs/CONFIGURATION.md"));
    }

    // ---------- Fehlercodes ----------

    [Fact]
    public void Error_codes_named_in_the_api_reference_exist_in_the_code()
    {
        var text = DocsRepo.Read("docs/API.md");
        var section = text[text.IndexOf("## Fehlercodes", StringComparison.Ordinal)..text.IndexOf("## Endpunkte", StringComparison.Ordinal)];
        var source = string.Join('\n', SourceFiles("src", "*.cs").Select(File.ReadAllText));

        var codes = DocsRepo.TableRows(section, l => Regex.IsMatch(l, @"^\|\s*`[a-z_]+`"))
            .SelectMany(cells => Regex.Matches(cells[0], @"`([a-z_]+)`").Select(m => m.Groups[1].Value))
            .Distinct()
            .ToList();
        Assert.True(codes.Count >= 30, $"Nur {codes.Count} Fehlercodes gefunden - Auswertung greift nicht");

        var unknown = codes.Where(c => !source.Contains($"\"{c}\"")).ToList();
        Assert.True(unknown.Count == 0, "Fehlercodes in der Doku, die der Code nicht kennt: " + string.Join(", ", unknown));
    }

    // ---------- Helfer ----------

    private static IEnumerable<string> SourceFiles(string folder, string pattern) =>
        Directory.EnumerateFiles(DocsRepo.FullPath(folder), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>Alle Schlüsselpfade einer JSON-Konfiguration ("Section:Key"), Kommentare erlaubt.</summary>
    private static IEnumerable<string> FlatKeys(string json)
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

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public FakeEnvironment(string name) => EnvironmentName = name;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "Lager.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
