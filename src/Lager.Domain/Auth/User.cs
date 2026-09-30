using System.Security.Cryptography;
using System.Text;
using Lager.Domain.Common;

namespace Lager.Domain.Auth;

/// <summary>
/// Local user account. The password is NEVER stored — only the BCrypt hash
/// (which embeds the salt + cost factor). Login throttling is enforced via
/// FailedLoginAttempts + LockedUntil: after N failed tries the account is
/// locked for a cooldown window.
/// </summary>
public class User : Entity
{
    public string Username { get; private set; } = string.Empty;
    public string? Email { get; private set; }
    public string? DisplayName { get; private set; }

    /// <summary>BCrypt hash. Includes algorithm-version, cost factor and salt — opaque to the domain.</summary>
    public string PasswordHash { get; private set; } = string.Empty;

    /// <summary>Bitwise OR of <see cref="Role"/> values.</summary>
    public Role Roles { get; private set; } = Role.None;

    public bool IsActive { get; private set; } = true;
    public DateTime? LastLoginAt { get; private set; }
    public int FailedLoginAttempts { get; private set; }
    public DateTime? LockedUntil { get; private set; }

    /// <summary>Set to true when a user MUST change their password on next login (e.g. after admin reset).</summary>
    public bool MustChangePassword { get; private set; }

    private User() { }

    public User(string username, string passwordHash, Role roles, string? email = null, string? displayName = null, bool mustChangePassword = false)
    {
        if (string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("Username darf nicht leer sein", nameof(username));
        if (string.IsNullOrWhiteSpace(passwordHash))
            throw new ArgumentException("PasswordHash darf nicht leer sein", nameof(passwordHash));

        Username = username.Trim().ToLowerInvariant();
        PasswordHash = passwordHash;
        Roles = roles;
        Email = email?.Trim();
        DisplayName = displayName?.Trim();
        MustChangePassword = mustChangePassword;
    }

    public bool HasRole(Role role) => (Roles & role) != 0;

    public bool IsLocked() => IsLocked(DateTime.UtcNow);

    /// <summary>Wie <see cref="IsLocked()"/>, mit explizitem Zeitpunkt (testbar, ohne Seiteneffekt).</summary>
    public bool IsLocked(DateTime utcNow) => LockedUntil is DateTime until && until > utcNow;

    /// <summary>
    /// Kurzer Fingerabdruck des aktuellen Passwort-Hashes. Wird beim Ausstellen als Claim "sstamp"
    /// ins Token geschrieben; ändert sich das Passwort (Wechsel/Reset), passt der Stamp alter
    /// Tokens nicht mehr und sie werden abgelehnt (Token-Widerruf ohne eigene Spalte).
    /// </summary>
    public string GetSecurityStamp()
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(PasswordHash));
        return Convert.ToHexString(digest, 0, 8).ToLowerInvariant();
    }

    /// <summary>
    /// Called after the application service has already verified the password.
    /// Resets the failed-counter and stamps last-login.
    /// </summary>
    public void OnSuccessfulLogin()
    {
        FailedLoginAttempts = 0;
        LockedUntil = null;
        LastLoginAt = DateTime.UtcNow;
        Touch();
    }

    /// <summary>
    /// Called when password verification failed. Locks the account once
    /// <see cref="LockoutPolicy.MaxFailedAttempts"/> attempts have been used up.
    /// Ist eine frühere Sperre bereits abgelaufen, beginnt die Zählung wieder bei 0 - sonst
    /// würde ein einziger Tippfehler nach Ablauf sofort wieder sperren. Während einer laufenden
    /// Sperre ändert sich nichts (die Sperre wird nicht verlängert).
    /// Rückgabe: true, wenn das Konto danach gesperrt ist.
    /// </summary>
    public bool OnFailedLogin(LockoutPolicy policy, DateTime? utcNow = null)
    {
        var now = utcNow ?? DateTime.UtcNow;
        if (IsLocked(now)) return true;

        if (LockedUntil is not null)
        {
            // Die Sperre ist abgelaufen -> frisch anfangen.
            FailedLoginAttempts = 0;
            LockedUntil = null;
        }

        FailedLoginAttempts++;
        if (FailedLoginAttempts >= policy.MaxFailedAttempts)
        {
            LockedUntil = now + policy.Duration;
        }
        Touch();
        return LockedUntil is not null;
    }

    /// <summary>Wie oben mit der Standard-Policy (5 Versuche, 15 Minuten).</summary>
    public bool OnFailedLogin() => OnFailedLogin(LockoutPolicy.Default);

    public void SetPasswordHash(string newHash, bool mustChange = false)
    {
        if (string.IsNullOrWhiteSpace(newHash))
            throw new ArgumentException("PasswordHash darf nicht leer sein", nameof(newHash));
        PasswordHash = newHash;
        MustChangePassword = mustChange;
        Touch();
    }

    public void SetRoles(Role roles)
    {
        Roles = roles;
        Touch();
    }

    public void UpdateProfile(string? email, string? displayName)
    {
        Email = email?.Trim();
        DisplayName = displayName?.Trim();
        Touch();
    }

    public void Activate()
    {
        IsActive = true;
        Touch();
    }

    public void Deactivate()
    {
        IsActive = false;
        Touch();
    }

    public void Unlock()
    {
        FailedLoginAttempts = 0;
        LockedUntil = null;
        Touch();
    }
}
