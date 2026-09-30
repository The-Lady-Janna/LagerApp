using System.Diagnostics;
using System.Text.Json;

namespace Lager.Tests.WP04;

/// <summary>
/// Hilfen für die Frontend-Tests: Repo-Wurzel finden, Dateien lesen und reine TypeScript-Module
/// (src/lib/*.ts ohne Laufzeit-Imports) mit Node ausführen. Das Frontend hat in dieser Welle noch
/// kein Vitest; diese Tests sichern die Akzeptanzkriterien und die reine Logik trotzdem ab.
/// </summary>
internal static class FrontendFixture
{
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string UiDir => Path.Combine(RepoRoot, "frontend", "lager-ui");

    public static string ReadUi(string relativePath) =>
        File.ReadAllText(Path.Combine(UiDir, relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lager.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Lager.sln nicht gefunden (Repo-Wurzel).");
    }

    /// <summary>file://-URL eines Frontend-Moduls für den Import im Node-Skript.</summary>
    public static string ModuleUrl(string relativePath) =>
        new Uri(Path.Combine(UiDir, relativePath.Replace('/', Path.DirectorySeparatorChar))).AbsoluteUri;

    // Node kann TypeScript seit 22.18 / 23.6 / 24 ohne Flag direkt importieren (Type-Stripping).
    private static readonly Lazy<bool> NodeUsable = new(() =>
    {
        try
        {
            var psi = new ProcessStartInfo("node", "--version") { RedirectStandardOutput = true, UseShellExecute = false };
            using var p = Process.Start(psi)!;
            var text = p.StandardOutput.ReadToEnd().Trim().TrimStart('v');
            p.WaitForExit();
            var parts = text.Split('.');
            var major = int.Parse(parts[0]);
            var minor = int.Parse(parts[1]);
            return major >= 24 || (major == 23 && minor >= 6) || (major == 22 && minor >= 18);
        }
        catch
        {
            return false;
        }
    });

    /// <summary>true, wenn ein Node mit Type-Stripping (>= 22.18 bzw. 23.6 / 24) im PATH liegt.</summary>
    public static bool IsNodeUsable => NodeUsable.Value;

    /// <summary>
    /// Führt ein ES-Modul-Skript mit Node aus (args landen in process.argv[2..]) und liefert die JSON-Ausgabe (stdout).
    /// Nur aus Tests mit [NodeFact] aufrufen: ohne passendes Node werden diese Tests als übersprungen gemeldet und nie ausgeführt.
    /// </summary>
    public static JsonDocument RunNode(string script, params string[] args)
    {
        if (!NodeUsable.Value)
            throw new InvalidOperationException("Node >= 22.18 nicht gefunden — der Test gehört mit [NodeFact] markiert.");

        var file = Path.Combine(Path.GetTempPath(), $"lager-wp04-{Guid.NewGuid():N}.mjs");
        File.WriteAllText(file, script);
        try
        {
            var psi = new ProcessStartInfo("node")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(file);
            foreach (var arg in args) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            p.WaitForExit();
            Assert.True(p.ExitCode == 0, $"node beendete sich mit {p.ExitCode}: {stderr.Result}");
            return JsonDocument.Parse(stdout.Result);
        }
        finally
        {
            File.Delete(file);
        }
    }
}

/// <summary>
/// [Fact], das ohne passendes Node (>= 22.18) als "übersprungen" gemeldet wird, statt wirkungslos grün
/// durchzulaufen (xUnit 2 hat kein Skip zur Laufzeit, aber ein Attribut kann Skip im Konstruktor setzen).
/// </summary>
public sealed class NodeFactAttribute : FactAttribute
{
    public NodeFactAttribute()
    {
        if (!FrontendFixture.IsNodeUsable)
            Skip = "Node >= 22.18 (bzw. 23.6 / 24) im PATH nötig, um die Frontend-Module direkt auszuführen.";
    }
}
