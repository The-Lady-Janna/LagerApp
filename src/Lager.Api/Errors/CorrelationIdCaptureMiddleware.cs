namespace Lager.Api.Errors;

/// <summary>
/// Merkt sich die vom <c>CorrelationIdMiddleware</c> gesetzte Korrelations-ID in <c>HttpContext.Items</c>. Der Exception-Handler
/// von ASP.NET Core leert vor der Fehlerantwort alle Response-Header - auch <c>X-Correlation-Id</c>. Ohne diese Kopie ginge die
/// ID verloren, die der Nutzer in der Fehlermeldung sieht und die in jeder Log-Zeile der Anfrage steht.
/// Muss direkt nach dem <c>CorrelationIdMiddleware</c> laufen.
/// </summary>
public class CorrelationIdCaptureMiddleware
{
    private readonly RequestDelegate _next;
    public CorrelationIdCaptureMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        if (context.Response.Headers.TryGetValue(CorrelationIds.HeaderName, out var id) && !string.IsNullOrEmpty(id))
            context.Items[CorrelationIds.ItemKey] = id.ToString();
        return _next(context);
    }
}
