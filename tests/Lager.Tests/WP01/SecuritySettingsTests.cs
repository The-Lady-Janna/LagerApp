using Lager.Api.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Lager.Tests.WP01;

/// <summary>Konfiguration der Sicherheitsfunktionen: Defaults, Validierung, HTTPS-Port, vertrauenswürdige Proxys.</summary>
public class SecuritySettingsTests
{
    private sealed class FakeEnvironment : IHostEnvironment
    {
        public FakeEnvironment(string name) => EnvironmentName = name;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "Lager.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    [Fact]
    public void Defaults_match_the_documented_limits_and_rate_limiting_is_only_off_in_Testing()
    {
        var production = SecuritySettings.From(Config(), new FakeEnvironment("Production"));
        var testing = SecuritySettings.From(Config(), new FakeEnvironment("Testing"));
        var testingButExplicit = SecuritySettings.From(Config(("Security:RateLimiting:Enabled", "true")), new FakeEnvironment("Testing"));

        Assert.True(production.RateLimitingEnabled);
        Assert.Equal(10, production.LoginRateLimitPerMinute);
        Assert.Equal(600, production.GlobalRateLimitPerMinute);
        Assert.Equal(5, production.Lockout.MaxFailedAttempts);
        Assert.Equal(TimeSpan.FromMinutes(15), production.Lockout.Duration);
        Assert.False(production.RequireHttps);
        Assert.False(production.ForwardedHeadersEnabled);
        Assert.False(testing.RateLimitingEnabled);
        Assert.True(testingButExplicit.RateLimitingEnabled);
    }

    [Fact]
    public void Limits_and_lockout_are_configurable_and_nonsense_values_abort_the_start()
    {
        var custom = SecuritySettings.From(Config(
            ("Security:LoginRateLimitPerMinute", "30"), ("Auth:MaxFailedAttempts", "3"), ("Auth:LockoutMinutes", "1")),
            new FakeEnvironment("Production"));

        Assert.Equal(30, custom.LoginRateLimitPerMinute);
        Assert.Equal(3, custom.Lockout.MaxFailedAttempts);
        Assert.Equal(TimeSpan.FromMinutes(1), custom.Lockout.Duration);

        foreach (var bad in new[] { "0", "-5", "viele" })
            Assert.Throws<InvalidOperationException>(() =>
                SecuritySettings.From(Config(("Auth:MaxFailedAttempts", bad)), new FakeEnvironment("Production")));
    }

    [Fact]
    public void Https_port_is_only_known_when_configured_or_derivable_from_the_urls()
    {
        Assert.Null(SecuritySettings.ResolveHttpsPort(Config()));
        Assert.Null(SecuritySettings.ResolveHttpsPort(Config(("urls", "http://localhost:5099"))));
        Assert.Equal(8443, SecuritySettings.ResolveHttpsPort(Config(("Security:HttpsPort", "8443"))));
        Assert.Equal(7443, SecuritySettings.ResolveHttpsPort(Config(("HTTPS_PORT", "7443"))));
        Assert.Equal(5443, SecuritySettings.ResolveHttpsPort(Config(("urls", "http://+:5000;https://+:5443"))));
        Assert.Equal(7098, SecuritySettings.ResolveHttpsPort(Config(("urls", "https://localhost:7098;http://localhost:5099"))));
    }

    [Fact]
    public void Forwarded_headers_trust_only_the_configured_proxies_and_networks()
    {
        var settings = SecuritySettings.From(Config(
            ("Security:ForwardedHeaders:Enabled", "true"),
            ("Security:ForwardedHeaders:KnownProxies:0", "10.1.2.3"),
            ("Security:ForwardedHeaders:KnownNetworks:0", "192.168.0.0/16")),
            new FakeEnvironment("Production"));

        var options = BuildForwardedHeadersOptions(settings);

        Assert.True(settings.ForwardedHeadersEnabled);
        Assert.Equal("10.1.2.3", Assert.Single(options.KnownProxies).ToString()); // Loopback-Standard ersetzt
        var network = Assert.Single(options.KnownNetworks);
        Assert.Equal("192.168.0.0", network.Prefix.ToString());
        Assert.Equal(16, network.PrefixLength);
        Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedFor));
        Assert.True(options.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedProto));
    }

    [Fact]
    public void Invalid_proxy_or_network_entries_abort_instead_of_silently_trusting_nothing_or_everything()
    {
        foreach (var (key, value) in new[]
                 {
                     ("Security:ForwardedHeaders:KnownProxies:0", "kein-ip"),
                     ("Security:ForwardedHeaders:KnownNetworks:0", "10.0.0.0"),       // ohne /Präfix
                     ("Security:ForwardedHeaders:KnownNetworks:0", "10.0.0.0/acht"),
                 })
        {
            var settings = SecuritySettings.From(
                Config(("Security:ForwardedHeaders:Enabled", "true"), (key, value)), new FakeEnvironment("Production"));

            Assert.Throws<InvalidOperationException>(() => BuildForwardedHeadersOptions(settings));
        }
    }

    private static ForwardedHeadersOptions BuildForwardedHeadersOptions(SecuritySettings settings)
    {
        var services = new ServiceCollection();
        services.AddLagerSecurity(
            Config(), new JwtKeyResolution(new byte[64], JwtKeySource.Configuration), settings);
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
    }
}
