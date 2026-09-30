namespace Lager.Domain.Auth;

/// <summary>
/// Sicherheitsrelevante Ereignisse, die als strukturierte Log-Einträge (SourceContext
/// "SecurityAudit") festgehalten werden. Nie Passwörter oder Hashes mitgeben.
/// </summary>
public enum SecurityEvent
{
    LoginSucceeded,
    /// <summary>Falsches Passwort (oder unbekannter Benutzer - der Grund steht nur im Server-Log).</summary>
    LoginFailed,
    /// <summary>Anmeldeversuch an einem gesperrten oder deaktivierten Konto.</summary>
    LoginBlocked,
    /// <summary>Dieser Fehlversuch hat die Kontosperre ausgelöst.</summary>
    LoginLockedOut,
    PasswordChanged,
    PasswordChangeFailed,
    PasswordReset,
    UserCreated,
    RolesChanged,
    UserActivated,
    UserDeactivated,
    /// <summary>Ein signiertes Token wurde abgelehnt (Konto deaktiviert, Passwort/Rollen geändert, unbekannter Benutzer).</summary>
    TokenRejected,
}
