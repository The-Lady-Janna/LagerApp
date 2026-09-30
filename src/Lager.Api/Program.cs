using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using FluentValidation;
using FluentValidation.AspNetCore;
using Lager.Api.Errors;
using Lager.Api.Health;
using Lager.Api.Middleware;
using Lager.Api.Security;
using Lager.Api.Seeding;
using Lager.Application;
using Lager.Infrastructure;
using Lager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;
using Serilog;
using Serilog.Events;
using Swashbuckle.AspNetCore.SwaggerGen;

var builder = WebApplication.CreateBuilder(args);

// Serilog: Console + Datei. Verzeichnis (Logging:Directory), Aufbewahrung (Logging:RetainedFileCount) und Mindest-Level/
// Overrides (Abschnitt "Serilog") kommen aus der Konfiguration bzw. Umgebungsvariablen, siehe LagerLogging weiter unten.
// Der Logger steht schon vor dem Rest des Aufbaus, damit auch die Start-Meldungen (Signing-Key, CORS ...) in der Datei landen.
// Die Korrelations-ID kommt aus dem CorrelationIdMiddleware via LogContext.
Log.Logger = LagerLogging.Configure(new LoggerConfiguration(), builder.Configuration).CreateLogger();
builder.Host.UseSerilog();

// Demo-Modus (Demo:Enabled, Standard aus): LAGER_DEMO=1 ist die Kurzform. In Production bricht der Start hier mit klarer Meldung
// ab, solange Demo:AllowInProduction nicht ausdrücklich gesetzt ist - Demo-Daten und Demo-Benutzer laufen nie unbeabsichtigt dort.
DemoSettings.ApplyEnvironmentShortcut(builder.Configuration, Environment.GetEnvironmentVariable);
DemoSettings demoSettings;
try
{
    demoSettings = DemoSettings.From(builder.Configuration, builder.Environment);
}
catch (InvalidOperationException ex)
{
    Log.Fatal("Start abgebrochen: {Reason}", ex.Message);
    Log.CloseAndFlush();
    throw;
}

// Adresse und Port kommen aus ASPNETCORE_URLS bzw. Kestrel:Endpoints (Docker: http://+:8080), nicht aus launchSettings.json.
// Produktions-Vorgabe: der Server nennt sich nicht per "Server: Kestrel"-Header.
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

builder.Services.AddControllers();
// Einheitliche Fehlerantworten (RFC 7807 ProblemDetails mit code + correlationId): globaler Exception-Handler,
// 400 bei ungültigem Modell im selben Format, Enums im JSON nur als Text (Zahlen -> 400), Query-Limits (days, top ...).
builder.Services.AddLagerApiErrors();
// FluentValidation: auto-validate incoming requests + scan this assembly for validators.
// MVC turns failed validation into ValidationProblemDetails (400) automatically.
builder.Services.AddFluentValidationAutoValidation();
// Standardmeldungen von FluentValidation ("darf nicht leer sein" ...) immer auf Deutsch, unabhängig von der Server-Sprache.
ValidatorOptions.Global.LanguageManager.Culture = System.Globalization.CultureInfo.GetCultureInfo("de");
builder.Services.AddValidatorsFromAssemblyContaining<Program>();
builder.Services.AddHttpContextAccessor();

// Health-Endpunkte /health/live und /health/ready (anonym, Antwort nur der Status): siehe src/Lager.Api/Health.
builder.Services.AddLagerHealthChecks();

// Antwortkomprimierung (Brotli bevorzugt, sonst Gzip) für JSON und die statischen Frontend-Dateien. Auf einer HTTPS-
// Verbindung bleibt sie aus (Standard von ASP.NET Core, Schutz vor BREACH); die übliche Betriebsart - TLS im Reverse-Proxy,
// HTTP bis zu dieser API - ist davon nicht betroffen.
builder.Services.AddResponseCompression(options =>
{
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[]
    {
        "application/problem+json", "application/manifest+json", "image/svg+xml", "text/javascript",
    });
});
builder.Services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

// Swagger/OpenAPI (Swagger UI unter /swagger, Beschreibung unter /swagger/v1/swagger.json): in Development immer, sonst nur mit
// Swagger:Enabled=true (Standard aus; Swagger:Enabled=false schaltet es auch in Development ab). Die Beschreibung enthält keine
// Daten und die Endpunkte bleiben geschützt, sie ist aber ohne Anmeldung lesbar: im Internet nur bewusst einschalten.
// Kopf, Bearer-Schema, XML-Kommentare und Fehlerverträge: LagerOpenApi am Ende dieser Datei.
var swaggerEnabled = LagerOpenApi.IsEnabled(builder.Configuration, builder.Environment);
if (swaggerEnabled)
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddLagerSwagger();
}

