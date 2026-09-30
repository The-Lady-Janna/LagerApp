using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lager.Contracts.Articles;
using Lager.Infrastructure;
using Lager.Infrastructure.Backup;
using Lager.Infrastructure.Persistence;
using Lager.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Lager.Tests.WP25;

/// <summary>
/// Startet die API gegen eine frische SQLite-Datei in einem EIGENEN Temp-Verzeichnis (Backups und Sicherheitskopien liegen
/// daneben und werden mit aufgeräumt). Environment und Einstellungen (Backup:...) sind wählbar; Admin-Zugangsdaten und
/// Signing-Key wie in <see cref="LagerApiFactory"/>, damit <c>AsReadyAdminAsync()</c> funktioniert.
/// </summary>
internal sealed class Wp25Factory : WebApplicationFactory<Program>
{
    private readonly string _environment;
    private readonly Dictionary<string, string?> _settings;

    public Wp25Factory(string environment = "Testing", IDictionary<string, string?>? settings = null)
    {
        _environment = environment;
        _settings = settings is null ? new() : new Dictionary<string, string?>(settings);
        Directory = Path.Combine(Path.GetTempPath(), $"lager-wp25-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Directory);
        DbPath = Path.Combine(Directory, "lager.db");
    }

    /// <summary>Verzeichnis der Datenbank (ohne Backup:Directory landen die Backups dort).</summary>
    public string Directory { get; }

    public string DbPath { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("Database:Provider", "Sqlite");
        builder.UseSetting("Database:ConnectionString", $"Data Source={DbPath}");
        builder.UseSetting("Database:Seed", "false");
        builder.UseSetting("Jwt:SigningKey", LagerApiFactory.SigningKey);
        builder.UseSetting("Auth:BootstrapAdminUsername", LagerApiFactory.AdminUser);
        builder.UseSetting("Auth:BootstrapAdminPassword", LagerApiFactory.AdminPassword);
        foreach (var (key, value) in _settings)
            builder.UseSetting(key, value);
    }

    /// <summary>Die Dateinamen im DB-Verzeichnis, die zum Muster passen (z. B. "lager-backup-*.db"), sortiert.</summary>
    public string[] FilesMatching(string pattern) =>
        System.IO.Directory.GetFiles(Directory, pattern).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        SqliteConnection.ClearAllPools();
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (IOException) { /* Temp-Verzeichnis, wird beim nächsten Cleanup entfernt */ }
        catch (UnauthorizedAccessException) { /* dito */ }
    }
}

/// <summary>Direkter SQL-Zugriff auf eine SQLite-Datei (eigene, nicht gepoolte Verbindung), unabhängig von der App.</summary>
internal static class Wp25Sql
{
    public static SqliteConnection Open(string path, bool create = false)
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
            Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite,
        }.ToString());
        conn.Open();
        return conn;
    }

    public static List<string> Strings(string path, string sql)
    {
        using var conn = Open(path);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();
        var values = new List<string>();
        while (reader.Read()) values.Add(Convert.ToString(reader.GetValue(0)) ?? string.Empty);
        return values;
    }

    public static long Count(string path, string sql) => Convert.ToInt64(Strings(path, sql).Single());

    /// <summary>Ergebnis von <c>PRAGMA integrity_check</c> (bei einer heilen Datei genau "ok").</summary>
    public static List<string> IntegrityCheck(string path) => Strings(path, "PRAGMA integrity_check;");

    /// <summary>Die Artikelnummern der Datenbank, sortiert (der fachliche Inhalt, an dem man Backup und Restore erkennt).</summary>
    public static string Skus(string path) => string.Join("|", Strings(path, "SELECT Sku FROM Articles ORDER BY Sku"));
}

internal static class Wp25Api
{
    public static async Task<ArticleDto> CreateArticleAsync(this HttpClient admin, string sku)
    {
        var response = await admin.PostAsJsonAsync("/api/articles", new CreateArticleRequest(
            sku, $"Testartikel {sku}", null, new DimensionsDto(100, 100, 100), 100, new StackingInfoDto(false, "Z", 0, null)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ArticleDto>())!;
    }

    /// <summary>Legt über die API ein Backup an und gibt seinen Namen zurück (201 mit dem Eintrag als Antwort).</summary>
    public static async Task<string> CreateBackupAsync(this HttpClient admin)
    {
        var response = await admin.PostAsync("/api/admin/backups", null);
        var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, json);
        return JsonDocument.Parse(json).RootElement.GetProperty("name").GetString()!;
    }

    public static async Task<JsonElement> ReadJsonAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    /// <summary>Der Fehlercode (<c>code</c>) einer Fehlerantwort; prüft dabei den erwarteten Status.</summary>
    public static async Task<string> CodeAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Erwartet {(int)expected}, war {(int)response.StatusCode}: {json}");
        return JsonDocument.Parse(json).RootElement.GetProperty("code").GetString()!;
    }

    /// <summary>Restore-Formular: entweder eine Datei (<paramref name="content"/>) oder ein vorhandenes Backup (<paramref name="backupName"/>).</summary>
    public static Task<HttpResponseMessage> RestoreAsync(
        this HttpClient admin, byte[]? content = null, string? backupName = null, string? confirm = "RESTORE")
    {
        // Immer ein Feld dabei: ein Multipart-Formular ohne jeden Teil wäre schon als Formular ungültig.
        var form = new MultipartFormDataContent { { new StringContent("Formular"), "hinweis" } };
        if (content is not null)
        {
            var file = new ByteArrayContent(content);
            file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", "upload.db");
        }
        if (backupName is not null) form.Add(new StringContent(backupName), "backupName");
        if (confirm is not null) form.Add(new StringContent(confirm), "confirm");
        return admin.PostAsync("/api/admin/restore", form);
    }
}

