using Lager.Application.Abstractions;
using Lager.Domain.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Lager.Infrastructure.Auth;

/// <summary>
/// Schreibt Sicherheitsereignisse als strukturierte Log-Einträge mit der Kategorie "SecurityAudit"
/// (bei Serilog erscheint sie als SourceContext). Ergänzt Akteur und Client-IP aus dem aktuellen
/// Request. Erfolge laufen als Information, Fehlversuche/Sperren/Ablehnungen als Warning;
/// Passwörter und Hashes kommen hier nie an, weil kein Aufrufer sie weiterreicht.
/// </summary>
public class SecurityAuditLogger : ISecurityAudit
{
    public const string Category = "SecurityAudit";

    private readonly ILogger _logger;
    private readonly IHttpContextAccessor _accessor;

    public SecurityAuditLogger(ILoggerFactory loggerFactory, IHttpContextAccessor accessor)
    {
        _logger = loggerFactory.CreateLogger(Category);
        _accessor = accessor;
    }

    public void Record(SecurityEvent securityEvent, string? username, Guid? userId = null, string? detail = null)
    {
        var http = _accessor.HttpContext;
        var actor = http is null
            ? "system"
            : http.User.Identity?.IsAuthenticated == true ? http.User.Identity.Name ?? "unknown" : "anonymous";
        var clientIp = http?.Connection.RemoteIpAddress?.ToString();

        var level = securityEvent switch
        {
            SecurityEvent.LoginFailed or SecurityEvent.LoginBlocked or SecurityEvent.LoginLockedOut
                or SecurityEvent.PasswordChangeFailed or SecurityEvent.TokenRejected => LogLevel.Warning,
            _ => LogLevel.Information,
        };

        _logger.Log(level,
            "Security {SecurityEvent}: Benutzer={Username} UserId={UserId} Akteur={Actor} ClientIp={ClientIp} Detail={Detail}",
            securityEvent.ToString(), Sanitize(username), userId, actor, clientIp, Sanitize(detail));
    }

    /// <summary>
    /// Benutzernamen stammen ungeprüft aus dem Login-Request: Steuerzeichen (Zeilenumbrüche) entfernen
    /// und die Länge begrenzen, damit niemand gefälschte Zeilen ins Log schreiben oder es aufblähen kann.
    /// </summary>
    private static string? Sanitize(string? value)
    {
        if (value is null) return null;
        var clean = new string(value.Where(c => !char.IsControl(c)).ToArray());
        return clean.Length > 128 ? clean[..128] : clean;
    }
}