// Sicherheits-Kern (siehe src/Lager.Api/Security):
//  - JWT-Signing-Key: Jwt:SigningKey (Env/Secret) -> Jwt:KeyFile -> nur Development ein flüchtiger
//    Zufallskey; sonst bricht der Start hier mit verständlicher Meldung ab. Der bekannte DEV-Key aus
//    dem Repository und Keys unter 32 Bytes werden in jeder Umgebung abgelehnt.
//  - Standardmäßig geschlossen: FallbackPolicy + RequireAuthorization() an den Controllern; anonym
//    bleibt nur POST /api/auth/login ([AllowAnonymous]). Ein ausstehender Passwortwechsel sperrt alles
//    außer GET /api/auth/me und POST /api/auth/change-password (Token-Widerruf: AuthenticatedUserValidator).
var securitySettings = SecuritySettings.From(builder.Configuration, builder.Environment);
var jwtKey = JwtKeyResolver.Resolve(builder.Configuration, builder.Environment.IsDevelopment(), builder.Environment.ContentRootPath);
switch (jwtKey.Source)
{
    case JwtKeySource.EphemeralDevelopment:
        Log.Warning("Kein Jwt:SigningKey/Jwt:KeyFile konfiguriert: Development nutzt einen flüchtigen Zufallskey. " +
                    "Alle Tokens verfallen beim Neustart; für Production Jwt:SigningKey oder Jwt:KeyFile setzen.");
        break;
    case JwtKeySource.KeyFileCreated:
        Log.Information("JWT-Signing-Key beim ersten Start angelegt: {KeyFile}", jwtKey.FilePath);
        break;
    default:
        Log.Information("JWT-Signing-Key geladen aus {KeySource}", jwtKey.Source);
        break;
}
builder.Services.AddLagerSecurity(builder.Configuration, jwtKey, securitySettings);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddApplication();

// CORS: erlaubte Origins aus Config (Cors:AllowedOrigins als Array). Default
// für Dev: Vite-Origin localhost:5173. AllowAnyOrigin gibt es nur, wenn ausdrücklich
// ["*"] konfiguriert wird (in jeder Umgebung möglich, deshalb die Warnung außerhalb von Development).
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:5173" };
if (allowedOrigins.Contains("*") && !builder.Environment.IsDevelopment())
    Log.Warning("Cors:AllowedOrigins enthält '*': jede Website darf die API aus dem Browser aufrufen. Für den Produktivbetrieb die konkreten Origins eintragen.");
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (allowedOrigins.Length == 1 && allowedOrigins[0] == "*")
        p.AllowAnyOrigin();
    else
        p.WithOrigins(allowedOrigins).AllowCredentials();
    // Location: die Adresse der neuen Ressource nach einem Anlegen (201), auch für Clients einer anderen Origin lesbar.
    p.AllowAnyHeader().AllowAnyMethod()
        .WithExposedHeaders("X-Correlation-Id", "Location");
}));

// HSTS + HTTPS-Redirect opt-in via Config (`Security:RequireHttps`). In dev mit
// Vite-Proxy würde HttpsRedirection alle Calls brechen, daher per Default off.
// Die Weiterleitung braucht einen ermittelbaren HTTPS-Port (Security:HttpsPort, ASPNETCORE_HTTPS_PORT
// oder eine https-Adresse in ASPNETCORE_URLS); ohne Port bliebe sie wirkungslos. Hinter einem
// TLS-terminierenden Reverse-Proxy leitet der Proxy um und RequireHttps bleibt aus.
var requireHttps = securitySettings.RequireHttps;
var redirectToHttps = requireHttps && securitySettings.HttpsPort is not null;
if (requireHttps)
{
    builder.Services.AddHttpsRedirection(o => o.HttpsPort = securitySettings.HttpsPort);
    builder.Services.AddHsts(_ => { });
}

var app = builder.Build();

// Frontend-Auslieferung: liegt ein gebautes Frontend in wwwroot (Docker-Image: dist -> /app/wwwroot), liefert das Backend
// die SPA mit aus. Ohne wwwroot/index.html bleibt es eine reine API (Entwicklung: Vite-Dev-Server mit /api-Proxy).
var serveFrontend = FrontendHosting.IsAvailable(app.Environment);

// Start-Meldungen als strukturierte Logs (Umgebung, Datenbank-Art, Frontend, Log-Verzeichnis); nie der Verbindungsstring.
var databaseSettings = app.Services.GetRequiredService<DatabaseSettings>();
app.Logger.LogInformation(
    "Lager startet: Umgebung {Environment}, Datenbank {DatabaseProvider}, Log-Verzeichnis {LogDirectory}",
    app.Environment.EnvironmentName, databaseSettings.IsMySql ? "MySql" : "Sqlite", LagerLogging.ResolveDirectory(app.Configuration));
if (databaseSettings.SqliteFilePath is { } sqliteFile)
    app.Logger.LogInformation("SQLite-Datenbankdatei: {DatabaseFile}", sqliteFile);
if (serveFrontend)
    app.Logger.LogInformation("Frontend wird aus {WebRoot} ausgeliefert (SPA-Fallback auf index.html).", app.Environment.WebRootPath);
else if (!string.IsNullOrEmpty(app.Environment.WebRootPath) && Directory.Exists(app.Environment.WebRootPath))
    app.Logger.LogWarning("Das Web-Root-Verzeichnis {WebRoot} enthält keine index.html: Es wird kein Frontend ausgeliefert.", app.Environment.WebRootPath);
