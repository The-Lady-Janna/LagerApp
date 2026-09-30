using Lager.Infrastructure;
using Lager.Infrastructure.Backup;
using Microsoft.Extensions.Configuration;

namespace Lager.Tests.WP25;

/// <summary>
/// Der Backup-Dienst ohne Host, gegen eine echte SQLite-Datei: konsistente Kopie, Liste, Aufbewahrung (die ältesten fliegen
/// raus, Sicherheitskopien und fremde Dateien nie) und die strenge Namensprüfung, die Pfad-Traversal ausschließt.
/// </summary>
public class BackupServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 2, 0, 0, TimeSpan.Zero);

    private static async Task CreateDailyAsync(StandaloneDatabase database, BackupService service, ManualTimeProvider clock, int count)
    {
        for (var i = 0; i < count; i++)
        {
            clock.SetUtcNow(Start.AddDays(i));
            await using var db = database.CreateContext();
            await service.CreateAsync(db, "test");
        }
    }

    [Fact]
    public async Task A_backup_is_an_intact_snapshot_next_to_the_database_without_temp_leftovers_or_a_server_path()
    {
        using var database = new StandaloneDatabase();
        var service = database.CreateService();

        BackupFileInfo info;
        await using (var db = database.CreateContext())
            info = await service.CreateAsync(db, "test");

        Assert.Matches(@"^lager-backup-\d{8}-\d{6}-\d{3}\.db$", info.Name);
        Assert.Equal(BackupKinds.Backup, info.Kind);
        Assert.True(info.SizeBytes > 0);
        Assert.InRange(info.CreatedUtc, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(DateTimeKind.Utc, info.CreatedUtc.Kind);

        var path = Path.Combine(database.Directory, info.Name);
        Assert.Equal(new[] { "ok" }, Wp25Sql.IntegrityCheck(path));
        Assert.Equal(1, Wp25Sql.Count(path, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'Articles'"));
        Assert.Equal(new FileInfo(path).Length, info.SizeBytes);
        Assert.Empty(Directory.GetFiles(database.Directory, "*.tmp"));
        Assert.DoesNotContain(database.Directory, System.Text.Json.JsonSerializer.Serialize(info));
    }

    [Fact]
    public async Task Retention_keeps_only_the_newest_backups_and_never_touches_safety_copies_or_foreign_files()
    {
        using var database = new StandaloneDatabase();
        var clock = new ManualTimeProvider(Start);
        var service = database.CreateService(new BackupOptions { RetentionCount = 2 }, clock);

        // Sicherheitskopie vor einem Restore (sehr alt) und Dateien, die keine Backups sind: bleiben immer liegen.
        var safety = Path.Combine(database.Directory, "lager-before-restore-20200101-000000-000.db");
        var notes = Path.Combine(database.Directory, "notes.txt");
        var lookalike = Path.Combine(database.Directory, "lager-backup-notes.db");
        File.WriteAllText(safety, "sicherheitskopie");
        File.WriteAllText(notes, "notizen");
        File.WriteAllText(lookalike, "kein backup");

        await CreateDailyAsync(database, service, clock, 4);

        var backups = (await service.ListAsync()).Where(f => f.Kind == BackupKinds.Backup).Select(f => f.Name).ToArray();
        Assert.Equal(
            new[] { "lager-backup-20260104-020000-000.db", "lager-backup-20260103-020000-000.db" },
            backups); // genau zwei, die neuesten
        Assert.True(File.Exists(safety));
        Assert.True(File.Exists(notes));
        Assert.True(File.Exists(lookalike));
        Assert.Equal(2, database.BackupFiles().Count(f => f.StartsWith("lager-backup-2026", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Without_a_positive_retention_count_nothing_is_deleted_automatically()
    {
        using var database = new StandaloneDatabase();
        var clock = new ManualTimeProvider(Start);
        var service = database.CreateService(new BackupOptions { RetentionCount = 0 }, clock);

        await CreateDailyAsync(database, service, clock, 4);

        Assert.Equal(4, (await service.ListAsync()).Count);
        Assert.Equal(0, await service.ApplyRetentionAsync());
    }

    [Fact]
    public async Task Retention_goes_by_the_backup_time_in_the_name_not_by_file_dates()
    {
        using var database = new StandaloneDatabase();
        var service = database.CreateService(new BackupOptions { RetentionCount = 2 });

        // Die Dateidaten stehen absichtlich auf dem Kopf: das älteste Backup wurde zuletzt "angefasst" (z. B. kopiert).
        var names = new[]
        {
            "lager-backup-20250101-020000-000.db", "lager-backup-20250102-020000-000.db",
            "lager-backup-20250103-020000-000.db", "lager-backup-20250104-020000-000.db",
        };
        for (var i = 0; i < names.Length; i++)
        {
            var path = Path.Combine(database.Directory, names[i]);
            File.WriteAllText(path, "x");
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(-i));
        }

        Assert.Equal(2, await service.ApplyRetentionAsync());

        Assert.Equal(new[] { names[3], names[2] }, (await service.ListAsync()).Select(f => f.Name).ToArray());
    }

    [Fact]
    public async Task Backups_in_the_same_millisecond_get_a_counter_and_nothing_is_overwritten()
    {
        using var database = new StandaloneDatabase();
        var clock = new ManualTimeProvider(Start);
        var service = database.CreateService(new BackupOptions { RetentionCount = 0 }, clock);

        var names = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            await using var db = database.CreateContext();
            names.Add((await service.CreateAsync(db, "test")).Name);
        }

        Assert.Equal(
            new[] { "lager-backup-20260101-020000-000.db", "lager-backup-20260101-020000-000-2.db", "lager-backup-20260101-020000-000-3.db" },
            names);
        // die neueste (höchster Zähler) steht in der Liste oben
        Assert.Equal(names.AsEnumerable().Reverse().ToArray(), (await service.ListAsync()).Select(f => f.Name).ToArray());
    }

    [Fact]
    public async Task The_list_shows_only_backup_files_newest_first_with_name_size_time_and_kind()
    {
        using var database = new StandaloneDatabase();
        var service = database.CreateService();
        File.WriteAllText(Path.Combine(database.Directory, "lager-backup-20260301-101500-250.db"), "1234567890");
        File.WriteAllText(Path.Combine(database.Directory, "lager-before-restore-20260302-000000-000.db"), "abc");
        File.WriteAllText(Path.Combine(database.Directory, "lager-backup-20260101-000000-000.db"), "z");
        // Keine Sicherungen: die Live-DB selbst, Notizen, ein Backup mit falschem Muster, eine Datei fremden Namens
        File.WriteAllText(Path.Combine(database.Directory, "notes.txt"), "n");
        File.WriteAllText(Path.Combine(database.Directory, "lager-backup-.db"), "n");
        File.WriteAllText(Path.Combine(database.Directory, "andere-backup-20260101-000000-000.db"), "n");

        var list = await service.ListAsync();

        Assert.Equal(
            new[]
            {
                ("lager-before-restore-20260302-000000-000.db", 3L, BackupKinds.BeforeRestore),
                ("lager-backup-20260301-101500-250.db", 10L, BackupKinds.Backup),
                ("lager-backup-20260101-000000-000.db", 1L, BackupKinds.Backup),
            },
            list.Select(f => (f.Name, f.SizeBytes, f.Kind)).ToArray());
        Assert.Equal(new DateTime(2026, 3, 1, 10, 15, 0, 250, DateTimeKind.Utc), list[1].CreatedUtc);
    }

    [Fact]
    public async Task A_configured_backup_directory_is_used_relative_to_the_content_root()
    {
        using var database = new StandaloneDatabase();
        var service = database.CreateService(new BackupOptions { Directory = "extern/sicherungen" });

        await using (var db = database.CreateContext())
            await service.CreateAsync(db, "test");

        var external = Path.Combine(database.Directory, "extern", "sicherungen");
        Assert.Single(Directory.GetFiles(external, "lager-backup-*.db"));
        Assert.Empty(database.BackupFiles());           // nicht neben der Live-DB
        Assert.Single(await service.ListAsync());
    }

    [Theory]
    [InlineData("../lager.db")]
    [InlineData("..\\lager.db")]
    [InlineData("..%2Flager.db")]
    [InlineData("lager.db")]
    [InlineData("lager.db-wal")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows\\win.ini")]
    [InlineData("sub/lager-backup-20260101-000000-000.db")]
    [InlineData("lager-backup-20260101-000000-000.db/../lager.db")]
    [InlineData("../lager-backup-20260101-000000-000.db")]
    [InlineData("lager-backup-20260101-000000-000.db\n")]
    [InlineData("lager-backup-20260101-000000-000.txt")]
    [InlineData("lager-backup-20261301-000000-000.db")]      // Monat 13: kein gültiger Zeitpunkt
    [InlineData("lager-backup-２０２６0101-000000-000.db")]  // Ziffern aus einem anderen Zeichensatz
    [InlineData("lager-backup-20260101-000000-000-.db")]
    [InlineData("")]
    [InlineData(" ")]
    public void A_name_that_is_not_exactly_a_backup_file_name_never_resolves_to_a_path(string name)
    {
        using var database = new StandaloneDatabase();
        var service = database.CreateService();

        Assert.False(service.IsValidName(name));
        Assert.False(service.TryResolvePath(name, out var path));
        Assert.Equal(string.Empty, path);
    }

    [Theory]
    [InlineData("lager-backup-20260101-000000-000.db")]
    [InlineData("lager-backup-20260101-000000-000-2.db")]
    [InlineData("lager-before-restore-20260101-000000-000.db")]
    public void The_names_the_service_creates_resolve_inside_the_backup_directory(string name)
    {
        using var database = new StandaloneDatabase();
        var service = database.CreateService();

        Assert.True(service.TryResolvePath(name, out var path));
        Assert.Equal(Path.Combine(database.Directory, name), path);
        Assert.Equal(service.GetDirectory(), Path.GetDirectoryName(path));
    }

    [Fact]
    public async Task Delete_removes_only_real_backup_files_and_reports_unknown_ones()
    {
        using var database = new StandaloneDatabase();
        var service = database.CreateService();
        var notes = Path.Combine(database.Directory, "notes.txt");
        File.WriteAllText(notes, "notizen");
        BackupFileInfo info;
        await using (var db = database.CreateContext())
            info = await service.CreateAsync(db, "test");

        // Die Live-DB und fremde Dateien sind keine "Backups": nichts wird gelöscht.
        Assert.False(await service.DeleteAsync("lager.db", "test"));
        Assert.False(await service.DeleteAsync("notes.txt", "test"));
        Assert.False(await service.DeleteAsync("../lager.db", "test"));
        Assert.True(File.Exists(database.DbPath));
        Assert.True(File.Exists(notes));

        Assert.True(await service.DeleteAsync(info.Name, "test"));
        Assert.False(File.Exists(Path.Combine(database.Directory, info.Name)));
        Assert.False(await service.DeleteAsync(info.Name, "test")); // schon weg
    }

    [Fact]
    public async Task MySQL_and_in_memory_databases_are_not_supported_by_the_service()
    {
        var mySql = DatabaseSettings.Create(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Provider"] = "MySql",
                ["Database:ConnectionString"] = "Server=unreachable.invalid;Database=lager;User=x;Password=y",
                ["Database:MySqlServerVersion"] = "8.0.36",
            }).Build(), Path.GetTempPath());
        var service = BackupService.Create(mySql, new BackupOptions(), Path.GetTempPath());

        Assert.False(service.IsSupported);
        Assert.True(service.IsMySql);
        Assert.Null(service.NextScheduledRun());
        Assert.False(service.IsValidName("lager-backup-20260101-000000-000.db"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ListAsync());
    }
}
