using Lager.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Lager.Tests.WP27;

/// <summary>Eine SQLite-Datei mit dem aktuellen EF-Modell, ohne die App (für Tests des Generators und der reinen Logik).</summary>
internal sealed class DemoDb : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"lager-wp27-{Guid.NewGuid():N}");

    public DemoDb()
    {
        Directory.CreateDirectory(_directory);
        DbPath = Path.Combine(_directory, "demo.db");
    }

    public string DbPath { get; }

    public LagerDbContext CreateContext() => new(new DbContextOptionsBuilder<LagerDbContext>()
        .UseSqlite($"Data Source={DbPath};Pooling=False")
        .Options);

    public async Task<LagerDbContext> CreateEmptyAsync()
    {
        var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
        return ctx;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); }
        catch (IOException) { /* Temp-Verzeichnis */ }
        catch (UnauthorizedAccessException) { /* dito */ }
    }
}

/// <summary>Ein Logger, der alle Einträge (Pegel und formatierter Text) sammelt: der "Test-Log-Sink" für die Demo-Zugangsdaten.</summary>
public sealed class ListLogger : ILogger
{
    public List<(LogLevel Level, string Message)> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Entries.Add((logLevel, formatter(state, exception)));
}
