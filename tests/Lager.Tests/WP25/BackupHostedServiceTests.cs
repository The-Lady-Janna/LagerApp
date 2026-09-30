using Lager.Infrastructure;
using Lager.Infrastructure.Backup;
using Lager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Lager.Tests.WP25;

/// <summary>
/// Der zeitgesteuerte Backup-Job ohne Host und ohne Warten: die Uhr ist eine <see cref="ManualTimeProvider"/>, der Test stellt sie vor.
/// Akzeptanz: mit Zeitplan und RetentionCount=2 bleiben nach vier Läufen genau zwei Dateien.
/// </summary>
public class BackupHostedServiceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private sealed class Rig : IDisposable
    {
        private readonly ServiceProvider _provider;

        public Rig(StandaloneDatabase database, BackupOptions options, DateTimeOffset start)
        {
            Database = database;
            Options = options;
            Clock = new ManualTimeProvider(start);
            Log = new ListLogger();
            Service = database.CreateService(options, Clock, Log);
            _provider = new ServiceCollection()
                .AddDbContext<LagerDbContext>(o => o.UseSqlite($"Data Source={database.DbPath};Pooling=False"))
                .BuildServiceProvider();
            Job = new BackupHostedService(_provider.GetRequiredService<IServiceScopeFactory>(), Service, Clock, new ListLogger<BackupHostedService>(Log));
        }

        public StandaloneDatabase Database { get; }
        public BackupOptions Options { get; }
        public ManualTimeProvider Clock { get; }
        public ListLogger Log { get; }
        public BackupService Service { get; }
        public BackupHostedService Job { get; }

        /// <summary>Stellt die Uhr Timer für Timer vor, bis der Job so viele Backups angelegt hat (gezählt am Log, nicht an den Dateien).</summary>
        public async Task AdvanceUntilRunsAsync(int runs)
        {
            for (var step = 0; step < 5000 && RunsSoFar() < runs; step++)
            {
                // Der Job schläft, sobald ein Timer bereitsteht; während eines Laufs gibt es keinen.
                Assert.True(await Clock.WaitForTimerAsync(Patience), "Der Job hat keinen neuen Timer gesetzt");
                Clock.AdvanceToNextTimer();
            }
            // Der letzte Lauf ist erst fertig, wenn der Job wieder schläft.
            Assert.True(await Clock.WaitForTimerAsync(Patience), "Der Job schläft nach dem Lauf nicht wieder");
            Assert.Equal(runs, RunsSoFar());
        }

        public int RunsSoFar() => Log.Entries.Count(e => e.Message.StartsWith("Zeitgesteuertes Backup erstellt", StringComparison.Ordinal));

        public string[] Backups() => Database.BackupFiles().Where(f => f.StartsWith("lager-backup-", StringComparison.Ordinal)).ToArray();

        public void Dispose() => _provider.Dispose();
    }

    [Fact]
    public async Task With_a_schedule_and_retention_of_two_exactly_two_files_remain_after_four_runs()
    {
        using var database = new StandaloneDatabase();
        // Start 2026-01-01 00:10 UTC, Zeitplan täglich 02:00 -> Läufe am 01.01., 02.01., 03.01. und 04.01. um 02:00
        using var rig = new Rig(database, new BackupOptions { Schedule = "02:00", RetentionCount = 2 }, new DateTimeOffset(2026, 1, 1, 0, 10, 0, TimeSpan.Zero));

        await rig.Job.StartAsync(CancellationToken.None);
        try
        {
            await rig.AdvanceUntilRunsAsync(1);
            Assert.Equal(new[] { "lager-backup-20260101-020000-000.db" }, rig.Backups()); // nicht vor der Startzeit, dann genau um 02:00 UTC

            await rig.AdvanceUntilRunsAsync(4);
        }
        finally
        {
            await rig.Job.StopAsync(CancellationToken.None);
        }

        Assert.Equal(
            new[] { "lager-backup-20260103-020000-000.db", "lager-backup-20260104-020000-000.db" },
            rig.Backups()); // genau zwei: die ältesten Läufe wurden gelöscht
        foreach (var name in rig.Backups())
            Assert.Equal(new[] { "ok" }, Wp25Sql.IntegrityCheck(Path.Combine(database.Directory, name)));
    }

    [Fact]
    public async Task Four_direct_runs_with_retention_of_two_leave_exactly_two_files_and_never_throw()
    {
        using var database = new StandaloneDatabase();
        using var rig = new Rig(database, new BackupOptions { RetentionCount = 2 }, new DateTimeOffset(2026, 5, 1, 2, 0, 0, TimeSpan.Zero));

        for (var day = 0; day < 4; day++)
        {
            rig.Clock.SetUtcNow(new DateTimeOffset(2026, 5, 1 + day, 2, 0, 0, TimeSpan.Zero));
            Assert.True(await rig.Job.RunBackupAsync());
        }

        Assert.Equal(new[] { "lager-backup-20260503-020000-000.db", "lager-backup-20260504-020000-000.db" }, rig.Backups());
    }

    [Fact]
    public async Task Without_a_schedule_the_job_creates_nothing_and_a_schedule_set_later_applies_without_a_restart()
    {
        using var database = new StandaloneDatabase();
        using var rig = new Rig(database, new BackupOptions(), new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        await rig.Job.StartAsync(CancellationToken.None);
        try
        {
            // Mehrere Stunden vergehen: kein Zeitplan, kein Backup.
            for (var i = 0; i < 6; i++)
            {
                Assert.True(await rig.Clock.WaitForTimerAsync(Patience));
                rig.Clock.AdvanceToNextTimer();
            }
            Assert.True(await rig.Clock.WaitForTimerAsync(Patience));
            Assert.Empty(rig.Backups());
            Assert.Equal(0, rig.RunsSoFar());

            // Der Zeitplan wird nachträglich gesetzt (appsettings geändert): der Job übernimmt ihn beim nächsten Blick auf die Uhr.
            rig.Options.Schedule = "05:00";
            await rig.AdvanceUntilRunsAsync(1);
        }
        finally
        {
            await rig.Job.StopAsync(CancellationToken.None);
        }

        Assert.Equal(new[] { "lager-backup-20260101-050000-000.db" }, rig.Backups());
    }

    [Fact]
    public async Task An_invalid_schedule_is_reported_once_and_treated_as_off()
    {
        using var database = new StandaloneDatabase();
        using var rig = new Rig(database, new BackupOptions { Schedule = "täglich um zwei" }, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

        await rig.Job.StartAsync(CancellationToken.None);
        try
        {
            for (var i = 0; i < 4; i++)
            {
                Assert.True(await rig.Clock.WaitForTimerAsync(Patience));
                rig.Clock.AdvanceToNextTimer();
            }
            Assert.True(await rig.Clock.WaitForTimerAsync(Patience));
        }
        finally
        {
            await rig.Job.StopAsync(CancellationToken.None);
        }

        Assert.Empty(rig.Backups());
        Assert.Single(rig.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("Backup:Schedule"));
    }

    [Fact]
    public async Task A_failed_run_is_only_logged_and_the_next_run_works_again()
    {
        using var database = new StandaloneDatabase();
        using var rig = new Rig(database, new BackupOptions { RetentionCount = 5 }, new DateTimeOffset(2026, 1, 1, 2, 0, 0, TimeSpan.Zero));

        // Die Datenbankdatei fehlt (z. B. während eines Austauschs): der Lauf scheitert, wirft aber nicht.
        File.Delete(database.DbPath);
        Assert.False(await rig.Job.RunBackupAsync());
        Assert.Contains(rig.Log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("fehlgeschlagen"));
        Assert.Empty(rig.Backups());

        // Die Datei ist wieder da: der nächste Lauf klappt.
        await using (var db = database.CreateContext())
            await db.Database.EnsureCreatedAsync();
        Assert.True(await rig.Job.RunBackupAsync());
        Assert.Single(rig.Backups());
    }

    [Fact]
    public async Task An_unreadable_setting_is_logged_and_neither_stops_the_job_nor_the_host()
    {
        using var database = new StandaloneDatabase();
        // Backup:RetentionCount=abc lässt sich nicht in eine Zahl umwandeln: schon der Zugriff auf die Einstellungen wirft.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Backup:Schedule"] = "02:00",
            ["Backup:RetentionCount"] = "abc",
        }).Build();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var log = new ListLogger();
        using var provider = new ServiceCollection()
            .AddSingleton(database.Settings)
            .AddSingleton<IHostEnvironment>(new Wp25Environment("Testing") { ContentRootPath = database.Directory })
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton<ILogger<BackupService>>(new ListLogger<BackupService>(log))
            .Configure<BackupOptions>(config.GetSection(BackupOptions.SectionName))
            .AddSingleton<BackupService>()
            .BuildServiceProvider();
        var job = new BackupHostedService(
            provider.GetRequiredService<IServiceScopeFactory>(), provider.GetRequiredService<BackupService>(), clock, new ListLogger<BackupHostedService>(log));

        await job.StartAsync(CancellationToken.None);
        try
        {
            // Der Job meldet den Fehler und schläft weiter (ein Timer steht bereit), statt mit einer Ausnahme zu enden.
            Assert.True(await clock.WaitForTimerAsync(Patience));
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("nicht auswerten"));
            Assert.False(job.ExecuteTask!.IsCompleted);
        }
        finally
        {
            await job.StopAsync(CancellationToken.None);
        }
        Assert.Empty(database.BackupFiles());
    }

    [Fact]
    public async Task With_MySQL_the_job_stays_idle_and_starts_no_timer()
    {
        var mySql = DatabaseSettings.Create(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "MySql",
            ["Database:ConnectionString"] = "Server=unreachable.invalid;Database=lager;User=x;Password=y",
            ["Database:MySqlServerVersion"] = "8.0.36",
        }).Build(), Path.GetTempPath());
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var log = new ListLogger();
        var service = BackupService.Create(mySql, new BackupOptions { Schedule = "02:00" }, Path.GetTempPath(), log, clock);
        using var provider = new ServiceCollection().BuildServiceProvider();
        var job = new BackupHostedService(provider.GetRequiredService<IServiceScopeFactory>(), service, clock, new ListLogger<BackupHostedService>(log));

        await job.StartAsync(CancellationToken.None);
        await job.ExecuteTask!.WaitAsync(Patience); // der Job ist sofort fertig

        Assert.Equal(0, clock.PendingTimers);
        Assert.Contains(log.Entries, e => e.Message.Contains("MySQL"));
        await job.StopAsync(CancellationToken.None);
    }
}
