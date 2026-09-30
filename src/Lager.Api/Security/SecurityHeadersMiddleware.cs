namespace Lager.Api.Security;

/// <summary>
/// Setzt Basis-Sicherheits-Header auf jede Antwort: nosniff, kein Framing, kein Referrer, eingeschränkte
/// Browser-Funktionen (Kamera bleibt für den Barcode-Scanner erlaubt) und eine restriktive
/// Content-Security-Policy. Die CSP bekommt /swagger nicht (Swagger UI braucht Inline-Skripte).
/// Die Header werden in OnStarting gesetzt, damit sie auch bei Kurzschlüssen (401/429) nicht fehlen.
/// </summary>
public class SecurityHeadersMiddleware
{
    private const string ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data: blob:; style-src 'self' 'unsafe-inline'; " +
        "frame-ancestors 'none'; base-uri 'self'";

    private readonly RequestDelegate _next;
    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var ctx = (HttpContext)state;
            var headers = ctx.Response.Headers;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = "camera=(self), microphone=(), geolocation=()";
            if (!ctx.Request.Path.StartsWithSegments("/swagger"))
                headers["Content-Security-Policy"] = ContentSecurityPolicy;
            return Task.CompletedTask;
        }, context);

        return _next(context);
    }
}
