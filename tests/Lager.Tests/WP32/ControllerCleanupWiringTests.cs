using System.Text.RegularExpressions;

namespace Lager.Tests.WP32;

/// <summary>
/// Die Struktur hinter dem Controller-Aufräumen: keine Controller-Action fängt Fachfehler selbst ab und baut
/// <c>{ error }</c>-Bodies, die Hilfsklasse <c>DomainErrorResults</c> und die tote <c>DomainRuleExceptionMiddleware</c> sind weg.
/// (Die HTTP-Wirkung prüft <see cref="ControllerErrorFormatTests"/>; hier fällt auf, wenn jemand das alte Muster wieder einführt.)
/// </summary>
public class ControllerCleanupWiringTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lager.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Lager.sln oberhalb von " + AppContext.BaseDirectory + " nicht gefunden");
    }

    private static string Api(params string[] path) => Path.Combine(new[] { RepoRoot(), "src", "Lager.Api" }.Concat(path).ToArray());

    private static IEnumerable<string> ControllerFiles() => Directory.GetFiles(Api("Controllers"), "*.cs");

    /// <summary>Quelltext ohne Kommentare: Erklärungen zur früheren Lösung sollen den Test nicht auslösen.</summary>
    private static string CodeOf(string file) =>
        Regex.Replace(File.ReadAllText(file), @"//[^\n]*|/\*.*?\*/", string.Empty, RegexOptions.Singleline);

    [Theory]
    [InlineData("PickListsController.cs")]
    [InlineData("PickWavesController.cs")]
    [InlineData("PurchaseOrdersController.cs")]
    [InlineData("ReturnsController.cs")]
    [InlineData("ShipmentsController.cs")]
    [InlineData("CustomersController.cs")]
    [InlineData("OrdersController.cs")]
    public void These_controllers_leave_error_mapping_to_the_global_handler(string file)
    {
        var code = CodeOf(Path.Combine(Api("Controllers"), file));

        Assert.DoesNotMatch(new Regex(@"\bcatch\b"), code);
        Assert.DoesNotMatch(new Regex(@"\bRunAsync\b"), code);
        Assert.DoesNotContain("DomainErrorResults", code);
        Assert.DoesNotMatch(new Regex(@"new\s*\{\s*(error|code)\b"), code);
    }

    [Fact]
    public void The_admin_controller_only_translates_the_io_error_and_builds_no_anonymous_error_bodies()
    {
        var code = CodeOf(Path.Combine(Api("Controllers"), "AdminController.cs"));

        Assert.DoesNotMatch(new Regex(@"new\s*\{\s*error\b"), code);
        Assert.DoesNotMatch(new Regex(@"catch\s*\(\s*(InvalidOperation|Argument|KeyNotFound)\w*Exception"), code);

        // Die IO-Ausnahme beim Austausch der DB-Datei wird zum Konflikt mit Code (die Antwort baut der globale Handler).
        Assert.Contains("throw new InvalidOperationException", code);
        Assert.Contains("database_in_use", code);
    }

    [Fact]
    public void No_controller_maps_domain_exceptions_to_responses()
    {
        foreach (var file in ControllerFiles())
        {
            var code = CodeOf(file);
            var name = Path.GetFileName(file);
            Assert.False(Regex.IsMatch(code, @"catch\s*\(\s*(InvalidOperationException|ArgumentException|KeyNotFoundException)"),
                $"{name} fängt eine Fach-Exception selbst ab");
            Assert.False(Regex.IsMatch(code, @"\b(Conflict|BadRequest|NotFound)\(\s*new\s*\{"),
                $"{name} baut einen anonymen Fehler-Body");
        }
    }

    [Fact]
    public void The_dead_helpers_are_gone()
    {
        Assert.False(File.Exists(Api("Security", "DomainRuleExceptionMiddleware.cs")));
        foreach (var file in Directory.GetFiles(Api(), "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") &&
                                 !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var code = CodeOf(file);
            Assert.False(code.Contains("DomainRuleExceptionMiddleware") || code.Contains("DomainErrorResults"),
                $"{Path.GetFileName(file)} benutzt eine entfernte Hilfsklasse");
        }
    }
}
