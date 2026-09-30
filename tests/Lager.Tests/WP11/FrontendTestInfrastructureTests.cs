using System.Text.Json;
using System.Text.RegularExpressions;
using Lager.Tests.WP04;

namespace Lager.Tests.WP11;

/// <summary>
/// Sichert die Vitest-Testinfrastruktur des Frontends (WP11) ab: Skripte, gepinnte Testpakete, Vitest-Block in
/// vite.config.ts, Typen und Setup-Datei. Die Tests selbst laufen mit <c>npm test</c> im Ordner frontend/lager-ui;
/// hier wird nur geprüft, dass die Verdrahtung nicht versehentlich verschwindet, und dass die behobenen Fehler
/// (wirkungslose Retouren-Invalidierung, unformatierte Zeitstempel) nicht wieder auftauchen.
/// </summary>
public class FrontendTestInfrastructureTests
{
    private static readonly string[] TestPackages =
    {
        "vitest", "jsdom", "@testing-library/react", "@testing-library/dom", "@testing-library/user-event", "@testing-library/jest-dom",
    };

    [Fact]
    public void PackageJson_hat_test_Skripte_und_gepinnte_Testpakete()
    {
        using var doc = JsonDocument.Parse(FrontendFixture.ReadUi("package.json"));
        var scripts = doc.RootElement.GetProperty("scripts");
        Assert.Equal("vitest run", scripts.GetProperty("test").GetString());
        Assert.Equal("vitest", scripts.GetProperty("test:watch").GetString());
        // WP04-Skripte bleiben bestehen.
        Assert.Equal("tsc -b", scripts.GetProperty("typecheck").GetString());
        Assert.Equal("eslint .", scripts.GetProperty("lint").GetString());

        var dev = doc.RootElement.GetProperty("devDependencies");
        foreach (var package in TestPackages)
        {
            Assert.True(dev.TryGetProperty(package, out var version), $"{package} fehlt in devDependencies");
            // Exakte Version ohne ^/~: Vitest 4 / jsdom 29 / Testing Library sind auf React 19 + Vite 8 abgestimmt.
            Assert.Matches(@"^\d+\.\d+\.\d+$", version.GetString()!);
        }
    }

    [Fact]
    public void Testpakete_stehen_im_Lockfile_und_verlangen_kein_neueres_Node_als_die_engines_Angabe()
    {
        using var doc = JsonDocument.Parse(FrontendFixture.ReadUi("package-lock.json"));
        var packages = doc.RootElement.GetProperty("packages");

        foreach (var package in TestPackages)
            Assert.True(packages.TryGetProperty($"node_modules/{package}", out _), $"{package} fehlt im Lockfile");

        // engines der UI: ^20.19.0 || >=22.13.0 -> Vitest 4 und jsdom 29 laufen auf Node 20.19 (Vitest 5 / jsdom 30 nicht).
        Assert.StartsWith("4.", packages.GetProperty("node_modules/vitest").GetProperty("version").GetString());
        Assert.StartsWith("29.", packages.GetProperty("node_modules/jsdom").GetProperty("version").GetString());
    }

    [Fact]
    public void Vitest_ist_in_vite_config_und_tsconfig_verdrahtet()
    {
        var vite = FrontendFixture.ReadUi("vite.config.ts");
        Assert.Contains("environment: 'jsdom'", vite);
        Assert.Contains("setupFiles: ['./src/tests/setup.ts']", vite);
        Assert.Contains("include: ['src/**/*.test.{ts,tsx}']", vite);
        Assert.Contains("__BUILD_ID__", vite); // WP04: Build-ID für sw.js bleibt

        Assert.Contains("\"vitest/globals\"", FrontendFixture.ReadUi("tsconfig.app.json"));

        var setup = FrontendFixture.ReadUi("src/tests/setup.ts");
        Assert.Contains("@testing-library/jest-dom/vitest", setup);
    }

    [Fact]
    public void Vitest_Tests_von_WP11_liegen_unter_src_tests_WP11()
    {
        var dir = Path.Combine(FrontendFixture.UiDir, "src", "tests", "WP11");
        var files = Directory.EnumerateFiles(dir, "*.test.ts*").Select(Path.GetFileName).ToHashSet();

        foreach (var expected in new[]
                 {
                     "format.test.ts", "articleEditor.test.tsx", "mobilePicker.test.tsx", "returnsQc.test.tsx",
                     "invalidation.test.tsx", "formState.test.tsx", "pickListsAdmin.test.tsx", "authStore.test.ts",
                 })
            Assert.Contains(expected, files);
    }

    [Fact]
    public void Retouren_QC_invalidiert_die_Liste_und_keinen_nicht_existierenden_Detail_Key()
    {
        var hooks = FrontendFixture.ReadUi("src/api/hooks.ts");

        Assert.DoesNotContain("['returns', r.id]", hooks);
        var qc = Regex.Match(hooks, @"export const useSetReturnQc = \(\) => \{.*?\n\}\n", RegexOptions.Singleline).Value;
        Assert.Contains("queryKeys.returns", qc);
    }

    [Fact]
    public void Seiten_formatieren_Zeitstempel_ueber_lib_format_und_nicht_per_toLocaleString()
    {
        var pages = Directory.EnumerateFiles(Path.Combine(FrontendFixture.UiDir, "src", "pages"), "*.tsx");
        var unformatted = new Regex(@"new Date\([^)]*\)\.toLocale(Date|Time)?String\(");

        foreach (var page in pages)
            Assert.False(unformatted.IsMatch(File.ReadAllText(page)),
                $"{Path.GetFileName(page)} formatiert einen Zeitstempel mit new Date(...).toLocale…String() - stattdessen formatDateTime/formatDate aus lib/format.ts nutzen.");
    }
}
