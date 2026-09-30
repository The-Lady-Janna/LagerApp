using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Lager.Tests.WP02;

/// <summary>
/// Mindestberechtigung eines Endpunkts. Die Stufen spiegeln die Policies aus <c>Program.cs</c>:
/// Authenticated = jeder eingeloggte Nutzer (auch Viewer), Receiver/Picker/Packer = jeweilige Rolle + Manager + Admin,
/// Manager = Manager + Admin, Admin = nur Admin.
/// </summary>
public enum Tier { Anonymous, Authenticated, Receiver, Picker, Packer, Manager, Admin }

/// <summary>
/// Eine Zeile der Policy-Matrix. <paramref name="Probe"/> = false heißt: der Endpunkt wird nicht per HTTP
/// aufgerufen (Login ist anonym, Admin-Endpunkte haben Nebenwirkungen wie Backup/Reseed) und nur über die
/// Reflection-Tests bzw. gezielte Einzeltests abgedeckt.
/// </summary>
public sealed record EndpointRule(string Method, string Template, Tier Tier, bool Probe = true)
{
    public string Key => $"{Method} {Template}";
}

/// <summary>
/// Die Endpunkt-Tabelle (Methode, Route, erlaubte Rollen) als einzige Quelle der Wahrheit für die Rollen-Tests.
/// Wer einen Endpunkt in einem Controller ergänzt - auch in einer weiteren Controller-Klasse mit demselben
/// Routenpräfix -, muss hier eine Zeile ergänzen (siehe <c>Endpoint_table_matches_the_real_routes</c>, das jede
/// [Http*]-Action jeder Controller-Klasse per Reflection zählt) und damit die Rollenfrage bewusst beantworten.
/// Eine Action ohne Zeile ist nur mit ausdrücklicher Begründung in <see cref="Exemptions"/> erlaubt.
/// </summary>
public static class EndpointMatrix
{
    /// <summary>Alle Rollen, für die ein Testnutzer mit genau dieser einen Rolle angelegt wird.</summary>
    public static readonly string[] Roles = { "Viewer", "Picker", "Packer", "Receiver", "Manager", "Admin" };

    /// <summary>Feste Test-Id für Routenparameter: existiert nie, liefert also 404 statt Datenänderungen.</summary>
    private const string UnknownId = "00000000-0000-4000-8000-0000000000a2";

    private static readonly Regex Placeholder = new(@"\{(?<name>\w+)(:[^}]*)?\}", RegexOptions.Compiled);

    public static bool IsAllowed(Tier tier, string role) => tier switch
    {
        Tier.Authenticated => true,
        Tier.Receiver => role is "Admin" or "Manager" or "Receiver",
        Tier.Picker => role is "Admin" or "Manager" or "Picker",
        Tier.Packer => role is "Admin" or "Manager" or "Packer",
        Tier.Manager => role is "Admin" or "Manager",
        Tier.Admin => role is "Admin",
        _ => throw new ArgumentOutOfRangeException(nameof(tier), tier, "Anonyme Endpunkte haben keine Rollenstufe"),
    };

    public static EndpointRule Find(string method, string template) =>
        Rules.Single(r => r.Method == method && r.Template == template);

    /// <summary>
    /// Ausdrückliche Ausnahmen: Actions, die bewusst KEINE Tabellenzeile haben (Schlüssel "METHOD route", Wert = Begründung).
    /// Leer, solange jede Action eine Zeile hat; ein neuer Eintrag ist eine bewusste Entscheidung im Code-Review.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Exemptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Eine [Http*]-Action einer Controller-Klasse: Klasse, Schlüssel "METHOD route" (wie <see cref="EndpointRule.Key"/>).</summary>
    public sealed record ScannedAction(Type Controller, string Key);

