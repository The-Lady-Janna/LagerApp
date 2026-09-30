using System.Text.RegularExpressions;

namespace Lager.Tests.WP18;

/// <summary>
/// Links und Verweise der Doku: relative Links samt Überschrift-Ankern lösen auf, Anker aus anderen Repository-Dateien
/// (CONTRIBUTING, SECURITY ...) in die Doku existieren noch, und Dateipfade, die die Doku in Backticks nennt, gibt es.
/// </summary>
public class DocsLinkTests
{
    public static IEnumerable<object[]> Pages() => DocsRepo.OwnedPageData();

    [Theory]
    [MemberData(nameof(Pages))]
    public void Relative_links_and_anchors_resolve(string page)
    {
        var file = DocsRepo.FullPath(page);
        var dir = Path.GetDirectoryName(file)!;
        var problems = new List<string>();

        foreach (var link in DocsRepo.Links(File.ReadAllText(file)))
        {
            if (DocsRepo.IsExternal(link)) continue;

            var hash = link.IndexOf('#');
            var path = hash >= 0 ? link[..hash] : link;
            var anchor = hash >= 0 ? link[(hash + 1)..] : null;

            var target = path.Length == 0 ? file : Path.GetFullPath(Path.Combine(dir, Uri.UnescapeDataString(path)));
            if (!File.Exists(target) && !Directory.Exists(target))
            {
                problems.Add($"Link '{link}' zeigt auf eine nicht vorhandene Datei");
                continue;
            }

            if (!string.IsNullOrEmpty(anchor) && File.Exists(target) && target.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                && !DocsRepo.HeadingAnchors(File.ReadAllText(target)).Contains(Uri.UnescapeDataString(anchor).ToLowerInvariant()))
                problems.Add($"Link '{link}': Anker '#{anchor}' existiert in {Path.GetFileName(target)} nicht");
        }

        Assert.True(problems.Count == 0, $"{page}:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    [Theory]
    [InlineData("CONTRIBUTING.md")]
    [InlineData("SECURITY.md")]
    [InlineData("CODE_OF_CONDUCT.md")]
    [InlineData("CHANGELOG.md")]
    [InlineData(".github/PULL_REQUEST_TEMPLATE.md")]
    [InlineData(".github/ISSUE_TEMPLATE/bug_report.md")]
    [InlineData(".github/ISSUE_TEMPLATE/feature_request.md")]
    public void Anchors_into_the_docs_from_other_repository_files_still_exist(string source)
    {
        // Diese Dateien gehören anderen Paketen und verlinken auf Überschriften der Doku (z. B. SECURITY.md ->
        // GETTING_STARTED.md#7-production-setup-kurz). Wer eine solche Überschrift umbenennt, bricht hier den Verweis.
        var file = DocsRepo.FullPath(source);
        Assert.True(File.Exists(file), $"{source} fehlt");
        var dir = Path.GetDirectoryName(file)!;
        var problems = new List<string>();

        foreach (var link in DocsRepo.Links(File.ReadAllText(file)))
        {
            var hash = link.IndexOf('#');
            if (DocsRepo.IsExternal(link) || hash < 0 || hash == link.Length - 1) continue;

            var target = Path.GetFullPath(Path.Combine(dir, Uri.UnescapeDataString(link[..hash])));
            var isOwned = DocsRepo.OwnedPages.Any(p => string.Equals(Path.GetFullPath(DocsRepo.FullPath(p)), target, StringComparison.OrdinalIgnoreCase));
            if (!isOwned) continue;

            var anchor = Uri.UnescapeDataString(link[(hash + 1)..]).ToLowerInvariant();
            if (!DocsRepo.HeadingAnchors(File.ReadAllText(target)).Contains(anchor))
                problems.Add($"'{link}': Anker '#{anchor}' existiert in {Path.GetFileName(target)} nicht");
        }

        Assert.True(problems.Count == 0, $"{source}:{Environment.NewLine}{string.Join(Environment.NewLine, problems)}");
    }

    /// <summary>Pfade, die zur Laufzeit entstehen, Beispiele oder Platzhalter sind und nicht im Repository liegen.</summary>
    private static readonly string[] RuntimeOrExamplePaths =
    {
        "docs/screenshots", "/dist", "/node_modules", "/bin/", "/obj/", "/logs", ".db", ".env", "publish/", "/publish",
        "SupplierPriceList", // Beispiel-Entität der Anleitung "Eine neue Domain-Entität end-to-end bauen"
    };

    [Theory]
    [MemberData(nameof(Pages))]
    public void Repository_paths_named_in_backticks_exist(string page)
    {
        var text = DocsRepo.Read(page);
        var problems = new List<string>();

        foreach (Match m in Regex.Matches(text, "`([^`\\n]+)`"))
        {
            var span = m.Groups[1].Value.Trim();
            if (!Regex.IsMatch(span, @"^(src|tests|frontend|docs|\.github)/[A-Za-z0-9_./-]+$")
                && !Regex.IsMatch(span, @"^(README(\.en)?\.md|TODO\.md|CHANGELOG\.md|CONTRIBUTING\.md|SECURITY\.md|CODE_OF_CONDUCT\.md|global\.json|Lager\.sln|Directory\.Build\.props)$"))
                continue;
            if (RuntimeOrExamplePaths.Any(x => span.Contains(x, StringComparison.OrdinalIgnoreCase))) continue;

            var path = DocsRepo.FullPath(span.TrimEnd('/'));
            if (!File.Exists(path) && !Directory.Exists(path))
                problems.Add($"`{span}` existiert nicht");
        }

        Assert.True(problems.Count == 0, $"{page}:{Environment.NewLine}{string.Join(Environment.NewLine, problems.Distinct())}");
    }
}
