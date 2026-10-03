using System.Text.RegularExpressions;

namespace Lager.Tests.WP17;

/// <summary>
/// Regression zu den roten Dependabot-PRs "Microsoft.EntityFrameworkCore(.Sqlite) 8 -> 9": Ein PR hob nur einen Teil der
/// EF-Core-Pakete an, die Mischung wirft zur Laufzeit eine <c>TypeLoadException</c> und lässt fast alle Datenbank-Tests
/// (auch in WP08, WP10, WP13 ...) scheitern. Diese Tests halten die zugrunde liegende Regel fest: Die Pakete einer Familie
/// haben dieselbe Hauptversion, und Dependabot darf Major-Sprünge dieser Familien nicht als Einzel-PR vorschlagen.
/// </summary>
public class DependencyAlignmentTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lager.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Lager.sln nicht gefunden");
    }

    private static IEnumerable<(string File, string Package, string Version)> PackageReferences()
    {
        var root = RepoRoot();
        var rx = new Regex(@"<PackageReference\s+Include=""(?<id>[^""]+)""\s+Version=""(?<v>[^""]+)""", RegexOptions.Compiled);
        foreach (var csproj in Directory.EnumerateFiles(root, "*.csproj", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                              && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                              && !f.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}")))
        {
            foreach (Match m in rx.Matches(File.ReadAllText(csproj)))
                yield return (Path.GetRelativePath(root, csproj), m.Groups["id"].Value, m.Groups["v"].Value);
        }
    }

    private static int Major(string version) => int.Parse(Regex.Match(version, @"^\d+").Value);

    [Fact]
    public void All_EntityFrameworkCore_packages_and_Pomelo_have_the_same_major_version()
    {
        var efFamily = PackageReferences()
            .Where(p => p.Package.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                     || p.Package == "Pomelo.EntityFrameworkCore.MySql")
            .ToList();

        Assert.NotEmpty(efFamily);
        var majors = efFamily.Select(p => Major(p.Version)).Distinct().ToList();
        Assert.True(majors.Count == 1,
            "EF-Core-Pakete mit unterschiedlicher Hauptversion (TypeLoadException zur Laufzeit): "
            + string.Join(", ", efFamily.Select(p => $"{p.Package} {p.Version} ({p.File})")));
    }

    [Fact]
    public void Framework_coupled_packages_do_not_run_ahead_of_the_target_framework()
    {
        // Die Projekte zielen auf net8.0: ASP.NET-Core- und Microsoft.Extensions-Pakete bleiben auf Hauptversion 8
        // (Ausnahme bewusst nur zusammen mit einem Framework-Upgrade).
        var ahead = PackageReferences()
            .Where(p => p.Package.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal)
                     || p.Package.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)
                     || p.Package.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal))
            .Where(p => Major(p.Version) > 8)
            .ToList();

        Assert.True(ahead.Count == 0, "Pakete über der Framework-Hauptversion 8: " + string.Join(", ", ahead.Select(p => $"{p.Package} {p.Version}")));
    }

    [Fact]
    public void Dependabot_does_not_propose_major_updates_for_the_framework_coupled_nuget_families()
    {
        var yaml = File.ReadAllText(Path.Combine(RepoRoot(), ".github", "dependabot.yml"));
        var nuget = Regex.Split(yaml.Replace("\r\n", "\n"), @"(?m)^\s*-\s+package-ecosystem:\s*").Skip(1)
            .Single(b => b.StartsWith("nuget", StringComparison.Ordinal));

        foreach (var family in new[] { "Microsoft.EntityFrameworkCore*", "Pomelo.EntityFrameworkCore.MySql", "Microsoft.AspNetCore.*", "Microsoft.Extensions.*" })
        {
            Assert.True(
                Regex.IsMatch(nuget, $@"dependency-name:\s*""{Regex.Escape(family)}""\s*\n\s+update-types:\s*\[""version-update:semver-major""\]"),
                $"dependabot.yml (nuget): Major-Updates für {family} sind nicht ausgeschlossen");
        }
    }
}
