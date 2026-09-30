using System.Text.Json;
using System.Text.RegularExpressions;

namespace Lager.Tests.WP17;

/// <summary>
/// Prüft die CI-Konfiguration unter <c>.github/</c> (Workflows und Dependabot). Reine Dateitests, kein Host:
/// Die Workflows können erst nach dem Push auf GitHub laufen, deshalb sichern diese Tests die Vorgaben
/// (Rechte, Pinning, Befehle, Verweise auf vorhandene Dateien und npm-Skripte) schon lokal ab.
/// Der Repository-Root wird über die Datei <c>Lager.sln</c> gefunden. Die YAML-Dateien werden bewusst nur
/// textbasiert geprüft (keine YAML-Bibliothek im Testprojekt); die Syntax selbst prüft der Parser-Lauf aus dem Paket.
/// </summary>
public class CiWorkflowTests
{
    private static readonly string Root = FindRepoRoot();

    private static readonly string[] Workflows =
    {
        ".github/workflows/ci.yml",
        ".github/workflows/codeql.yml",
        ".github/workflows/docker.yml",
    };

    // ---------------------------------------------------------------- Dateien und Hygiene

    [Fact]
    public void Workflows_und_Dependabot_existieren_und_sind_LF_ohne_BOM_und_ohne_Tabs()
    {
        var fehler = new List<string>();
        foreach (var rel in Workflows.Append(".github/dependabot.yml"))
        {
            if (!File.Exists(Pfad(rel)))
            {
                fehler.Add($"{rel}: fehlt");
                continue;
            }

            var bytes = File.ReadAllBytes(Pfad(rel));
            if (bytes.Length == 0) fehler.Add($"{rel}: ist leer");
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                fehler.Add($"{rel}: enthält ein UTF-8-BOM");
            if (Array.IndexOf(bytes, (byte)'\r') >= 0)
                fehler.Add($"{rel}: enthält CR (CRLF-Zeilenenden)");
            if (Array.IndexOf(bytes, (byte)'\t') >= 0)
                fehler.Add($"{rel}: enthält Tabulatoren (YAML erlaubt nur Leerzeichen zum Einrücken)");
            if (bytes.Length > 0 && bytes[^1] != (byte)'\n')
                fehler.Add($"{rel}: endet nicht mit einem Zeilenumbruch");
        }

        Assert.True(fehler.Count == 0, string.Join(Environment.NewLine, fehler));
    }

    // ---------------------------------------------------------------- Sicherheit

    [Fact]
    public void Kein_Workflow_enthaelt_Secrets_Registry_Push_oder_pull_request_target()
    {
        var verboten = new (string Grund, Regex Muster)[]
        {
            ("pull_request_target läuft mit Schreibrechten im Kontext des Basis-Repositorys", new Regex(@"\bpull_request_target\b")),
            ("Secrets (nur das automatische GITHUB_TOKEN wäre erlaubt)", new Regex(@"\bsecrets\.(?!GITHUB_TOKEN\b)|\bsecrets:\s*inherit\b")),
            ("Registry-Login oder -Push für Container-Images", new Regex(@"\bdocker\s+(push|login)\b|docker/(login|build-push)-action|\bpush:\s*true\b|\bghcr\.io\b")),
            ("Veröffentlichung von Paketen", new Regex(@"\bnpm\s+publish\b|\bdotnet\s+nuget\s+push\b")),
        };

        var fehler = new List<string>();
        foreach (var datei in AlleWorkflowDateien())
        {
            var text = OhneKommentare(File.ReadAllText(datei));
            foreach (var (grund, muster) in verboten)
            {
                var treffer = muster.Match(text);
                if (treffer.Success)
                    fehler.Add($"{Path.GetFileName(datei)}: '{treffer.Value}' - {grund}");
            }
        }

        Assert.True(fehler.Count == 0, string.Join(Environment.NewLine, fehler));
    }

