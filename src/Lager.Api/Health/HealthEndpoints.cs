using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Lager.Api.Health;

/// <summary>
/// Health-Endpunkte für Orchestrierung, Reverse-Proxy und Monitoring (Docker-Healthcheck, Kubernetes-Probes):
/// <list type="bullet">
/// <item><c>/health/live</c> - der Prozess läuft und beantwortet Anfragen; prüft nichts, antwortet immer 200.</item>
/// <item><c>/health/ready</c> - die Anwendung ist einsatzbereit: 200, wenn die Datenbank erreichbar ist, sonst 503.</item>
/// </list>
/// Beide sind anonym erreichbar (die Standard-Policy der API ist "angemeldet") und antworten minimal
/// (<c>{"status":"Healthy"}</c>): keine Details zu Datenbank, Pfaden oder Fehlern.
/// </summary>
public static class HealthEndpoints
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";

    /// <summary>Tag der Prüfungen, die zur Bereitschaft gehören.</summary>
    public const string ReadyTag = "ready";

    public static IServiceCollection AddLagerHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<DbHealthCheck>("database", failureStatus: HealthStatus.Unhealthy, tags: new[] { ReadyTag });
        return services;
    }

    public static IEndpointRouteBuilder MapLagerHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        // Liveness: keine Prüfung, damit ein Datenbankproblem den Container nicht "neu starten" lässt.
        endpoints.MapHealthChecks(LivePath, new HealthCheckOptions
        {
            Predicate = _ => false,
            ResponseWriter = WriteStatusAsync,
        }).AllowAnonymous();

        endpoints.MapHealthChecks(ReadyPath, new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag),
            ResponseWriter = WriteStatusAsync,
        }).AllowAnonymous();

        return endpoints;
    }

    /// <summary>Schreibt nur den Gesamtstatus (Healthy/Degraded/Unhealthy); der Statuscode (200/503) kommt aus den Options.</summary>
    private static Task WriteStatusAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new { status = report.Status.ToString() }));
    }
}
