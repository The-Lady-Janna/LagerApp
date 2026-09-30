using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Lager.Api.Errors;

/// <summary>Korrelations-ID der aktuellen Anfrage (vom <c>CorrelationIdMiddleware</c> gesetzt).</summary>
public static class CorrelationIds
{
    public const string HeaderName = "X-Correlation-Id";
    /// <summary>Schlüssel in <c>HttpContext.Items</c> (gesetzt vom <see cref="CorrelationIdCaptureMiddleware"/>).</summary>
    public const string ItemKey = "Lager.CorrelationId";

    /// <summary>
    /// Die ID der Anfrage: aus <c>Items</c> (übersteht das Leeren der Header durch den Exception-Handler), sonst aus dem
    /// Response-Header; fehlt beides (Kurzschluss vor der Middleware), die Trace-ID der Anfrage.
    /// </summary>
    public static string Get(HttpContext http)
    {
        if (http.Items.TryGetValue(ItemKey, out var item) && item is string { Length: > 0 } id) return id;
        return http.Response.Headers.TryGetValue(HeaderName, out var value) && !string.IsNullOrEmpty(value)
            ? value.ToString()
            : http.TraceIdentifier;
    }
}

/// <summary>
/// Baut und schreibt die einheitliche Fehlerantwort (RFC 7807, <c>application/problem+json</c>) mit den Feldern
/// <c>type, title, status, detail, code, correlationId</c> und bei Validierungsfehlern <c>errors</c>. Zusätzlich
/// steht <c>error</c> (= detail) als Rückwärtskompatibilität für Clients, die bisher <c>{ error }</c> gelesen haben
/// (Login, Passwortwechsel im Frontend).
/// </summary>
public static class Problems
{
    public const string ContentType = "application/problem+json";

    /// <summary>Erzeugt die Fehlerantwort über die <see cref="ProblemDetailsFactory"/> von MVC (gleiches Format wie die automatischen 400/404).</summary>
    public static ProblemDetails Create(
        HttpContext http, int status, string code, string? detail = null, IReadOnlyDictionary<string, string[]>? errors = null)
    {
        var factory = http.RequestServices.GetRequiredService<ProblemDetailsFactory>();
        var title = ProblemCatalog.Title(status);
        detail ??= ProblemCatalog.Detail(status);

        ProblemDetails problem;
        if (errors is { Count: > 0 })
        {
            var modelState = new ModelStateDictionary();
            foreach (var (field, messages) in errors)
                foreach (var message in messages)
                    modelState.AddModelError(field, message);
            problem = factory.CreateValidationProblemDetails(http, modelState, status, title, detail: detail);
        }
        else
        {
            problem = factory.CreateProblemDetails(http, status, title, detail: detail);
        }

        // Den Standardcode des Statuscodes hat der Customizer schon gesetzt; der genauere gewinnt.
        problem.Extensions["code"] = code;
        Decorate(http, problem);
        return problem;
    }

    /// <summary>Schreibt die Antwort mit Statuscode und Korrelations-Header (unabhängig vom Accept-Header des Clients).</summary>
    public static Task WriteAsync(HttpContext http, ProblemDetails problem, CancellationToken ct = default)
    {
        http.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        http.Response.Headers[CorrelationIds.HeaderName] = CorrelationIds.Get(http);
        return http.Response.WriteAsJsonAsync(problem, problem.GetType(), options: null, contentType: ContentType, cancellationToken: ct);
    }

    /// <summary>
    /// Ergänzt jede ProblemDetails-Antwort (auch die automatischen von MVC und die der Status-Seiten) um
    /// <c>code</c>, <c>correlationId</c> und <c>error</c>; die <c>traceId</c> des Frameworks entfällt, damit es genau
    /// eine Referenz-ID gibt.
    /// </summary>
    public static void Decorate(HttpContext http, ProblemDetails problem)
    {
        var status = problem.Status ?? StatusCodes.Status500InternalServerError;
        problem.Extensions.Remove("traceId");
        problem.Title ??= ProblemCatalog.Title(status);
        problem.Detail ??= ProblemCatalog.Detail(status);
        if (!problem.Extensions.ContainsKey("code"))
            problem.Extensions["code"] = ProblemCodes.ForStatus(status);
        problem.Extensions["correlationId"] = CorrelationIds.Get(http);
        problem.Extensions["error"] = problem.Detail ?? problem.Title;
    }
}
