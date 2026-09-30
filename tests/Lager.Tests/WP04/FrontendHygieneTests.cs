using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lager.Tests.WP04;

/// <summary>
/// Sichert die Akzeptanzkriterien von WP04 ab, die sich aus den Dateien ergeben (das Frontend hat in
/// dieser Welle noch keine automatischen Tests): gepatchte Abhängigkeiten, Paket-Metadaten, keine
/// Default-Zugangsdaten im UI, Rollen-Guards, Service-Worker-Strategie.
/// </summary>
public class FrontendHygieneTests
{
    [Fact]
    public void PackageJson_hat_Metadaten_Engines_typecheck_Skript_und_MIT_Lizenz()
    {
        using var doc = JsonDocument.Parse(FrontendFixture.ReadUi("package.json"));
        var root = doc.RootElement;

        Assert.Equal("lager-ui", root.GetProperty("name").GetString());
        Assert.Equal("0.1.0", root.GetProperty("version").GetString());
        Assert.True(root.GetProperty("private").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("description").GetString()));
        Assert.Equal("tsc -b", root.GetProperty("scripts").GetProperty("typecheck").GetString());
        Assert.Equal("MIT", root.GetProperty("license").GetString());

        // Toolchain (vite 8, eslint 10) verlangt Node >= 20.19 bzw. >= 22.13.
        var node = root.GetProperty("engines").GetProperty("node").GetString()!;
        Assert.Contains("20.19", node);
        Assert.Contains("22.13", node);
    }

    [Fact]
    public void PackageLock_enthaelt_keine_verwundbaren_Versionen_der_npm_audit_Funde()
    {
        // Mindestversionen laut npm audit (axios, react-router, vite und die transitiven Funde).
        var minimum = new Dictionary<string, Version>
        {
            ["axios"] = new(1, 18, 0),
            ["react-router"] = new(7, 18, 2),
            ["react-router-dom"] = new(7, 18, 2),
            ["vite"] = new(8, 0, 16),
            ["form-data"] = new(4, 0, 6),
            ["postcss"] = new(8, 5, 23),
            ["nanoid"] = new(3, 3, 18),
        };

        using var doc = JsonDocument.Parse(FrontendFixture.ReadUi("package-lock.json"));
        var packages = doc.RootElement.GetProperty("packages");

        foreach (var (name, min) in minimum)
        {
            Assert.True(packages.TryGetProperty($"node_modules/{name}", out var entry), $"{name} fehlt im Lockfile");
            var version = Version.Parse(Regex.Match(entry.GetProperty("version").GetString()!, @"^\d+\.\d+\.\d+").Value);
            Assert.True(version >= min, $"{name} {version} ist älter als die gepatchte Version {min}");
        }
    }

    [Fact]
    public void Verwaistes_frontend_package_lock_ist_entfernt()
    {
        Assert.False(File.Exists(Path.Combine(FrontendFixture.RepoRoot, "frontend", "package-lock.json")));
        Assert.True(File.Exists(Path.Combine(FrontendFixture.UiDir, "package-lock.json")));
    }

    [Fact]
    public void Login_zeigt_keine_Default_Zugangsdaten_und_belegt_den_Benutzernamen_nicht_vor()
    {
        var sourceFiles = new[] { "src", "public" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(FrontendFixture.UiDir, d), "*", SearchOption.AllDirectories))
            .Append(Path.Combine(FrontendFixture.UiDir, "index.html"));
        foreach (var file in sourceFiles)
            Assert.DoesNotContain("ChangeMe", File.ReadAllText(file), StringComparison.OrdinalIgnoreCase);

        var login = FrontendFixture.ReadUi("src/pages/LoginPage.tsx");
        Assert.DoesNotContain("useState('admin')", login);
        Assert.DoesNotContain("Standard-Admin", login);
        Assert.Contains("useState('')", login);

        // Der Erststart-Hinweis (das Einmalpasswort steht in der Server-Konsole) steht seit WP29 in der Sprachdatei, nicht im Quelltext der Seite.
        Assert.Contains("t('auth:login.firstStart')", login);
        using var locale = JsonDocument.Parse(FrontendFixture.ReadUi("src/locales/de/auth.json"));
        Assert.Contains("Server-Konsole", locale.RootElement.GetProperty("login").GetProperty("firstStart").GetString());
    }

    [Fact]
    public void Routen_sind_rollengeschuetzt_und_unbekannte_URLs_zeigen_NotFoundPage()
    {
        var app = FrontendFixture.ReadUi("src/App.tsx");

        Assert.Matches(@"path=""/users""\s+element=\{<RequireRole role=""Admin"">", app);
        Assert.Matches(@"path=""/audit""\s+element=\{<RequireRole role=""Manager"">", app);
        Assert.Matches(@"path=""/cart-configs""\s+element=\{<RequireRole role=""Manager"">", app);
        Assert.Matches(@"path=""\*""\s+element=\{<NotFoundPage />\}", app);
        Assert.DoesNotContain("adminOnly", app);                // Sidebar-Filter läuft über hasRole
    }

    [Fact]
    public void Logout_leert_Query_Cache_und_Lagerauswahl_und_401_bei_Passwortpruefung_beendet_die_Session_nicht()
    {
        var auth = FrontendFixture.ReadUi("src/state/auth.ts");
        Assert.Contains("queryClient.clear()", auth);
        Assert.Contains("useActiveWarehouse.getState().setActive(null)", auth);

        var client = FrontendFixture.ReadUi("src/api/client.ts");
        Assert.Contains("'/auth/login'", client);
        Assert.Contains("'/auth/change-password'", client);
        Assert.Contains("password_change_required", client);

        // Der Lieferschein wird per Axios-Blob geladen (mit Authorization-Header), nicht per <a href>.
        Assert.DoesNotContain("href={`/api/", FrontendFixture.ReadUi("src/pages/PackPickListPage.tsx"));
    }

    [Fact]
    public void ServiceWorker_wird_nur_im_Production_Build_aus_main_registriert_und_sw_js_bekommt_eine_Build_ID()
    {
        // Kein Inline-Skript mehr in index.html (hostname-basierte Heuristik entfällt, CSP-tauglich).
        Assert.DoesNotContain("serviceWorker", FrontendFixture.ReadUi("index.html"));
        Assert.Contains("import.meta.env.PROD", FrontendFixture.ReadUi("src/lib/serviceWorker.ts"));
        Assert.Contains("registerServiceWorker()", FrontendFixture.ReadUi("src/main.tsx"));

        // Cache-Version als Konstante; die Build-ID wird beim `vite build` in dist/sw.js gestempelt.
        Assert.Matches(@"const VERSION = '[^']+'", FrontendFixture.ReadUi("public/sw.js"));
        Assert.Contains("__BUILD_ID__", FrontendFixture.ReadUi("public/sw.js"));
        Assert.Contains("__BUILD_ID__", FrontendFixture.ReadUi("vite.config.ts"));
    }
}