/// <summary>
/// Eine SQLite-Datei mit dem aktuellen EF-Modell in eigenem Temp-Verzeichnis, ohne die App (für Tests des Backup-Dienstes und
/// des Hintergrundjobs ohne Host). Die Datei heißt <c>lager.db</c>, Backups landen daneben.
/// </summary>
internal sealed class StandaloneDatabase : IDisposable
{
    public StandaloneDatabase()
    {
        Directory = Path.Combine(Path.GetTempPath(), $"lager-wp25-db-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(Directory);
        DbPath = Path.Combine(Directory, "lager.db");
        Settings = DatabaseSettings.Create(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "Sqlite",
                ["Database:ConnectionString"] = $"Data Source={DbPath}",
            }).Build(),
            Directory);

        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public string Directory { get; }
    public string DbPath { get; }
    public DatabaseSettings Settings { get; }

    public LagerDbContext CreateContext() => new(new DbContextOptionsBuilder<LagerDbContext>()
        .UseSqlite($"Data Source={DbPath};Pooling=False")
        .Options);

    public BackupService CreateService(BackupOptions? options = null, TimeProvider? time = null, ILogger? logger = null) =>
        BackupService.Create(Settings, options ?? new BackupOptions(), Directory, logger, time);

    /// <summary>Namen aller Sicherungsdateien (Backups und Sicherheitskopien) im DB-Verzeichnis, sortiert.</summary>
    public string[] BackupFiles() =>
        System.IO.Directory.GetFiles(Directory, "lager-*.db").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (IOException) { /* Temp-Verzeichnis */ }
        catch (UnauthorizedAccessException) { /* dito */ }
    }
}

/// <summary>
/// Eine Uhr, die nur der Test vorstellt: <see cref="GetUtcNow"/> steht still, <see cref="AdvanceToNextTimer"/> springt zum
/// nächsten fälligen Timer (z. B. dem <c>Task.Delay</c> des Hintergrundjobs) und löst ihn aus. So laufen mehrere Tage
/// Zeitplan in Millisekunden ab, ohne dass der Test wartet.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate) return _now;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Setzt die Uhr auf einen bestimmten Zeitpunkt (ohne Timer auszulösen).</summary>
    public void SetUtcNow(DateTimeOffset value)
    {
        lock (_gate) _now = value;
    }

    /// <summary>Zahl der Timer, die auf ihre Fälligkeit warten (der schlafende Hintergrundjob hat genau einen).</summary>
    public int PendingTimers
    {
        get { lock (_gate) return _timers.Count; }
    }

    /// <summary>Wartet (in echter Zeit), bis ein Timer bereitsteht; false, wenn er nicht innerhalb von <paramref name="timeout"/> kommt.</summary>
    public async Task<bool> WaitForTimerAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (PendingTimers == 0)
        {
            if (DateTime.UtcNow > deadline) return false;
            await Task.Delay(2);
        }
        return true;
    }

    /// <summary>Stellt die Uhr auf die Fälligkeit des nächsten Timers und löst ihn aus. false, wenn keiner wartet.</summary>
    public bool AdvanceToNextTimer()
    {
        ManualTimer? next;
        lock (_gate)
        {
            next = _timers.OrderBy(t => t.Due).FirstOrDefault();
            if (next is null) return false;
            if (next.Due > _now) _now = next.Due;
            _timers.Remove(next);
        }
        next.Fire();
        return true;
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
        }

        public DateTimeOffset Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._gate)
            {
                _owner._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan) return true;
                Due = _owner._now + dueTime;
                _owner._timers.Add(this);
                return true;
            }
        }

        public void Fire() => _callback(_state);

        public void Dispose()
        {
            lock (_owner._gate) _owner._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>Sammelt Logeinträge, damit Tests prüfen können, dass Fehler geloggt statt verschluckt werden.</summary>
internal sealed class ListLogger : ILogger
{
    private readonly List<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get { lock (_entries) return _entries.ToList(); }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        lock (_entries) _entries.Add((logLevel, formatter(state, exception) + (exception is null ? string.Empty : " | " + exception.Message)));
    }
}

/// <summary>Logger&lt;T&gt; auf einem <see cref="ListLogger"/> (für Klassen, die einen typisierten Logger verlangen).</summary>
internal sealed class ListLogger<T> : ILogger<T>
{
    public ListLogger(ListLogger inner) => Inner = inner;

    public ListLogger Inner { get; }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Inner.Log(logLevel, eventId, state, exception, formatter);
}

/// <summary>Eine Hosting-Umgebung mit wählbarem Namen (für Actions, die ohne Host aufgerufen werden).</summary>
internal sealed class Wp25Environment : IWebHostEnvironment
{
    public Wp25Environment(string name) => EnvironmentName = name;
    public string EnvironmentName { get; set; }
    public string ApplicationName { get; set; } = "Lager.Tests";
    public string WebRootPath { get; set; } = string.Empty;
    public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    public string ContentRootPath { get; set; } = Path.GetTempPath();
    public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
}