else
    app.Logger.LogInformation("Kein Frontend in wwwroot: Das Backend läuft als reine API.");
app.Lifetime.ApplicationStarted.Register(() =>
    app.Logger.LogInformation("Lager läuft und lauscht auf {Urls}", string.Join(", ", app.Urls)));

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<LagerDbContext>();

    // Datenbank-Start: EnsureCreated (leere DB aus dem Modell) + SchemaUpgrader (jede DB auf den aktuellen Stand).
    // Der SchemaUpgrader ist der einzige Weg der Schema-Evolution (neue Schritte als Datei in Persistence/SchemaSteps).
    await DatabaseInitializer.InitializeAsync(db, app.Logger);

    // Bootstrap-Admin: legt beim ersten Start (Users leer) einen Admin an, damit man sich überhaupt
    // einloggen kann. Es gibt kein Default-Passwort: entweder "Auth:BootstrapAdminPassword" (vom
    // Betreiber per Env/Secret gesetzt, wird nie geloggt) oder ein Zufalls-Einmalpasswort, das genau
    // einmal auf der Konsole erscheint. In beiden Fällen ist MustChangePassword gesetzt.
    await BootstrapAdminService.EnsureAdminAsync(scope.ServiceProvider, app.Configuration, app.Logger);

    // Demo-Modus (Demo:Enabled, Standard aus): der neutrale Demo-Datensatz mit 60 Tagen Historie und Demo-Benutzern, nur in eine
    // leere Fachdatenbank und nie erneut (die Production-Prüfung ist oben schon gelaufen). Die Zugangsdaten der Demo-Benutzer
    // stehen einmalig in der Konsole. Database:Seed ist der veraltete Schalter für den kleinen Altbestand-Datensatz: er wirkt nur,
    // solange Demo:Enabled aus ist, und warnt bei jedem Start (in Production weiterhin nur mit Demo:AllowInProduction).
    if (demoSettings.Enabled)
    {
        if (demoSettings.LegacySeed)
            app.Logger.LogWarning("Database:Seed ist veraltet und wird ignoriert, weil Demo:Enabled aktiv ist. Database:Seed bitte entfernen.");
        await DemoDataSeeder.SeedAsync(db, scope.ServiceProvider, app.Logger);
    }
    else if (demoSettings.LegacySeed)
    {
        app.Logger.LogWarning(
            "Database:Seed ist veraltet: es legt nur den kleinen Altbestand-Datensatz an (20 Artikel, keine Historie). " +
            "Für den vollständigen Demo-Datensatz mit Historie und Demo-Benutzern Demo:Enabled=true (oder LAGER_DEMO=1) setzen.");
        await DemoDataSeeder.SeedAsync(db, app.Environment, app.Configuration, app.Logger);
    }
}

// Hinter einem Reverse-Proxy zuerst die Weitergabe-Header auswerten (Client-IP für Rate-Limiting, Schema für HTTPS).
if (securitySettings.ForwardedHeadersEnabled)
    app.UseForwardedHeaders();

// Sicherheits-Header ganz vorn (setzt sie in OnStarting): auch Swagger, Weiterleitungen und 401/429 bekommen sie.
app.UseMiddleware<SecurityHeadersMiddleware>();

// Antworten komprimieren (JSON, Frontend-Dateien): vor allem, was Inhalt schreibt.
app.UseResponseCompression();

if (swaggerEnabled)
{
    app.UseSwagger();
    app.UseSwaggerUI(options => options.DocumentTitle = "Lager API");
}

if (requireHttps)
{
    app.UseHsts();
    if (redirectToHttps)
        app.UseHttpsRedirection();
    else
        app.Logger.LogWarning("Security:RequireHttps ist aktiv, aber es ist kein HTTPS-Port ermittelbar (Security:HttpsPort, ASPNETCORE_HTTPS_PORT oder https-Adresse in ASPNETCORE_URLS). Die Weiterleitung auf HTTPS bleibt aus.");
}

// Frontend aus wwwroot: "/" -> index.html, gehashte Bundles unter /assets/ dauerhaft cachebar, index.html und sw.js immer
// neu prüfen (FrontendHosting). Bewusst vor Request-Log, CORS und Authentifizierung: statische Dateien sind öffentlich, kosten
// keinen Rate-Limit-Slot und fluten das Request-Log nicht. Die Sicherheits-Header (CSP ...) tragen sie trotzdem.
if (serveFrontend)
{
    app.UseDefaultFiles();
    app.UseStaticFiles(FrontendHosting.CreateStaticFileOptions());
}

// Korrelations-ID + Serilog-Request-Logging vor dem Auth/Cors-Stack, damit
// auch 401/403 mit Korrelations-ID rauskommen.
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<CorrelationIdCaptureMiddleware>();
// Das Request-Log (UseSerilogRequestLogging(), hier mit eigenem Pegel) steht zwischen Korrelations-ID und Exception-Handler.
// Die Health-Probes (Compose-Healthcheck alle 30 s) loggen nur bei Fehlern (LagerLogging.GetRequestLogLevel): sonst füllten sie
// das Log mit rund 2900 Zeilen pro Tag.
app.UseSerilogRequestLogging(options => options.GetLevel = LagerLogging.GetRequestLogLevel);

