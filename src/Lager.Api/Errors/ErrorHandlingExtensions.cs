using System.Text.Json.Serialization;
using Lager.Api.Validation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace Lager.Api.Errors;

/// <summary>Registrierung und Pipeline-Einbindung der einheitlichen Fehlerbehandlung.</summary>
public static class ErrorHandlingExtensions
{
    /// <summary>
    /// Registriert:
    /// <list type="bullet">
    /// <item>ProblemDetails mit <c>code</c>/<c>correlationId</c>/<c>error</c> für jede Fehlerantwort (auch die automatischen von MVC),</item>
    /// <item>den <see cref="GlobalExceptionHandler"/> für unbehandelte Exceptions,</item>
    /// <item>die 400-Antwort bei ungültigem Modell/Body (<see cref="ValidationProblemDetails"/> im selben Format),</item>
    /// <item>Enums im JSON nur noch als Text (Zahlen werden mit 400 abgelehnt).</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddLagerApiErrors(this IServiceCollection services)
    {
        services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context => Problems.Decorate(context.HttpContext, context.ProblemDetails));
        services.AddExceptionHandler<GlobalExceptionHandler>();

        // PostConfigure: läuft nach den Standardeinstellungen von AddControllers(), egal in welcher Reihenfolge registriert wird.
        services.AddOptions<ApiBehaviorOptions>()
            .PostConfigure(options =>
            {
                options.InvalidModelStateResponseFactory = context =>
                {
                    var errors = context.ModelState
                        .Where(entry => entry.Value is { Errors.Count: > 0 })
                        .ToDictionary(
                            entry => entry.Key,
                            entry => entry.Value!.Errors
                                .Select(e => string.IsNullOrEmpty(e.ErrorMessage) ? "Der Wert ist ungültig." : e.ErrorMessage)
                                .ToArray());
                    var problem = Problems.Create(context.HttpContext, StatusCodes.Status400BadRequest,
                        ProblemCodes.ValidationFailed, SummarizeErrors(errors), errors);
                    return new ObjectResult(problem)
                    {
                        StatusCode = StatusCodes.Status400BadRequest,
                        ContentTypes = { Problems.ContentType },
                    };
                };

                // Auch für 405/413/429 einen Typ-Link, dann deutsche Titel für die automatischen Antworten von
                // [ApiController] (NotFound(), Forbid() ...).
                options.ClientErrorMapping.TryAdd(StatusCodes.Status405MethodNotAllowed,
                    new ClientErrorData { Link = "https://tools.ietf.org/html/rfc9110#section-15.5.6" });
                options.ClientErrorMapping.TryAdd(StatusCodes.Status413PayloadTooLarge,
                    new ClientErrorData { Link = "https://tools.ietf.org/html/rfc9110#section-15.5.14" });
                options.ClientErrorMapping.TryAdd(StatusCodes.Status429TooManyRequests,
                    new ClientErrorData { Link = "https://tools.ietf.org/html/rfc6585#section-4" });
                foreach (var (status, data) in options.ClientErrorMapping)
                    data.Title = ProblemCatalog.Title(status);
            });

        // Enums nur als Text: ein Zahlenwert im Body ist ein Fehler (400) statt einer stillen Umdeutung.
        services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options =>
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false)));

        // Obergrenzen für Zeitraum-/Paging-Parameter der Query-String (days, range, top, take ...).
        services.Configure<MvcOptions>(options => options.Filters.Add<QueryLimitsFilter>());
        return services;
    }

    /// <summary>
    /// Fehlerantworten für leere 4xx/5xx-Antworten der Pipeline (401 ohne Token, 403 durch eine Rolle, 404 ohne Route,
    /// 405 falsche Methode ...). Bereits gesetzte Bodies - etwa <c>password_change_required</c>, 429 mit
    /// <c>too_many_requests</c> oder die ProblemDetails von MVC - überschreibt die Middleware nie: sie greift nur bei
    /// Antworten ohne Inhalt und ohne Content-Type.
    /// </summary>
    public static IApplicationBuilder UseLagerStatusCodePages(this IApplicationBuilder app) =>
        app.UseStatusCodePages(context =>
        {
            var http = context.HttpContext;
            var status = http.Response.StatusCode;
            return Problems.WriteAsync(http, Problems.Create(http, status, ProblemCodes.ForStatus(status)));
        });

    /// <summary>Kurzfassung der Feldfehler für <c>detail</c> (das Frontend zeigt nur diesen Text): erste Meldung + Anzahl der weiteren.</summary>
    private static string SummarizeErrors(IReadOnlyDictionary<string, string[]> errors)
    {
        var messages = errors.Values.SelectMany(m => m).ToList();
        if (messages.Count == 0) return ProblemCatalog.Detail(StatusCodes.Status400BadRequest);
        return messages.Count == 1
            ? $"Die Eingabe ist ungültig: {messages[0]}"
            : $"Die Eingabe ist ungültig: {messages[0]} (und {messages.Count - 1} weitere Fehler)";
    }
}
