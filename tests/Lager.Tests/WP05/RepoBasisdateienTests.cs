using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace Lager.Tests.WP05;

/// <summary>
/// Prüft die Repo-Basisdateien für die Veröffentlichung (.gitignore, .gitattributes, .editorconfig,
/// SECURITY, CONTRIBUTING, CODE_OF_CONDUCT, CHANGELOG, Issue-/PR-Vorlagen). Reine Dateitests, kein Host.
/// Der Repository-Root wird über die Datei <c>Lager.sln</c> gefunden.
/// </summary>
public class RepoBasisdateienTests
{
    private static readonly string Root = FindRepoRoot();

    /// <summary>Dateien, die dieses Paket neu anlegt (relativ zum Repo-Root, mit /).</summary>
    private static readonly string[] NeueDateien =
    {
        ".gitattributes",
        ".editorconfig",
        "SECURITY.md",
        "CONTRIBUTING.md",
        "CODE_OF_CONDUCT.md",
        "CHANGELOG.md",
        ".github/ISSUE_TEMPLATE/bug_report.md",
        ".github/ISSUE_TEMPLATE/feature_request.md",
        ".github/ISSUE_TEMPLATE/config.yml",
        ".github/PULL_REQUEST_TEMPLATE.md",
    };

    /// <summary>Markdown-Dateien, deren relative Links (inkl. Überschrift-Anker) auflösbar sein müssen.</summary>
    private static readonly string[] MarkdownMitLinks =
    {
        "README.md",
        "SECURITY.md",
        "CONTRIBUTING.md",
        "CODE_OF_CONDUCT.md",
        "CHANGELOG.md",
        ".github/PULL_REQUEST_TEMPLATE.md",
        ".github/ISSUE_TEMPLATE/bug_report.md",
        ".github/ISSUE_TEMPLATE/feature_request.md",
    };

    [Fact]
    public void Alle_Basisdateien_existieren_und_sind_nicht_leer()
    {
        var fehler = NeueDateien.Append(".gitignore")
            .Where(p => !File.Exists(Pfad(p)) || new FileInfo(Pfad(p)).Length == 0)
            .ToList();

        Assert.True(fehler.Count == 0, "Fehlende oder leere Dateien: " + string.Join(", ", fehler));
    }

    [Fact]
    public void Neue_Textdateien_sind_LF_ohne_BOM_und_enden_mit_Zeilenumbruch()
    {
        var fehler = new List<string>();
        foreach (var rel in NeueDateien.Append(".gitignore"))
        {
            var bytes = File.ReadAllBytes(Pfad(rel));
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                fehler.Add($"{rel}: enthält ein UTF-8-BOM");
            if (Array.IndexOf(bytes, (byte)'\r') >= 0)
                fehler.Add($"{rel}: enthält CR (CRLF-Zeilenenden)");
            if (bytes.Length > 0 && bytes[^1] != (byte)'\n')
                fehler.Add($"{rel}: endet nicht mit einem Zeilenumbruch");
        }

        Assert.True(fehler.Count == 0, string.Join(Environment.NewLine, fehler));
    }

    [Fact]
    public void Relative_Markdown_Links_und_Anker_zeigen_auf_Vorhandenes()
    {
        var fehler = new List<string>();

        foreach (var rel in MarkdownMitLinks)
        {
            var datei = Pfad(rel);
            Assert.True(File.Exists(datei), $"{rel} fehlt");
            var verzeichnis = Path.GetDirectoryName(datei)!;

            foreach (var link in RelativeLinks(File.ReadAllText(datei)))
            {
                var hash = link.IndexOf('#');
                var zielPfad = hash >= 0 ? link[..hash] : link;
                var anker = hash >= 0 ? link[(hash + 1)..] : null;

                // Reiner Anker (#abschnitt) verweist auf die eigene Datei.
                var ziel = zielPfad.Length == 0 ? datei : Path.GetFullPath(Path.Combine(verzeichnis, Uri.UnescapeDataString(zielPfad)));

                if (!File.Exists(ziel) && !Directory.Exists(ziel))
                {
                    fehler.Add($"{rel}: Link '{link}' zeigt auf eine nicht vorhandene Datei");
                    continue;
                }

                // Überschrift-Anker werden nur in Dateien dieses Pakets geprüft. Die Doku unter docs/ und das README
                // werden von späteren Paketen umgeschrieben; ein umbenannter Abschnitt dort darf diesen Test nicht
                // brechen (ein toter Anker lädt auf GitHub trotzdem die richtige Datei, nur ohne Sprung).
                if (!string.IsNullOrEmpty(anker) && File.Exists(ziel) && GehoertZuDiesemPaket(ziel))
                {
                    if (!UeberschriftAnker(File.ReadAllText(ziel)).Contains(anker.ToLowerInvariant()))
                        fehler.Add($"{rel}: Link '{link}' - Anker '#{anker}' existiert in {Path.GetFileName(ziel)} nicht");
                }
            }
        }

        Assert.True(fehler.Count == 0, string.Join(Environment.NewLine, fehler));
    }

