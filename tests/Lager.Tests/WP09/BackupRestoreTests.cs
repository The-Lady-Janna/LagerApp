using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Lager.Api.Controllers;
using Lager.Contracts.Articles;
using Lager.Infrastructure;
using Lager.Infrastructure.Persistence;
using Lager.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lager.Tests.WP09;

/// <summary>
/// Backup (konsistent per VACUUM INTO, auch unter Schreiblast) und Restore (nur mit Freigabe, nur geprüfte Dateien,
/// die Live-DB bleibt bei jeder Ablehnung unangetastet).
/// </summary>
public class BackupRestoreTests
{
    private static readonly Dictionary<string, string?> AllowRestore = new() { ["Backup:AllowRestore"] = "true" };

    private static async Task<(string FileName, JsonElement Body)> BackupAsync(HttpClient admin)
    {
        var response = await admin.PostAsync("/api/admin/backup", null);
        var json = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, json);
        var body = JsonDocument.Parse(json).RootElement.Clone();
        return (body.GetProperty("fileName").GetString()!, body);
    }

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient admin, byte[] content, string fileName = "backup.db")
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        return await admin.PostAsync("/api/admin/restore", form);
    }

    /// <summary>
    /// Inhalt der Live-DB als Text (Artikel und Benutzer). Bewusst kein Hash der Datei: Im WAL-Modus ändert sich die
    /// Hauptdatei auch ohne fachliche Änderung, sobald ein Checkpoint läuft (z. B. wenn die App eine Verbindung schließt).
    /// </summary>
    private static string LiveContent(Wp09Factory factory) =>
        string.Join("|", Wp09Sql.Strings(factory.DbPath, "SELECT Sku FROM Articles ORDER BY Sku"))
        + "#" + string.Join("|", Wp09Sql.Strings(factory.DbPath, "SELECT Username FROM Users ORDER BY Username"))
        + "#" + Wp09Sql.Count(factory.DbPath, "SELECT COUNT(*) FROM AuditEntries");

    private static async Task<string> ErrorOf(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").GetString()!;

    // ---- Backup ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Backup_unter_paralleler_Schreiblast_besteht_den_Integritaetstest_und_verraet_keinen_Pfad()
    {
        using var factory = new Wp09Factory();
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        await admin.CreateArticleAsync("BASIS-1");

        // Schreiblast: vier Clients legen fortlaufend Artikel an, während die Backups laufen.
        using var stop = new CancellationTokenSource();
        var written = 0;
        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
        {
            var client = await factory.CreateClient().AsReadyAdminAsync();
            for (var i = 0; !stop.IsCancellationRequested; i++)
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

            var backups = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                var (fileName, body) = await BackupAsync(admin);
                backups.Add(fileName);

                // Nur Dateiname und Größe, kein Serverpfad (weder das Temp-Verzeichnis noch ein Laufwerk).
                Assert.False(body.TryGetProperty("path", out _));
                Assert.DoesNotContain(factory.Directory, body.GetRawText());
                Assert.Matches(@"^lager-backup-\d{8}-\d{6}-\d{3}\.db$", fileName);
                Assert.True(body.GetProperty("sizeBytes").GetInt64() > 0);
            }
            Assert.Equal(3, backups.Distinct().Count()); // kein Überschreiben trotz gleicher Sekunde

            stop.Cancel();
            await Task.WhenAll(writers);

            foreach (var fileName in backups)
            {
                var path = Path.Combine(factory.Directory, fileName);
                Assert.Equal(new[] { "ok" }, Wp09Sql.IntegrityCheck(path));
                Assert.True(Wp09Sql.Count(path, "SELECT COUNT(*) FROM Articles") >= 1);
            }
        }
        finally
        {
            stop.Cancel();
            await Task.WhenAll(writers);
        }
    }

    [Fact]
    public async Task Backup_Directory_legt_Backups_ausserhalb_des_DB_Verzeichnisses_ab()
    {
        var external = Path.Combine(Path.GetTempPath(), $"lager-wp09-extern-{Guid.NewGuid():N}");
        try
        {
            using var factory = new Wp09Factory(settings: new Dictionary<string, string?> { ["Backup:Directory"] = external });
            var admin = await factory.CreateClient().AsReadyAdminAsync();

            var (fileName, body) = await BackupAsync(admin);

            Assert.True(File.Exists(Path.Combine(external, fileName)));
            Assert.False(File.Exists(Path.Combine(factory.Directory, fileName))); // nicht neben der Live-DB
            Assert.DoesNotContain("lager-wp09-extern", body.GetRawText());
            Assert.Equal(new[] { "ok" }, Wp09Sql.IntegrityCheck(Path.Combine(external, fileName)));
        }
        finally
        {
            if (Directory.Exists(external)) Directory.Delete(external, recursive: true);
        }
    }

    // ---- Restore -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Backup_und_Restore_Roundtrip_stellt_den_Stand_wieder_her()
    {
        using var factory = new Wp09Factory(settings: AllowRestore);
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        await admin.CreateArticleAsync("RT-A");
        var (backupName, _) = await BackupAsync(admin);
        await admin.CreateArticleAsync("RT-B");

        var response = await UploadAsync(admin, await File.ReadAllBytesAsync(Path.Combine(factory.Directory, backupName)));
        var json = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(json).RootElement;
        Assert.True(body.GetProperty("restartRequired").GetBoolean());
        Assert.DoesNotContain(factory.Directory, json); // auch hier kein Serverpfad

        // Live-DB: Stand des Backups (RT-A ja, RT-B nein), heil, ohne Reste eines Temp-Uploads.
        Assert.Equal(new[] { "ok" }, Wp09Sql.IntegrityCheck(factory.DbPath));
        Assert.Equal(1, Wp09Sql.Count(factory.DbPath, "SELECT COUNT(*) FROM Articles WHERE Sku = 'RT-A'"));
        Assert.Equal(0, Wp09Sql.Count(factory.DbPath, "SELECT COUNT(*) FROM Articles WHERE Sku = 'RT-B'"));
        Assert.Empty(Directory.GetFiles(factory.Directory, "*.tmp"));

        // Die Sicherheitskopie 'before-restore' hält den Stand vor dem Restore (mit RT-B), damit er umkehrbar ist.
        var safety = Path.Combine(factory.Directory, body.GetProperty("safetyBackup").GetString()!);
        Assert.Contains("-before-restore-", safety);
        Assert.Equal(new[] { "ok" }, Wp09Sql.IntegrityCheck(safety));
        Assert.Equal(1, Wp09Sql.Count(safety, "SELECT COUNT(*) FROM Articles WHERE Sku = 'RT-B'"));
    }

    [Fact]
    public async Task Restore_einer_Textdatei_wird_abgelehnt_und_veraendert_die_Live_DB_nicht()
    {
        using var factory = new Wp09Factory(settings: AllowRestore);
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        await admin.CreateArticleAsync("LIVE-1");
        var before = LiveContent(factory);

        var response = await UploadAsync(admin, Encoding.UTF8.GetBytes("Das ist keine Datenbank, sondern nur Text. " + new string('x', 5000)), "notes.txt");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("SQLite", await ErrorOf(response));
        AssertLiveDbUntouched(factory, before);

        // Die App arbeitet unverändert weiter.
        var articles = await admin.GetFromJsonAsync<List<ArticleDto>>("/api/articles");
        Assert.Contains(articles!, a => a.Sku == "LIVE-1");
    }

    [Fact]
    public async Task Restore_lehnt_Dateien_mit_falschem_Schema_defektem_Inhalt_oder_neuerem_Schemastand_ab()
    {
        using var factory = new Wp09Factory(settings: AllowRestore);
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        await admin.CreateArticleAsync("LIVE-2");
        var (backupName, _) = await BackupAsync(admin);
        var backupPath = Path.Combine(factory.Directory, backupName);
        var before = LiveContent(factory);

        // 1. Gültige SQLite-Datei, aber keine Lager-Datenbank (Pflichttabellen fehlen).
        var foreign = Path.Combine(factory.Directory, "fremd.sqlite");
        Wp09Sql.CreateDatabase(foreign, "CREATE TABLE Notizen (Id INTEGER PRIMARY KEY, Text TEXT)", "INSERT INTO Notizen (Text) VALUES ('hallo')");
        var wrongSchema = await UploadAsync(admin, await File.ReadAllBytesAsync(foreign));
        Assert.Equal(HttpStatusCode.BadRequest, wrongSchema.StatusCode);
        Assert.Contains("Tabellen", await ErrorOf(wrongSchema));

        // 2. Gültiger Header, aber abgeschnitten: die Integritätsprüfung schlägt an.
        var backupBytes = await File.ReadAllBytesAsync(backupPath);
        var truncated = await UploadAsync(admin, backupBytes.AsSpan(0, backupBytes.Length * 6 / 10).ToArray());
        Assert.Equal(HttpStatusCode.BadRequest, truncated.StatusCode);

        // 3. Backup einer NEUEREN Programmversion: unbekannter Schema-Schritt in __LagerSchemaVersion.
        var future = Path.Combine(factory.Directory, "zukunft.db");
        File.Copy(backupPath, future);
        Wp09Sql.Execute(future, "INSERT INTO __LagerSchemaVersion (Name, StepOrder, AppliedAt) VALUES ('9999_FromTheFuture', 9999, '2099-01-01 00:00:00')");
        var newer = await UploadAsync(admin, await File.ReadAllBytesAsync(future));
        Assert.Equal(HttpStatusCode.BadRequest, newer.StatusCode);
        Assert.Contains("neueren Programmversion", await ErrorOf(newer));

        AssertLiveDbUntouched(factory, before);
        Assert.DoesNotContain(Directory.GetFiles(factory.Directory), f => f.Contains("-before-restore-")); // vor der Prüfung keine Sicherheitskopie nötig
    }

    private static void AssertLiveDbUntouched(Wp09Factory factory, string contentBefore)
    {
        Assert.Equal(contentBefore, LiveContent(factory));
        Assert.Empty(Directory.GetFiles(factory.Directory, "*.tmp"));
        Assert.Equal(new[] { "ok" }, Wp09Sql.IntegrityCheck(factory.DbPath));
    }

    [Fact]
    public async Task Restore_ist_ausserhalb_von_Development_nur_mit_Backup_AllowRestore_erlaubt()
    {
        // Ohne Freigabe: Testing (wie jede Nicht-Development-Umgebung) gesperrt, mit klarer Meldung.
        using (var locked = new Wp09Factory("Testing"))
        {
            var admin = await locked.CreateClient().AsReadyAdminAsync();
            var response = await UploadAsync(admin, new byte[] { 1, 2, 3 });
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("Backup:AllowRestore", await ErrorOf(response));
        }

        // Production ohne Freigabe: gesperrt; mit Freigabe erreicht der Aufruf die Prüfung (Junk -> 400 statt 403).
        using (var production = new Wp09Factory("Production"))
        {
            var admin = await production.CreateClient().AsReadyAdminAsync();
            Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(admin, new byte[] { 1, 2, 3 })).StatusCode);
        }
        using (var allowed = new Wp09Factory("Production", AllowRestore))
        {
            var admin = await allowed.CreateClient().AsReadyAdminAsync();
            Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(admin, new byte[] { 1, 2, 3 })).StatusCode);
        }

        // Development braucht keine Freigabe.
        using (var development = new Wp09Factory("Development"))
        {
            var admin = await development.CreateClient().AsReadyAdminAsync();
            Assert.Equal(HttpStatusCode.BadRequest, (await UploadAsync(admin, new byte[] { 1, 2, 3 })).StatusCode);
        }
    }

    [Fact]
    public async Task Bei_MySQL_antworten_Backup_und_Restore_mit_klarer_Meldung_nur_SQLite()
    {
        var mySql = DatabaseSettings.Create(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "MySql",
            ["Database:ConnectionString"] = "Server=unreachable.invalid;Database=lager;User=x;Password=y",
            ["Database:MySqlServerVersion"] = "8.0.36",
        }).Build(), Path.GetTempPath());
        await using var db = new LagerDbContext(new DbContextOptionsBuilder<LagerDbContext>().UseSqlite("Data Source=:memory:").Options);
        var controller = new AdminController(db, new FakeEnvironment("Development"), mySql,
            new ConfigurationBuilder().Build(), NullLogger<AdminController>.Instance);

        var backup = Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(await controller.Backup(default));
        Assert.Contains("SQLite", JsonSerializer.Serialize(backup.Value));
        Assert.Contains("mysqldump", JsonSerializer.Serialize(backup.Value));

        var restore = Assert.IsType<Microsoft.AspNetCore.Mvc.BadRequestObjectResult>(await controller.Restore(null, default));
        Assert.Contains("SQLite", JsonSerializer.Serialize(restore.Value));
    }

    private sealed class FakeEnvironment : IWebHostEnvironment
    {
        public FakeEnvironment(string name) => EnvironmentName = name;
        public string EnvironmentName { get; set; }
        public string ApplicationName { get; set; } = "Lager.Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    // ---- Reseed ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reseed_loescht_alle_Fachtabellen_auch_mit_Wareneingang_und_behaelt_die_Benutzer()
    {
        using var factory = new Wp09Factory("Development");
        var admin = await factory.CreateClient().AsReadyAdminAsync();

        // Ein Wareneingang verweist per Fremdschlüssel (Restrict) auf Artikel und Lagerplatz; dazu ein Kunde und ein Lieferant.
        await factory.WithDbAsync(async db =>
        {
            await Lager.Api.Seeding.DemoDataSeeder.SeedAsync(db);
            var article = await db.Articles.FirstAsync();
            var bin = await db.StorageLocations.FirstAsync();
            var shipment = new Lager.Domain.Inbound.InboundShipment("EIN-RESEED", null, null);
            shipment.AddLine(article.Id, bin.Id, 3, null, null);
            db.InboundShipments.Add(shipment);
            db.Customers.Add(new Lager.Domain.Customers.Customer("K-1", "Kunde"));
            db.Suppliers.Add(new Lager.Domain.Suppliers.Supplier("L-1", "Lieferant"));
            await db.SaveChangesAsync();
        });
        Assert.Equal(1, Wp09Sql.Count(factory.DbPath, "SELECT COUNT(*) FROM InboundLines"));

        var response = await admin.PostAsync("/api/admin/reseed", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(0, Wp09Sql.Count(factory.DbPath, "SELECT COUNT(*) FROM InboundShipments"));
        Assert.Equal(0, Wp09Sql.Count(factory.DbPath, "SELECT COUNT(*) FROM Customers"));
        Assert.Equal(0, Wp09Sql.Count(factory.DbPath, "SELECT COUNT(*) FROM Suppliers"));
        Assert.Equal(20, Wp09Sql.Count(factory.DbPath, "SELECT COUNT(*) FROM Articles")); // Demo-Daten neu, nicht doppelt
        Assert.Equal(1, Wp09Sql.Count(factory.DbPath, "SELECT COUNT(*) FROM Users WHERE Username = 'admin'"));

        // Und noch einmal: der zweite Reseed startet von den Demo-Daten und endet gleich.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync("/api/admin/reseed", null)).StatusCode);
        Assert.Equal(20, Wp09Sql.Count(factory.DbPath, "SELECT COUNT(*) FROM Articles"));
    }

    [Fact]
    public async Task Reseed_Loeschreihenfolge_kommt_aus_dem_Modell_und_deckt_alle_Fachtabellen_ab()
    {
        using var factory = new Wp09Factory();
        await factory.CreateClient().AsReadyAdminAsync();

        var order = (await factory.WithDbAsync(db => Task.FromResult(DatabaseReset.GetDeleteOrder(db)))).ToList();

        // Kinder vor Eltern (Fremdschlüssel aus dem Modell).
        void Before(string child, string parent)
        {
            Assert.Contains(child, order);
            Assert.Contains(parent, order);
            Assert.True(order.IndexOf(child) < order.IndexOf(parent), $"{child} muss vor {parent} gelöscht werden");
        }
        Before("InboundLines", "InboundShipments");
        Before("InboundLines", "Articles");
        Before("InboundLines", "StorageLocations");
        Before("StockItems", "StorageLocations");
        Before("StorageLocations", "Shelves");
        Before("Shelves", "Aisles");
        Before("Aisles", "Zones");
        Before("Zones", "Warehouses");
        Before("Walls", "Warehouses");
        Before("OrderLines", "Orders");
        Before("Shipments", "Orders");
        Before("Shipments", "PickLists");
        Before("Orders", "Customers");
        Before("Orders", "CustomerAddresses");
        Before("CustomerAddresses", "Customers");
        Before("PurchaseOrders", "Suppliers");
        Before("Articles", "Suppliers");
        Before("ReturnLines", "ReturnShipments");
        Before("ReplenishmentTasks", "Articles");

        // Benutzer und Audit-Trail bleiben; sonst ist jede Tabelle der Datenbank dabei (auch künftige, ohne Codeänderung).
        Assert.DoesNotContain("Users", order);
        Assert.DoesNotContain("AuditEntries", order);
        Assert.Equal(order.Count, order.Distinct().Count());
        var allTables = Wp09Sql.Strings(factory.DbPath, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'");
        var uncovered = allTables.Except(order).Except(new[] { "Users", "AuditEntries", SchemaUpgrader.VersionTable }).ToList();
        Assert.Empty(uncovered);
    }

    [Fact]
    public async Task Reseed_bleibt_ausserhalb_von_Development_gesperrt()
    {
        using var factory = new Wp09Factory("Testing");
        var admin = await factory.CreateClient().AsReadyAdminAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync("/api/admin/reseed", null)).StatusCode);
    }
}
