using System.Security.Claims;
using Lager.Application.Abstractions;
using Lager.Application.Auth;
using Lager.Domain.Auth;
using Microsoft.AspNetCore.Http;

namespace Lager.Infrastructure.Auth;

/// <summary>
/// Reads the current principal from HttpContext. When called outside an HTTP
/// request (background job, startup seeder), reports anonymous.
/// </summary>
public class HttpContextCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _accessor;
    public HttpContextCurrentUser(IHttpContextAccessor accessor) => _accessor = accessor;

    private ClaimsPrincipal? Principal => _accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    /// <summary>Ohne HttpContext läuft der Code nicht im Auftrag eines Requests (Startup, Seeder, Hintergrund).</summary>
    public bool IsSystemContext => _accessor.HttpContext is null;

    public Guid? UserId
    {
        get
        {
            // FindFirstValue is an AspNetCore extension we don't reference here —
            // FindFirst(...)?.Value is the equivalent on ClaimsPrincipal directly.
            var sub = Principal?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                      ?? Principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return Guid.TryParse(sub, out var id) ? id : null;
        }
    }

    public string? Username => Principal?.Identity?.Name
                              ?? Principal?.FindFirst(ClaimTypes.Name)?.Value;

    public Role Roles
    {
        get
        {
            if (Principal is null) return Role.None;
            var roleNames = Principal.FindAll(ClaimTypes.Role).Select(c => c.Value);
            // Lenient: ein unbekannter Claim-Wert darf hier nie werfen (Getter ohne Fehlerpfad).
            return AuthService.CombineRolesLenient(roleNames);
        }
    }
}