app.UseCors();
// Einheitliche Fehlerantworten. Der Exception-Handler fängt alles, was Controller und Services werfen (fachliche Fehler
// -> 404/400/409, DbUpdate(Concurrency)Exception -> 409, sonst 500 ohne Details) und ersetzt die frühere
// ConcurrencyExceptionMiddleware und DomainRuleExceptionMiddleware. Die Status-Seiten füllen leere 401/403/404/405 mit
// ProblemDetails; bereits geschriebene Bodies (password_change_required, too_many_requests ...) bleiben unangetastet.
app.UseExceptionHandler();
app.UseLagerStatusCodePages();
// Rate-Limiting vor der Authentifizierung: abgewiesene Anfragen kosten weder DB-Lookup noch BCrypt.
if (securitySettings.RateLimitingEnabled)
    app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
// RequireAuthorization() hängt die Default-Policy (angemeldet + kein ausstehender Passwortwechsel) an JEDEN
// Controller-Endpunkt - auch an solche mit [Authorize(Roles = ...)], die sie sonst nicht einbeziehen.
// Nur [AllowAnonymous] (Login) bleibt offen.
app.MapControllers().RequireAuthorization();
// GET /api/version: App-Version und Schema-Stand. Bewusst kein Controller (jede Controller-Action braucht eine Zeile in der
// Rollenmatrix der Tests): ein einzelner Lese-Endpunkt für jeden Angemeldeten, mit derselben Default-Policy wie die Controller.
app.MapGet(ApiVersionInfo.Route, async (LagerDbContext db, CancellationToken ct) => TypedResults.Ok(await ApiVersionInfo.LoadAsync(db, ct)))
    .RequireAuthorization()
    .WithName("GetVersion")
    .WithTags("System")
    .WithSummary("App- und Schema-Version")
    .WithDescription("Version der laufenden Anwendung und Stand des Datenbankschemas (höchster angewendeter Schema-Schritt). " +
                     "Für Support, Monitoring und Integratoren, die den Stand der API prüfen wollen.")
    .Produces<VersionInfoDto>(StatusCodes.Status200OK);
// Anonym (Docker-Healthcheck, Proxy, Monitoring): /health/live und /health/ready.
app.MapLagerHealthChecks();
// SPA-Fallback ganz zuletzt (niedrigste Priorität): jeder Nicht-API-Pfad ohne Dateiendung (Deep-Link wie /orders, Reload)
// liefert index.html. /api, /health und /swagger sind ausgenommen: ein unbekannter API-Pfad bleibt ein 404 als JSON.
// Anonym, sonst würde die Fallback-Policy (angemeldet) die Startseite mit 401 sperren.
if (serveFrontend)
    app.MapFallbackToFile(FrontendHosting.FallbackPattern, FrontendHosting.IndexFile, FrontendHosting.CreateStaticFileOptions())
        .AllowAnonymous();

try
{
    app.Run();
}
finally
{
    Log.CloseAndFlush();
}

// Sichtbar für WebApplicationFactory<Program> in tests/Lager.Tests.
public partial class Program { }

