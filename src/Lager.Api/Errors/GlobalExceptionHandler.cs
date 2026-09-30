using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Lager.Api.Errors;

/// <summary>
/// Der eine zentrale Handler für alle nicht abgefangenen Exceptions der Pipeline (<c>UseExceptionHandler</c>):
/// bildet sie über <see cref="ExceptionProblemMapper"/> auf einen Statuscode ab und antwortet mit
/// <c>application/problem+json</c> samt Fehlercode und Korrelations-ID (Body und Header <c>X-Correlation-Id</c>).
/// Fachliche Fehler (4xx) werden ohne Stacktrace geloggt, alles Unerwartete (500) als Error mit Exception und
/// Korrelations-ID. Die Antwort enthält bei einem 500 weder Meldung noch Stacktrace - nur in Development steht
/// beides zusätzlich im Body.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;
    private readonly IHostEnvironment _environment;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger, IHostEnvironment environment)
    {
        _logger = logger;
        _environment = environment;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken ct)
    {
        // Ist die Antwort schon unterwegs, lässt sich der Status nicht mehr ändern: die Middleware bricht die Verbindung ab.
        if (http.Response.HasStarted) return false;

        var mapping = ExceptionProblemMapper.Map(exception);
        var correlationId = CorrelationIds.Get(http);

        if (mapping.Expected)
        {
            _logger.LogInformation(
                "Fachlicher Fehler {Status} {Code} bei {Method} {Path} (CorrelationId {CorrelationId}): {Message}",
                mapping.Status, mapping.Code, http.Request.Method, http.Request.Path, correlationId, exception.GetBaseException().Message);
        }
        else
        {
            _logger.LogError(exception,
                "Unbehandelte Ausnahme bei {Method} {Path} (CorrelationId {CorrelationId})",
                http.Request.Method, http.Request.Path, correlationId);
        }

        var detail = mapping.Detail;
        if (!mapping.Expected && _environment.IsDevelopment())
            detail = $"{exception.GetType().Name}: {exception.Message}";

        var problem = Problems.Create(http, mapping.Status, mapping.Code, detail, mapping.Errors);
        if (!mapping.Expected && _environment.IsDevelopment())
        {
            // Nur Development: der Entwickler sieht die Ursache direkt in der Antwort (früher die Developer-Exception-Seite).
            problem.Extensions["exception"] = exception.GetType().FullName;
            problem.Extensions["stackTrace"] = exception.StackTrace;
        }

        if (!mapping.CorrelationIdInBody) problem.Extensions.Remove("correlationId");

        await Problems.WriteAsync(http, problem, ct);
        return true;
    }
}
