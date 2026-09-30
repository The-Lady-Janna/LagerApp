using System.Net;
using Lager.Domain.Auth;
using Lager.Infrastructure.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Lager.Tests.WP01;

/// <summary>Sicherheitsereignisse landen als strukturierter Eintrag mit SourceContext "SecurityAudit".</summary>
public class SecurityAuditLoggerTests
{
    private sealed record Entry(string Category, LogLevel Level, string Message, IReadOnlyDictionary<string, object?> Properties);

    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        public List<Entry> Entries { get; } = new();
        public void AddProvider(ILoggerProvider provider) { }
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);
        public void Dispose() { }

        private sealed class CapturingLogger : ILogger
        {
            private readonly string _category;
            private readonly List<Entry> _entries;
            public CapturingLogger(string category, List<Entry> entries) { _category = category; _entries = entries; }
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var props = (state as IEnumerable<KeyValuePair<string, object?>>)?.ToDictionary(p => p.Key, p => p.Value)
                            ?? new Dictionary<string, object?>();
                _entries.Add(new Entry(_category, logLevel, formatter(state, exception), props));
            }
        }
    }

    [Fact]
    public void Events_are_logged_under_the_SecurityAudit_category_with_level_actor_and_client_ip()
    {
        var factory = new CapturingLoggerFactory();
        var accessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { Connection = { RemoteIpAddress = IPAddress.Parse("10.0.0.5") } },
        };
        var audit = new SecurityAuditLogger(factory, accessor);
        var userId = Guid.NewGuid();

        audit.Record(SecurityEvent.LoginSucceeded, "anna", userId);
        audit.Record(SecurityEvent.LoginFailed, "anna", userId, "Fehlversuch 2");
        audit.Record(SecurityEvent.LoginLockedOut, "anna", userId);

        Assert.All(factory.Entries, e => Assert.Equal("SecurityAudit", e.Category));
        Assert.Equal(new[] { LogLevel.Information, LogLevel.Warning, LogLevel.Warning }, factory.Entries.Select(e => e.Level).ToArray());
        var failed = factory.Entries[1];
        Assert.Equal("LoginFailed", failed.Properties["SecurityEvent"]);
        Assert.Equal("anna", failed.Properties["Username"]);
        Assert.Equal(userId, failed.Properties["UserId"]);
        Assert.Equal("anonymous", failed.Properties["Actor"]);   // Request ohne Anmeldung
        Assert.Equal("10.0.0.5", failed.Properties["ClientIp"]);
        Assert.Equal("Fehlversuch 2", failed.Properties["Detail"]);
    }

    [Fact]
    public void Outside_a_request_the_actor_is_system_and_user_supplied_names_cannot_forge_log_lines()
    {
        var factory = new CapturingLoggerFactory();
        var audit = new SecurityAuditLogger(factory, new HttpContextAccessor()); // kein HttpContext
        var forged = "anna\n2026-01-01 [ERR] gefaelscht" + new string('x', 300);

        audit.Record(SecurityEvent.LoginFailed, forged, null, "Detail\r\nzweite Zeile");

        var entry = Assert.Single(factory.Entries);
        Assert.Equal("system", entry.Properties["Actor"]);
        var username = Assert.IsType<string>(entry.Properties["Username"]);
        Assert.DoesNotContain('\n', username);
        Assert.True(username.Length <= 128);
        Assert.DoesNotContain('\n', (string)entry.Properties["Detail"]!);
        Assert.DoesNotContain('\r', (string)entry.Properties["Detail"]!);
    }
}
