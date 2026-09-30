using Lager.Domain.Auth;

namespace Lager.Application.Abstractions;

/// <summary>
/// Snapshot of the currently-authenticated user, derived from the request
/// principal. Implemented in Infrastructure via IHttpContextAccessor so the
/// Application layer stays HTTP-free.
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    Guid? UserId { get; }
    string? Username { get; }
    Role Roles { get; }

    /// <summary>
    /// true, wenn der Code außerhalb eines HTTP-Requests läuft (Startup, Seeder, Hintergrundjobs).
    /// Nur dann darf im Audit-Trail "system" als Akteur stehen; ein nicht angemeldeter Request ist
    /// "anonymous". Default false, damit bestehende Implementierungen weiter kompilieren.
    /// </summary>
    bool IsSystemContext => false;
}

/// <summary>
/// Schreibt sicherheitsrelevante Ereignisse (Login, Sperre, Passwort-/Rollenänderung, Token-Ablehnung)
/// als strukturierten Log-Eintrag mit SourceContext "SecurityAudit". Der Aufrufer übergibt nie
/// Passwörter oder Hashes; Akteur und Client-IP ergänzt die Implementierung aus dem Request.
/// Liegt hier (Application), damit die Services kein Logging-Framework kennen müssen.
/// </summary>
public interface ISecurityAudit
{
    void Record(SecurityEvent securityEvent, string? username, Guid? userId = null, string? detail = null);
}

/// <summary>Tut nichts - Standard, wenn kein <see cref="ISecurityAudit"/> registriert ist (z. B. in Unit-Tests).</summary>
public sealed class NullSecurityAudit : ISecurityAudit
{
    public static NullSecurityAudit Instance { get; } = new();
    private NullSecurityAudit() { }

    public void Record(SecurityEvent securityEvent, string? username, Guid? userId = null, string? detail = null) { }
}
