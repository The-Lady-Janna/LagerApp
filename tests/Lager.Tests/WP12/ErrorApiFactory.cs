using System.Reflection;
using System.Text.Json;
using Lager.Application.Auth;
using Lager.Domain.Auth;
using Lager.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Lager.Infrastructure.Persistence;

namespace Lager.Tests.WP12;

/// <summary>
/// API-Host für die Fehlerformat-Tests: wie <see cref="LagerApiFactory"/> (eigene SQLite-Datei, Bootstrap-Admin), aber mit
/// wählbarer Umgebung und einem zusätzlichen Test-Controller (<see cref="ErrorProbeController"/>), der gezielt Exceptions wirft.
/// Der Controller liegt nur im Test-Host und ist wie jeder andere Endpunkt geschützt (Admin-Token nötig).
/// </summary>
public sealed class ErrorApiFactory : WebApplicationFactory<Program>
{
    private readonly string _environment;
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"lager-wp12-{Guid.NewGuid():N}.db");

    public ErrorApiFactory(string environment = "Testing", bool seed = false)
    {
        _environment = environment;
        Seed = seed;
    }

    public bool Seed { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("Database:ConnectionString", $"Data Source={_dbPath}");
        builder.UseSetting("Database:Seed", Seed ? "true" : "false");
        builder.UseSetting("Jwt:SigningKey", LagerApiFactory.SigningKey);
        builder.UseSetting("Auth:BootstrapAdminUsername", LagerApiFactory.AdminUser);
        builder.UseSetting("Auth:BootstrapAdminPassword", LagerApiFactory.AdminPassword);

        builder.ConfigureServices(services =>
            services.AddControllers().ConfigureApplicationPartManager(manager =>
                manager.FeatureProviders.Add(new SingleControllerProvider(typeof(ErrorProbeController)))));
    }

    /// <summary>Liest den Body als JSON-Objekt.</summary>
    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            try { File.Delete(_dbPath + suffix); } catch (IOException) { /* Temp-Datei */ }
        }
    }

    /// <summary>Nimmt genau einen Controller in den Host auf (ohne die ganze Test-Assembly als Application Part zu laden).</summary>
    private sealed class SingleControllerProvider : IApplicationFeatureProvider<ControllerFeature>
    {
        private readonly TypeInfo _controller;
        public SingleControllerProvider(Type controller) => _controller = controller.GetTypeInfo();
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature) => feature.Controllers.Add(_controller);
    }
}

public enum ProbeColor { Red = 0, Green = 1 }

public record ProbeBody(ProbeColor Color);

/// <summary>Wirft je Route eine bestimmte Exception, damit die Tests die zentrale Abbildung über HTTP prüfen können.</summary>
[ApiController]
[Route("__wp12")]
public class ErrorProbeController : ControllerBase
{
    [HttpGet("conflict")]
    public IActionResult Conflict_() => throw new InvalidOperationException("Insufficient stock for article ABC-1 (missing 3)");

    [HttpGet("conflict-code")]
    public IActionResult ConflictWithCode() =>
        throw new InvalidOperationException("Bestand reicht nicht") { Data = { ["code"] = "stock_insufficient" } };

    [HttpGet("not-found")]
    public IActionResult NotFound_() => throw new KeyNotFoundException("Artikel 42 nicht gefunden");

    [HttpGet("bad-argument")]
    public IActionResult BadArgument() => throw new ArgumentException("Menge muss größer 0 sein", "quantity");

    [HttpGet("out-of-range")]
    public IActionResult OutOfRange() => throw new ArgumentOutOfRangeException("qty", 0, "Menge außerhalb von 1..10");

    [HttpGet("format")]
    public IActionResult Format() => throw new FormatException("Guid.Parse intern: 'xyz' ist keine Guid");

    [HttpGet("boom")]
    public IActionResult Boom() => throw new Exception("Geheimes Detail: Verbindungszeichenfolge Server=intern;Passwort=x");

    [HttpGet("framework-conflict")]
    public IActionResult FrameworkConflict() => Ok(new List<int>().First()); // InvalidOperationException aus System.Linq = Programmfehler

    [HttpGet("concurrency")]
    public IActionResult Concurrency() => throw new DbUpdateConcurrencyException("SQLite-Interna: UPDATE Articles SET ... WHERE ConcurrencyToken");

    [HttpGet("invalid-credentials")]
    public IActionResult InvalidCredentials() => throw new InvalidCredentialsException();

    [HttpGet("user-rule")]
    public IActionResult UserRule() => throw new UserRuleViolationException("Der letzte Administrator kann nicht deaktiviert werden.");

    [HttpGet("unknown-role")]
    public IActionResult UnknownRole() => throw new UnknownRoleException("Unbekannte Rolle 'Chef'");

    [HttpGet("bad-request-body")]
    public IActionResult BadRequestBody() => throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge);

    /// <summary>Zwei Artikel mit derselben SKU über den DbContext: die Unique-Verletzung kommt erst beim Speichern.</summary>
    [HttpPost("duplicate-sku")]
    public async Task<IActionResult> DuplicateSku([FromServices] LagerDbContext db, CancellationToken ct)
    {
        // Eigene Wertobjekte je Artikel: EF verfolgt "owned" Instanzen nur einmal.
        foreach (var name in new[] { "Erster", "Zweiter" })
            db.Articles.Add(new Lager.Domain.Articles.Article("DUP-1", name, new Lager.Domain.Articles.Dimensions(10, 10, 10), 1,
                new Lager.Domain.Articles.StackingInfo(false, Lager.Domain.Articles.StackingAxis.Z, 0, null)));
        await db.SaveChangesAsync(ct);
        return Ok();
    }

    [HttpPost("enum")]
    public IActionResult EnumBody([FromBody] ProbeBody body) => Ok(body);
}