/// <summary>
/// Logging-Konfiguration. Vorgaben im Code (Console + Datei, Mindest-Level Information, Framework-Lärm gedämpft), die sich
/// ohne Neubau ändern lassen:
/// <list type="bullet">
/// <item><c>Logging:Directory</c> (Umgebungsvariable <c>Logging__Directory</c>): Verzeichnis der Logdateien
/// <c>lager-JJJJMMTT.log</c>; Standard <c>logs</c> relativ zum Arbeitsverzeichnis, im Docker-Image <c>/data/logs</c>.</item>
/// <item><c>Logging:RetainedFileCount</c>: wie viele Tagesdateien behalten werden (Standard 14).</item>
/// <item>Abschnitt <c>Serilog</c> (<c>Serilog:MinimumLevel:Default</c>, <c>Serilog:MinimumLevel:Override:&lt;Namespace&gt;</c>,
/// weitere Sinks unter <c>Serilog:WriteTo</c>): schlägt die Vorgaben im Code. Beispiel per Umgebungsvariable:
/// <c>Serilog__MinimumLevel__Default=Debug</c>.</item>
/// </list>
/// Der Abschnitt <c>Logging:LogLevel</c> der Microsoft-Konfiguration hat keine Wirkung: Serilog ersetzt die Logging-Infrastruktur.
/// </summary>
public static class LagerLogging
{
    public const string ConsoleTemplate = "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}";
    public const string FileTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{CorrelationId}] {Message:lj} {Properties:j}{NewLine}{Exception}";

    private const int DefaultRetainedFileCount = 14;

    /// <summary>Name der Log-Eigenschaft mit den Demo-Zugangsdaten (siehe DemoDataSeeder): solche Einträge gehen nicht in die Logdatei.</summary>
    public const string DemoCredentialsProperty = "DemoCredentials";

    /// <summary>
    /// Pegel der Request-Log-Zeile von <c>UseSerilogRequestLogging</c>. Die Health-Probes (<c>/health/live</c>, <c>/health/ready</c>;
    /// der Compose-Healthcheck ruft sie alle 30 Sekunden auf, das sind rund 2900 Zeilen pro Tag) loggen nur noch auf Debug, damit sie
    /// das Log nicht füllen; scheitert eine Probe (5xx oder Ausnahme), bleibt es eine Warnung. Alle anderen Anfragen: Information,
    /// bei 5xx oder Ausnahme Error (wie bisher).
    /// </summary>
    public static LogEventLevel GetRequestLogLevel(HttpContext context, double elapsedMs, Exception? exception)
    {
        var failed = exception is not null || context.Response.StatusCode > 499;
        if (IsHealthProbe(context))
            return failed ? LogEventLevel.Warning : LogEventLevel.Debug;
        return failed ? LogEventLevel.Error : LogEventLevel.Information;
    }

    /// <summary>Ist die Anfrage ein Aufruf der Health-Endpunkte (Pfad /health und alles darunter, ohne Beachtung der Schreibweise)?</summary>
    public static bool IsHealthProbe(HttpContext context) =>
        context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);

    /// <summary>Verzeichnis der Logdateien (absolut): <c>Logging:Directory</c>, sonst <c>logs</c> im Arbeitsverzeichnis.</summary>
    public static string ResolveDirectory(IConfiguration config)
    {
        var configured = config["Logging:Directory"];
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? "logs" : configured.Trim());
    }

    public static LoggerConfiguration Configure(LoggerConfiguration logger, IConfiguration config)
    {
        var retained = DefaultRetainedFileCount;
        var retainedRaw = config["Logging:RetainedFileCount"];
        if (!string.IsNullOrWhiteSpace(retainedRaw) && (!int.TryParse(retainedRaw, out retained) || retained < 1))
            throw new InvalidOperationException($"Konfiguration Logging:RetainedFileCount muss eine ganze Zahl >= 1 sein (war: '{retainedRaw}').");

        return logger
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            // Den Fehler-Logeintrag der ExceptionHandlerMiddleware (Error samt Stacktrace bei JEDER Exception, auch bei einem
            // fachlichen 409) schreibt der GlobalExceptionHandler selbst: fachliche Fehler ohne Stacktrace, unerwartete als
            // Error mit Korrelations-ID.
            .MinimumLevel.Override("Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware", LogEventLevel.Fatal)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate: ConsoleTemplate)
            // Die einmaligen Demo-Zugangsdaten (Eigenschaft DemoCredentials) erscheinen nur in der Konsole, nie in der Logdatei.
            .WriteTo.Conditional(logEvent => !logEvent.Properties.ContainsKey(DemoCredentialsProperty), sink => sink.File(
                Path.Combine(ResolveDirectory(config), "lager-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: retained,
                outputTemplate: FileTemplate))
            // Zuletzt: was im Abschnitt "Serilog" steht, überschreibt die Vorgaben oben.
            .ReadFrom.Configuration(config);
    }
}

/// <summary>
/// Auslieferung des gebauten Frontends (Vite-<c>dist</c>) aus wwwroot durch das Backend: ein Prozess, ein Port, ein Origin
/// (das Frontend ruft die API mit relativen /api-Pfaden auf, CORS ist dafür nicht nötig).
/// </summary>
public static class FrontendHosting
{
    public const string IndexFile = "index.html";

    /// <summary>
    /// Route für den SPA-Fallback: jeder Pfad ohne Dateiendung (<c>nonfile</c>: ein fehlendes <c>/assets/x.js</c> bleibt ein
    /// 404, statt HTML als JavaScript auszuliefern), außer /api, /health und /swagger (ganze Pfadsegmente, ohne
    /// Unterscheidung der Groß-/Kleinschreibung; <c>\x2F</c> ist der Schrägstrich).
    /// </summary>
    public const string FallbackPattern = @"{*path:nonfile:regex(^(?!(api|health|swagger)($|\x2F)).*$)}";

    /// <summary>Nur mit einer <c>index.html</c> im Web-Root wird das Frontend ausgeliefert; sonst bleibt das Backend eine reine API.</summary>
    public static bool IsAvailable(IWebHostEnvironment environment) =>
        !string.IsNullOrEmpty(environment.WebRootPath) && File.Exists(Path.Combine(environment.WebRootPath, IndexFile));

    public static StaticFileOptions CreateStaticFileOptions() => new() { OnPrepareResponse = SetCacheControl };

