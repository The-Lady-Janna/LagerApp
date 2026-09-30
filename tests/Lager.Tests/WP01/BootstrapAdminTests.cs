using System.Net;
using System.Text;
using Lager.Api.Security;
using Lager.Application.Auth;
using Lager.Domain.Auth;
using Lager.Tests.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Lager.Tests.WP01;

/// <summary>Kein Default-Passwort: Zufalls-Einmalpasswort (einmal auf der Konsole) oder ein vom Betreiber gesetztes.</summary>
public class BootstrapAdminTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value)).Build();

    /// <summary>Thread-sicherer Konsolen-Puffer: parallel laufende Hosts (Serilog) dürfen während der Aufnahme mitschreiben.</summary>
    private sealed class LockedWriter : TextWriter
    {
        private readonly StringBuilder _text = new();
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) { lock (_text) _text.Append(value); }
        public override void Write(string? value) { lock (_text) _text.Append(value); }
        public override string ToString() { lock (_text) return _text.ToString(); }
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    [Fact]
    public void Generated_passwords_are_random_long_enough_and_satisfy_the_password_policy()
    {
        var passwords = Enumerable.Range(0, 50).Select(_ => BootstrapAdminService.GeneratePassword()).ToList();

        Assert.Equal(50, passwords.Distinct().Count());
        Assert.All(passwords, p =>
        {
            Assert.Equal(BootstrapAdminService.GeneratedPasswordLength, p.Length);
            Assert.Null(PasswordPolicy.Check(p, "admin"));
            Assert.All(p, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
        });
    }

    [Fact]
    public async Task Without_configured_password_a_one_time_password_is_printed_once_and_never_logged()
    {
        var users = new FakeUserRepository();
        var hasher = new FakePasswordHasher();
        var console = new StringWriter();
        var logger = new CapturingLogger();

        var first = await BootstrapAdminService.EnsureAdminAsync(users, hasher, new FakeUnitOfWork(), Config(), logger, console);
        var second = await BootstrapAdminService.EnsureAdminAsync(users, hasher, new FakeUnitOfWork(), Config(), logger, console);

        Assert.True(first.Created);
        Assert.True(first.PasswordWasGenerated);
        Assert.False(second.Created); // Users nicht mehr leer: nichts wird angelegt oder ausgegeben
        var admin = Assert.Single(users.Users);
        Assert.Equal("admin", admin.Username);
        Assert.True(admin.HasRole(Role.Admin));
        Assert.True(admin.MustChangePassword);

        // Genau ein Einmalpasswort auf der Konsole; es passt zum gespeicherten Hash, steht aber nicht im Log.
        var printed = console.ToString();
        var password = printed.Split('\n').Single(l => l.Contains("Passwort :")).Split(':', 2)[1].Trim();
        Assert.Equal(1, printed.Split(password).Length - 1);
        Assert.True(hasher.Verify(password, admin.PasswordHash));
        Assert.DoesNotContain(logger.Messages, m => m.Contains(password));
        Assert.NotEmpty(logger.Messages);
    }

    [Fact]
    public async Task A_configured_password_is_used_but_never_printed_or_logged()
    {
        var users = new FakeUserRepository();
        var console = new StringWriter();
        var logger = new CapturingLogger();
        const string configured = "Vom-Betreiber-Gesetzt-2025";

        var result = await BootstrapAdminService.EnsureAdminAsync(users, new FakePasswordHasher(), new FakeUnitOfWork(),
            Config(("Auth:BootstrapAdminPassword", configured), ("Auth:BootstrapAdminUsername", "Chef")), logger, console);

        Assert.True(result.Created);
        Assert.False(result.PasswordWasGenerated);
        var admin = Assert.Single(users.Users);
        Assert.Equal("chef", admin.Username);
        Assert.Equal("hash:" + configured, admin.PasswordHash);
        Assert.True(admin.MustChangePassword);
        Assert.Equal("", console.ToString());
        Assert.DoesNotContain(logger.Messages, m => m.Contains(configured));
    }

    [Fact]
    public async Task A_weak_configured_password_aborts_the_start()
    {
        var users = new FakeUserRepository();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BootstrapAdminService.EnsureAdminAsync(users, new FakePasswordHasher(), new FakeUnitOfWork(),
                Config(("Auth:BootstrapAdminPassword", "admin")), new CapturingLogger(), new StringWriter()));

        Assert.Contains("Auth:BootstrapAdminPassword", ex.Message);
        Assert.Empty(users.Users);
    }

    [Fact]
    public async Task Running_app_without_configured_password_prints_a_one_time_password_that_works_and_must_be_changed()
    {
        // Ganze Anwendung ohne Auth:BootstrapAdminPassword: kein Default-Passwort, nur das Einmalpasswort von der Konsole.
        // Console.SetOut ist prozessweit; außer diesem Test schreibt in dieser Testsuite niemand auf Console.Out.
        var captured = new LockedWriter();
        var original = Console.Out;
        Console.SetOut(captured);
        HttpClient client;
        using var factory = new ConfigurableApiFactory("Testing", new Dictionary<string, string?>
        {
            ["Jwt:SigningKey"] = LagerApiFactory.SigningKey,
            ["Auth:BootstrapAdminPassword"] = "",
        });
        try { client = factory.CreateClient(); }
        finally { Console.SetOut(original); }

        var line = captured.ToString().Split('\n').Single(l => l.Contains("Passwort :"));
        var password = line.Split(':', 2)[1].Trim();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.TryLoginAsync("admin", "admin")).StatusCode);
        var login = await client.LoginOkAsync("admin", password);
        Assert.True(login.User.MustChangePassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateClient().WithToken(login.Token).GetAsync("/api/articles")).StatusCode);
    }
}
