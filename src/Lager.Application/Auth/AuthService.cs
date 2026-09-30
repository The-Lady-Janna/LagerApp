using Lager.Application.Abstractions;
using Lager.Contracts.Auth;
using Lager.Domain.Auth;

namespace Lager.Application.Auth;

/// <summary>
/// Login + password-change flow. All sensitive paths (failed verify, locked
/// account, inactive account, missing user) return the same generic "wrong credentials" error
/// — never leak which step failed — and cost the same BCrypt time, so neither the
/// response body nor the timing reveals whether a username exists.
/// </summary>
public class AuthService
{
    private readonly IUserRepository _users;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtTokenService _jwt;
    private readonly IUnitOfWork _uow;
    private readonly ISecurityAudit _audit;
    private readonly LockoutPolicy _lockout;

    public AuthService(
        IUserRepository users,
        IPasswordHasher hasher,
        IJwtTokenService jwt,
        IUnitOfWork uow,
        ISecurityAudit? audit = null,
        LockoutPolicy? lockout = null)
    {
        _users = users;
        _hasher = hasher;
        _jwt = jwt;
        _uow = uow;
        _audit = audit ?? NullSecurityAudit.Instance;
        _lockout = lockout ?? LockoutPolicy.Default;
    }

    public async Task<LoginResponse> LoginAsync(LoginRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            throw new InvalidCredentialsException();

        var username = req.Username.Trim().ToLowerInvariant();
        var user = await _users.FindByUsernameAsync(username, ct);

        // Unbekannt / inaktiv / gesperrt: trotzdem einen BCrypt-Durchlauf bezahlen (Timing-Angleich)
        // und mit derselben Meldung ablehnen. Der Grund steht nur im Sicherheits-Log.
        if (user is null || !user.IsActive || user.IsLocked())
        {
            _hasher.VerifyDummy(req.Password);
            var reason = user is null ? "unbekannter Benutzer" : !user.IsActive ? "Konto deaktiviert" : "Konto gesperrt";
            _audit.Record(user is null ? SecurityEvent.LoginFailed : SecurityEvent.LoginBlocked, username, user?.Id, reason);
            throw new InvalidCredentialsException();
        }

        if (!_hasher.Verify(req.Password, user.PasswordHash))
        {
            var lockedNow = user.OnFailedLogin(_lockout);
            await _uow.SaveChangesAsync(ct);
            _audit.Record(SecurityEvent.LoginFailed, user.Username, user.Id, $"Fehlversuch {user.FailedLoginAttempts}");
            if (lockedNow)
                _audit.Record(SecurityEvent.LoginLockedOut, user.Username, user.Id, $"gesperrt bis {user.LockedUntil:O}");
            throw new InvalidCredentialsException();
        }

        user.OnSuccessfulLogin();
        var (token, expiry) = _jwt.IssueToken(user);
        await _uow.SaveChangesAsync(ct);
        _audit.Record(SecurityEvent.LoginSucceeded, user.Username, user.Id);

        return new LoginResponse(token, expiry, ToDto(user));
    }

    /// <summary>
    /// Wechselt das Passwort des angemeldeten Benutzers und liefert ein NEUES Token (der Stamp des
    /// alten Tokens passt danach nicht mehr). Ein falsches Altpasswort ist ein 400er-Fall
    /// (ArgumentException mit Data["code"] = "invalid_current_password"), kein 401: der Benutzer ist ja
    /// angemeldet, und das Frontend meldet bei 401 ab. Es zählt wie ein Login-Fehlversuch.
    /// </summary>
    public async Task<LoginResponse> ChangePasswordAsync(Guid userId, ChangePasswordRequest req, CancellationToken ct = default)
    {
        var user = await _users.GetAsync(userId, ct);
        if (user is null || !user.IsActive) throw new InvalidCredentialsException();

        // Gesperrt oder kein Altpasswort: nicht prüfen, aber gleich teuer und gleich antworten.
        if (string.IsNullOrEmpty(req.CurrentPassword) || user.IsLocked())
        {
            _hasher.VerifyDummy(req.CurrentPassword ?? string.Empty);
            _audit.Record(SecurityEvent.PasswordChangeFailed, user.Username, user.Id, "Konto gesperrt oder Altpasswort fehlt");
            throw InvalidCurrentPassword();
        }

        if (!_hasher.Verify(req.CurrentPassword, user.PasswordHash))
        {
            var lockedNow = user.OnFailedLogin(_lockout);
            await _uow.SaveChangesAsync(ct);
            _audit.Record(SecurityEvent.PasswordChangeFailed, user.Username, user.Id, $"falsches Altpasswort, Fehlversuch {user.FailedLoginAttempts}");
            if (lockedNow)
                _audit.Record(SecurityEvent.LoginLockedOut, user.Username, user.Id, $"gesperrt bis {user.LockedUntil:O}");
            throw InvalidCurrentPassword();
        }

        PasswordPolicy.Validate(req.NewPassword, user.Username, req.CurrentPassword);

        user.Unlock(); // richtiges Altpasswort = Identität bestätigt -> Fehlversuchszähler zurücksetzen
        user.SetPasswordHash(_hasher.Hash(req.NewPassword));
        var (token, expiry) = _jwt.IssueToken(user);
        await _uow.SaveChangesAsync(ct);
        _audit.Record(SecurityEvent.PasswordChanged, user.Username, user.Id);

        return new LoginResponse(token, expiry, ToDto(user));
    }

