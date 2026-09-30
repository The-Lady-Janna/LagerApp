using System.ComponentModel;
using System.Diagnostics;

namespace Lager.Tests.WP33;

/// <summary>
/// Das gebaute Frontend liegt zur Auslieferung in <c>src/Lager.Api/wwwroot</c> (Dockerfile: <c>COPY --from=frontend .../dist ./wwwroot</c>;
/// lokal dorthin kopiert) und gehört nicht ins Repository. Ein eingechecktes <c>wwwroot/index.html</c> würde die reine API
/// (<c>WithoutFrontend_PureApi</c> in WP16) zur Web-App machen und diesen Test rot färben - die <c>.gitignore</c> hält es fern.
/// </summary>
public class GitignoreWwwrootTests
{
    private static readonly string Root = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lager.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Lager.sln oberhalb von " + AppContext.BaseDirectory + " nicht gefunden");
    }

    [Fact]
    public void The_gitignore_names_the_frontend_build_folder_of_the_api()
    {
        // Ohne git prüfbar: ein Eintrag für genau diesen Ordner (mit oder ohne führenden/abschließenden Schrägstrich), nicht negiert.
        var patterns = File.ReadAllLines(Path.Combine(Root, ".gitignore"))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();
        static string Normalize(string pattern) => pattern.TrimStart('!').Trim('/');

        var covering = patterns.Where(p => !p.StartsWith('!') && Normalize(p) is "src/Lager.Api/wwwroot" or "wwwroot" or "**/wwwroot").ToList();
        var reincluded = patterns.Where(p => p.StartsWith('!') && Normalize(p).Contains("wwwroot", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(covering);
        Assert.Empty(reincluded);
    }

    [Fact]
    public void Git_ignores_the_delivered_frontend_but_not_the_api_sources_next_to_it()
    {
        string[] ignored =
        {
            "src/Lager.Api/wwwroot/index.html",
            "src/Lager.Api/wwwroot/assets/index-abc123.js",
            "src/Lager.Api/wwwroot/sw.js",
        };
        string[] versioned =
        {
            "src/Lager.Api/Program.cs",
            "src/Lager.Api/appsettings.json",
            "frontend/lager-ui/public/favicon.svg",
        };

        var temp = Directory.CreateTempSubdirectory("lager-wp33-gitignore-").FullName;
        try
        {
            if (RunGit(temp, "init", "-q") is null) return; // git ist nicht installiert: der Text-Test oben deckt den Eintrag ab

            File.Copy(Path.Combine(Root, ".gitignore"), Path.Combine(temp, ".gitignore"));
            foreach (var relative in ignored.Concat(versioned))
            {
                var file = Path.Combine(temp, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "x");
            }

            // check-ignore nennt genau die ignorierten Pfade (Exit-Code 1 = keiner ignoriert).
            var output = RunGit(temp, ["check-ignore", "--", .. ignored, .. versioned]);
            Assert.NotNull(output);
            var actual = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

            Assert.True(ignored.All(actual.Contains), "Werden nicht ignoriert: " + string.Join(", ", ignored.Where(p => !actual.Contains(p))));
            Assert.True(!versioned.Any(actual.Contains), "Werden fälschlich ignoriert: " + string.Join(", ", versioned.Where(actual.Contains)));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>Führt git aus; null, wenn git nicht installiert ist oder der Aufruf scheitert (Exit-Code über 1).</summary>
    private static string? RunGit(string workingDirectory, params string[] arguments)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);

        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return process.ExitCode <= 1 ? output : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}
