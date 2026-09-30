using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Lager.Api.Controllers;
using Lager.Contracts.Articles;
using Lager.Infrastructure;
using Lager.Infrastructure.Backup;
using Lager.Infrastructure.Persistence;
using Lager.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lager.Tests.WP25;

/// <summary>
/// Die Backup-Oberfläche der API (Admin): erstellen, auflisten, herunterladen, löschen, Aufbewahrung, Einstellungen und die
/// Rollen. Ein Download besteht den Integritätstest auch unter Schreiblast; ein Name aus der Anfrage kann nie auf eine andere
/// Datei zeigen.
/// </summary>
public class BackupApiTests
{
    private static async Task<JsonElement> ListAsync(HttpClient admin)
    {
        var response = await admin.GetAsync("/api/admin/backups");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.ReadJsonAsync();
    }

    private static string[] Names(JsonElement list) => list.EnumerateArray().Select(e => e.GetProperty("name").GetString()!).ToArray();

    [Fact]
    public async Task A_backup_taken_under_write_load_is_listed_downloads_as_an_attachment_and_passes_the_integrity_check()
    {
        using var factory = new Wp25Factory();
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        await admin.CreateArticleAsync("BASIS-1");

        // Schreiblast: vier Clients legen fortlaufend Artikel an, während die Backups laufen.
        using var stop = new CancellationTokenSource();
        var written = 0;
        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
        {
            var client = await factory.CreateClient().AsReadyAdminAsync();
            for (var i = 0; i < 100_000 && !stop.IsCancellationRequested; i++)
            {
                var response = await client.PostAsJsonAsync("/api/articles", new CreateArticleRequest(
                    $"LAST-{w}-{i}", "Lastartikel", null, new DimensionsDto(1, 1, 1), 1, new StackingInfoDto(false, "Z", 0, null)));
                if (response.IsSuccessStatusCode) Interlocked.Increment(ref written);
            }
        })).ToList();

        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Volatile.Read(ref written) < 15 && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.True(Volatile.Read(ref written) >= 15, "Die Schreiblast kam nicht in Gang");

            var names = new List<string>();
            for (var i = 0; i < 3; i++) names.Add(await admin.CreateBackupAsync());
            Assert.Equal(3, names.Distinct().Count());

            stop.Cancel();
            await Task.WhenAll(writers);

            // Liste: alle drei, mit Größe, Zeit (UTC) und Art - und ohne Serverpfad.
            var list = await ListAsync(admin);
            Assert.Equal(names.AsEnumerable().Reverse(), Names(list)); // neueste zuerst
            foreach (var entry in list.EnumerateArray())
            {
                Assert.True(entry.GetProperty("sizeBytes").GetInt64() > 0);
                Assert.Equal("backup", entry.GetProperty("kind").GetString());
                Assert.EndsWith("Z", entry.GetProperty("createdUtc").GetString());
            }
            Assert.DoesNotContain(factory.Directory, list.GetRawText());

            // Download: Datei-Stream mit Content-Disposition, nicht zwischenspeicherbar, byteweise die Datei auf der Platte, heil.
            foreach (var name in names)
            {
                var response = await admin.GetAsync($"/api/admin/backups/{name}");
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
                Assert.Equal(name, response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
                Assert.Contains("no-store", response.Headers.CacheControl?.ToString());

                var bytes = await response.Content.ReadAsByteArrayAsync();
                Assert.Equal(await File.ReadAllBytesAsync(Path.Combine(factory.Directory, name)), bytes);

                var copy = Path.Combine(factory.Directory, "heruntergeladen-" + name);
                await File.WriteAllBytesAsync(copy, bytes);
                Assert.Equal(new[] { "ok" }, Wp25Sql.IntegrityCheck(copy));
                Assert.True(Wp25Sql.Count(copy, "SELECT COUNT(*) FROM Articles") >= 1);
            }
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(writers);
        }
    }

    [Fact]
    public async Task Creating_a_backup_answers_201_with_the_entry_and_a_location_and_it_shows_up_in_the_list()
    {
        using var factory = new Wp25Factory();
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        Assert.Empty((await ListAsync(admin)).EnumerateArray());

        var response = await admin.PostAsync("/api/admin/backups", null);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.ReadJsonAsync();
        var name = body.GetProperty("name").GetString()!;
        Assert.Matches(@"^lager-backup-\d{8}-\d{6}-\d{3}\.db$", name);
        Assert.Equal("backup", body.GetProperty("kind").GetString());
        Assert.EndsWith($"/api/admin/backups/{name}", response.Headers.Location?.ToString());
        Assert.False(body.TryGetProperty("path", out _));
        Assert.Equal(new[] { name }, Names(await ListAsync(admin)));

        // Die Antwortform aus WP09 (message/fileName/sizeBytes) bleibt am alten Endpunkt erhalten.
        var legacy = await (await admin.PostAsync("/api/admin/backup", null)).ReadJsonAsync();
        Assert.Equal("Backup erstellt.", legacy.GetProperty("message").GetString());
        Assert.Equal(2, Names(await ListAsync(admin)).Length);
    }

    [Fact]
    public async Task Retention_keeps_the_newest_two_backups_when_more_are_created_through_the_api()
    {
        using var factory = new Wp25Factory(settings: new Dictionary<string, string?> { ["Backup:RetentionCount"] = "2" });
        var admin = await factory.CreateClient().AsReadyAdminAsync();

        var created = new List<string>();
        for (var i = 0; i < 4; i++) created.Add(await admin.CreateBackupAsync());

        Assert.Equal(created.TakeLast(2).Reverse(), Names(await ListAsync(admin)));
        Assert.Equal(2, factory.FilesMatching("lager-backup-*.db").Length); // auch auf der Platte: genau zwei
    }

    [Fact]
    public async Task Deleting_a_backup_removes_it_answers_404_for_an_unknown_one_and_leaves_other_files_alone()
    {
        using var factory = new Wp25Factory();
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        var keep = await admin.CreateBackupAsync();
        var remove = await admin.CreateBackupAsync();

        var deleted = await admin.DeleteAsync($"/api/admin/backups/{remove}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(File.Exists(Path.Combine(factory.Directory, remove)));
        Assert.Equal(new[] { keep }, Names(await ListAsync(admin)));

        Assert.Equal("not_found", await (await admin.DeleteAsync($"/api/admin/backups/{remove}")).CodeAsync(HttpStatusCode.NotFound));
        Assert.Equal("not_found", await (await admin.GetAsync($"/api/admin/backups/{remove}")).CodeAsync(HttpStatusCode.NotFound));
    }

    [Theory]
    [InlineData("..%2Flager.db")]
    [InlineData("..%2F..%2Flager.db")]
    [InlineData("%2E%2E%2Flager.db")]
    [InlineData("..%5Clager.db")]
    [InlineData("..\\lager.db")]
    [InlineData("lager.db")]
    [InlineData("lager.db-wal")]
    [InlineData("notizen.txt")]
    public async Task Download_and_delete_with_a_name_that_is_no_backup_never_touch_another_file(string name)
    {
        using var factory = new Wp25Factory();
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        await admin.CreateArticleAsync("LIVE-1"); // die Live-DB hat Inhalt und existiert sicher
        var decoy = Path.Combine(factory.Directory, "notizen.txt");
        await File.WriteAllTextAsync(decoy, "nicht löschen");
        var skusBefore = Wp25Sql.Skus(factory.DbPath);

        var download = await admin.GetAsync($"/api/admin/backups/{name}");
        Assert.True(download.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, $"Download: {(int)download.StatusCode}");
        var bytes = await download.Content.ReadAsByteArrayAsync();
        Assert.DoesNotContain("SQLite format 3", System.Text.Encoding.Latin1.GetString(bytes)); // keine Datenbank ausgeliefert
        Assert.DoesNotContain("nicht löschen", System.Text.Encoding.UTF8.GetString(bytes));

        var delete = await admin.DeleteAsync($"/api/admin/backups/{name}");
        Assert.True(delete.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound, $"Delete: {(int)delete.StatusCode}");

        Assert.True(File.Exists(factory.DbPath));
        Assert.True(File.Exists(decoy));
        Assert.Equal(skusBefore, Wp25Sql.Skus(factory.DbPath));
    }

    [Fact]
    public async Task A_route_value_with_a_path_in_it_is_rejected_with_400_by_the_action_itself()
    {
        // Routing entpackt "..%2F" schon vor der Action. Hier die Action mit dem entpackten Wert, wie sie ihn zu sehen bekäme.
        using var factory = new Wp25Factory();
        await factory.CreateClient().AsReadyAdminAsync();
        using var scope = factory.Services.CreateScope();
        var controller = ActivatorUtilities.CreateInstance<AdminController>(scope.ServiceProvider);

        foreach (var name in new[] { "../lager.db", "..\\lager.db", "/etc/passwd", "lager.db", "sub/lager-backup-20260101-000000-000.db" })
        {
            var download = Assert.IsType<BadRequestObjectResult>(controller.DownloadBackup(name));
            Assert.Contains("invalid_backup_name", JsonSerializer.Serialize(download.Value));
            var delete = Assert.IsType<BadRequestObjectResult>(await controller.DeleteBackup(name, default));
            Assert.Contains("invalid_backup_name", JsonSerializer.Serialize(delete.Value));
        }
        Assert.True(File.Exists(factory.DbPath));
    }

    [Fact]
    public async Task The_settings_report_schedule_retention_and_restore_state_without_a_server_path()
    {
        // Standard: kein Zeitplan, 14 Backups, Restore in dieser (Nicht-Development-)Umgebung gesperrt
        using (var defaults = new Wp25Factory())
        {
            var admin = await defaults.CreateClient().AsReadyAdminAsync();
            var body = await (await admin.GetAsync("/api/admin/backup-settings")).ReadJsonAsync();

            Assert.Equal("Sqlite", body.GetProperty("provider").GetString());
            Assert.True(body.GetProperty("supported").GetBoolean());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("schedule").ValueKind);
            Assert.Equal(JsonValueKind.Null, body.GetProperty("nextRunUtc").ValueKind);
            Assert.True(body.GetProperty("scheduleValid").GetBoolean());
            Assert.Equal(14, body.GetProperty("retentionCount").GetInt32());
            Assert.False(body.GetProperty("allowRestore").GetBoolean());
            Assert.False(body.GetProperty("restoreAllowed").GetBoolean());
            Assert.False(body.GetProperty("customDirectory").GetBoolean());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("mysqlDumpCommand").ValueKind);
            Assert.DoesNotContain(defaults.Directory, body.GetRawText());
        }

        // Konfiguriert: Zeitplan 03:30 UTC, 5 Backups, Restore freigegeben, eigenes Verzeichnis (nur als Flag, nie als Pfad)
        var settings = new Dictionary<string, string?>
        {
            ["Backup:Schedule"] = "03:30", ["Backup:RetentionCount"] = "5", ["Backup:AllowRestore"] = "true", ["Backup:Directory"] = "extern",
        };
        using (var configured = new Wp25Factory(settings: settings))
        {
            var admin = await configured.CreateClient().AsReadyAdminAsync();
            var body = await (await admin.GetAsync("/api/admin/backup-settings")).ReadJsonAsync();

            Assert.Equal("03:30", body.GetProperty("schedule").GetString());
            Assert.Equal("UTC", body.GetProperty("scheduleTimeZone").GetString());
            Assert.Equal(5, body.GetProperty("retentionCount").GetInt32());
            Assert.True(body.GetProperty("allowRestore").GetBoolean());
            Assert.True(body.GetProperty("restoreAllowed").GetBoolean());
            Assert.True(body.GetProperty("customDirectory").GetBoolean());
            var next = body.GetProperty("nextRunUtc").GetDateTime();
            Assert.Equal(new TimeSpan(3, 30, 0), next.TimeOfDay);
            Assert.InRange(next, DateTime.UtcNow, DateTime.UtcNow.AddDays(1).AddMinutes(1));
            Assert.DoesNotContain("extern", body.GetRawText().Replace("\"customDirectory\"", string.Empty));
        }

        // Ungültiger Zeitplan: wird gemeldet, es gibt keinen nächsten Lauf
        using (var invalid = new Wp25Factory(settings: new Dictionary<string, string?> { ["Backup:Schedule"] = "25:99" }))
        {
            var admin = await invalid.CreateClient().AsReadyAdminAsync();
            var body = await (await admin.GetAsync("/api/admin/backup-settings")).ReadJsonAsync();

            Assert.False(body.GetProperty("scheduleValid").GetBoolean());
            Assert.Equal("25:99", body.GetProperty("schedule").GetString());
            Assert.Equal(JsonValueKind.Null, body.GetProperty("nextRunUtc").ValueKind);
        }
    }

    [Fact]
    public async Task Development_allows_restore_without_the_flag_and_the_background_job_is_part_of_the_host()
    {
        using var development = new Wp25Factory("Development");
        var admin = await development.CreateClient().AsReadyAdminAsync();

        var body = await (await admin.GetAsync("/api/admin/backup-settings")).ReadJsonAsync();
        Assert.False(body.GetProperty("allowRestore").GetBoolean());
        Assert.True(body.GetProperty("restoreAllowed").GetBoolean());
        Assert.True(body.GetProperty("isDevelopment").GetBoolean());

        Assert.Single(development.Services.GetServices<IHostedService>().OfType<BackupHostedService>());
    }

    [Fact]
    public async Task The_swagger_document_lists_the_backup_endpoints_including_the_restore_form()
    {
        // Swagger gibt es nur in Development; die Dokumentgenerierung darf an den Formularfeldern des Restores nicht scheitern.
        using var factory = new Wp25Factory("Development");
        var admin = await factory.CreateClient().AsReadyAdminAsync();

        var response = await admin.GetAsync("/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paths = (await response.ReadJsonAsync()).GetProperty("paths");
        foreach (var path in new[] { "/api/admin/backups", "/api/admin/backups/{name}", "/api/admin/backup-settings", "/api/admin/restore" })
            Assert.True(paths.TryGetProperty(path, out _), $"{path} fehlt im Swagger-Dokument");
        var restoreFields = paths.GetProperty("/api/admin/restore").GetProperty("post").GetProperty("requestBody")
            .GetProperty("content").GetProperty("multipart/form-data").GetProperty("schema").GetProperty("properties");
        Assert.True(restoreFields.TryGetProperty("file", out _));
        Assert.True(restoreFields.TryGetProperty("backupName", out _));
        Assert.True(restoreFields.TryGetProperty("confirm", out _));
    }

    [Fact]
    public async Task Every_backup_endpoint_is_admin_only_and_closed_to_anonymous_callers()
    {
        using var factory = new LagerApiFactory();
        var manager = await factory.CreateClientWithRolesAsync("Manager");
        var viewer = await factory.CreateClientWithRolesAsync("Viewer");
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        var anonymous = factory.CreateClient();
        const string sample = "lager-backup-20260101-000000-000.db";

        Task<HttpResponseMessage>[] Calls(HttpClient client) => new[]
        {
            client.GetAsync("/api/admin/backup-settings"),
            client.GetAsync("/api/admin/backups"),
            client.PostAsync("/api/admin/backups", null),
            client.GetAsync($"/api/admin/backups/{sample}"),
            client.DeleteAsync($"/api/admin/backups/{sample}"),
            client.RestoreAsync(backupName: sample),
        };

        foreach (var client in new[] { manager, viewer })
            foreach (var call in Calls(client))
                Assert.Equal(HttpStatusCode.Forbidden, (await call).StatusCode);
        foreach (var call in Calls(anonymous))
            Assert.Equal(HttpStatusCode.Unauthorized, (await call).StatusCode);

        // Der Admin kommt durch (Einstellungen und Liste; die schreibenden Aufrufe haben ihre eigenen Tests).
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/backup-settings")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/backups")).StatusCode);
        // ... und Manager und Viewer haben nebenbei nichts angelegt.
        Assert.Empty((await ListAsync(admin)).EnumerateArray());
    }

    [Fact]
    public async Task With_MySQL_the_settings_show_the_mysqldump_commands_instead_of_an_error_and_the_backup_endpoints_answer_400()
    {
        var mySql = DatabaseSettings.Create(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "MySql",
            ["Database:ConnectionString"] = "Server=unreachable.invalid;Database=lager;User=x;Password=geheim",
            ["Database:MySqlServerVersion"] = "8.0.36",
        }).Build(), Path.GetTempPath());
        await using var db = new LagerDbContext(new DbContextOptionsBuilder<LagerDbContext>().UseSqlite("Data Source=:memory:").Options);
        var controller = new AdminController(db, new Wp25Environment("Development"), mySql, new ConfigurationBuilder().Build(), NullLogger<AdminController>.Instance);

        var settings = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(controller.GetBackupSettings()).Value);
        Assert.Equal("MySql", settings.GetProperty("provider").GetString());
        Assert.False(settings.GetProperty("supported").GetBoolean());
        Assert.Equal("mysql", settings.GetProperty("unsupportedReason").GetString());
        Assert.StartsWith("mysqldump ", settings.GetProperty("mysqlDumpCommand").GetString());
        Assert.StartsWith("mysql ", settings.GetProperty("mysqlRestoreCommand").GetString());
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("nextRunUtc").ValueKind);
        Assert.DoesNotContain("geheim", settings.GetRawText());

        var list = Assert.IsType<BadRequestObjectResult>(await controller.ListBackups(default));
        Assert.Contains("sqlite_only", JsonSerializer.Serialize(list.Value));
        Assert.Contains("mysqldump", JsonSerializer.Serialize(list.Value));
        Assert.IsType<BadRequestObjectResult>(await controller.CreateBackup(default));
        Assert.IsType<BadRequestObjectResult>(controller.DownloadBackup("lager-backup-20260101-000000-000.db"));
    }
}
