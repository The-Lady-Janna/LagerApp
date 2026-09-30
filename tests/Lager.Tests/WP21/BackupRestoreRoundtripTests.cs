using System.Net;
using System.Net.Http.Headers;
using Lager.Contracts.Orders;
using Lager.Contracts.PickLists;
using Lager.Contracts.Shipping;
using Lager.Tests.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Lager.Tests.WP21;

/// <summary>
/// Szenario 11: Backup mitten im Betrieb, danach weiterarbeiten, Restore, Neustart. Nach dem Restore steht genau der Stand des
/// Backups in der Datenbank (Bestand, Bestellungen, Picklisten, Ledger), die App läuft auf der wiederhergestellten Datei
/// vollständig weiter, und die Sicherheitskopie hält den Stand vor dem Restore fest (der Restore ist umkehrbar).
/// Die Einzelheiten von Backup und Restore (Integritätsprüfung, Ablehnung defekter Dateien, Rechte) prüft WP09.
/// </summary>
public class BackupRestoreRoundtripTests
{
    [Fact]
    public async Task A_backup_taken_mid_workflow_restores_exactly_that_state_and_the_app_keeps_working_after_the_restart()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"lager-wp21-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            WorldBuilder.World world;
            WorldBuilder.ArticleRef article;
            Guid packedOrderId;
            Guid laterOrderId;
            string safetyBackup;

            // ---- Betrieb, Backup, weiterarbeiten, Restore ----
            using (var live = new RestartableFactory(directory))
            {
                var w = new WorldBuilder(live);
                world = await w.BuildAsync();
                var admin = await w.AdminAsync();
                var manager = await w.ClientAsync("Manager");
                var packer = await w.ClientAsync("Packer");
                article = await w.AddArticleAsync(priceCents: 90);
                await manager.ReceiveAsync(new ApiCalls.Receipt(article.Id, world.PickA, 50, "LOT-B", WorldBuilder.InDays(80)));
                var (packedOrder, _) = await manager.PackedOrderAsync(article.Id, 5);
                packedOrderId = packedOrder.Id;
                Assert.Equal(45, await manager.TotalStockAsync(article.Id));

                var backup = await (await admin.PostAsync("/api/admin/backup", null)).BodyAsync();
                var backupFile = Path.Combine(directory, backup.GetProperty("fileName").GetString()!);
                Assert.True(File.Exists(backupFile));

                // Nach dem Backup: neue Bestellung auf einer Pickliste, mehr Wareneingang, die gepackte Bestellung geht raus
                var later = await manager.PlaceOrderAsync(article.Id, 3);
                laterOrderId = later.Id;
                await manager.GenerateAsync(later.Id);
                await manager.ReceiveAsync(new ApiCalls.Receipt(article.Id, world.PickB, 25));
                await packer.ShipAsync(packedOrderId);
                Assert.Equal(70, await manager.TotalStockAsync(article.Id));
                Assert.Equal("Shipped", (await manager.GetOrderAsync(packedOrderId)).Status);

                using var form = new MultipartFormDataContent();
                var file = new ByteArrayContent(await File.ReadAllBytesAsync(backupFile));
                file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                form.Add(file, "file", "backup.db");
                var restored = await admin.PostAsync("/api/admin/restore", form);
                await restored.ExpectStatusAsync(HttpStatusCode.OK);
                var body = await restored.BodyAsync();
                Assert.True(body.GetProperty("restartRequired").GetBoolean());
                safetyBackup = Path.Combine(directory, body.GetProperty("safetyBackup").GetString()!);
            }

            // ---- Neustart auf der wiederhergestellten Datei ----
            using (var restarted = new RestartableFactory(directory))
            {
                var w = new WorldBuilder(restarted);
                var manager = await w.ClientAsync("Manager");   // das Admin-Passwort stammt aus dem Backup (nach dem Wechsel gesichert)
                var packer = await w.ClientAsync("Packer");

                // Stand des Backups: 45 auf Lager, die gepackte Bestellung ist noch nicht versendet, die spätere Bestellung gibt es nicht
                Assert.Equal(45, await manager.TotalStockAsync(article.Id));
                Assert.Equal("Packed", (await manager.GetOrderAsync(packedOrderId)).Status);
                Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync($"/api/orders/{laterOrderId}")).StatusCode);
                var lists = await (await manager.GetAsync("/api/picklists")).ExpectAsync<List<PickListDto>>();
                Assert.Equal("Completed", Assert.Single(lists).Status);
                Assert.Empty(await (await manager.GetAsync("/api/shipments")).ExpectAsync<List<ShipmentDto>>());
                Assert.Equal(2, (await w.MovementsAsync(article.Id)).Count);   // Wareneingang und Pick
                Assert.Empty(await w.LedgerViolationsAsync());

                // Die App arbeitet weiter: die gepackte Bestellung geht raus, ein kompletter neuer Durchlauf klappt
                Assert.Equal("Shipped", (await packer.ShipAsync(packedOrderId)).Status);
                var (next, _) = await manager.PackedOrderAsync(article.Id, 4);
                Assert.Equal("Packed", (await manager.GetOrderAsync(next.Id)).Status);
                Assert.Equal(41, await manager.TotalStockAsync(article.Id));
                Assert.Empty(await w.LedgerViolationsAsync());
                Assert.Equal(new[] { "ok" }, Sql.Strings(restarted.DbPath, "PRAGMA integrity_check;"));
            }

            // ---- Die Sicherheitskopie hat den Stand vor dem Restore (mit der späteren Bestellung und dem Versand) ----
            Assert.Equal("2", Sql.Strings(safetyBackup, "SELECT COUNT(*) FROM Orders").Single());
            Assert.Equal("1", Sql.Strings(safetyBackup, "SELECT COUNT(*) FROM Shipments").Single());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { /* Temp-Verzeichnis, wird beim nächsten Cleanup entfernt */ }
            catch (UnauthorizedAccessException) { /* dito */ }
        }
    }

    /// <summary>Direkter, nicht gepoolter Lesezugriff auf eine SQLite-Datei, unabhängig von der App.</summary>
    private static class Sql
    {
        public static List<string> Strings(string path, string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
                Mode = SqliteOpenMode.ReadWrite,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var values = new List<string>();
            while (reader.Read()) values.Add(Convert.ToString(reader.GetValue(0)) ?? string.Empty);
            return values;
        }
    }
}
