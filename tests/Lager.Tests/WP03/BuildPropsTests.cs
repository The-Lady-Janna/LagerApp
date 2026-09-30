using System.Xml.Linq;

namespace Lager.Tests.WP03;

/// <summary>
/// Prüft die zentrale Build-Konfiguration (Directory.Build.props) und dass die einzelnen .csproj-Dateien
/// sie nicht mehr duplizieren. Liest die Dateien direkt aus dem Repository (Wurzel = Ordner mit Lager.sln).
/// </summary>
public class BuildPropsTests
{
    private static readonly string[] CentralSettings =
        { "TargetFramework", "Nullable", "ImplicitUsings", "LangVersion", "TreatWarningsAsErrors" };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lager.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Lager.sln oberhalb von " + AppContext.BaseDirectory + " nicht gefunden");
    }

    private static IEnumerable<string> ProjectFiles()
    {
        var root = RepoRoot();
        return new[] { "src", "tests" }
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.csproj", SearchOption.AllDirectories))
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
    }

    private static XDocument LoadProps() => XDocument.Load(Path.Combine(RepoRoot(), "Directory.Build.props"));

    [Fact]
    public void Directory_Build_props_defines_central_settings()
    {
        var unconditional = LoadProps().Descendants("PropertyGroup")
            .Where(g => g.Attribute("Condition") is null)
            .SelectMany(g => g.Elements())
            .ToDictionary(e => e.Name.LocalName, e => e.Value.Trim());

        Assert.Equal("net8.0", unconditional["TargetFramework"]);
        Assert.Equal("enable", unconditional["Nullable"]);
        Assert.Equal("enable", unconditional["ImplicitUsings"]);
        Assert.Equal("latest", unconditional["LangVersion"]);
        Assert.Equal("true", unconditional["Deterministic"]);
        Assert.Equal("true", unconditional["NuGetAudit"]);
        Assert.Equal("all", unconditional["NuGetAuditMode"]);

        // Metadaten: Autor und Repository sind gesetzt; die Projekte sind keine NuGet-Pakete, daher keine Lizenz-Metadaten.
        Assert.Equal("The-Lady-Janna", unconditional["Authors"]);
        Assert.Equal("https://github.com/The-Lady-Janna/LagerApp", unconditional["RepositoryUrl"]);
        Assert.DoesNotContain("PackageLicenseExpression", unconditional.Keys);
        Assert.DoesNotContain("PackageLicenseFile", unconditional.Keys);
    }

    [Fact]
    public void Warnings_are_errors_only_in_strict_mode()
    {
        var groups = LoadProps().Descendants("PropertyGroup")
            .Where(g => g.Elements().Any(e => e.Name.LocalName == "TreatWarningsAsErrors"))
            .ToList();

        Assert.NotEmpty(groups);
        Assert.All(groups, g =>
        {
            // Leerraum ignorieren, sonst aber exakt: ein "!=" oder ein anderer Wert darf nicht durchrutschen.
            var condition = System.Text.RegularExpressions.Regex.Replace(g.Attribute("Condition")?.Value ?? "", @"\s+", "");
            Assert.Equal("'$(LagerStrict)'=='true'", condition);
            Assert.Equal("true", g.Elements().Single(e => e.Name.LocalName == "TreatWarningsAsErrors").Value.Trim());
        });
    }

    [Fact]
    public void Projects_do_not_repeat_central_settings_and_use_no_legacy_packages()
    {
        var projects = ProjectFiles().ToList();
        Assert.True(projects.Count >= 6, $"Erwartet: 5 src-Projekte + Testprojekt, gefunden: {projects.Count}");

        foreach (var project in projects)
        {
            var doc = XDocument.Load(project);
            var duplicated = doc.Descendants()
                .Select(e => e.Name.LocalName)
                .Where(n => CentralSettings.Contains(n))
                .ToList();
            Assert.True(duplicated.Count == 0,
                $"{Path.GetFileName(project)} wiederholt zentrale Einstellungen: {string.Join(", ", duplicated)}");

            var legacy = doc.Descendants("PackageReference")
                .Any(p => p.Attribute("Include")?.Value == "Microsoft.AspNetCore.Http.Abstractions");
            Assert.False(legacy,
                $"{Path.GetFileName(project)} referenziert das Legacy-Paket Http.Abstractions 2.x; FrameworkReference Microsoft.AspNetCore.App verwenden.");
        }
    }

    [Fact]
    public void Entity_framework_packages_use_one_version_everywhere()
    {
        // Ohne Central Package Management stehen die EF-Core-Versionen in mehreren .csproj - sie müssen
        // synchron bleiben, sonst drohen Laufzeitfehler durch gemischte EF-Assemblies.
        var versions = ProjectFiles()
            .SelectMany(p => XDocument.Load(p).Descendants("PackageReference"))
            .Where(p => p.Attribute("Include")?.Value.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true)
            .Select(p => p.Attribute("Version")?.Value)
            .Distinct()
            .ToList();

        Assert.True(versions.Count == 1, "Uneinheitliche EF-Core-Versionen: " + string.Join(", ", versions));
    }
}
