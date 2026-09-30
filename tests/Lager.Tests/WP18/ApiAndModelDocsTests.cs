using System.Reflection;
using System.Text.RegularExpressions;
using Lager.Api.Controllers;
using Lager.Domain.Stock;
using Lager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;

namespace Lager.Tests.WP18;

/// <summary>
/// docs/API.md und docs/DATA_MODEL.md gegen den Code: jede dokumentierte Route existiert mit derselben Methode und derselben
/// Mindestrolle, und das ER-Diagramm zeigt nur Entitäten, Eigenschaften und Fremdschlüssel, die das EF-Modell wirklich hat.
/// Bewusst nur in dieser Richtung: neue Endpunkte anderer Pakete brechen diese Tests nicht.
/// </summary>
public class ApiAndModelDocsTests
{
    // ---------- API.md: Routen und Rollen ----------

    private sealed record ActualRoute(string Method, string Path, string Tier);

    private static readonly string[] PolicyNames = { "Admin", "Manager", "Picker", "Packer", "Receiver" };

    private static readonly Dictionary<string, string[]> PolicyRoles = new()
    {
        ["Admin"] = new[] { "Admin" },
        ["Manager"] = new[] { "Admin", "Manager" },
        ["Picker"] = new[] { "Admin", "Manager", "Picker" },
        ["Packer"] = new[] { "Admin", "Manager", "Packer" },
        ["Receiver"] = new[] { "Admin", "Manager", "Receiver" },
    };

    private static string Normalize(string path) =>
        Regex.Replace("/" + path.Trim('/'), @"\{\*{0,2}(\w+)(:[^}]*)?\}", "{$1}").ToLowerInvariant();

