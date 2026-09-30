using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Lager.Application.Abstractions;
using Lager.Application.Auth;
using Lager.Domain.Auth;
using Lager.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace Lager.Api.Security;

/// <summary>Der aus der Datenbank geladene Benutzer des aktuellen Requests (Token allein ist nicht massgeblich).</summary>
public static class RequestUser
{
    private const string ItemKey = "Lager.RequestUser";

    public static void Set(HttpContext context, User user) => context.Items[ItemKey] = user;

    public static User? Get(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) ? value as User : null;
}

/// <summary>
/// Token-Widerruf ohne Schema-Änderung: Nach der Signaturprüfung wird der Benutzer je Request aus der
/// Datenbank geladen (JwtBearerEvents.OnTokenValidated). Das Token wird mit 401 abgelehnt, wenn
///   - der Benutzer nicht mehr existiert,
///   - das Konto deaktiviert wurde,
///   - der Security-Stamp nicht passt (Passwortwechsel/-reset seit dem Ausstellen; Tokens ohne Stamp
///     aus älteren Versionen sind damit ebenfalls ungültig) oder
///   - die Rollen im Token nicht mehr den Rollen in der Datenbank entsprechen.
/// Der geladene Benutzer landet in <see cref="RequestUser"/> für die Passwortwechsel-Prüfung.
/// </summary>
public static class AuthenticatedUserValidator
{
    public static async Task OnTokenValidated(TokenValidatedContext context)
    {
        var http = context.HttpContext;
        var principal = context.Principal;

        var sub = principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                  ?? principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        User? user = null;
        Guid? subjectId = null;
        if (Guid.TryParse(sub, out var id))
        {
            subjectId = id;
            user = await http.RequestServices.GetRequiredService<IUserRepository>().GetAsync(id, http.RequestAborted);
        }

        var stamp = principal?.FindFirst(AuthClaims.SecurityStamp)?.Value;
        var roleClaims = principal?.Claims
            .Where(c => c.Type is ClaimTypes.Role or "role")
            .Select(c => c.Value) ?? Enumerable.Empty<string>();

        var rejection = Check(user, stamp, roleClaims);
        if (rejection is not null)
        {
            http.RequestServices.GetService<ISecurityAudit>()?
                .Record(SecurityEvent.TokenRejected, user?.Username, subjectId, rejection);
            context.Fail("Token ist nicht mehr gültig");
            return;
        }

        RequestUser.Set(http, user!);
    }

    /// <summary>Liefert den Ablehnungsgrund (für das Sicherheits-Log) oder null, wenn das Token zum Benutzer passt.</summary>
    public static string? Check(User? user, string? tokenStamp, IEnumerable<string> tokenRoles)
    {
        if (user is null) return "Benutzer existiert nicht mehr";
        if (!user.IsActive) return "Konto deaktiviert";
        if (!string.Equals(tokenStamp, user.GetSecurityStamp(), StringComparison.Ordinal))
            return "Passwort seit Ausstellung geändert (oder Token ohne Stamp)";

        var dbRoles = AuthService.SplitRoles(user.Roles);
        if (!new HashSet<string>(tokenRoles, StringComparer.Ordinal).SetEquals(dbRoles))
            return "Rollen seit Ausstellung geändert";

        return null;
    }
}
