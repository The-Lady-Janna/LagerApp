using System.Text;
using Lager.Api.Security;
using Lager.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace Lager.Tests.WP01;

/// <summary>Kein Default-Key mehr: ohne echten Key startet die API außerhalb von Development nicht.</summary>
public class JwtKeyGuardTests : IDisposable
{
    // Der früher im Repo eingecheckte, öffentlich bekannte Key.
    private const string KnownRepoKey = "DEV-ONLY-please-replace-in-production-with-256bit-secret";
    private const string GoodKey = "ein-zufaelliger-key-mit-mehr-als-32-bytes-0123456789";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lager-keytest-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { /* Temp */ }
    }

    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    [Fact]
    public void Missing_key_outside_development_aborts_with_a_helpful_message()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => JwtKeyResolver.Resolve(Config(), isDevelopment: false, _dir));

        Assert.Contains("Jwt:SigningKey", ex.Message);
        Assert.Contains("Jwt:KeyFile", ex.Message);
    }

    [Fact]
    public void Known_repo_key_and_short_keys_are_rejected_in_every_environment()
    {
        foreach (var development in new[] { false, true })
        {
            var known = Assert.Throws<InvalidOperationException>(() =>
                JwtKeyResolver.Resolve(Config(("Jwt:SigningKey", KnownRepoKey)), development, _dir));
            Assert.Contains("bekannte Entwicklungs-Key", known.Message);

            var shortKey = Assert.Throws<InvalidOperationException>(() =>
                JwtKeyResolver.Resolve(Config(("Jwt:SigningKey", new string('k', 31))), development, _dir));
            Assert.Contains("zu kurz", shortKey.Message);
        }
    }

    [Fact]
    public void Configured_key_wins_and_development_falls_back_to_an_ephemeral_random_key()
    {
        var configured = JwtKeyResolver.Resolve(Config(("Jwt:SigningKey", GoodKey), ("Jwt:KeyFile", Path.Combine(_dir, "ignored.key"))), false, _dir);
        Assert.Equal(JwtKeySource.Configuration, configured.Source);
        Assert.Equal(Encoding.UTF8.GetBytes(GoodKey), configured.Key);
        Assert.False(File.Exists(Path.Combine(_dir, "ignored.key"))); // KeyFile wird nur ohne SigningKey angefasst

        var first = JwtKeyResolver.Resolve(Config(), isDevelopment: true, _dir);
        var second = JwtKeyResolver.Resolve(Config(), isDevelopment: true, _dir);
        Assert.Equal(JwtKeySource.EphemeralDevelopment, first.Source);
        Assert.True(first.Key.Length >= 32);
        Assert.NotEqual(first.Key, second.Key); // pro Start neu, nirgends festgeschrieben
    }

    [Fact]
    public void Key_file_is_created_with_random_bytes_on_first_start_and_reused_afterwards()
    {
        var path = Path.Combine(_dir, "sub", "jwt.key"); // Verzeichnis existiert noch nicht
        var config = Config(("Jwt:KeyFile", path));

        var created = JwtKeyResolver.Resolve(config, isDevelopment: false, _dir);
        var contentAfterFirstStart = File.ReadAllText(path);
        var reused = JwtKeyResolver.Resolve(config, isDevelopment: false, _dir);

        Assert.Equal(JwtKeySource.KeyFileCreated, created.Source);
        Assert.Equal(JwtKeySource.KeyFile, reused.Source);
        Assert.Equal(64, Convert.FromBase64String(contentAfterFirstStart.Trim()).Length); // 64 Zufallsbytes
        Assert.Equal(contentAfterFirstStart, File.ReadAllText(path)); // nicht neu geschrieben
        Assert.Equal(created.Key, reused.Key);
    }

    [Fact]
    public void A_key_file_with_a_short_or_known_key_is_rejected()
    {
        Directory.CreateDirectory(_dir);
        var shortFile = Path.Combine(_dir, "short.key");
        File.WriteAllText(shortFile, "zu-kurz");
        var knownFile = Path.Combine(_dir, "known.key");
        File.WriteAllText(knownFile, KnownRepoKey + "\n");

        Assert.Throws<InvalidOperationException>(() => JwtKeyResolver.Resolve(Config(("Jwt:KeyFile", shortFile)), false, _dir));
        Assert.Throws<InvalidOperationException>(() => JwtKeyResolver.Resolve(Config(("Jwt:KeyFile", knownFile)), false, _dir));
    }

    // ---- Start der ganzen Anwendung ---------------------------------------------------------------------------------

    [Fact]
    public void Production_start_without_or_with_the_known_key_aborts()
    {
        foreach (var settings in new[]
        {
            new Dictionary<string, string?>(),
            new Dictionary<string, string?> { ["Jwt:SigningKey"] = KnownRepoKey },
        })
        {
            using var factory = new ConfigurableApiFactory("Production", settings);

            var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

            Assert.Contains("Jwt:SigningKey", ex.ToString());
        }
    }

    [Fact]
    public void Production_start_with_key_file_creates_it_once_and_a_restart_reuses_it()
    {
        var path = Path.Combine(_dir, "prod", "jwt.key");
        var settings = new Dictionary<string, string?> { ["Jwt:KeyFile"] = path };

        using (var first = new ConfigurableApiFactory("Production", settings))
        {
            first.CreateClient();
        }
        var created = File.ReadAllText(path);
        using (var second = new ConfigurableApiFactory("Production", settings))
        {
            second.CreateClient();
        }

        Assert.False(string.IsNullOrWhiteSpace(created));
        Assert.Equal(created, File.ReadAllText(path));
    }
}