    /// <summary>
    /// Cache-Regeln: die gehashten Bundles unter /assets/ ändern sich nie (neuer Inhalt = neuer Dateiname) und gelten ein Jahr
    /// als unveränderlich. Alles andere (index.html, sw.js, Manifest, Icons) hat einen festen Namen und wird bei jeder Nutzung
    /// neu geprüft (<c>no-cache</c> heißt: speichern erlaubt, aber vor der Verwendung per ETag nachfragen) - sonst bliebe nach
    /// einem Update die alte Oberfläche bzw. der alte Service-Worker aktiv.
    /// </summary>
    public static void SetCacheControl(StaticFileResponseContext context)
    {
        var isHashedBundle = context.Context.Request.Path.StartsWithSegments("/assets")
                             && !string.Equals(context.File.Name, IndexFile, StringComparison.OrdinalIgnoreCase);
        context.Context.Response.Headers.CacheControl = isHashedBundle
            ? "public, max-age=31536000, immutable"
            : "no-cache";
    }
}

/// <summary>
/// App-Version (aus der Assembly) und Stand des Datenbankschemas. Quelle für den Kopf des Swagger-Dokuments und für
/// <c>GET /api/version</c>.
/// </summary>
public static class ApiVersionInfo
{
    /// <summary>Route der Versions-Abfrage.</summary>
    public const string Route = "/api/version";

    /// <summary>Version der Anwendung: <c>AssemblyInformationalVersion</c> ohne die Build-Metadaten hinter "+" (Commit), sonst die Assembly-Version.</summary>
    public static string AppVersion { get; } = ResolveAppVersion();

    private static string ResolveAppVersion()
    {
        var assembly = typeof(Program).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    /// <summary>
    /// Liest den Schemastand aus <c>__LagerSchemaVersion</c> (die der <c>SchemaUpgrader</c> beim Start führt): die höchste
    /// angewendete <c>StepOrder</c> und der Name dieses Schritts. Eine Datenbank ohne Eintrag steht auf 0.
    /// </summary>
    public static async Task<VersionInfoDto> LoadAsync(LagerDbContext db, CancellationToken ct)
    {
        var steps = await db.Database
            .SqlQueryRaw<SchemaStepRow>("SELECT Name, StepOrder FROM " + SchemaUpgrader.VersionTable)
            .ToListAsync(ct);
        var latest = steps.OrderByDescending(s => s.StepOrder).FirstOrDefault();
        return new VersionInfoDto(AppVersion, latest?.StepOrder ?? 0, latest?.Name);
    }

    /// <summary>Eine Zeile von <c>__LagerSchemaVersion</c> (Spaltennamen wie in der Tabelle).</summary>
    public sealed class SchemaStepRow
    {
        public string Name { get; set; } = "";
        public int StepOrder { get; set; }
    }
}

/// <summary>Antwort von <c>GET /api/version</c>.</summary>
/// <param name="AppVersion">Version der laufenden Anwendung (z. B. <c>1.0.0</c>).</param>
/// <param name="SchemaVersion">Stand des Datenbankschemas: die höchste angewendete Schema-Schritt-Nummer (0 = nur Baseline). Steigt mit jedem neuen Schritt des SchemaUpgraders.</param>
/// <param name="SchemaStep">Name des zuletzt angewendeten Schema-Schritts (z. B. <c>2400_AddSomething</c>); null, wenn noch keiner vermerkt ist.</param>
public sealed record VersionInfoDto(string AppVersion, int SchemaVersion, string? SchemaStep);

/// <summary>
/// OpenAPI/Swagger der API: Schalter, Dokument-Kopf (Titel, Version aus der Assembly, Beschreibung), Bearer-Schema, die
/// XML-Kommentare der Controller und die zentralen Fehlerverträge (401/403 für jeden geschützten Endpunkt, ProblemDetails-Schema).
/// </summary>
public static class LagerOpenApi
{
    /// <summary>Konfigurationsschlüssel: <c>Swagger:Enabled</c> schaltet Swagger ein oder aus; ohne Angabe gilt: nur in Development.</summary>
    public const string EnabledKey = "Swagger:Enabled";

    /// <summary>Name des Sicherheitsschemas (JWT im Authorization-Header).</summary>
    public const string BearerSchemeId = "Bearer";

    /// <summary>Content-Type aller Fehlerantworten.</summary>
    public const string ProblemContentType = "application/problem+json";

    private const string Description =
        "REST-API des Lager-Systems (Self-hosted Warehouse-Management).\n\n" +
        "**Anmeldung:** `POST /api/auth/login` liefert ein JWT, das als `Authorization: Bearer <token>` an jede weitere Anfrage gehört " +
        "(Schaltfläche *Authorize*). Die API ist standardmäßig geschlossen: Ohne gültiges Token antwortet jeder Endpunkt außer dem Login " +
        "mit 401, mit zu wenig Rechten mit 403. Die Mindestrolle steht je Endpunkt am Anfang der Beschreibung.\n\n" +
        "**Fehler:** RFC 7807 (`application/problem+json`) mit `type`, `title`, `status`, `detail`, dem maschinenlesbaren `code` " +
        "(snake_case) und der `correlationId` (auch im Header `X-Correlation-Id`); Validierungsfehler zusätzlich mit `errors` je Feld. " +
        "404 = unbekannte Id, 409 = Regelverstoß, Duplikat oder gleichzeitige Änderung.\n\n" +
        "**Konventionen:** JSON in camelCase, Enums nur als Text, Zeitstempel in UTC, IDs als GUID, Beträge in Cent, Maße in Millimetern, " +
        "Gewichte in Gramm. Anlegen antwortet 201 mit `Location` auf die neue Ressource (oder auf die Liste, die sie enthält), Löschen " +
        "von Stammdaten 204; Änderungen an Zeilen und Adressen antworten mit dem aktualisierten übergeordneten Datensatz.";

