using System.Text.Json;
using Lager.Api.Seeding;
using Lager.Infrastructure.Persistence.Converters;
using Lager.Domain.Stock;
using Lager.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Lager.Tests.WP09;

/// <summary>
/// Zeitstempel: Aus der Datenbank gelesene DateTime-Werte tragen Kind=Utc, die JSON-Ausgabe hat also ein "Z".
/// Ohne den Converter lieferte EF Kind=Unspecified, die API schrieb sie ohne Offset, und JavaScript zeigte sie als
/// Ortszeit (um den UTC-Offset verschoben).
/// </summary>
public class UtcTimestampTests
{
    [Fact]
    public async Task Api_liefert_Zeitstempel_aus_der_Datenbank_mit_Z()
    {
        using var factory = new LagerApiFactory();
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        var article = await admin.CreateArticleAsync("UTC-001");
        await admin.CreateOrderAsync("UTC-ORD-1", article.Id);

        // Roh-JSON prüfen (nicht das deserialisierte Modell): Die Liste liest aus der Datenbank, nicht aus dem frischen Objekt.
        var orders = JsonDocument.Parse(await admin.GetStringAsync("/api/orders")).RootElement;
        var createdAt = orders.EnumerateArray().Single().GetProperty("createdAt").GetString();
        Assert.NotNull(createdAt);
        Assert.EndsWith("Z", createdAt);

        // Auch der Audit-Trail (AuditEntry.At) und ein zweiter Lesepfad (AsNoTracking) liefern das Suffix.
        var audit = JsonDocument.Parse(await admin.GetStringAsync("/api/audit")).RootElement;
        Assert.NotEmpty(audit.EnumerateArray());
        foreach (var entry in audit.EnumerateArray())
            Assert.EndsWith("Z", entry.GetProperty("at").GetString());
    }

