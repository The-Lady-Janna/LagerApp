using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using Lager.Application.Abstractions;
using Lager.Infrastructure.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;

namespace Lager.Api.Security;

/// <summary>Registriert Authentifizierung, Autorisierung, Rate-Limiting und Proxy-Header-Behandlung.</summary>
public static class SecurityServiceCollectionExtensions
{
    public const string LoginRateLimitPolicy = "login";

    public static IServiceCollection AddLagerSecurity(
        this IServiceCollection services, IConfiguration config, JwtKeyResolution jwtKey, SecuritySettings settings)
    {
        services.AddHttpContextAccessor(); // Passwortwechsel-Handler und Sicherheits-Log lesen den aktuellen Request
        services.AddSingleton<ISecurityAudit, SecurityAuditLogger>();
        services.AddSingleton(settings.Lockout);
        services.AddLagerAuthentication(config, jwtKey);
        services.AddLagerAuthorization();
        if (settings.RateLimitingEnabled) services.AddLagerRateLimiting(settings);
        if (settings.ForwardedHeadersEnabled) services.AddLagerForwardedHeaders(settings);
        return services;
    }

    private static void AddLagerAuthentication(this IServiceCollection services, IConfiguration config, JwtKeyResolution jwtKey)
    {
        var jwtSection = config.GetSection("Jwt");
        services.Configure<JwtSettings>(jwtSection);
        // Der aufgelöste, geprüfte Key hat Vorrang (auch der Token-Service benutzt ihn).
        services.PostConfigure<JwtSettings>(o => o.SigningKeyBytes = jwtKey.Key);

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwtSection["Issuer"] ?? "Lager",
                    ValidAudience = jwtSection["Audience"] ?? "Lager",
                    IssuerSigningKey = new SymmetricSecurityKey(jwtKey.Key),
                    // Default 5 min clock-skew is too lax for short-lived tokens — tighten.
                    ClockSkew = TimeSpan.FromSeconds(30),
                };
                // Widerruf: Benutzer je Request aus der DB laden (aktiv? Stamp? Rollen?).
                options.Events = new JwtBearerEvents
                {
                    OnTokenValidated = AuthenticatedUserValidator.OnTokenValidated,
                };
            });
    }

    /// <summary>
    /// Standardmäßig geschlossen: FallbackPolicy (angemeldet + kein ausstehender Passwortwechsel) für alles ohne
    /// eigene Autorisierung, dieselbe Anforderung an der DefaultPolicy und an allen Rollen-Policies.
    /// Zusätzlich hängt Program.cs die DefaultPolicy per RequireAuthorization() an alle Controller-Endpunkte,
    /// weil ein <c>[Authorize(Roles = ...)]</c> allein die Default-/Fallback-Policy nicht einbezieht.
    /// Anonym bleibt nur, was ausdrücklich [AllowAnonymous] trägt (POST /api/auth/login).
    /// </summary>
    private static void AddLagerAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<IAuthorizationHandler, PasswordChangeNotPendingHandler>();
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, LagerAuthorizationResultHandler>();

        static AuthorizationPolicy Authenticated() => new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .AddRequirements(new PasswordChangeNotPendingRequirement())
            .Build();

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(Authenticated())
            .SetDefaultPolicy(Authenticated())
            .AddPolicy("Admin", p => p.RequireRole("Admin").AddRequirements(new PasswordChangeNotPendingRequirement()))
            .AddPolicy("Manager", p => p.RequireRole("Admin", "Manager").AddRequirements(new PasswordChangeNotPendingRequirement()))
            .AddPolicy("Picker", p => p.RequireRole("Admin", "Manager", "Picker").AddRequirements(new PasswordChangeNotPendingRequirement()))
            .AddPolicy("Packer", p => p.RequireRole("Admin", "Manager", "Packer").AddRequirements(new PasswordChangeNotPendingRequirement()))
            .AddPolicy("Receiver", p => p.RequireRole("Admin", "Manager", "Receiver").AddRequirements(new PasswordChangeNotPendingRequirement()));
    }

    /// <summary>
    /// Rate-Limiting je Client-IP (fixes Fenster, 1 Minute): Login streng (Security:LoginRateLimitPerMinute,
    /// Standard 10) über [EnableRateLimiting("login")], dazu ein grobes globales Limit
    /// (Security:GlobalRateLimitPerMinute, Standard 600). Antwort 429 mit Retry-After.
    /// </summary>
    private static void AddLagerRateLimiting(this IServiceCollection services, SecuritySettings settings)
    {
        static string ClientKey(HttpContext ctx) => ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        static FixedWindowRateLimiterOptions Window(int perMinute) => new()
        {
            PermitLimit = perMinute,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        };

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx), _ => Window(settings.GlobalRateLimitPerMinute)));
            options.AddPolicy(LoginRateLimitPolicy, ctx =>
                RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx), _ => Window(settings.LoginRateLimitPerMinute)));

            options.OnRejected = async (rejected, ct) =>
            {
                var response = rejected.HttpContext.Response;
                var retryAfter = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait)
                    ? (int)Math.Ceiling(wait.TotalSeconds)
                    : 60;
                response.Headers.RetryAfter = retryAfter.ToString(CultureInfo.InvariantCulture);
                response.StatusCode = StatusCodes.Status429TooManyRequests;
                await response.WriteAsJsonAsync(new
                {
                    code = "too_many_requests",
                    error = "Zu viele Anfragen. Bitte in einer Minute erneut versuchen.",
                }, ct);
            };
        });
    }

    /// <summary>
    /// Vertrauenswürdige Reverse-Proxys per Konfiguration (Security:ForwardedHeaders:*). Ohne eigene
    /// Listen bleibt es beim ASP.NET-Default (nur Loopback-Proxys); mit Listen gelten NUR diese.
    /// </summary>
    private static void AddLagerForwardedHeaders(this IServiceCollection services, SecuritySettings settings)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            if (settings.KnownProxies.Length == 0 && settings.KnownNetworks.Length == 0) return;

            options.KnownProxies.Clear();
            options.KnownNetworks.Clear();
            foreach (var proxy in settings.KnownProxies)
            {
                if (!IPAddress.TryParse(proxy, out var address))
                    throw new InvalidOperationException($"Security:ForwardedHeaders:KnownProxies enthält keine gültige IP-Adresse: '{proxy}'");
                options.KnownProxies.Add(address);
            }
            foreach (var network in settings.KnownNetworks)
            {
                var parts = network.Split('/');
                if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var prefix) || !int.TryParse(parts[1], out var length))
                    throw new InvalidOperationException($"Security:ForwardedHeaders:KnownNetworks erwartet CIDR-Notation (z. B. 10.0.0.0/8): '{network}'");
                options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, length));
            }
        });
    }
}