    /// <summary>
    /// Zählt per Reflection ALLE [Http*]-Actions der Controller-Klassen unter <paramref name="types"/> (Klassen-[Route]
    /// plus Action-Template, mehrere HTTP-Methoden je Attribut ergeben mehrere Einträge). Unabhängig vom Routing-System:
    /// eine Action, die dort aus irgendeinem Grund nicht auftaucht, fällt trotzdem auf.
    /// </summary>
    public static IReadOnlyList<ScannedAction> ScanActions(IEnumerable<Type> types)
    {
        var result = new List<ScannedAction>();
        foreach (var controller in types
                     .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ControllerBase).IsAssignableFrom(t))
                     .OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            var prefixes = controller.GetCustomAttributes<RouteAttribute>(inherit: true).Select(r => r.Template).DefaultIfEmpty("").ToList();
            var name = controller.Name.EndsWith("Controller", StringComparison.Ordinal) ? controller.Name[..^"Controller".Length] : controller.Name;

            foreach (var action in controller.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                         .Where(m => !m.IsDefined(typeof(NonActionAttribute), inherit: true)))
            {
                foreach (var http in action.GetCustomAttributes<HttpMethodAttribute>(inherit: true))
                {
                    var template = http.Template ?? "";
                    var absolute = template.StartsWith('/') || template.StartsWith("~/", StringComparison.Ordinal);
                    foreach (var prefix in absolute ? new List<string> { "" } : prefixes)
                    {
                        var route = string.Join('/', new[] { prefix, template }
                            .Select(p => p.TrimStart('~').Trim('/')).Where(p => p.Length > 0))
                            .Replace("[controller]", name, StringComparison.OrdinalIgnoreCase)
                            .Replace("[action]", action.Name, StringComparison.OrdinalIgnoreCase);
                        foreach (var method in http.HttpMethods)
                            result.Add(new ScannedAction(controller, $"{method} {route}"));
                    }
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Die Schlüssel unter <paramref name="actualKeys"/>, die weder eine Tabellenzeile noch eine ausdrückliche Ausnahme
    /// haben - also Endpunkte, für die die Rollenfrage nie beantwortet wurde.
    /// </summary>
    public static IReadOnlyList<string> Uncovered(IEnumerable<string> actualKeys)
    {
        var known = Rules.Select(r => r.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return actualKeys
            .Where(k => !known.Contains(k) && !Exemptions.ContainsKey(k))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Setzt für jeden Routenparameter einen Platzhalterwert ein und stellt "/" voran.</summary>
    public static string ToPath(string template) =>
        "/" + Placeholder.Replace(template, m => m.Groups["name"].Value == "lotNumber" ? "LOT-WP02" : UnknownId);

    private static EndpointRule Get(string template, Tier tier = Tier.Authenticated) => new("GET", template, tier);
    private static EndpointRule Post(string template, Tier tier) => new("POST", template, tier);
    private static EndpointRule Put(string template, Tier tier) => new("PUT", template, tier);
    private static EndpointRule Delete(string template, Tier tier) => new("DELETE", template, tier);

    public static readonly IReadOnlyList<EndpointRule> Rules = new[]
    {
        // Admin: nur Admin (Reseed/Backup/Restore haben Nebenwirkungen -> nicht per HTTP aufgerufen)
        new EndpointRule("POST", "api/admin/reseed", Tier.Admin, Probe: false),
        new EndpointRule("POST", "api/admin/seed-bulk", Tier.Admin, Probe: false),
        new EndpointRule("POST", "api/admin/backup", Tier.Admin, Probe: false),
        new EndpointRule("POST", "api/admin/restore", Tier.Admin, Probe: false),
        new EndpointRule("GET", "api/admin/backup-settings", Tier.Admin, Probe: false),
        new EndpointRule("GET", "api/admin/backups", Tier.Admin, Probe: false),
        new EndpointRule("POST", "api/admin/backups", Tier.Admin, Probe: false),
        new EndpointRule("GET", "api/admin/backups/{name}", Tier.Admin, Probe: false),
        new EndpointRule("DELETE", "api/admin/backups/{name}", Tier.Admin, Probe: false),

        // Articles: lesen für alle, Stammdaten pflegen = Manager
        Get("api/articles"),
        Get("api/articles/{id:guid}"),
        Get("api/articles/by-code/{**code}"),
        Post("api/articles", Tier.Manager),
        Put("api/articles/{id:guid}", Tier.Manager),

        // Audit: komplett Manager
        Get("api/audit", Tier.Manager),
        Get("api/audit/entity-types", Tier.Manager),

        // Auth: Login anonym, me/change-password für jeden eingeloggten Nutzer
        new EndpointRule("POST", "api/auth/login", Tier.Anonymous, Probe: false),
        Get("api/auth/me"),
        new EndpointRule("POST", "api/auth/change-password", Tier.Authenticated, Probe: false),

        // Customers
        Get("api/customers"),
        Get("api/customers/{id:guid}"),
        Post("api/customers", Tier.Manager),
        Put("api/customers/{id:guid}", Tier.Manager),
        Post("api/customers/{id:guid}/addresses", Tier.Manager),
        Delete("api/customers/{id:guid}/addresses/{addressId:guid}", Tier.Manager),
        Post("api/customers/{id:guid}/activate", Tier.Manager),
        Post("api/customers/{id:guid}/deactivate", Tier.Manager),

        // Hardware (Waage): Packer
        Get("api/hardware/scale/status", Tier.Packer),
        Get("api/hardware/scale/read", Tier.Packer),

        // Inbound: anlegen/empfangen = Receiver, stornieren = Manager
        Get("api/inbound"),
        Get("api/inbound/{id:guid}"),
        Post("api/inbound", Tier.Receiver),
        Post("api/inbound/{id:guid}/lines", Tier.Receiver),
        Delete("api/inbound/{id:guid}/lines/{lineId:guid}", Tier.Receiver),
        Post("api/inbound/{id:guid}/receive", Tier.Receiver),
        Post("api/inbound/{id:guid}/cancel", Tier.Manager),

        // Inventory: anlegen/zählen = Receiver, abschließen/abbrechen = Manager
        Get("api/inventory"),
        Get("api/inventory/{id:guid}"),
        Post("api/inventory/start", Tier.Receiver),
        Put("api/inventory/{id:guid}/lines/{lineId:guid}", Tier.Receiver),
        Post("api/inventory/{id:guid}/reconcile", Tier.Manager),
        Post("api/inventory/{id:guid}/cancel", Tier.Manager),

        // Labels: Picker
        Get("api/labels/bin/{binId:guid}.zpl", Tier.Picker),
        Get("api/labels/article/{articleId:guid}.zpl", Tier.Picker),
        Get("api/labels/order/{orderId:guid}.zpl", Tier.Picker),

        // Orders: Anlegen (manuell und externe API) = Manager
        Get("api/orders"),
        Get("api/orders/{id:guid}"),
        Post("api/orders/manual", Tier.Manager),
        Post("api/orders", Tier.Manager),
        // Storno steht in einer eigenen Controller-Klasse (OrderCancellationController, gleiches Routenpräfix)
        Post("api/orders/{id:guid}/cancel", Tier.Manager),

        // Packing: der Plan wird nur berechnet (keine Mutation) und auf der Bestell-Detailseite für jede Rolle
        // angezeigt -> jeder eingeloggte Nutzer darf ihn abrufen
        Post("api/packing/{orderId:guid}/plan", Tier.Authenticated),

        // Pick-Cart-Konfigurationen
        Get("api/cart-configs"),
        Get("api/cart-configs/{id:guid}"),
        Post("api/cart-configs", Tier.Manager),
        Put("api/cart-configs/{id:guid}", Tier.Manager),
        Delete("api/cart-configs/{id:guid}", Tier.Manager),

        // Picklisten: erzeugen = Manager, picken = Picker, packen = Packer, Reset = Admin
        Post("api/picklists/generate", Tier.Manager),
        Get("api/picklists"),
        Get("api/picklists/{id:guid}"),
        Post("api/picklists/{id:guid}/recalculate", Tier.Manager),
        Delete("api/picklists", Tier.Admin),
        Post("api/picklists/generate-cart", Tier.Manager),
        Post("api/picklists/{id:guid}/pack", Tier.Packer),
        Post("api/picklists/{id:guid}/mark-picked", Tier.Picker),
        Get("api/picklists/{id:guid}/shipping-label.pdf"),

        // Pick-Waves
        Get("api/pick-waves"),
        Get("api/pick-waves/{id:guid}"),
        Post("api/pick-waves", Tier.Manager),
        Post("api/pick-waves/{id:guid}/orders", Tier.Manager),
        Delete("api/pick-waves/{id:guid}/orders/{orderId:guid}", Tier.Manager),
        Post("api/pick-waves/{id:guid}/release", Tier.Manager),
        Post("api/pick-waves/{id:guid}/cancel", Tier.Manager),

        // Bestellungen beim Lieferanten: Wareneingang der Zeile = Receiver, alles andere = Manager
        Get("api/purchase-orders"),
        Get("api/purchase-orders/suggestions"),
        Get("api/purchase-orders/{id:guid}"),
        Post("api/purchase-orders", Tier.Manager),
        Post("api/purchase-orders/{id:guid}/lines", Tier.Manager),
        Delete("api/purchase-orders/{id:guid}/lines/{lineId:guid}", Tier.Manager),
        Post("api/purchase-orders/{id:guid}/send", Tier.Manager),
        Post("api/purchase-orders/{id:guid}/lines/{lineId:guid}/receive", Tier.Receiver),
        Post("api/purchase-orders/{id:guid}/cancel", Tier.Manager),
        // Brücke Bestellung -> Wareneingang (PurchaseOrderInboundController): wie der Wareneingang selbst = Receiver
        Post("api/purchase-orders/{id:guid}/create-inbound", Tier.Receiver),

        // Nachschub: Scan/Abbruch = Manager, Ausführen = Picker
        Get("api/replenishment"),
        Get("api/replenishment/open"),
        Post("api/replenishment/scan", Tier.Manager),
        Post("api/replenishment/{id:guid}/complete", Tier.Picker),
        Post("api/replenishment/{id:guid}/cancel", Tier.Manager),

        // Reports: lesen für alle, nur die Bestandsbewertung ist Manager
        Get("api/reports/dashboard"),
        Get("api/reports/bin-heatmap"),
        Get("api/reports/dead-stock"),
        Get("api/reports/abc-analysis"),
        Get("api/reports/live-status"),
        Get("api/reports/stock-trend/{articleId:guid}"),
        Get("api/reports/charge/{lotNumber}"),
        Get("api/reports/stock-valuation", Tier.Manager),
        Get("api/reports/expiring"),
        // CSV-Import/-Export (WP24): ganze Klasse Manager
        Get("api/export/articles.csv", Tier.Manager),
        Get("api/export/stock.csv", Tier.Manager),
        Get("api/export/orders.csv", Tier.Manager),
        Get("api/export/movements.csv", Tier.Manager),
        Get("api/export/audit.csv", Tier.Manager),
        Post("api/import/articles", Tier.Manager),
        Post("api/import/stock", Tier.Manager),
        Post("api/import/orders", Tier.Manager),
        Get("api/reports/picker-performance"),

        // Retouren: anlegen = Receiver, Qualitätsprüfung/Verarbeiten/Abbrechen = Manager
        Get("api/returns"),
        Get("api/returns/{id:guid}"),
        Post("api/returns", Tier.Receiver),
        Post("api/returns/{id:guid}/lines", Tier.Receiver),
        Put("api/returns/{id:guid}/lines/{lineId:guid}/qc", Tier.Manager),
        Post("api/returns/{id:guid}/process", Tier.Manager),
        Post("api/returns/{id:guid}/cancel", Tier.Manager),

        // Versand: anlegen/Tracking/Versand = Packer, stornieren = Manager
        Get("api/shipments/carriers"),
        Get("api/shipments"),
        Get("api/shipments/{id:guid}"),
        Post("api/shipments", Tier.Packer),
        Post("api/shipments/{id:guid}/tracking", Tier.Packer),
        Post("api/shipments/{id:guid}/ship", Tier.Packer),
        Post("api/shipments/{id:guid}/delivered", Tier.Packer),
        Post("api/shipments/{id:guid}/cancel", Tier.Manager),

        // Slotting: nur Vorschläge (lesend)
        Get("api/slotting/suggestions"),
        Get("api/slotting/putaway"),

        // Bestand: Korrektur = Manager
        Get("api/stock"),
        Get("api/stock/summary"),
        Get("api/stock/article/{articleId:guid}"),
        Post("api/stock/adjust", Tier.Manager),
        Get("api/stock/alerts"),

        // Lieferanten
        Get("api/suppliers"),
        Get("api/suppliers/{id:guid}"),
        Post("api/suppliers", Tier.Manager),
        Put("api/suppliers/{id:guid}", Tier.Manager),
        Post("api/suppliers/{id:guid}/activate", Tier.Manager),
        Post("api/suppliers/{id:guid}/deactivate", Tier.Manager),

        // Benutzerverwaltung: komplett Admin
        Get("api/users", Tier.Admin),
        Post("api/users", Tier.Admin),
        Put("api/users/{id:guid}", Tier.Admin),
        Post("api/users/{id:guid}/reset-password", Tier.Admin),
        Post("api/users/{id:guid}/activate", Tier.Admin),
        Post("api/users/{id:guid}/deactivate", Tier.Admin),

        // Lager-Layout: lesen für alle, ändern/löschen = Manager
        Get("api/warehouse/layout"),
        Get("api/warehouse/{id:guid}"),
        Get("api/warehouse/storage-locations"),
        Post("api/warehouse/storage-locations", Tier.Manager),
        Put("api/warehouse/storage-locations/{id:guid}/position", Tier.Manager),
        Put("api/warehouse/shelves/{id:guid}/position", Tier.Manager),
        Post("api/warehouse/shelves", Tier.Manager),
        Post("api/warehouse/shelves/{id:guid}/bins", Tier.Manager),
        Delete("api/warehouse/storage-locations/{id:guid}", Tier.Manager),
        Put("api/warehouse/storage-locations/{id:guid}/bin-type", Tier.Manager),
        Post("api/warehouse", Tier.Manager),
        Put("api/warehouse/{id:guid}", Tier.Manager),
        Delete("api/warehouse/{id:guid}", Tier.Manager),
        Post("api/warehouse/zones", Tier.Manager),
        Put("api/warehouse/zones/{id:guid}", Tier.Manager),
        Delete("api/warehouse/zones/{id:guid}", Tier.Manager),
        Post("api/warehouse/aisles", Tier.Manager),
        Put("api/warehouse/aisles/{id:guid}", Tier.Manager),
        Delete("api/warehouse/aisles/{id:guid}", Tier.Manager),
        Put("api/warehouse/shelves/{id:guid}", Tier.Manager),
        Delete("api/warehouse/shelves/{id:guid}", Tier.Manager),
        Put("api/warehouse/storage-locations/{id:guid}", Tier.Manager),
        Get("api/warehouse/walls"),
        Post("api/warehouse/walls", Tier.Manager),
        Put("api/warehouse/walls/{id:guid}/points", Tier.Manager),
        Delete("api/warehouse/walls/{id:guid}", Tier.Manager),
        Get("api/warehouse/pick-points"),
        Post("api/warehouse/pick-points", Tier.Manager),
        Put("api/warehouse/pick-points/{id:guid}", Tier.Manager),
        Put("api/warehouse/pick-points/{id:guid}/position", Tier.Manager),
        Delete("api/warehouse/pick-points/{id:guid}", Tier.Manager),
    };
}