    [Fact]
    public async Task Kein_Endpunkt_liefert_einen_Zeitstempel_ohne_Zone_und_Berichte_laufen_mit_dem_Converter()
    {
        using var factory = new Wp09Factory();
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        await factory.WithDbAsync(async db =>
        {
            await DemoDataSeeder.SeedAsync(db);
            var article = await db.Articles.FirstAsync();
            var bin = await db.StorageLocations.FirstAsync();
            // Ledger-Eintrag: die Berichte aggregieren darüber (Max(At)) und vergleichen Zeitpunkte in der Abfrage.
            db.StockMovements.Add(new StockMovement(article.Id, bin.Id, 5, 100, StockMovementReason.Inbound));
            await db.SaveChangesAsync();
        });

        var endpoints = new[]
        {
            "/api/orders", "/api/audit", "/api/users", "/api/inbound", "/api/inventory", "/api/picklists", "/api/replenishment",
            "/api/purchase-orders", "/api/returns", "/api/shipments", "/api/stock", "/api/stock/alerts", "/api/pick-waves",
            "/api/customers", "/api/suppliers",
            "/api/reports/dashboard", "/api/reports/bin-heatmap", "/api/reports/dead-stock?days=1", "/api/reports/abc-analysis",
            "/api/reports/live-status", "/api/reports/stock-valuation", "/api/reports/picker-performance",
        };

        var offsetless = new System.Text.RegularExpressions.Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?$");
        var withoutZone = new List<string>();
        var timestamps = 0;
        void Walk(string endpoint, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var property in element.EnumerateObject()) Walk(endpoint, property.Value);
                    break;
                case JsonValueKind.Array:
                    foreach (var item in element.EnumerateArray()) Walk(endpoint, item);
                    break;
                case JsonValueKind.String:
                    var text = element.GetString()!;
                    if (offsetless.IsMatch(text)) withoutZone.Add($"{endpoint}: {text}");
                    else if (text.EndsWith('Z') && DateTime.TryParse(text, out _)) timestamps++;
                    break;
            }
        }

        foreach (var endpoint in endpoints)
        {
            var response = await admin.GetAsync(endpoint);
            var body = await response.Content.ReadAsStringAsync();
            Assert.True(response.IsSuccessStatusCode, $"{endpoint}: {(int)response.StatusCode} {body}");
            Walk(endpoint, JsonDocument.Parse(body).RootElement);
        }

        Assert.Empty(withoutZone);
        Assert.True(timestamps > 10, "Es wurden kaum Zeitstempel geprüft"); // Aufträge, Benutzer, Audit-Einträge ...
    }

    [Fact]
    public async Task Datenbankwerte_werden_als_Utc_gelesen_auch_nullbare()
    {
        using var factory = new Wp09Factory();
        await factory.CreateClient().AsReadyAdminAsync();

        // Ablaufdatum ist DateTime? (StockItem.ExpiryDate); Ortszeit wird beim Schreiben nach UTC umgerechnet.
        var expiryLocal = new DateTime(2031, 5, 17, 12, 0, 0, DateTimeKind.Local);
        var expiryUtc = expiryLocal.ToUniversalTime();

        await factory.WithDbAsync(async db =>
        {
            await DemoDataSeeder.SeedAsync(db); // gültige Artikel und Lagerplätze für die Fremdschlüssel
            var article = await db.Articles.FirstAsync();
            var bin = await db.StorageLocations.FirstAsync();
            db.StockItems.Add(new StockItem(article.Id, bin.Id, 3, "L-UTC", expiryLocal));
            await db.SaveChangesAsync();
        });

        var item = await factory.WithDbAsync(db => db.StockItems.AsNoTracking().SingleAsync(s => s.LotNumber == "L-UTC"));
        Assert.Equal(DateTimeKind.Utc, item.CreatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, item.UpdatedAt.Kind);
        Assert.Equal(DateTimeKind.Utc, item.ExpiryDate!.Value.Kind);
        Assert.Equal(expiryUtc, item.ExpiryDate.Value);

        // Gespeichert ist der UTC-Wert (keine zweite Umrechnung beim Lesen).
        var raw = Wp09Sql.Scalar(factory.DbPath, "SELECT ExpiryDate FROM StockItems WHERE LotNumber = 'L-UTC'") as string;
        Assert.StartsWith(expiryUtc.ToString("yyyy-MM-dd HH:mm:ss"), raw);
    }

    [Fact]
    public async Task Alle_DateTime_Properties_des_Modells_tragen_den_Utc_Converter()
    {
        using var factory = new Wp09Factory();
        await factory.CreateClient().AsReadyAdminAsync();

        var withoutConverter = await factory.WithDbAsync(db => Task.FromResult(
            db.Model.GetEntityTypes()
                .SelectMany(t => t.GetProperties().Select(p => (Type: t.ClrType.Name, Property: p)))
                .Where(x => x.Property.ClrType == typeof(DateTime) || x.Property.ClrType == typeof(DateTime?))
                .Where(x => x.Property.GetValueConverter() is not (UtcDateTimeConverter or UtcNullableDateTimeConverter))
                .Select(x => $"{x.Type}.{x.Property.Name}")
                .ToList()));

        Assert.Empty(withoutConverter);
    }

    [Fact]
    public void Umrechnungsregeln_Ortszeit_wird_UTC_und_Unbestimmtes_gilt_als_UTC()
    {
        var utc = new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(utc, UtcDateTimeRules.ToUtc(utc));
        Assert.Equal(DateTimeKind.Utc, UtcDateTimeRules.ToUtc(utc).Kind);

        var unspecified = new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Unspecified);
        var fromUnspecified = UtcDateTimeRules.ToUtc(unspecified);
        Assert.Equal(DateTimeKind.Utc, fromUnspecified.Kind);
        Assert.Equal(unspecified.Ticks, fromUnspecified.Ticks); // Wanduhrzeit bleibt, nur die Kennzeichnung kommt dazu

        var local = new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Local);
        Assert.Equal(local.ToUniversalTime(), UtcDateTimeRules.ToUtc(local));

        Assert.Null(UtcDateTimeRules.ToUtc((DateTime?)null));
        Assert.Null(UtcDateTimeRules.AsUtc(null));
        Assert.Equal(DateTimeKind.Utc, UtcDateTimeRules.AsUtc(unspecified)!.Value.Kind);
    }
}
