using Lager.Application.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http.Features;

namespace Lager.Api.Security;

/// <summary>
/// Markiert Endpunkte, die auch mit ausstehendem Passwortwechsel (User.MustChangePassword) erreichbar
/// bleiben: nur GET /api/auth/me und POST /api/auth/change-password. Alles andere antwortet mit
/// 403 und {"code":"password_change_required"} (siehe <see cref="PasswordChangeNotPendingHandler"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AllowPasswordChangePendingAttribute : Attribute { }

/// <summary>Der Benutzer muss sein Passwort geändert haben, bevor er die API nutzen darf.</summary>
public sealed class PasswordChangeNotPendingRequirement : IAuthorizationRequirement { }

/// <summary>
/// Setzt den Passwortwechsel-Zwang serverseitig durch: hat der (per Token angemeldete) Benutzer noch
/// MustChangePassword, scheitert die Anforderung - außer der Endpunkt trägt
/// <see cref="AllowPasswordChangePendingAttribute"/>. Der Benutzer wurde bereits in
/// <see cref="AuthenticatedUserValidator"/> aus der Datenbank geladen (Token allein reicht nicht).
/// </summary>
public sealed class PasswordChangeNotPendingHandler : AuthorizationHandler<PasswordChangeNotPendingRequirement>
{
    private readonly IHttpContextAccessor _accessor;
    public PasswordChangeNotPendingHandler(IHttpContextAccessor accessor) => _accessor = accessor;

    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context, PasswordChangeNotPendingRequirement requirement)
    {
        // Nicht angemeldet: hier wird bewusst weder Succeed noch Fail gerufen. Die Anforderung bleibt
        // unerfüllt (also verweigert), und RequireAuthenticatedUser sorgt für die 401-Antwort.
        if (context.User.Identity?.IsAuthenticated != true) return Task.CompletedTask;

        var http = _accessor.HttpContext;
        var user = http is null ? null : RequestUser.Get(http);
        if (user is null)
        {
            // Angemeldet, aber nicht aus der DB geladen: das darf nie vorkommen -> lieber verweigern.
            context.Fail(new AuthorizationFailureReason(this, "Benutzer wurde nicht geladen"));
            return Task.CompletedTask;
        }

        if (!user.MustChangePassword)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var endpoint = context.Resource as Endpoint ?? http?.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<AllowPasswordChangePendingAttribute>() is not null)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        context.Fail(new AuthorizationFailureReason(this, AuthErrorCodes.PasswordChangeRequired));
        return Task.CompletedTask;
    }
}

/// <summary>
/// Liefert für den Fall "Passwortwechsel ausstehend" eine JSON-Antwort mit maschinenlesbarem Code
/// statt eines leeren 403, damit das Frontend den Wechsel-Dialog zeigen kann. Alle anderen
/// Ergebnisse behandelt der Standard-Handler.
/// </summary>
public sealed class LagerAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(
        RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden
            && authorizeResult.AuthorizationFailure is { } failure
            && failure.FailureReasons.Any(r => r.Handler is PasswordChangeNotPendingHandler
                                               && r.Message == AuthErrorCodes.PasswordChangeRequired))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                code = AuthErrorCodes.PasswordChangeRequired,
                error = "Das Passwort muss zuerst geändert werden (POST /api/auth/change-password).",
            });
            return;
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
