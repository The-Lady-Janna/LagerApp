using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Lager.Application.Abstractions;
using Lager.Application.Auth;
using Lager.Domain.Auth;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Lager.Infrastructure.Auth;

/// <summary>
/// HMAC-SHA256 signed JWT. Claims layout:
///   sub    — user id
///   name   — username
///   role   — one entry per granted role (Picker, Packer, …)
///   sstamp — Fingerabdruck des Passwort-Hashes (Token-Widerruf bei Passwortwechsel/-reset)
/// The middleware in Program.cs validates issuer/audience/signature against
/// the same JwtSettings.
/// </summary>
public class JwtTokenService : IJwtTokenService
{
    private readonly JwtSettings _settings;
    private readonly SigningCredentials _credentials;

    public JwtTokenService(IOptions<JwtSettings> settings)
    {
        _settings = settings.Value;

        // Der Host löst den Key beim Start auf und prüft ihn (JwtKeyResolver); dieser Check fängt
        // nur noch Konstruktionen ohne Host ab (z. B. Unit-Tests) und gilt für beide Wege gleich.
        var keyBytes = _settings.SigningKeyBytes ?? Encoding.UTF8.GetBytes(_settings.SigningKey ?? string.Empty);
        JwtKeyGuard.EnsureAcceptable(keyBytes, "Jwt:SigningKey");

        _credentials = new SigningCredentials(new SymmetricSecurityKey(keyBytes), SecurityAlgorithms.HmacSha256);
    }

    public (string Token, DateTime ExpiresAt) IssueToken(User user)
    {
        var now = DateTime.UtcNow;
        var expires = now.AddMinutes(_settings.LifetimeMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, user.Username),
            new(AuthClaims.SecurityStamp, user.GetSecurityStamp()),
        };
        foreach (var role in AuthService.SplitRoles(user.Roles))
            claims.Add(new Claim(ClaimTypes.Role, role));

        var token = new JwtSecurityToken(
            issuer: _settings.Issuer,
            audience: _settings.Audience,
            claims: claims,
            notBefore: now,
            expires: expires,
            signingCredentials: _credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}
