using System.Globalization;
using System.Text.RegularExpressions;

namespace Lager.Tests.WP33;

/// <summary>
/// Kein Test darf an einer knappen Wanduhr-Grenze hängen: Auf einem langsamen oder ausgelasteten Rechner (CI, paralleler
/// <c>npm test</c>, Virenscanner) kippt sie, ohne dass im Code etwas kaputt ist. Wer wirklich gegen ein Hängen schützen will,
/// nimmt eine großzügige Grenze (mindestens <see cref="MinimumLimit"/>); Korrektheit prüft man am Ergebnis, nicht an der Dauer.
/// Dieser Test liest die Testquellen und meldet jede wörtliche Grenze darunter (<c>Elapsed &lt; ...</c>, <c>ElapsedMilliseconds
/// &lt; ...</c>, <c>WaitAsync(...)</c>). Variablen und Konstanten wertet er nicht aus - sie sind der vorgesehene Weg für Grenzen
/// mit einer Begründung im Kommentar.
/// </summary>
public class NoHardWallClockLimitsTests
{
    private static readonly TimeSpan MinimumLimit = TimeSpan.FromSeconds(10);

    // "Elapsed < TimeSpan.FromSeconds(2)" / "Elapsed <= TimeSpan.FromMilliseconds(500)" / "ElapsedMilliseconds < 2000"
    private static readonly Regex ElapsedComparison = new(
        @"Elapsed(?<ms>Milliseconds)?\s*<=?\s*(?:TimeSpan\s*\.\s*From(?<unit>Seconds|Milliseconds|Minutes)\s*\(\s*(?<n>\d[\d_]*(?:\.\d+)?)|(?<plain>\d[\d_]*))",
        RegexOptions.Compiled);

    // "WaitAsync(TimeSpan.FromSeconds(5))": ein hartes Zeitlimit als Testlogik
    private static readonly Regex WaitLimit = new(
        @"\.WaitAsync\s*\(\s*TimeSpan\s*\.\s*From(?<unit>Seconds|Milliseconds|Minutes)\s*\(\s*(?<n>\d[\d_]*(?:\.\d+)?)",
        RegexOptions.Compiled);

    private static string TestsDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lager.sln")))
            dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("Lager.sln oberhalb von " + AppContext.BaseDirectory + " nicht gefunden");
        return Path.Combine(root, "tests", "Lager.Tests");
    }

    /// <summary>Die wörtlichen Zeitgrenzen einer Zeile unter <see cref="MinimumLimit"/>; leer, wenn keine oder nur großzügige vorkommen.</summary>
    internal static IReadOnlyList<TimeSpan> TooTightLimits(string line)
    {
        var code = line.Split("//", 2)[0]; // Kommentare erwähnen Grenzen gern als Begründung
        var found = new List<TimeSpan>();

        foreach (Match match in ElapsedComparison.Matches(code))
        {
            if (match.Groups["plain"].Success)
                found.Add(TimeSpan.FromMilliseconds(Number(match.Groups["plain"].Value))); // ElapsedMilliseconds < 2000
            else
                found.Add(Span(match.Groups["unit"].Value, match.Groups["n"].Value));
        }
        foreach (Match match in WaitLimit.Matches(code))
            found.Add(Span(match.Groups["unit"].Value, match.Groups["n"].Value));

        return found.Where(limit => limit < MinimumLimit).ToList();
    }

    private static double Number(string text) => double.Parse(text.Replace("_", ""), CultureInfo.InvariantCulture);

    private static TimeSpan Span(string unit, string number) => unit switch
    {
        "Milliseconds" => TimeSpan.FromMilliseconds(Number(number)),
        "Minutes" => TimeSpan.FromMinutes(Number(number)),
        _ => TimeSpan.FromSeconds(Number(number)),
    };

    [Theory]
    [InlineData("Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), \"x\");", true)]
    [InlineData("Assert.True(watch.Elapsed <= TimeSpan.FromMilliseconds(500));", true)]
    [InlineData("Assert.True(watch.ElapsedMilliseconds < 2000, msg);", true)]
    [InlineData("var r = await call.WaitAsync(TimeSpan.FromSeconds(5));", true)]
    [InlineData("Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30));", false)]
    [InlineData("Assert.True(watch.ElapsedMilliseconds < 60_000);", false)]
    [InlineData("var r = await call.WaitAsync(TimeSpan.FromSeconds(60));", false)]
    [InlineData("Assert.True(watch.Elapsed < HangGuard, msg);", false)]            // Konstante: mit Begründung an der Stelle
    [InlineData("Assert.True(x, \"vorher ging es\"); // Elapsed < TimeSpan.FromSeconds(2)", false)] // nur im Kommentar
    public void The_detector_flags_literal_limits_below_ten_seconds(string line, bool flagged)
    {
        Assert.Equal(flagged, TooTightLimits(line).Count > 0);
    }

    [Fact]
    public void No_test_asserts_a_wall_clock_limit_below_ten_seconds()
    {
        var self = nameof(NoHardWallClockLimitsTests) + ".cs";
        var files = Directory.EnumerateFiles(TestsDirectory(), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => Path.GetFileName(f) != self)
            .ToList();
        Assert.True(files.Count > 50, $"Nur {files.Count} Testdateien gefunden - die Suche greift nicht");

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var limits = TooTightLimits(lines[i]);
                if (limits.Count > 0)
                    offenders.Add($"{Path.GetRelativePath(TestsDirectory(), file)}:{i + 1}: {lines[i].Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Knappe Wanduhr-Grenzen (unter " + MinimumLimit.TotalSeconds + " s) in Tests - großzügig wählen oder die Korrektheit statt der Dauer prüfen:"
            + Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }
}
