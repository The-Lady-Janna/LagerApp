using Lager.Domain.Auth;

namespace Lager.Application.Abstractions;

public interface IJwtTokenService
{
    /// <summary>
    /// Issues a signed JWT for the given user. Returns the encoded token + the
    /// UTC expiry timestamp so the caller can hand both back to the frontend.
    /// Das Token trägt den Security-Stamp des Passworts (<see cref="User.GetSecurityStamp"/>);
    /// nach einem Passwortwechsel muss daher ein neues Token ausgestellt werden.
    /// </summary>
    (string Token, DateTime ExpiresAt) IssueToken(User user);
}