    [Fact]
    public void Neue_Dateien_enthalten_nur_Platzhalter_statt_Kontakt_Passwort_oder_Lizenzaussage()
    {
        var fehler = new List<string>();
        var email = new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
        var lizenz = new Regex(@"licensed under|lizenziert unter|MIT License|Apache License|GNU General Public|BSD License", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        foreach (var rel in NeueDateien)
        {
            var text = File.ReadAllText(Pfad(rel));
            foreach (Match m in email.Matches(text))
                fehler.Add($"{rel}: echte E-Mail-Adresse '{m.Value}'");
            if (text.Contains("ChangeMe", StringComparison.OrdinalIgnoreCase))
                fehler.Add($"{rel}: nennt das Standard-Passwort");
            if (lizenz.IsMatch(text))
                fehler.Add($"{rel}: enthält eine Lizenzaussage (Lizenz bleibt Eigentümer-Entscheidung)");
        }

        Assert.True(fehler.Count == 0, string.Join(Environment.NewLine, fehler));
        // Kontakte laufen über GitHub (kein erfundenes Postfach): kein Platzhalter mehr, stattdessen der Verweis auf das Profil.
        var security = File.ReadAllText(Pfad("SECURITY.md"));
        var conduct = File.ReadAllText(Pfad("CODE_OF_CONDUCT.md"));
        Assert.DoesNotContain("<SECURITY-KONTAKT>", security);
        Assert.DoesNotContain("<CoC-KONTAKT>", conduct);
        Assert.Contains("Report a vulnerability", security);
        Assert.Contains("https://github.com/The-Lady-Janna", security);
        Assert.Contains("https://github.com/The-Lady-Janna", conduct);
    }

    [Fact]
    public void Issue_Vorlagen_und_PR_Vorlage_sind_gueltig_aufgebaut()
    {
        foreach (var rel in new[] { ".github/ISSUE_TEMPLATE/bug_report.md", ".github/ISSUE_TEMPLATE/feature_request.md" })
        {
            var text = File.ReadAllText(Pfad(rel));
            Assert.StartsWith("---\n", text);
            var ende = text.IndexOf("\n---\n", 4, StringComparison.Ordinal);
            Assert.True(ende > 0, $"{rel}: Front-Matter nicht geschlossen");
            var frontMatter = text[..ende];
            Assert.Contains("\nname: ", frontMatter);
            Assert.Contains("\nabout: ", frontMatter);
        }

        var config = File.ReadAllText(Pfad(".github/ISSUE_TEMPLATE/config.yml"));
        Assert.Contains("blank_issues_enabled: true", config);
        Assert.Contains("SECURITY.md", config);

        var pr = File.ReadAllText(Pfad(".github/PULL_REQUEST_TEMPLATE.md"));
        foreach (var pflicht in new[] { "dotnet build", "dotnet test", "npm run lint", "npm run build", "CHANGELOG" })
            Assert.Contains(pflicht, pr);
    }

    [Fact]
    public void Editorconfig_und_Gitattributes_enthalten_die_Kernregeln()
    {
        var editorconfig = File.ReadAllText(Pfad(".editorconfig"));
        Assert.Contains("root = true", editorconfig);
        Assert.Contains("charset = utf-8", editorconfig);
        Assert.Contains("insert_final_newline = true", editorconfig);
        Assert.Contains("trim_trailing_whitespace = true", editorconfig);
        Assert.Matches(@"\[\*\.md\]\s+trim_trailing_whitespace = false", editorconfig);
        Assert.Matches(@"\[\*\.sln\][^\[]*end_of_line = crlf", editorconfig);
        // file-scoped Namespaces nur als Hinweis, nie als Warnung/Fehler erzwingen (sonst bricht ein Build mit TreatWarningsAsErrors).
        Assert.DoesNotMatch(@"csharp_style_namespace_declarations\s*=\s*\S+:(warning|error)", editorconfig);

        var attributes = File.ReadAllLines(Pfad(".gitattributes"))
            .Select(l => Regex.Replace(l.Trim(), @"\s+", " "))
            .ToList();
        Assert.Contains("* text=auto eol=lf", attributes);
        Assert.Contains("*.sh text eol=lf", attributes);
        Assert.Contains("*.sln text eol=crlf", attributes);
        foreach (var endung in new[] { "png", "ico", "woff2", "pdf" })
            Assert.Contains($"*.{endung} binary", attributes);
    }

    [Fact]
    public void Gitignore_schliesst_Secrets_Datenbanken_und_Artefakte_aus_und_laesst_env_example_zu()
    {
        string[] ignoriert =
        {
            "src/Lager.Api/appsettings.Production.json",
            "src/Lager.Api/appsettings.Staging.local.json",
            ".env",
            ".env.production",
            "frontend/lager-ui/.env.local",
            "src/Lager.Api/lager.db",
            "src/Lager.Api/lager.db-wal",
            "src/Lager.Api/lager.db-shm",
            "src/Lager.Api/lager.sqlite",
            "src/Lager.Api/lager-backup-20260101.sqlite",
            "src/Lager.Api/lager.sqlite3",
            "src/Lager.Api/logs/lager-20260101.log",
            "frontend/lager-ui/node_modules/x/index.js",
            "src/Lager.Api/bin/Debug/Lager.Api.dll",
            "src/Lager.Api/obj/project.assets.json",
            "frontend/lager-ui/dist/index.html",
            "jwt.key",
            "certs/server.pfx",
            "certs/server.pem",
            "certs/server.key",
            "tests/Lager.Tests/TestResults/ergebnis.trx",
            "coverage/cobertura.xml",
            "publish/Lager.Api.dll",
            ".claude/settings.local.json",
            "src/Lager.Api/Lager.Api.csproj.user",
        };
        string[] versioniert =
        {
            ".env.example",
            "frontend/lager-ui/.env.example",
            "src/Lager.Api/appsettings.json",
            "src/Lager.Api/appsettings.Development.json",
            "src/Lager.Api/Program.cs",
            "SECURITY.md",
            ".github/PULL_REQUEST_TEMPLATE.md",
        };

        var temp = Path.Combine(Path.GetTempPath(), $"lager-gitignore-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        try
        {
            if (RunGit(temp, "init", "-q") is null)
                return; // git ist nicht installiert: nichts zu prüfen (in CI und auf Entwicklerrechnern ist git vorhanden)

            File.Copy(Pfad(".gitignore"), Path.Combine(temp, ".gitignore"));
            foreach (var rel in ignoriert.Concat(versioniert))
            {
                var datei = Path.Combine(temp, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(datei)!);
                File.WriteAllText(datei, "x");
            }

            // check-ignore gibt genau die ignorierten Pfade aus (Exit-Code 1 = keiner ignoriert).
            var ausgabe = RunGit(temp, ["check-ignore", "--", .. ignoriert, .. versioniert]);
            Assert.NotNull(ausgabe);
            var tatsaechlich = ausgabe.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();

            var nichtIgnoriert = ignoriert.Where(p => !tatsaechlich.Contains(p)).ToList();
            var faelschlichIgnoriert = versioniert.Where(tatsaechlich.Contains).ToList();

            Assert.True(nichtIgnoriert.Count == 0, "Werden nicht ignoriert: " + string.Join(", ", nichtIgnoriert));
            Assert.True(faelschlichIgnoriert.Count == 0, "Werden fälschlich ignoriert: " + string.Join(", ", faelschlichIgnoriert));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ---------- Hilfsfunktionen ----------

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lager.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository-Root (Lager.sln) nicht gefunden.");
    }

    private static string Pfad(string relativ) => Path.Combine(Root, relativ.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>True, wenn die Markdown-Datei zu den in diesem Paket angelegten Dateien gehört.</summary>
    private static bool GehoertZuDiesemPaket(string vollerPfad) =>
        vollerPfad.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        && NeueDateien.Any(n => string.Equals(Path.GetFullPath(Pfad(n)), Path.GetFullPath(vollerPfad), StringComparison.OrdinalIgnoreCase));

    /// <summary>Entfernt Code-Blöcke und Inline-Code, damit Beispiele in Codeblöcken nicht als Links zählen.</summary>
    private static string OhneCode(string markdown)
    {
        var sb = new StringBuilder();
        var imBlock = false;
        foreach (var zeile in markdown.Split('\n'))
        {
            if (zeile.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                imBlock = !imBlock;
                continue;
            }
            if (!imBlock)
                sb.AppendLine(Regex.Replace(zeile, "`[^`]*`", string.Empty));
        }
        return sb.ToString();
    }

    private static IEnumerable<string> RelativeLinks(string markdown)
    {
        foreach (Match m in Regex.Matches(OhneCode(markdown), @"\[[^\]]*\]\(([^)\s]+)(?:\s+""[^""]*"")?\)"))
        {
            var ziel = m.Groups[1].Value;
            if (ziel.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || ziel.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || ziel.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
                continue;
            yield return ziel;
        }
    }

    /// <summary>Anker aller Überschriften nach der GitHub-Regel (klein, Satzzeichen weg, Leerzeichen zu Bindestrich).</summary>
    private static HashSet<string> UeberschriftAnker(string markdown)
    {
        var anker = new HashSet<string>();
        var imBlock = false;
        foreach (var zeile in markdown.Split('\n'))
        {
            if (zeile.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                imBlock = !imBlock;
                continue;
            }
            var m = imBlock ? null : Regex.Match(zeile, @"^#{1,6}\s+(.+?)\s*#*\s*$");
            if (m is not { Success: true })
                continue;

            var slug = new StringBuilder();
            foreach (var c in m.Groups[1].Value.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c) || c is '-' or '_')
                    slug.Append(c);
                else if (c == ' ')
                    slug.Append('-');
            }
            anker.Add(slug.ToString());
        }
        return anker;
    }

    /// <summary>
    /// Startet git ohne globale/System-Konfiguration (damit keine Benutzer-Ignore-Regeln das Ergebnis verfälschen)
    /// und liefert stdout. <c>null</c>, wenn git nicht gestartet werden kann.
    /// </summary>
    private static string? RunGit(string arbeitsVerzeichnis, params string[] argumente)
    {
        var leer = Path.Combine(arbeitsVerzeichnis, "..", $"leer-{Guid.NewGuid():N}");
        File.WriteAllText(leer, string.Empty);

        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = arbeitsVerzeichnis,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("core.excludesFile=" + Path.GetFullPath(leer).Replace('\\', '/'));
        foreach (var a in argumente)
            psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_GLOBAL"] = Path.GetFullPath(leer);
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["XDG_CONFIG_HOME"] = Path.GetFullPath(Path.Combine(arbeitsVerzeichnis, "..", "xdg-leer"));
        foreach (var v in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE" })
            psi.Environment.Remove(v);

        try
        {
            using var prozess = Process.Start(psi)!;
            var stdout = prozess.StandardOutput.ReadToEnd();
            var stderr = prozess.StandardError.ReadToEnd();
            prozess.WaitForExit();

            // check-ignore: 0 = mindestens ein Pfad ignoriert, 1 = keiner. Alles ab 2 ist ein echter Fehler.
            Assert.True(prozess.ExitCode is 0 or 1, $"git {string.Join(' ', argumente)} schlug fehl ({prozess.ExitCode}): {stderr}");
            return stdout;
        }
        catch (Win32Exception)
        {
            return null;
        }
        finally
        {
            try { File.Delete(leer); } catch (IOException) { }
        }
    }
}
