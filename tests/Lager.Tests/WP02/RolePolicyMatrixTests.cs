using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Articles;
using Lager.Contracts.Stock;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Lager.Tests.WP02;

/// <summary>
/// Rollen-Durchsetzung gegen die laufende API: jede Rolle (mit genau dieser einen Rolle) ruft jeden Endpunkt
/// der Tabelle in <see cref="EndpointMatrix"/> auf. Erwartung: unterhalb der Mindestberechtigung immer 403,
/// sonst nie 401/403. Die Autorisierung greift vor der Action - eine gesperrte Rolle ändert also keine Daten.
/// Anonyme Zugriffe (401) gehören zu WP01 und werden hier nicht geprüft.
/// </summary>
public class RolePolicyMatrixTests : IClassFixture<RoleClientsFixture>
{
    private readonly RoleClientsFixture _api;

    public RolePolicyMatrixTests(RoleClientsFixture api) => _api = api;

    public static IEnumerable<object[]> ProbedEndpoints() =>
        EndpointMatrix.Rules.Where(r => r.Probe).Select(r => new object[] { r.Method, r.Template });

    [Theory]
    [MemberData(nameof(ProbedEndpoints))]
    public async Task Endpoint_is_forbidden_exactly_for_roles_below_its_policy(string method, string template)
    {
        var rule = EndpointMatrix.Find(method, template);
        var problems = new List<string>();

        foreach (var role in EndpointMatrix.Roles)
        {
            var status = await _api.SendAsync(role, method, EndpointMatrix.ToPath(template));
            var allowed = EndpointMatrix.IsAllowed(rule.Tier, role);

            if (allowed && status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                problems.Add($"{role} sollte durchkommen, bekam aber {(int)status}");
            else if (!allowed && status != HttpStatusCode.Forbidden)
                problems.Add($"{role} sollte 403 bekommen, bekam aber {(int)status}");
        }

        Assert.True(problems.Count == 0,
            $"{method} /{template} (Stufe {rule.Tier}): {string.Join("; ", problems)}");
    }

    /// <summary>
    /// Die Tabelle deckt JEDE Route ab: keine Zeile ohne Route (Tippfehler), keine Route ohne Zeile - gleich in welcher
    /// Controller-Klasse sie liegt (auch Klassen mit demselben Routenpräfix wie ein anderer Controller, etwa
    /// OrderCancellationController und PurchaseOrderInboundController) - und jede per Reflection gezählte [Http*]-Action
    /// hat eine Zeile oder eine ausdrückliche Ausnahme in <see cref="EndpointMatrix.Exemptions"/>.
    /// </summary>
    [Fact]
    public void Endpoint_table_matches_the_real_routes()
    {
        var endpoints = _api.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<ControllerActionDescriptor>() is not null)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? Array.Empty<string>())
                .Select(m => (
                    Controller: e.Metadata.GetMetadata<ControllerActionDescriptor>()!.ControllerTypeInfo.Name,
                    Key: $"{m} {e.RoutePattern.RawText!.TrimStart('/')}")))
            .ToList();

        var rows = EndpointMatrix.Rules.Select(r => r.Key).ToList();
        var known = rows.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = endpoints.Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Doppelte Zeilen: eine Zeile pro Route, sonst ist unklar, welche Stufe gilt.
        var duplicates = rows.GroupBy(k => k, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).Order().ToList();
        Assert.True(duplicates.Count == 0, "Doppelte Tabellenzeilen: " + string.Join(", ", duplicates));

        // Tippfehler/veraltete Zeilen: jede Tabellenzeile muss eine echte Route sein.
        var stale = known.Except(actual).Order(StringComparer.Ordinal).ToList();
        Assert.True(stale.Count == 0, "Tabellenzeilen ohne passende Route: " + string.Join(", ", stale));

        // Vergessene Endpunkte: JEDE Route jeder Controller-Klasse braucht eine Zeile - nicht nur die Klassen, die
        // schon in der Tabelle vorkommen (sonst bliebe die erste Route einer neuen Klasse ungeprüft).
        var unlisted = endpoints
            .Where(e => EndpointMatrix.Uncovered(new[] { e.Key }).Count > 0)
            .Select(e => $"{e.Controller}: {e.Key}").Distinct().Order(StringComparer.Ordinal).ToList();
        Assert.True(unlisted.Count == 0, "Endpunkte ohne Zeile in EndpointMatrix: " + string.Join(", ", unlisted));

        // Reflection: alle [Http*]-Actions aller Controller-Klassen der API-Assembly. Jede braucht eine Zeile oder eine
        // ausdrückliche Ausnahme, und die Zählung stimmt mit dem Routing-System überein.
        var scanned = EndpointMatrix.ScanActions(typeof(Program).Assembly.GetTypes());
        var uncovered = EndpointMatrix.Uncovered(scanned.Select(a => a.Key));
        Assert.True(uncovered.Count == 0,
            "[Http*]-Actions ohne Zeile (oder ausdrückliche Ausnahme) in EndpointMatrix: " + string.Join(", ", uncovered));

        var scannedKeys = scanned.Select(a => a.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var notRouted = scannedKeys.Except(actual).Order(StringComparer.Ordinal).ToList();
        var notScanned = actual.Except(scannedKeys).Order(StringComparer.Ordinal).ToList();
        Assert.True(notRouted.Count == 0, "Per Reflection gefundene Actions ohne Route im Host: " + string.Join(", ", notRouted));
        Assert.True(notScanned.Count == 0, "Routen im Host, die die Reflection-Zählung nicht findet: " + string.Join(", ", notScanned));

        // Ausnahmen müssen echte Routen ohne Tabellenzeile sein (sonst sind sie tot oder überflüssig).
        var badExemptions = EndpointMatrix.Exemptions.Keys.Where(k => !actual.Contains(k) || known.Contains(k)).Order().ToList();
        Assert.True(badExemptions.Count == 0, "Ausnahmen ohne Route oder mit Tabellenzeile: " + string.Join(", ", badExemptions));
        Assert.All(EndpointMatrix.Exemptions.Values, reason => Assert.False(string.IsNullOrWhiteSpace(reason), "Eine Ausnahme braucht eine Begründung"));
    }

    [Fact]
    public async Task Viewer_can_read_but_every_write_is_rejected_before_it_runs()
    {
        var viewer = _api.Client("Viewer");
        var manager = _api.Client("Manager");

        // Lesen ist erlaubt (Liste 200, unbekannte Id 404 - nie 403).
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/articles")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await viewer.GetAsync(EndpointMatrix.ToPath("api/articles/{id:guid}"))).StatusCode);

        // Die vier Schreibzugriffe aus der Akzeptanz: 403.
        var article = NewArticle("WP02-VIEWER-1");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/articles", article)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/stock/adjust",
            new AdjustStockRequest(Guid.NewGuid(), Guid.NewGuid(), 5, null, null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/inbound", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/picklists/generate", new { })).StatusCode);

        // Die gesperrte Anfrage hat nichts angelegt ...
        var afterViewer = await manager.GetFromJsonAsync<List<ArticleDto>>("/api/articles");
        Assert.DoesNotContain(afterViewer!, a => a.Sku == "WP02-VIEWER-1");

        // ... während derselbe Aufruf mit Manager-Rolle wirklich durchläuft (Policy sperrt keine legitime Nutzung).
        var created = await manager.PostAsJsonAsync("/api/articles", NewArticle("WP02-MANAGER-1"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var articles = await viewer.GetFromJsonAsync<List<ArticleDto>>("/api/articles");
        Assert.Contains(articles!, a => a.Sku == "WP02-MANAGER-1");
    }

    [Fact]
    public async Task Picker_may_not_pack_but_packer_may()
    {
        var path = EndpointMatrix.ToPath("api/picklists/{id:guid}/pack");

        Assert.Equal(HttpStatusCode.Forbidden, await _api.SendAsync("Picker", "POST", path));
        Assert.NotEqual(HttpStatusCode.Forbidden, await _api.SendAsync("Packer", "POST", path));
        Assert.NotEqual(HttpStatusCode.Forbidden, await _api.SendAsync("Admin", "POST", path));
    }

    [Fact]
    public async Task Picker_may_mark_picked_but_packer_may_not()
    {
        var path = EndpointMatrix.ToPath("api/picklists/{id:guid}/mark-picked");

        Assert.NotEqual(HttpStatusCode.Forbidden, await _api.SendAsync("Picker", "POST", path));
        Assert.Equal(HttpStatusCode.Forbidden, await _api.SendAsync("Packer", "POST", path));
    }

    [Fact]
    public async Task Resetting_all_picklists_is_admin_only()
    {
        // Manager ist für alles Operative zuständig, aber das Löschen ALLER Picklisten bleibt dem Admin vorbehalten.
        var manager = await _api.Client("Manager").DeleteAsync("/api/picklists");
        Assert.Equal(HttpStatusCode.Forbidden, manager.StatusCode);

        var admin = await _api.Client("Admin").DeleteAsync("/api/picklists");
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
    }

    [Fact]
    public async Task Admin_endpoints_stop_non_admins_at_the_policy_not_at_the_environment_check()
    {
        // Reseed prüft im Controller zusätzlich "nur Development" und liefert dann selbst 403 mit Meldung.
        // Damit lässt sich unterscheiden, wer schon an der Policy scheitert: der Manager (leerer 403) oder
        // der Admin, der bis in die Action kommt (403 mit "Development"-Meldung). Testumgebung = Testing.
        var manager = await _api.Client("Manager").PostAsync("/api/admin/reseed", null);
        Assert.Equal(HttpStatusCode.Forbidden, manager.StatusCode);
        Assert.DoesNotContain("Development", await manager.Content.ReadAsStringAsync());

        var admin = await _api.Client("Admin").PostAsync("/api/admin/reseed", null);
        Assert.Equal(HttpStatusCode.Forbidden, admin.StatusCode);
        Assert.Contains("Development", await admin.Content.ReadAsStringAsync());
    }

    private static CreateArticleRequest NewArticle(string sku) => new(
        Sku: sku,
        Name: $"Testartikel {sku}",
        Description: null,
        Dimensions: new DimensionsDto(100, 100, 100),
        WeightGrams: 250,
        Stacking: new StackingInfoDto(false, "Z", 0, null));
}