    /// <summary>Alle Routen der Controller mit Methode und Mindestberechtigung, ermittelt aus den Attributen.</summary>
    private static List<ActualRoute> ActualRoutes()
    {
        var routes = new List<ActualRoute>();
        foreach (var controller in typeof(AuthController).Assembly.GetTypes()
                     .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t)))
        {
            var prefix = controller.GetCustomAttribute<RouteAttribute>()?.Template ?? string.Empty;
            foreach (var method in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                foreach (var http in method.GetCustomAttributes(true).OfType<HttpMethodAttribute>())
                {
                    var path = string.IsNullOrEmpty(http.Template) ? prefix : $"{prefix}/{http.Template}";
                    foreach (var verb in http.HttpMethods)
                        routes.Add(new ActualRoute(verb, Normalize(path), TierOf(controller, method)));
                }
            }
        }

        Assert.True(routes.Count >= 100, $"Nur {routes.Count} Routen gefunden - Reflection greift nicht");

        // GET /api/version ist bewusst kein Controller (Minimal-API in Program.cs, siehe ApiVersionInfo): jeder Angemeldete, keine Rolle.
        routes.Add(new ActualRoute("GET", Normalize(ApiVersionInfo.Route), "angemeldet"));
        return routes;
    }

    /// <summary>"anonym", "angemeldet" oder der Name der strengsten Policy (bei mehreren gilt die Schnittmenge der Rollen).</summary>
    private static string TierOf(Type controller, MethodInfo method)
    {
        var attributes = controller.GetCustomAttributes(true).Concat(method.GetCustomAttributes(true)).ToList();
        if (attributes.OfType<IAllowAnonymous>().Any()) return "anonym";

        var allowed = attributes.OfType<IAuthorizeData>()
            .Select(a => a.Policy)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => PolicyRoles[p!].ToHashSet())
            .Aggregate((HashSet<string>?)null, (acc, next) => acc is null ? next : acc.Intersect(next).ToHashSet());
        if (allowed is null) return "angemeldet";

        return PolicyNames.First(p => PolicyRoles[p].ToHashSet().SetEquals(allowed));
    }

    [Fact]
    public void Every_route_and_role_listed_in_the_api_reference_matches_the_controllers()
    {
        var text = DocsRepo.Read("docs/API.md");
        var actual = ActualRoutes()
            .GroupBy(r => (r.Method, r.Path))
            .ToDictionary(g => g.Key, g => g.Select(r => r.Tier).Distinct().ToList());

        var rows = DocsRepo.TableRows(text, l => Regex.IsMatch(l, @"^\|\s*(GET|POST|PUT|DELETE)\s*\|\s*/api/"))
            .Select(c => (Method: c[0], Path: Normalize(c[1]), Tier: c[2]))
            .ToList();
        Assert.True(rows.Count >= 120, $"Nur {rows.Count} Endpunkt-Zeilen in API.md gefunden - Auswertung greift nicht");

        var problems = new List<string>();
        foreach (var (method, path, tier) in rows)
        {
            if (!actual.TryGetValue((method, path), out var tiers))
            {
                problems.Add($"{method} {path}: gibt es nicht");
                continue;
            }
            if (!tiers.Contains(tier))
                problems.Add($"{method} {path}: Doku sagt '{tier}', Code verlangt '{string.Join("/", tiers)}'");
        }

        var duplicates = rows.GroupBy(r => (r.Method, r.Path)).Where(g => g.Count() > 1).Select(g => $"{g.Key.Method} {g.Key.Path}").ToList();
        problems.AddRange(duplicates.Select(d => $"{d}: mehrfach dokumentiert"));

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    // ---------- DATA_MODEL.md: ER-Diagramm und Ledger-Gründe ----------

    private static LagerDbContext ModelContext() =>
        new(new DbContextOptionsBuilder<LagerDbContext>().UseSqlite("Data Source=:memory:").Options);

    [Fact]
    public void The_er_diagram_only_shows_entities_properties_and_foreign_keys_of_the_ef_model()
    {
        var text = DocsRepo.Read("docs/DATA_MODEL.md");
        var start = text.IndexOf("```mermaid", StringComparison.Ordinal);
        var end = text.IndexOf("```", start + 10, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "Kein Mermaid-ER-Diagramm in DATA_MODEL.md");
        var diagram = text[start..end];
        Assert.Contains("erDiagram", diagram);

        using var db = ModelContext();
        var entities = db.Model.GetEntityTypes().Where(t => !t.IsOwned()).ToDictionary(t => t.ClrType.Name, t => t);
        var problems = new List<string>();

        // Beziehungen: A ||--o{ B : Beschriftung  (durchgezogen = Fremdschlüssel B -> A)
        var relations = Regex.Matches(diagram, @"^\s*(?<a>\w+)\s+(?<rel>[|}{o]+(?<line>--|\.\.)[|}{o]+)\s+(?<b>\w+)\s*:", RegexOptions.Multiline);
        Assert.True(relations.Count >= 30, $"Nur {relations.Count} Beziehungen gefunden - Auswertung greift nicht");
        foreach (Match m in relations)
        {
            var (a, b) = (m.Groups["a"].Value, m.Groups["b"].Value);
            if (!entities.TryGetValue(a, out var parent)) { problems.Add($"Entität '{a}' gibt es im Modell nicht"); continue; }
            if (!entities.TryGetValue(b, out var child)) { problems.Add($"Entität '{b}' gibt es im Modell nicht"); continue; }
            if (m.Groups["line"].Value != "--") continue;

            var hasForeignKey = child.GetForeignKeys().Any(fk => fk.PrincipalEntityType == parent);
            if (!hasForeignKey) problems.Add($"{b} -> {a}: kein Fremdschlüssel im Modell");
        }

        // Attribut-Blöcke: Name { Typ Eigenschaft [PK|FK|UK] }
        foreach (Match block in Regex.Matches(diagram, @"^\s*(?<name>\w+)\s*\{\s*\n(?<body>.*?)^\s*\}", RegexOptions.Multiline | RegexOptions.Singleline))
        {
            var name = block.Groups["name"].Value;
            if (!entities.TryGetValue(name, out var entity)) { problems.Add($"Entität '{name}' (Attribute) gibt es im Modell nicht"); continue; }

            var properties = entity.GetProperties().Select(p => p.Name).ToHashSet();
            foreach (Match attribute in Regex.Matches(block.Groups["body"].Value, @"^\s*\w+\s+(?<prop>\w+)(\s+(PK|FK|UK)(\s*,\s*(PK|FK|UK))*)?\s*$", RegexOptions.Multiline))
                if (!properties.Contains(attribute.Groups["prop"].Value))
                    problems.Add($"{name}.{attribute.Groups["prop"].Value}: Eigenschaft gibt es im Modell nicht");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Ledger_reasons_and_tables_named_in_the_data_model_exist()
    {
        var text = DocsRepo.Read("docs/DATA_MODEL.md");
        var reasons = Enum.GetNames<StockMovementReason>().ToHashSet();

        // Tabelle "Grund (`Reason`)": jede genannte Bezeichnung ist ein Wert des Enums (neue Werte anderer Pakete stören nicht).
        var section = text[text.IndexOf("| Grund (`Reason`)", StringComparison.Ordinal)..];
        section = section[..section.IndexOf("\n\n", StringComparison.Ordinal)];
        var documented = Regex.Matches(section, @"`([A-Za-z]+)`").Select(m => m.Groups[1].Value).Where(s => s != "Reason").ToHashSet();
        Assert.Empty(documented.Except(reasons));

        // Tabellennamen aus der Übersichtstabelle existieren im Modell (oder sind die Schema-Versionstabelle).
        using var db = ModelContext();
        var tables = db.Model.GetEntityTypes().Select(t => t.GetTableName()).Where(n => n is not null).ToHashSet();
        var overview = text[text.IndexOf("## Tabellen im Überblick", StringComparison.Ordinal)..text.IndexOf("**Benutzer:**", StringComparison.Ordinal)];
        var named = Regex.Matches(overview, @"`([A-Z_][A-Za-z_]+)`").Select(m => m.Groups[1].Value)
            .Where(n => n != "DbSet" && n != "SchemaUpgrader" && n != "EnsureCreated")
            .Distinct()
            .ToList();
        var unknown = named.Where(n => !tables.Contains(n) && n != "__LagerSchemaVersion").ToList();
        Assert.True(unknown.Count == 0, "Tabellen in der Übersicht, die das Modell nicht kennt: " + string.Join(", ", unknown));
    }
}
