using System.Net;
using System.Text;
using System.Text.Json;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP25;

/// <summary>
/// Der Restore über die Oberfläche: nur mit Freigabe (Backup:AllowRestore oder Development), nur mit der Bestätigung
/// "RESTORE", nur mit geprüfter Datei - und aus einem vorhandenen Backup oder einem Upload. Jede Ablehnung lässt die
/// Live-Datenbank unangetastet; ein erfolgreicher Restore sichert vorher den alten Stand als "before-restore".
/// </summary>
public class RestoreApiTests
{
    private static readonly Dictionary<string, string?> AllowRestore = new() { ["Backup:AllowRestore"] = "true" };

    private static void AssertLiveDbUntouched(Wp25Factory factory, string skusBefore)
    {
        Assert.Equal(skusBefore, Wp25Sql.Skus(factory.DbPath));
        Assert.Equal(new[] { "ok" }, Wp25Sql.IntegrityCheck(factory.DbPath));
        Assert.Empty(Directory.GetFiles(factory.Directory, "*.tmp"));
        Assert.Empty(factory.FilesMatching("lager-before-restore-*.db")); // vor einer Ablehnung wird keine Sicherheitskopie gebraucht
    }

    [Fact]
    public async Task Restore_is_refused_in_production_without_the_flag_for_both_sources_and_the_live_db_stays_as_it_was()
    {
        using var factory = new Wp25Factory("Production");
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        await admin.CreateArticleAsync("PROD-1");
        var backup = await admin.CreateBackupAsync();
        var skus = Wp25Sql.Skus(factory.DbPath);

        var fromBackup = await admin.RestoreAsync(backupName: backup);
        Assert.Equal("restore_disabled", await fromBackup.CodeAsync(HttpStatusCode.Forbidden));
        var fromUpload = await admin.RestoreAsync(content: await File.ReadAllBytesAsync(Path.Combine(factory.Directory, backup)));
        Assert.Equal("restore_disabled", await fromUpload.CodeAsync(HttpStatusCode.Forbidden));

        AssertLiveDbUntouched(factory, skus);
    }

    [Fact]
    public async Task Restore_needs_the_confirmation_RESTORE_and_without_it_nothing_changes()
    {
        using var factory = new Wp25Factory(settings: AllowRestore);
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        await admin.CreateArticleAsync("BESTAND-A");
        var backup = await admin.CreateBackupAsync();
        await admin.CreateArticleAsync("BESTAND-B");
        var skus = Wp25Sql.Skus(factory.DbPath);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(factory.Directory, backup));

        // Ein vorhandenes Backup: die Bestätigung ist Pflicht (fehlt, leer, falsch geschrieben, andere Schreibweise)
        foreach (var confirm in new string?[] { null, "", "restore", "Restore", "RESTORE!", "JA" })
            Assert.Equal("confirmation_required", await (await admin.RestoreAsync(backupName: backup, confirm: confirm)).CodeAsync(HttpStatusCode.BadRequest));

        // Ein Upload: ist die Bestätigung angegeben, muss sie stimmen
        Assert.Equal("confirmation_required", await (await admin.RestoreAsync(content: bytes, confirm: "restore")).CodeAsync(HttpStatusCode.BadRequest));