    [Fact]
    public void Workflows_haben_nur_Leserechte_und_nur_CodeQL_schreibt_Security_Events()
    {
        var fehler = new List<string>();
        foreach (var datei in AlleWorkflowDateien())
        {
            var name = Path.GetFileName(datei);
            var text = OhneKommentare(File.ReadAllText(datei));

            // Oberste Ebene: "permissions:" direkt gefolgt von "contents: read" (Standard-Token nur lesend).
            if (!Regex.IsMatch(text, @"(?m)^permissions:\s*\n\s+contents:\s*read\s*$"))
                fehler.Add($"{name}: es fehlt 'permissions: contents: read' auf oberster Ebene");

            if (Regex.IsMatch(text, @"write-all|permissions:\s*write\b"))
                fehler.Add($"{name}: vergibt pauschal Schreibrechte");

            foreach (Match m in Regex.Matches(text, @"(?m)^\s+([\w-]+):\s*write\s*$"))
            {
                var erlaubt = name == "codeql.yml" && m.Groups[1].Value == "security-events";
                if (!erlaubt)
                    fehler.Add($"{name}: unerwartetes Schreibrecht '{m.Groups[1].Value}: write'");
            }
        }

        // CodeQL braucht das Recht tatsächlich, sonst schlägt das Hochladen der Ergebnisse fehl.
        Assert.Matches(@"security-events:\s*write", OhneKommentare(Lies(".github/workflows/codeql.yml")));
        Assert.True(fehler.Count == 0, string.Join(Environment.NewLine, fehler));
    }

    [Fact]
    public void Actions_sind_auf_feste_Versionen_gepinnt_nicht_auf_Branches()
    {
        // Erlaubt: Major-Tag (@v4), genauer Tag (@v4.1.2) oder Commit-SHA. Nicht erlaubt: @main, @master, kein Suffix.
        var erlaubt = new Regex(@"^[\w.-]+/[\w./-]+@(v\d+(\.\d+){0,2}|[0-9a-f]{40})$");

        var fehler = new List<string>();
        var gesamt = 0;
        foreach (var datei in AlleWorkflowDateien())
        {
            foreach (Match m in Regex.Matches(OhneKommentare(File.ReadAllText(datei)), @"(?m)^\s*-?\s*uses:\s*(\S+)\s*$"))
            {
                gesamt++;
                var verweis = m.Groups[1].Value.Trim('"', '\'');
                if (!erlaubt.IsMatch(verweis))
                    fehler.Add($"{Path.GetFileName(datei)}: '{verweis}' ist nicht auf eine feste Version gepinnt");
            }
        }

        Assert.True(gesamt >= 6, $"Erwartet: mindestens 6 'uses:'-Zeilen in den Workflows, gefunden: {gesamt}");
        Assert.True(fehler.Count == 0, string.Join(Environment.NewLine, fehler));
    }

    // ---------------------------------------------------------------- ci.yml

    [Fact]
    public void Ci_Workflow_baut_strikt_testet_und_prueft_Abhaengigkeiten_in_zwei_unabhaengigen_Jobs()
    {
        var ci = OhneKommentare(Lies(".github/workflows/ci.yml"));

        // Trigger, Rechte, Abbruch veralteter Läufe
        Assert.Matches(@"(?m)^on:\s*\n\s+push:\s*\n\s+branches:\s*\[main\]\s*\n\s+pull_request:", ci);
        Assert.Matches(@"(?m)^concurrency:\s*\n(\s+.*\n)*?\s+cancel-in-progress:\s*true\s*$", ci);

        // Zwei Jobs ohne Abhängigkeit voneinander
        Assert.Matches(@"(?m)^  backend:\s*$", ci);
        Assert.Matches(@"(?m)^  frontend:\s*$", ci);
        Assert.DoesNotMatch(@"(?m)^\s+needs:", ci);

        // Backend: SDK aus global.json, strikter Build (auch beim Restore, sonst greifen die NU19xx-Meldungen nicht)
        Assert.Contains("global-json-file: global.json", ci);
        Assert.Contains("dotnet restore Lager.sln -p:LagerStrict=true", ci);
        Assert.Contains("dotnet build Lager.sln -c Release -p:LagerStrict=true --no-restore", ci);
        Assert.Contains("dotnet test tests/Lager.Tests -c Release --no-build", ci);
        Assert.Contains("XPlat Code Coverage", ci);
        Assert.Contains("--logger", ci);
        Assert.Contains("actions/upload-artifact@", ci);
        Assert.Contains("dotnet list Lager.sln package --vulnerable --include-transitive", ci);
        Assert.Contains("has the following vulnerable packages", ci);

        // Frontend: Matrix aus Node 20.19 und 22, npm-Cache, alle Prüfschritte in fester Reihenfolge
        Assert.Matches(@"node:\s*\[\s*'20\.19'\s*,\s*'22'\s*\]", ci);
        Assert.Contains("cache: npm", ci);
        Assert.Contains("working-directory: frontend/lager-ui", ci);
        var reihenfolge = new[] { "npm ci", "npm run lint", "npm run typecheck", "npm test", "npm run build", "npm audit --audit-level=high" };
        var letzte = -1;
        foreach (var befehl in reihenfolge)
        {
            var position = ci.IndexOf(befehl, StringComparison.Ordinal);
            Assert.True(position >= 0, $"ci.yml enthält '{befehl}' nicht");
            Assert.True(position > letzte, $"'{befehl}' steht in ci.yml vor einem Schritt, der davor laufen sollte");
            letzte = position;
        }
    }

