using System.Text;
using System.Text.RegularExpressions;

namespace Lager.Tests.WP18;

/// <summary>
/// Gemeinsame Helfer der Doku-Tests: Repository-Root, Markdown-Seiten des Pakets, Link- und Anker-Auswertung nach der
/// GitHub-Regel. Die Tests prüfen bewusst nur die Richtung "Doku -> Code" (was die Doku behauptet, muss es geben), damit
/// ein neuer Endpunkt oder Konfigurationsschlüssel anderer Pakete diese Tests nicht bricht.
/// </summary>
internal static class DocsRepo
{
    public static readonly string Root = FindRoot();

    /// <summary>Die Markdown-Seiten dieses Pakets (README, TODO und die Doku unter docs/).</summary>
    public static readonly string[] OwnedPages =
    {
        "README.md",
        "README.en.md",
        "TODO.md",
        "docs/ARCHITECTURE.md",
        "docs/GETTING_STARTED.md",
        "docs/USAGE.md",
        "docs/CONFIGURATION.md",
        "docs/API.md",
        "docs/DATA_MODEL.md",
        "docs/TROUBLESHOOTING.md",
    };

    public static IEnumerable<object[]> OwnedPageData() => OwnedPages.Select(p => new object[] { p });

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lager.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository-Root (Lager.sln) nicht gefunden.");
    }

    public static string FullPath(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public static string Read(string relative) => File.ReadAllText(FullPath(relative));

    /// <summary>Entfernt Code-Blöcke und Inline-Code, damit Beispiele darin nicht als Links zählen.</summary>
    public static string WithoutCode(string markdown)
    {
        var sb = new StringBuilder();
        var inBlock = false;
        foreach (var line in markdown.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inBlock = !inBlock;
                continue;
            }
            if (!inBlock) sb.AppendLine(Regex.Replace(line, "`[^`]*`", string.Empty));
        }
        return sb.ToString();
    }

    /// <summary>Alle Markdown-Links und Bilder <c>[Text](Ziel)</c> außerhalb von Code.</summary>
    public static IEnumerable<string> Links(string markdown)
    {
        foreach (Match m in Regex.Matches(WithoutCode(markdown), @"\[[^\]]*\]\(([^)\s]+)(?:\s+""[^""]*"")?\)"))
            yield return m.Groups[1].Value;
    }

    public static bool IsExternal(string link) =>
        link.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || link.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || link.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Anker aller Überschriften nach der GitHub-Regel: klein, Buchstaben, Ziffern, "-" und "_" bleiben, Leerzeichen werden
    /// zu "-", alles andere (Satzzeichen, Emojis) entfällt; doppelte Überschriften bekommen "-1", "-2" ...
    /// </summary>
    public static HashSet<string> HeadingAnchors(string markdown)
    {
        var anchors = new HashSet<string>();
        var seen = new Dictionary<string, int>();
        var inBlock = false;
        foreach (var line in markdown.Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                inBlock = !inBlock;
                continue;
            }
            if (inBlock) continue;

            var m = Regex.Match(line.TrimEnd('\r'), @"^#{1,6}\s+(.+?)\s*#*\s*$");
            if (!m.Success) continue;

            var text = Regex.Replace(m.Groups[1].Value, @"\[([^\]]*)\]\([^)]*\)", "$1");
            var slug = new StringBuilder();
            foreach (var c in text.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c) || c is '-' or '_') slug.Append(c);
                else if (c == ' ') slug.Append('-');
            }

            var baseSlug = slug.ToString();
            var n = seen.GetValueOrDefault(baseSlug);
            seen[baseSlug] = n + 1;
            anchors.Add(n == 0 ? baseSlug : $"{baseSlug}-{n}");
        }
        return anchors;
    }

    /// <summary>Zeilen einer Markdown-Tabelle, die mit <paramref name="prefix"/> beginnen (z. B. "| GET |"), als Zellen.</summary>
    public static IEnumerable<string[]> TableRows(string markdown, Func<string, bool> filter)
    {
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (!line.StartsWith('|') || !filter(line)) continue;
            yield return line.Trim('|').Split('|').Select(c => c.Trim()).ToArray();
        }
    }
}
