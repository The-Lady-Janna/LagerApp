using System.Text.RegularExpressions;

namespace Lager.Tests.WP12;

/// <summary>
/// Die Struktur hinter der einheitlichen Fehlerbehandlung: Fehler laufen zentral über den Handler, die Controller
/// für Login, Lieferanten und Benutzer haben keine eigene Abbildung mehr, die alten Einzel-Middlewares sind weg.
/// </summary>
public class ErrorHandlingWiringTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lager.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Lager.sln oberhalb von " + AppContext.BaseDirectory + " nicht gefunden");
    }

    private static string Api(params string[] path) => Path.Combine(new[] { RepoRoot(), "src", "Lager.Api" }.Concat(path).ToArray());

    [Theory]
    [InlineData("AuthController.cs")]
    [InlineData("SuppliersController.cs")]
    [InlineData("UsersController.cs")]
    public void These_controllers_no_longer_map_exceptions_themselves(string file)
    {
        var source = File.ReadAllText(Api("Controllers", file));

        Assert.DoesNotMatch(new Regex(@"\bcatch\b"), source);
        Assert.DoesNotContain("BadRequest(new { error", source);
        Assert.DoesNotContain("Conflict(new { error", source);
    }

    [Fact]
    public void The_concurrency_middleware_is_replaced_by_the_central_handler()
    {
        var program = File.ReadAllText(Api("Program.cs"));

        Assert.False(File.Exists(Api("Middleware", "ConcurrencyExceptionMiddleware.cs")));
        Assert.DoesNotContain("UseMiddleware<ConcurrencyExceptionMiddleware>", program);
        Assert.DoesNotContain("UseMiddleware<DomainRuleExceptionMiddleware>", program);
        Assert.Contains("AddLagerApiErrors()", program);
        Assert.Contains("UseExceptionHandler()", program);
        Assert.Contains("UseLagerStatusCodePages()", program);
    }

    [Fact]
    public void The_exception_handler_sits_inside_request_logging_and_the_correlation_id_middleware()
    {
        var program = File.ReadAllText(Api("Program.cs"));

        var correlation = program.IndexOf("UseMiddleware<CorrelationIdMiddleware>", StringComparison.Ordinal);
        var capture = program.IndexOf("UseMiddleware<CorrelationIdCaptureMiddleware>", StringComparison.Ordinal);
        var logging = program.IndexOf("UseSerilogRequestLogging()", StringComparison.Ordinal);
        var handler = program.IndexOf("UseExceptionHandler()", StringComparison.Ordinal);
        var statusPages = program.IndexOf("UseLagerStatusCodePages()", StringComparison.Ordinal);
        var authentication = program.IndexOf("UseAuthentication()", StringComparison.Ordinal);

        // Korrelations-ID zuerst (auch der Handler loggt damit), das Request-Log sieht den fertigen Statuscode, und die
        // Status-Seiten stehen vor Authentifizierung/Autorisierung, damit sie deren leere 401/403 füllen.
        Assert.True(correlation >= 0 && correlation < capture, "CorrelationIdMiddleware muss vor der Capture-Middleware laufen");
        Assert.True(capture < logging && logging < handler, "Reihenfolge: Korrelations-ID, Request-Log, Exception-Handler");
        Assert.True(handler < statusPages && statusPages < authentication, "Status-Seiten vor der Authentifizierung");
    }
}
