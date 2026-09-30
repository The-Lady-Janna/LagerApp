using System.Text.RegularExpressions;

namespace Lager.Tests.WP16;

/// <summary>
/// Die Container-Dateien (Dockerfile, .dockerignore, Compose, Caddy, .env.example) werden ohne Docker geprüft: Ein Build
/// braucht Docker und Netz, aber die typischen Fehler - ein COPY auf eine Datei, die es nicht gibt, ein Daten-Pfad außerhalb
/// des Volumes, ein Secret in der Vorlage - lassen sich am Text erkennen.
/// </summary>
public class DockerArtifactsTests
{
    private static readonly string Root = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lager.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Lager.sln oberhalb von " + AppContext.BaseDirectory + " nicht gefunden");
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>Anweisungen ohne Kommentare, Fortsetzungszeilen (Backslash am Zeilenende) zu einer Zeile verbunden.</summary>
    private static List<string> Instructions(string dockerfile)
    {
        var joined = Regex.Replace(dockerfile, @"\\\r?\n", " ");
        return joined.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
    }

    [Fact]
    public void Dockerfile_builds_from_the_repo_root_without_extra_arguments_and_every_copied_source_exists()
    {
        var lines = Instructions(Read("Dockerfile"));

        // docker.yml führt "docker build ." ohne --build-arg aus: kein ARG ohne Vorgabewert.
        Assert.DoesNotContain(lines, l => Regex.IsMatch(l, @"^ARG\s+\w+\s*$"));

        // Jede COPY-Quelle aus dem Build-Kontext (nicht aus einer früheren Stufe) muss im Repo liegen.
        var copies = lines.Where(l => l.StartsWith("COPY ", StringComparison.Ordinal) && !l.Contains("--from=")).ToList();
        Assert.NotEmpty(copies);
        foreach (var copy in copies)
        {
            var parts = copy.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Where(p => !p.StartsWith("--")).ToList();
            foreach (var source in parts.Take(parts.Count - 1))
            {
                var path = Path.Combine(Root, source.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path) || Directory.Exists(path), $"COPY-Quelle '{source}' fehlt im Repository ({copy})");
            }
        }
    }

    [Fact]
    public void Dockerfile_has_the_three_stages_native_libraries_a_non_root_user_and_keeps_all_data_on_the_volume()
    {
        var text = Read("Dockerfile");
        var lines = Instructions(text);

        // Frontend bauen -> API veröffentlichen -> Laufzeit-Image mit dem Frontend in wwwroot.
        Assert.Matches(@"FROM node:\S+ AS frontend", text);
        Assert.Contains("npm ci", text);
        Assert.Contains("npm run build", text);
        Assert.Matches(@"FROM mcr\.microsoft\.com/dotnet/sdk:8\.0 AS \w+", text);
        Assert.Contains("dotnet publish src/Lager.Api", text);
        Assert.Matches(@"FROM mcr\.microsoft\.com/dotnet/aspnet:8\.0", text);
        Assert.Matches(@"COPY --from=frontend \S+/dist \./wwwroot", text);

        // QuestPDF/SkiaSharp (PDF) und der Healthcheck brauchen fontconfig bzw. curl im Laufzeit-Image.
        Assert.Contains("libfontconfig1", text);
        Assert.Contains("curl", text);

        // Kein root: USER gesetzt, und /data gehört diesem Benutzer.
        Assert.Contains(lines, l => Regex.IsMatch(l, @"^USER\s+(\$APP_UID|\d+|app)\b"));
        Assert.Contains(lines, l => l.StartsWith("VOLUME", StringComparison.Ordinal) && l.Contains("/data"));
        Assert.Contains(lines, l => l.StartsWith("EXPOSE 8080", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("HEALTHCHECK", StringComparison.Ordinal) && l.Contains("/health/ready"));

        // Produktions-Vorgaben: alles Veränderliche (Datenbank, JWT-Key, Logs, Backups) liegt auf dem Volume.
        var env = string.Join(" ", lines.Where(l => l.StartsWith("ENV ", StringComparison.Ordinal)));
        Assert.Contains("ASPNETCORE_ENVIRONMENT=Production", env);
        Assert.Contains("ASPNETCORE_URLS=http://+:8080", env);
        Assert.Contains("Database__ConnectionString=\"Data Source=/data/lager.db\"", env);
        Assert.Contains("Jwt__KeyFile=/data/jwt.key", env);
        Assert.Contains("Logging__Directory=/data/logs", env);
        Assert.Contains("Backup__Directory=/data/backups", env);
        // Und kein Secret im Image.
        Assert.DoesNotContain("Jwt__SigningKey", env);
        Assert.DoesNotContain("BootstrapAdminPassword", env);
    }

    [Fact]
    public void Dockerignore_keeps_local_build_output_databases_logs_and_secrets_out_of_the_image()
    {
        var entries = Read(".dockerignore").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();

        foreach (var required in new[] { "**/node_modules/", "**/bin/", "**/obj/", "**/*.db", "**/logs/", ".git/", ".env" })
            Assert.Contains(required, entries);

        // Nichts, was das Dockerfile per COPY braucht, darf ausgeschlossen sein.
        foreach (var needed in new[] { "src", "frontend", "Directory.Build.props" })
            Assert.DoesNotContain(entries, e => e.TrimEnd('/') == needed);
    }

    [Fact]
    public void Compose_files_start_lager_with_a_data_volume_a_ready_healthcheck_and_optional_mysql_and_https()
    {
        var compose = Read("docker-compose.yml");
        Assert.Contains("build: .", compose);
        Assert.Contains("\"${LAGER_PORT:-8080}:8080\"", compose);
        Assert.Contains("lager-data:/data", compose);
        Assert.Contains("restart: unless-stopped", compose);
        Assert.Contains("/health/ready", compose);
        // Einstellungen aus .env werden namentlich durchgereicht (ohne .env startet es trotzdem), nie die ganze Datei:
        // sonst gelangte LAGER_DB_ROOT_PASSWORD in den Lager-Container.
        Assert.DoesNotMatch(@"(?m)^\s*env_file:", compose);
        Assert.Matches(@"(?m)^\s+Auth__BootstrapAdminPassword:\s*$", compose);
        Assert.Matches(@"(?m)^\s+Jwt__SigningKey:\s*$", compose);
        // Der Healthcheck geht über localhost: der Host bleibt in AllowedHosts, auch wenn der Betreiber eigene Namen einträgt.
        Assert.Contains("AllowedHosts: \"${AllowedHosts:-localhost};localhost;127.0.0.1\"", compose);
        Assert.Contains("profiles: [\"https\"]", compose);
        Assert.Contains("./deploy/Caddyfile", compose);

        var mysql = Read("docker-compose.mysql.yml");
        Assert.Contains("image: mysql:", mysql);
        Assert.Contains("condition: service_healthy", mysql);
        Assert.Contains("Database__Provider: MySql", mysql);
        Assert.Contains("mysqladmin ping", mysql);
        // Ohne Passwort kein Start: Pflichtvariablen statt eines Standardpassworts.
        Assert.Contains("${LAGER_DB_PASSWORD:?", mysql);
        Assert.Contains("${LAGER_DB_ROOT_PASSWORD:?", mysql);

        var caddy = Read("deploy/Caddyfile");
        Assert.Contains("reverse_proxy lager:8080", caddy);
        Assert.Contains("X-Forwarded-For", caddy);
        Assert.Contains("X-Forwarded-Proto", caddy);
    }

    [Fact]
    public void Env_example_documents_the_settings_but_contains_no_active_secret()
    {
        var text = Read(".env.example");
        var active = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();

        // Alles auskommentiert (oder nur ein harmloser Wert): kein Passwort, kein Key, kein Token als aktive Zeile.
        Assert.DoesNotContain(active, l => Regex.IsMatch(l, @"(Password|Key|Secret|Token)\w*=\S", RegexOptions.IgnoreCase));

        foreach (var documented in new[]
                 {
                     "LAGER_PORT", "Auth__BootstrapAdminPassword", "Security__ForwardedHeaders__Enabled",
                     "Cors__AllowedOrigins__0", "AllowedHosts", "Backup__AllowRestore", "LAGER_DB_PASSWORD",
                 })
            Assert.Contains(documented, text);
        Assert.Contains("docker compose logs lager", text); // das Einmalpasswort steht im Container-Log
    }

    [Fact]
    public void The_built_frontend_needs_nothing_the_csp_would_block()
    {
        // Die CSP erlaubt nur 'self' (keine Inline-Skripte, keine fremden Hosts). Vite baut aus index.html eine Seite mit
        // externen Bundles unter /assets/: steht in der Quelle ein Inline-Skript oder eine fremde Adresse, bliebe die Seite im Container leer.
        var index = Read("frontend/lager-ui/index.html");

        foreach (Match script in Regex.Matches(index, @"<script\b[^>]*>", RegexOptions.IgnoreCase))
            Assert.Contains("src=", script.Value); // kein Inline-Skript
        Assert.DoesNotMatch(@"(?i)\b(src|href)\s*=\s*[""']?(https?:)?//", index);
        Assert.DoesNotMatch(@"(?i)\bon\w+\s*=", index); // keine Inline-Event-Handler

        var manifest = Read("frontend/lager-ui/public/manifest.webmanifest");
        Assert.DoesNotMatch(@"https?://", manifest);
    }
}