    [Fact]
    public void Ci_Workflow_verweist_nur_auf_vorhandene_Dateien_und_npm_Skripte()
    {
        var ci = OhneKommentare(Lies(".github/workflows/ci.yml"));

        Assert.True(File.Exists(Pfad("Lager.sln")), "Lager.sln fehlt");
        Assert.True(File.Exists(Pfad("global.json")), "global.json fehlt (setup-dotnet liest die SDK-Version daraus)");
        Assert.True(Directory.Exists(Pfad("tests/Lager.Tests")), "tests/Lager.Tests fehlt");
        Assert.True(File.Exists(Pfad("frontend/lager-ui/package-lock.json")), "package-lock.json fehlt (npm ci und der npm-Cache brauchen sie)");
        Assert.Contains("cache-dependency-path: frontend/lager-ui/package-lock.json", ci);

        // LagerStrict muss in der zentralen Build-Konfiguration existieren, sonst wäre "-p:LagerStrict=true" wirkungslos.
        Assert.Contains("LagerStrict", File.ReadAllText(Pfad("Directory.Build.props")));

        // Jedes "npm run <skript>" und "npm test" muss in package.json definiert sein.
        using var paket = JsonDocument.Parse(File.ReadAllText(Pfad("frontend/lager-ui/package.json")));
        var skripte = paket.RootElement.GetProperty("scripts").EnumerateObject().Select(p => p.Name).ToHashSet();
        var benutzt = Regex.Matches(ci, @"\bnpm run ([\w:-]+)").Select(m => m.Groups[1].Value)
            .Concat(Regex.IsMatch(ci, @"\bnpm test\b") ? new[] { "test" } : Array.Empty<string>())
            .Distinct()
            .ToList();
        Assert.Contains("lint", benutzt);
        Assert.Contains("typecheck", benutzt);
        Assert.Contains("build", benutzt);
        Assert.Contains("test", benutzt);
        var fehlend = benutzt.Where(s => !skripte.Contains(s)).ToList();
        Assert.True(fehlend.Count == 0, "In package.json fehlen die npm-Skripte: " + string.Join(", ", fehlend));
    }

    // ---------------------------------------------------------------- codeql.yml und docker.yml

    [Fact]
    public void CodeQL_Workflow_analysiert_beide_Sprachen_bei_Push_PR_und_woechentlich()
    {
        var text = OhneKommentare(Lies(".github/workflows/codeql.yml"));

        Assert.Matches(@"(?m)^\s+push:\s*\n\s+branches:\s*\[main\]", text);
        Assert.Matches(@"(?m)^\s+pull_request:", text);
        // Wöchentlicher Cron: fünf Felder, Wochentag konkret gesetzt (kein "*")
        Assert.Matches(@"cron:\s*'[^']+'", text);
        Assert.Matches(@"cron:\s*'\S+ \S+ \S+ \S+ [0-7]'", text);

        // C# wird manuell gebaut, JavaScript/TypeScript braucht keinen Build
        Assert.Matches(@"language:\s*csharp\s*\n\s+build-mode:\s*manual", text);
        Assert.Matches(@"language:\s*javascript-typescript\s*\n\s+build-mode:\s*none", text);
        Assert.Contains("dotnet build Lager.sln", text);
        Assert.Contains("global-json-file: global.json", text);

        Assert.Matches(@"github/codeql-action/init@v\d+", text);
        Assert.Matches(@"github/codeql-action/analyze@v\d+", text);
    }

