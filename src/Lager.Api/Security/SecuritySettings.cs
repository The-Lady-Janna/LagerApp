using Lager.Domain.Auth;

namespace Lager.Api.Security;

/// <summary>
/// Sicherheits-relevante Einstellungen aus der Konfiguration ("Security:*" und "Auth:*"), einmal gelesen
/// und validiert. Ungültige Werte (0, negativ, keine Zahl) brechen den Start ab, statt still
/// auf einen Default zu fallen.
/// </summary>
public sealed record SecuritySettings(
    bool RequireHttps,
    int? HttpsPort,
    bool RateLimitingEnabled,
    int LoginRateLimitPerMinute,
    int GlobalRateLimitPerMinute,
    bool ForwardedHeadersEnabled,
    string[] KnownProxies,
    string[] KnownNetworks,
    LockoutPolicy Lockout)
{
    public static SecuritySettings From(IConfiguration config, IHostEnvironment env)
    {
        // Rate-Limiting ist im Environment "Testing" aus (viele parallele Logins von einer IP), sonst an.
        // Security:RateLimiting:Enabled schlägt beide Defaults.
        var rateLimiting = config.GetValue<bool?>("Security:RateLimiting:Enabled") ?? !env.IsEnvironment("Testing");

        return new SecuritySettings(
            RequireHttps: config.GetValue<bool>("Security:RequireHttps"),
            HttpsPort: ResolveHttpsPort(config),
            RateLimitingEnabled: rateLimiting,
            LoginRateLimitPerMinute: Positive(config, "Security:LoginRateLimitPerMinute", 10),
            GlobalRateLimitPerMinute: Positive(config, "Security:GlobalRateLimitPerMinute", 600),
            ForwardedHeadersEnabled: config.GetValue<bool>("Security:ForwardedHeaders:Enabled"),
            KnownProxies: config.GetSection("Security:ForwardedHeaders:KnownProxies").Get<string[]>() ?? Array.Empty<string>(),
            KnownNetworks: config.GetSection("Security:ForwardedHeaders:KnownNetworks").Get<string[]>() ?? Array.Empty<string>(),
            Lockout: new LockoutPolicy(
                Positive(config, "Auth:MaxFailedAttempts", LockoutPolicy.Default.MaxFailedAttempts),
                TimeSpan.FromMinutes(Positive(config, "Auth:LockoutMinutes", (int)LockoutPolicy.Default.Duration.TotalMinutes))));
    }

    private static int Positive(IConfiguration config, string key, int fallback)
    {
        var raw = config[key];
        if (string.IsNullOrWhiteSpace(raw)) return fallback;
        if (!int.TryParse(raw, out var value) || value < 1)
            throw new InvalidOperationException($"Konfiguration {key} muss eine ganze Zahl >= 1 sein (war: '{raw}').");
        return value;
    }

    /// <summary>
    /// Der HTTPS-Port für die Weiterleitung, falls ermittelbar: Security:HttpsPort, HTTPS_PORT
    /// (Umgebungsvariable ASPNETCORE_HTTPS_PORT) oder die erste https-Adresse in ASPNETCORE_URLS.
    /// Ohne Port kann UseHttpsRedirection nicht weiterleiten (es wäre ein wirkungsloser Aufruf mit
    /// Warnung) - hinter einem TLS-terminierenden Proxy leitet ohnehin der Proxy um.
    /// </summary>
    public static int? ResolveHttpsPort(IConfiguration config)
    {
        foreach (var key in new[] { "Security:HttpsPort", "HTTPS_PORT" })
        {
            var raw = config[key];
            if (int.TryParse(raw, out var port) && port is > 0 and <= 65535) return port;
        }

        var urls = config["urls"] ?? config["URLS"];
        if (!string.IsNullOrWhiteSpace(urls))
        {
            foreach (var part in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!part.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) continue;
                // Platzhalter-Hosts (+, *) sind keine gültige Uri - für die Port-Ermittlung durch localhost ersetzen.
                var candidate = part.Replace("://+", "://localhost").Replace("://*", "://localhost");
                if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Port > 0) return uri.Port;
            }
        }
        return null;
    }
}
