using System.Reflection;
using Lager.Api.Controllers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;

namespace Lager.Tests.WP02;

/// <summary>
/// Reflection-Tests ohne Host: prüfen die Autorisierungs-Attribute aller Controller-Actions. Sie fangen
/// vor allem neu hinzugefügte Endpunkte ab, die ohne Schutz oder ohne Rollenbezug ausgeliefert würden.
/// </summary>
public class AuthorizationAttributeTests
{
    /// <summary>Policy-Namen, die <c>Program.cs</c> definiert. Ein anderer Name würde zur Laufzeit mit 500 scheitern.</summary>
    private static readonly string[] KnownPolicies = { "Admin", "Manager", "Picker", "Packer", "Receiver" };

    private static readonly string[] ReadVerbs = { "GET", "HEAD", "OPTIONS" };

    private sealed record ActionInfo(Type Controller, MethodInfo Method, string[] HttpMethods)
    {
        public string Name => $"{Controller.Name}.{Method.Name}";

        public bool IsRead => HttpMethods.All(m => ReadVerbs.Contains(m));

        public bool IsAnonymous =>
            Controller.GetCustomAttributes(true).OfType<IAllowAnonymous>().Any()
            || Method.GetCustomAttributes(true).OfType<IAllowAnonymous>().Any();

        public IReadOnlyList<IAuthorizeData> Authorization =>
            Controller.GetCustomAttributes(true).OfType<IAuthorizeData>()
                .Concat(Method.GetCustomAttributes(true).OfType<IAuthorizeData>())
                .ToList();

        /// <summary>Verlangt die Action eine bestimmte Rolle bzw. Policy (statt nur "eingeloggt")?</summary>
        public bool RequiresRoleOrPolicy =>
            Authorization.Any(a => !string.IsNullOrEmpty(a.Policy) || !string.IsNullOrEmpty(a.Roles));
    }

    private static IReadOnlyList<ActionInfo> AllActions()
    {
        var actions = typeof(AuthController).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract && typeof(ControllerBase).IsAssignableFrom(t))
            .SelectMany(controller => controller
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(m => m.GetCustomAttribute<NonActionAttribute>() is null)
                .Select(m => new ActionInfo(controller, m, m.GetCustomAttributes(true)
                    .OfType<IActionHttpMethodProvider>().SelectMany(p => p.HttpMethods).ToArray()))
                .Where(a => a.HttpMethods.Length > 0))
            .ToList();

        // Schutz gegen einen leeren Fund, der alle folgenden Tests wirkungslos machen würde.
        Assert.True(actions.Count >= 100, $"Nur {actions.Count} Controller-Actions gefunden - Reflection greift nicht");
        return actions;
    }

    [Fact]
    public void Every_action_requires_authorization_or_is_explicitly_anonymous()
    {
        var unprotected = AllActions()
            .Where(a => !a.IsAnonymous && a.Authorization.Count == 0)
            .Select(a => a.Name).Order(StringComparer.Ordinal).ToList();

        Assert.True(unprotected.Count == 0, "Actions ohne [Authorize]/[AllowAnonymous]: " + string.Join(", ", unprotected));
    }

    [Fact]
    public void Only_login_is_anonymous()
    {
        var anonymous = AllActions().Where(a => a.IsAnonymous).Select(a => a.Name).ToList();

        Assert.Equal(new[] { "AuthController.Login" }, anonymous);
    }

    [Fact]
    public void Every_write_action_names_a_role_or_policy()
    {
        // Ein blankes [Authorize] genügt nur für Lesezugriffe, die Konto-Endpunkte des eingeloggten Nutzers
        // (Passwort ändern) und die reine Berechnung des Packplans (POST, ändert aber keine Daten, wird auf der
        // Bestell-Detailseite jeder Rolle angezeigt); jede andere Mutation muss eine Rolle/Policy verlangen,
        // sonst dürfte sie der Viewer.
        var notRoleProtected = AllActions()
            .Where(a => !a.IsRead && !a.IsAnonymous && !a.RequiresRoleOrPolicy)
            .Select(a => a.Name)
            .Except(new[] { "AuthController.ChangePassword", "PackingController.Plan" })
            .Order(StringComparer.Ordinal).ToList();

        Assert.True(notRoleProtected.Count == 0,
            "Schreib-Actions ohne Rolle/Policy (Viewer dürfte sie aufrufen): " + string.Join(", ", notRoleProtected));
    }

    [Fact]
    public void Only_policies_defined_in_Program_are_referenced()
    {
        var unknown = AllActions()
            .SelectMany(a => a.Authorization.Where(x => !string.IsNullOrEmpty(x.Policy)).Select(x => (a.Name, x.Policy!)))
            .Where(x => !KnownPolicies.Contains(x.Item2))
            .Select(x => $"{x.Name} -> {x.Item2}").Order(StringComparer.Ordinal).ToList();

        Assert.True(unknown.Count == 0, "Unbekannte Policy-Namen: " + string.Join(", ", unknown));
    }

    [Fact]
    public void Reads_stay_open_for_viewers_except_the_documented_exceptions()
    {
        // Viewer = nur lesen: jede GET-Action ist für jeden Eingeloggten offen, ausgenommen Audit-Log,
        // Bestandsbewertung, Waage, Etiketten, Benutzerliste, die Backup-Verwaltung (nur Admin) und den CSV-Export (Manager). Neue Ausnahmen müssen hier bewusst ergänzt werden.
        var restrictedReads = AllActions()
            .Where(a => a.IsRead && !a.IsAnonymous && a.RequiresRoleOrPolicy)
            .Select(a => a.Name).Order(StringComparer.Ordinal).ToList();

        var expected = new[]
        {
            "AdminController.DownloadBackup",
            "AdminController.GetBackupSettings",
            "AdminController.ListBackups",
            "AuditController.EntityTypes",
            "AuditController.List",
            "HardwareController.ScaleRead",
            "HardwareController.ScaleStatus",
            "ImportExportController.ExportArticles",
            "ImportExportController.ExportAudit",
            "ImportExportController.ExportMovements",
            "ImportExportController.ExportOrders",
            "ImportExportController.ExportStock",
            "LabelsController.ArticleLabel",
            "LabelsController.BinLabel",
            "LabelsController.OrderLabel",
            "ReportsController.StockValuation",
            "UsersController.List",
        };
        Assert.Equal(expected, restrictedReads);
    }

    [Fact]
    public void Admin_only_controllers_use_the_Admin_policy_on_class_level()
    {
        foreach (var controller in new[] { typeof(AdminController), typeof(UsersController) })
        {
            var attribute = controller.GetCustomAttribute<AuthorizeAttribute>();
            Assert.True(attribute is { Policy: "Admin" }, $"{controller.Name} braucht [Authorize(Policy = \"Admin\")] auf Klassenebene");
        }
    }
}