    [Fact]
    public void Docker_Workflow_baut_nur_bei_vorhandenem_Dockerfile_und_veroeffentlicht_nichts()
    {
        var text = OhneKommentare(Lies(".github/workflows/docker.yml"));

        // Läuft nur bei relevanten Änderungen (Push und Pull Request)
        foreach (var pfad in new[] { "'Dockerfile'", "'src/**'", "'frontend/**'" })
            Assert.Equal(2, Regex.Matches(text, @"-\s*" + Regex.Escape(pfad)).Count);

        // Fehlt das Dockerfile (etwa in einem Fork ohne Docker-Dateien), wird der Build-Schritt übersprungen statt rot zu werden
        Assert.Matches(@"if:\s*\$\{\{\s*hashFiles\('Dockerfile'\)\s*!=\s*''\s*\}\}\s*\n\s+run:\s*docker build\b", text);
        Assert.Contains("docker build", text);

        // Nur Build-Check: nichts wird übertragen
        Assert.DoesNotMatch(@"docker\s+(push|login)|--push|push:\s*true|build-push-action|login-action", text);
    }

    // ---------------------------------------------------------------- dependabot.yml

    [Fact]
    public void Dependabot_deckt_NuGet_npm_Actions_und_Docker_woechentlich_mit_Gruppen_ab()
    {
        var text = OhneKommentare(Lies(".github/dependabot.yml"));
        Assert.Matches(@"(?m)^version:\s*2\s*$", text);

        var bloecke = Regex.Split(text, @"(?m)^\s*-\s+package-ecosystem:\s*").Skip(1)
            .ToDictionary(b => b.Split('\n')[0].Trim().Trim('"'), b => b);

        Assert.Equal(new[] { "docker", "github-actions", "npm", "nuget" }, bloecke.Keys.OrderBy(k => k, StringComparer.Ordinal));

        var verzeichnisse = new Dictionary<string, string>
        {
            ["nuget"] = "/",
            ["npm"] = "/frontend/lager-ui",
            ["github-actions"] = "/",
            ["docker"] = "/",
        };

        foreach (var (name, block) in bloecke)
        {
            Assert.Matches($@"directory:\s*""{Regex.Escape(verzeichnisse[name])}""", block);
            Assert.True(Regex.IsMatch(block, @"interval:\s*weekly\b"), $"{name}: Intervall ist nicht wöchentlich");
            Assert.True(Regex.IsMatch(block, @"open-pull-requests-limit:\s*5\b"), $"{name}: open-pull-requests-limit ist nicht 5");
            Assert.True(Regex.IsMatch(block, @"commit-message:\s*\n\s+prefix:\s*deps\s*(\n|$)"), $"{name}: Commit-Präfix ist nicht 'deps'");
            Assert.True(Regex.IsMatch(block, @"groups:\s*\n(.*\n)*?\s+update-types:\s*\n\s+- minor\s*\n\s+- patch\b"), $"{name}: keine Gruppe für Minor- und Patch-Updates");
        }

        // Die Verzeichnisse müssen es geben (für docker ist es das Dockerfile im Root)
        Assert.True(File.Exists(Pfad("Lager.sln")), "Lager.sln fehlt (NuGet-Verzeichnis '/')");
        Assert.True(File.Exists(Pfad("Dockerfile")), "Dockerfile fehlt (docker-Verzeichnis '/')");
        Assert.True(File.Exists(Pfad("frontend/lager-ui/package.json")), "package.json fehlt (npm-Verzeichnis '/frontend/lager-ui')");
        Assert.True(Directory.Exists(Pfad(".github/workflows")), ".github/workflows fehlt (github-actions-Verzeichnis '/')");
    }

    // ---------------------------------------------------------------- Hilfsfunktionen

    /// <summary>Entfernt Kommentarzeilen und Zeilenendkommentare (" # ...") und normalisiert die Zeilenenden.</summary>
    private static string OhneKommentare(string yaml)
    {
        var zeilen = yaml.Replace("\r\n", "\n").Split('\n')
            .Where(z => !z.TrimStart().StartsWith('#'))
            .Select(z => Regex.Replace(z, @"\s+#.*$", ""));
        return string.Join('\n', zeilen);
    }

    private static IEnumerable<string> AlleWorkflowDateien()
    {
        var verzeichnis = Pfad(".github/workflows");
        Assert.True(Directory.Exists(verzeichnis), ".github/workflows fehlt");
        var dateien = Directory.EnumerateFiles(verzeichnis, "*.yml")
            .Concat(Directory.EnumerateFiles(verzeichnis, "*.yaml"))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(dateien);
        return dateien;
    }

    private static string Lies(string relativ) => File.ReadAllText(Pfad(relativ));

    private static string Pfad(string relativ) => Path.Combine(Root, relativ.Replace('/', Path.DirectorySeparatorChar));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lager.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository-Root (Lager.sln) nicht gefunden.");
    }
}
