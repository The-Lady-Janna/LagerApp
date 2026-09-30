using System.Text.RegularExpressions;
using Serilog.Context;

namespace Lager.Api.Middleware;

/// <summary>
/// Hängt jeder Request eine Korrelations-ID an. Wird über den Response-Header
/// `X-Correlation-Id` zurückgeschickt und in jedes Serilog-Log-Statement
/// gepusht (via LogContext.PushProperty). Wenn der Client schon eine ID
/// schickt (z. B. ein gateway davor), übernehmen wir die — aber nur, wenn sie
/// harmlos aussieht (<c>^[A-Za-z0-9._-]{1,64}$</c>); sonst wird neu generiert.
/// Der Header kommt ungeprüft von außen und landet in Response und Logdatei:
/// ohne Prüfung könnte jeder Log-Zeilen fälschen oder das Log aufblähen.
/// </summary>
public class CorrelationIdMiddleware
{
    private const string HeaderName = "X-Correlation-Id";

    // \z statt $: "$" würde auch vor einem abschließenden Zeilenumbruch matchen.
    private static readonly Regex ValidId = new(@"^[A-Za-z0-9._-]{1,64}\z", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly RequestDelegate _next;
    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx)
    {
        var id = ctx.Request.Headers.TryGetValue(HeaderName, out var existing) && IsValid(existing.ToString())
            ? existing.ToString()
            : Guid.NewGuid().ToString("N");
        ctx.Response.Headers[HeaderName] = id;
        using (LogContext.PushProperty("CorrelationId", id))
            await _next(ctx);
    }

    /// <summary>Erlaubt sind 1-64 Zeichen aus Buchstaben, Ziffern, Punkt, Unterstrich und Bindestrich.</summary>
    public static bool IsValid(string? id) => id is not null && ValidId.IsMatch(id);
}