    public async Task<UserDto?> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var u = await _users.GetAsync(userId, ct);
        return u is null ? null : ToDto(u);
    }

    public static UserDto ToDto(User u) => new(
        u.Id, u.Username, u.Email, u.DisplayName,
        SplitRoles(u.Roles),
        u.IsActive, u.MustChangePassword, u.LastLoginAt, u.CreatedAt);

    public static string[] SplitRoles(Role roles) =>
        Enum.GetValues<Role>()
            .Where(r => r != Role.None && (roles & r) != 0)
            .Select(r => r.ToString())
            .ToArray();

    /// <summary>
    /// Strikt: jeder Name muss exakt (ohne Beachtung der Gross-/Kleinschreibung) einer Rolle entsprechen.
    /// Tippfehler, Zahlen ("32"), Komma-Listen ("Picker,Packer") und "None" werfen eine
    /// <see cref="UnknownRoleException"/> statt still verworfen zu werden.
    /// </summary>
    public static Role CombineRoles(IEnumerable<string>? names)
    {
        var combined = Role.None;
        foreach (var n in names ?? Array.Empty<string>())
        {
            if (!TryParseRole(n, out var r))
            {
                var shown = n is null ? "" : n.Length > 64 ? n[..64] + "…" : n;
                throw new UnknownRoleException(
                    $"Unbekannte Rolle '{shown}'. Erlaubt: {string.Join(", ", AssignableRoleNames)}");
            }
            combined |= r;
        }
        return combined;
    }

    /// <summary>Wie <see cref="CombineRoles"/>, ignoriert unbekannte Namen (für Rollen-Claims aus Tokens).</summary>
    public static Role CombineRolesLenient(IEnumerable<string> names)
    {
        var combined = Role.None;
        foreach (var n in names)
        {
            if (TryParseRole(n, out var r)) combined |= r;
        }
        return combined;
    }

    private static readonly string[] AssignableRoleNames =
        Enum.GetValues<Role>().Where(r => r != Role.None).Select(r => r.ToString()).ToArray();

    private static bool TryParseRole(string? name, out Role role)
    {
        role = Role.None;
        if (string.IsNullOrWhiteSpace(name)) return false;
        var trimmed = name.Trim();
        foreach (var candidate in Enum.GetValues<Role>())
        {
            if (candidate == Role.None) continue;
            if (string.Equals(candidate.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                role = candidate;
                return true;
            }
        }
        return false;
    }

    private static ArgumentException InvalidCurrentPassword()
    {
        var ex = new ArgumentException("Aktuelles Passwort ist falsch");
        ex.Data["code"] = AuthErrorCodes.InvalidCurrentPassword;
        return ex;
    }
}

/// <summary>Maschinenlesbare Fehlercodes der Auth-Endpunkte (Feld "code" in der JSON-Antwort).</summary>
public static class AuthErrorCodes
{
    public const string InvalidCredentials = "invalid_credentials";
    public const string InvalidCurrentPassword = "invalid_current_password";
    public const string PasswordPolicy = "password_policy";
    public const string PasswordChangeRequired = "password_change_required";
}

/// <summary>
/// Login fehlgeschlagen (oder Token-Besitzer existiert nicht mehr). Reserviert für den Login-Pfad -
/// ein falsches Altpasswort beim Passwortwechsel ist bewusst keine InvalidCredentialsException.
/// </summary>
public class InvalidCredentialsException : Exception
{
    public InvalidCredentialsException() : base("Anmeldung fehlgeschlagen") { }
    public InvalidCredentialsException(string message) : base(message) { }
}