    /// <summary>
    /// Swagger läuft in Development, sonst nur mit <c>Swagger:Enabled=true</c>. Ein ausdrückliches <c>false</c> schaltet es auch in
    /// Development ab. Die Beschreibung enthält keine Daten und die Endpunkte bleiben geschützt, sie ist aber ohne Anmeldung lesbar.
    /// </summary>
    public static bool IsEnabled(IConfiguration config, IHostEnvironment environment) =>
        config.GetValue<bool?>(EnabledKey) ?? environment.IsDevelopment();

    public static IServiceCollection AddLagerSwagger(this IServiceCollection services) =>
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Lager API",
                Version = ApiVersionInfo.AppVersion,
                Description = Description,
            });

            c.AddSecurityDefinition(BearerSchemeId, new OpenApiSecurityScheme
            {
                Description = "JWT mit Bearer-Schema. Login via POST /api/auth/login, dann 'Bearer <token>' eintragen.",
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header,
            });

            // XML-Kommentare der Controller (Lager.Api.xml) und - sobald Lager.Contracts eine Dokumentationsdatei erzeugt - der DTOs.
            foreach (var file in new[] { "Lager.Api.xml", "Lager.Contracts.xml" })
            {
                var path = Path.Combine(AppContext.BaseDirectory, file);
                if (File.Exists(path)) c.IncludeXmlComments(path, includeControllerXmlComments: true);
            }

            c.SupportNonNullableReferenceTypes();
            // Dateidownloads (PDF, ZPL, CSV, Backup) sind Binärdaten, kein JSON-Objekt.
            c.MapType<FileResult>(() => new OpenApiSchema { Type = "string", Format = "binary" });
            c.SchemaFilter<ProblemDetailsSchemaFilter>();
            c.OperationFilter<LagerOperationFilter>();
        });
}

/// <summary>
/// Ergänzt das Fehlerformat der API im Schema von <see cref="ProblemDetails"/> (und <see cref="ValidationProblemDetails"/>):
/// <c>code</c>, <c>correlationId</c> und <c>error</c> stehen in jeder Fehlerantwort (siehe <c>Problems.Decorate</c>), sind aber
/// nur Erweiterungsdaten und würden sonst im Vertrag fehlen.
/// </summary>
public sealed class ProblemDetailsSchemaFilter : ISchemaFilter
{
    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        if (!typeof(ProblemDetails).IsAssignableFrom(context.Type)) return;

        schema.Properties["code"] = new OpenApiSchema
        {
            Type = "string",
            Description = "Maschinenlesbarer Fehlercode in snake_case, z. B. validation_failed, not_found, conflict, duplicate, " +
                          "concurrency_conflict, in_use, unauthorized, forbidden, password_change_required.",
        };
        schema.Properties["correlationId"] = new OpenApiSchema
        {
            Type = "string",
            Description = "Referenz-ID der Anfrage (auch im Header X-Correlation-Id und in der Logdatei).",
        };
        schema.Properties["error"] = new OpenApiSchema
        {
            Type = "string",
            Description = "Dieselbe Meldung wie detail (Rückwärtskompatibilität für Clients, die { error } lesen).",
        };
    }
}

/// <summary>
/// Der zentrale Teil des Vertrags jedes Endpunkts, den weder XML-Kommentar noch <c>[ProducesResponseType]</c> einer Action sinnvoll
/// pro Action wiederholen: das Bearer-Sicherheitsschema (alle Endpunkte außer <c>[AllowAnonymous]</c>), die Fehlerantworten 401 und 403
/// als ProblemDetails (Anmeldung und Rollen greifen global vor jeder Action, siehe SecurityServiceCollectionExtensions) und die
/// Mindestrolle als erste Zeile der Beschreibung. Die fachlichen Antworten (2xx, 400, 404, 409) deklariert jede Action selbst;
/// der Filter vereinheitlicht nur ihre Darstellung (Fehler als <c>application/problem+json</c>, Erfolg nur als JSON, deutsche
/// Beschreibungen, der Header <c>Location</c> an jedem 201).
/// </summary>
public sealed class LagerOperationFilter : IOperationFilter
{
    // Mindestrollen der Policies aus SecurityServiceCollectionExtensions (AddLagerAuthorization), schärfste zuerst.
    private static readonly (string Policy, string Roles)[] PolicyRoles =
    {
        ("Admin", "Admin"),
        ("Manager", "Manager, Admin"),
        ("Picker", "Picker, Manager, Admin"),
        ("Packer", "Packer, Manager, Admin"),
        ("Receiver", "Receiver, Manager, Admin"),
    };