        AssertLiveDbUntouched(factory, skus);
    }

    [Fact]
    public async Task Restoring_an_existing_backup_with_the_confirmation_saves_the_live_db_first_and_replaces_it()
    {
        using var factory = new Wp25Factory(settings: AllowRestore);
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        await admin.CreateArticleAsync("RT-A");
        var backup = await admin.CreateBackupAsync();
        await admin.CreateArticleAsync("RT-B"); // kommt nach dem Backup: geht beim Restore verloren, bleibt aber in der Sicherheitskopie

        var response = await admin.RestoreAsync(backupName: backup);

        var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, json);
        var body = JsonDocument.Parse(json).RootElement;
        Assert.True(body.GetProperty("restartRequired").GetBoolean());
        Assert.Contains("neu starten", body.GetProperty("message").GetString());
        Assert.DoesNotContain(factory.Directory, json); // kein Serverpfad

        // Live-DB: Stand des Backups (RT-A ja, RT-B nein), heil, ohne Reste eines Temp-Uploads
        Assert.Equal("RT-A", Wp25Sql.Skus(factory.DbPath));
        Assert.Equal(new[] { "ok" }, Wp25Sql.IntegrityCheck(factory.DbPath));
        Assert.Empty(Directory.GetFiles(factory.Directory, "*.tmp"));

        // Die Sicherheitskopie 'before-restore' hält den Stand davor und bleibt liegen; das Backup selbst bleibt unverändert.
        var safety = body.GetProperty("safetyBackup").GetString()!;
        Assert.Matches(@"^lager-before-restore-\d{8}-\d{6}-\d{3}\.db$", safety);
        Assert.Equal("RT-A|RT-B", Wp25Sql.Skus(Path.Combine(factory.Directory, safety)));
        Assert.Equal(new[] { "ok" }, Wp25Sql.IntegrityCheck(Path.Combine(factory.Directory, safety)));
        Assert.Equal("RT-A", Wp25Sql.Skus(Path.Combine(factory.Directory, backup)));

        // Beide erscheinen in der Liste: die Sicherheitskopie mit ihrer Art (auch sie lässt sich wieder einspielen oder löschen)
        var list = await (await admin.GetAsync("/api/admin/backups")).ReadJsonAsync();
        var kinds = list.EnumerateArray().ToDictionary(e => e.GetProperty("name").GetString()!, e => e.GetProperty("kind").GetString());
        Assert.Equal("before-restore", kinds[safety]);
        Assert.Equal("backup", kinds[backup]);
    }

    [Fact]
    public async Task Restoring_an_upload_with_the_confirmation_works_like_restoring_a_backup()
    {
        using var factory = new Wp25Factory(settings: AllowRestore);
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        await admin.CreateArticleAsync("UP-A");
        var backup = await admin.CreateBackupAsync();
        await admin.CreateArticleAsync("UP-B");

        var response = await admin.RestoreAsync(content: await File.ReadAllBytesAsync(Path.Combine(factory.Directory, backup)));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var safety = (await response.ReadJsonAsync()).GetProperty("safetyBackup").GetString()!;
        Assert.Equal("UP-A", Wp25Sql.Skus(factory.DbPath));
        Assert.Equal("UP-A|UP-B", Wp25Sql.Skus(Path.Combine(factory.Directory, safety)));
    }

    [Fact]
    public async Task A_file_that_is_no_valid_backup_is_rejected_even_with_the_confirmation_and_the_live_db_is_not_touched()
    {
        using var factory = new Wp25Factory(settings: AllowRestore);
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        await admin.CreateArticleAsync("LIVE-1");
        var skus = Wp25Sql.Skus(factory.DbPath);

        // Text statt SQLite
        var text = await admin.RestoreAsync(content: Encoding.UTF8.GetBytes("Das ist keine Datenbank. " + new string('x', 5000)));
        Assert.Equal("invalid_backup_file", await text.CodeAsync(HttpStatusCode.BadRequest));

        // Gültige SQLite-Datei, aber keine Lager-Datenbank (Pflichttabellen fehlen)
        var foreign = Path.Combine(factory.Directory, "fremd.sqlite");
        using (var conn = Wp25Sql.Open(foreign, create: true))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE TABLE Notizen (Id INTEGER PRIMARY KEY, Text TEXT)";
            cmd.ExecuteNonQuery();
        }
        var wrongSchema = await admin.RestoreAsync(content: await File.ReadAllBytesAsync(foreign));
        Assert.Equal("invalid_backup_file", await wrongSchema.CodeAsync(HttpStatusCode.BadRequest));

        // Abgeschnitten: der SQLite-Header stimmt, die Datei ist aber kaputt
        var backup = await admin.CreateBackupAsync();
        var whole = await File.ReadAllBytesAsync(Path.Combine(factory.Directory, backup));
        var truncated = await admin.RestoreAsync(content: whole.AsSpan(0, whole.Length * 6 / 10).ToArray());
        Assert.Equal("invalid_backup_file", await truncated.CodeAsync(HttpStatusCode.BadRequest));

        AssertLiveDbUntouched(factory, skus);
        // Die App arbeitet unverändert weiter.
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/articles")).StatusCode);
    }

    [Fact]
    public async Task Restore_needs_exactly_one_source_and_a_real_backup_name()
    {
        using var factory = new Wp25Factory(settings: AllowRestore);
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        await admin.CreateArticleAsync("QUELLE-1");
        var backup = await admin.CreateBackupAsync();
        var skus = Wp25Sql.Skus(factory.DbPath);

        // Weder Datei noch Backup
        Assert.Equal("validation_failed", await (await admin.RestoreAsync()).CodeAsync(HttpStatusCode.BadRequest));
        // Beides gleichzeitig ist zweideutig
        var both = await admin.RestoreAsync(content: await File.ReadAllBytesAsync(Path.Combine(factory.Directory, backup)), backupName: backup);
        Assert.Equal("validation_failed", await both.CodeAsync(HttpStatusCode.BadRequest));
        // Ein Name, der keine Sicherung ist (Live-DB, Pfad): 400, nie eine andere Datei
        foreach (var name in new[] { "lager.db", "../lager.db", "..\\lager.db", "notizen.txt" })
            Assert.Equal("invalid_backup_name", await (await admin.RestoreAsync(backupName: name)).CodeAsync(HttpStatusCode.BadRequest));
        // Ein gültiger Name ohne Datei: 404
        Assert.Equal("not_found", await (await admin.RestoreAsync(backupName: "lager-backup-20200101-000000-000.db")).CodeAsync(HttpStatusCode.NotFound));

        AssertLiveDbUntouched(factory, skus);
    }

    [Fact]
    public async Task In_Development_the_restore_needs_no_flag_but_still_the_confirmation()
    {
        using var factory = new Wp25Factory("Development");
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        var backup = await admin.CreateBackupAsync();

        Assert.Equal("confirmation_required", await (await admin.RestoreAsync(backupName: backup, confirm: null)).CodeAsync(HttpStatusCode.BadRequest));
        Assert.Equal(HttpStatusCode.OK, (await admin.RestoreAsync(backupName: backup)).StatusCode);
    }
}
