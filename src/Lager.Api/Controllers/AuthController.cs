using System.Security.Claims;
using Lager.Api.Security;
using Lager.Application.Auth;
using Lager.Contracts.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Lager.Api.Controllers;

/// <summary>Anmeldung und Konto des angemeldeten Benutzers (Login, eigenes Profil, Passwort wechseln).</summary>
[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly AuthService _auth;
    public AuthController(AuthService auth) => _auth = auth;

    /// <summary>Meldet einen Benutzer an und liefert das JWT.</summary>
    /// <remarks>
    /// Username + Password gegen die DB prüfen, JWT zurückgeben. Antwort enthält
    /// den Token + die User-Daten — Frontend speichert beides im Auth-Store.
    /// Der einzige Endpunkt ohne Anmeldung; je Client-IP rate-limitiert (429).
    /// Jeder Fehlschlag (unbekannt, falsches Passwort, gesperrt, deaktiviert) antwortet identisch:
    /// 401 mit code=invalid_credentials (<see cref="InvalidCredentialsException"/>, abgebildet vom globalen Fehler-Handler).
    /// </remarks>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(SecurityServiceCollectionExtensions.LoginRateLimitPolicy)]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<ActionResult<LoginResponse>> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        var resp = await _auth.LoginAsync(req, ct);
        return Ok(resp);
    }

    /// <summary>Liefert den angemeldeten Benutzer.</summary>
    /// <remarks>
    /// Aktueller User aus dem Token. Dient dem Frontend zum Validieren des
    /// Tokens (z. B. nach Reload). Auch bei ausstehendem Passwortwechsel erreichbar.
    /// </remarks>
    [HttpGet("me")]
    [Authorize]
    [AllowPasswordChangePending]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<UserDto>> Me(CancellationToken ct)
    {
        var sub = User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
                  ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(sub, out var id)) return Unauthorized();
        var dto = await _auth.GetAsync(id, ct);
        return dto is null ? Unauthorized() : Ok(dto);
    }

    /// <summary>Wechselt das Passwort des angemeldeten Benutzers und liefert ein neues Token.</summary>
    /// <remarks>
    /// Auch bei ausstehendem Pflicht-Wechsel erreichbar. Liefert 200 mit einem NEUEN
    /// Token (<see cref="LoginResponse"/>), weil der Stamp des alten Tokens mit dem Passwort ungültig wird.
    /// Falsches Altpasswort → 400 mit code=invalid_current_password (kein 401: der Benutzer ist ja angemeldet),
    /// zu schwaches neues Passwort → 400 mit code=password_policy; beides bildet der globale Fehler-Handler aus
    /// <c>ArgumentException.Data["code"]</c> ab.
    /// </remarks>
    [HttpPost("change-password")]
    [Authorize]
    [AllowPasswordChangePending]
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<LoginResponse>> ChangePassword([FromBody] ChangePasswordRequest req, CancellationToken ct)
    {
        var sub = User.FindFirstValue(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)
                  ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(sub, out var id)) return Unauthorized();
        var resp = await _auth.ChangePasswordAsync(id, req, ct);
        return Ok(resp);
    }
}