    // Beschreibungen der Fehler- und Sonderantworten (Swashbuckle liefert für [ProducesResponseType] sonst nur "Not Found" usw.).
    private static readonly Dictionary<string, string> ResponseTexts = new()
    {
        ["401"] = "Nicht angemeldet: das Token fehlt, ist ungültig oder abgelaufen (Konto deaktiviert, Passwort geändert ...); " +
                  "beim Login: Zugangsdaten falsch, Konto gesperrt oder deaktiviert (code invalid_credentials).",
        ["403"] = "Keine Berechtigung: die Rolle genügt nicht, oder ein Passwortwechsel steht aus (code password_change_required).",
        ["201"] = "Angelegt. Der Header Location nennt die Adresse der neuen Ressource.",
        ["204"] = "Erfolg, ohne Antwortdaten.",
        ["400"] = "Ungültige Eingabe (code validation_failed oder ein fachlicher Code; bei Feldfehlern mit errors je Feld).",
        ["404"] = "Nicht gefunden: die Id (oder der Name) ist unbekannt.",
        ["409"] = "Konflikt: Regelverstoß, Duplikat oder gleichzeitige Änderung (code z. B. conflict, duplicate, in_use, concurrency_conflict).",
        ["413"] = "Die Anfrage ist zu groß.",
        ["429"] = "Zu viele Anfragen; der Header Retry-After nennt die Wartezeit.",
        ["500"] = "Interner Fehler; bei einer Meldung die correlationId angeben.",
    };

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        AddSecurity(operation, context);
        PolishResponses(operation);
    }

    private static void AddSecurity(OpenApiOperation operation, OperationFilterContext context)
    {
        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
        if (metadata.OfType<IAllowAnonymous>().Any())
        {
            Describe(operation, "**Berechtigung:** ohne Anmeldung.");
            return;
        }

        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = LagerOpenApi.BearerSchemeId },
            }] = Array.Empty<string>(),
        });

        var policies = metadata.OfType<IAuthorizeData>()
            .Select(a => a.Policy)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToHashSet(StringComparer.Ordinal);
        var roleRule = PolicyRoles.FirstOrDefault(r => policies.Contains(r.Policy));
        var roleBound = roleRule.Policy is not null;

        Describe(operation, roleBound
            ? $"**Berechtigung:** Rolle {roleRule.Roles}."
            : "**Berechtigung:** jeder angemeldete Benutzer.");

        AddProblem(operation, context, StatusCodes.Status401Unauthorized);
        // Nur GET /api/auth/me und POST /api/auth/change-password bleiben bei ausstehendem Passwortwechsel offen: dort kann es kein 403 geben.
        var passwordChangeEndpoint = metadata.OfType<AllowPasswordChangePendingAttribute>().Any();
        if (roleBound || !passwordChangeEndpoint)
            AddProblem(operation, context, StatusCodes.Status403Forbidden);
    }

    private static void PolishResponses(OpenApiOperation operation)
    {
        foreach (var (code, response) in operation.Responses)
        {
            if (ResponseTexts.TryGetValue(code, out var text)) response.Description = text;

            // Die API antwortet JSON: die zusätzlichen Formatter-Einträge text/plain und text/json sind Rauschen.
            if (response.Content.ContainsKey("application/json"))
            {
                response.Content.Remove("text/plain");
                response.Content.Remove("text/json");
            }

            // Fehlerantworten sind immer application/problem+json (ProblemDetails bzw. ValidationProblemDetails).
            if (code.Length == 3 && code[0] is ('4' or '5')
                && !response.Content.ContainsKey(LagerOpenApi.ProblemContentType)
                && response.Content.Values.FirstOrDefault()?.Schema?.Reference?.Id is "ProblemDetails" or "ValidationProblemDetails")
            {
                var schema = response.Content.Values.First().Schema;
                response.Content.Clear();
                response.Content[LagerOpenApi.ProblemContentType] = new OpenApiMediaType { Schema = schema };
            }

            if (code == "201")
                response.Headers["Location"] = new OpenApiHeader
                {
                    Description = "Adresse der neuen Ressource; ein GET darauf liefert sie (200).",
                    Schema = new OpenApiSchema { Type = "string", Format = "uri" },
                };
        }
    }

    private static void Describe(OpenApiOperation operation, string access) =>
        operation.Description = string.IsNullOrWhiteSpace(operation.Description) ? access : $"{access}\n\n{operation.Description}";

    private static void AddProblem(OpenApiOperation operation, OperationFilterContext context, int status)
    {
        var key = status.ToString(CultureInfo.InvariantCulture);
        if (operation.Responses.ContainsKey(key)) return;

        operation.Responses[key] = new OpenApiResponse
        {
            Description = ResponseTexts[key],
            Content =
            {
                [LagerOpenApi.ProblemContentType] = new OpenApiMediaType
                {
                    Schema = context.SchemaGenerator.GenerateSchema(typeof(ProblemDetails), context.SchemaRepository),
                },
            },
        };
    }
}
