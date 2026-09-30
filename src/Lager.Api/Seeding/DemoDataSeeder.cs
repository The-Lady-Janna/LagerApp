using Lager.Api.Security;
using Lager.Application.Abstractions;
using Lager.Domain.Articles;
using Lager.Domain.Auth;
using Lager.Domain.Orders;
using Lager.Domain.Stock;
using Lager.Domain.Warehouse;
using Lager.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WarehouseEntity = Lager.Domain.Warehouse.Warehouse;

namespace Lager.Api.Seeding;

/// <summary>
/// Der Demo-Modus aus der Konfiguration: <c>Demo:Enabled</c> (Standard aus; Kurzform per Umgebungsvariable <c>LAGER_DEMO=1</c>,
/// siehe <see cref="ApplyEnvironmentShortcut"/>) und <c>Demo:AllowInProduction</c>. Der veraltete Schalter <c>Database:Seed</c> bleibt
/// als Alias für den kleinen Altbestand-Datensatz erhalten (siehe <see cref="DemoDataSeeder"/>).
/// </summary>
/// <param name="Enabled">Der Demo-Modus mit dem neuen Datensatz ist ausdrücklich eingeschaltet.</param>
/// <param name="LegacySeed"><c>Database:Seed</c> ist gesetzt (veraltet).</param>
public sealed record DemoSettings(bool Enabled, bool LegacySeed)
{
    public const string EnabledKey = "Demo:Enabled";
    public const string AllowInProductionKey = "Demo:AllowInProduction";
    public const string LegacySeedKey = "Database:Seed";

    /// <summary>Name der Umgebungsvariable, die <c>Demo:Enabled</c> kurz setzt.</summary>
    public const string EnvironmentVariable = "LAGER_DEMO";

    /// <summary>
    /// Liest die Einstellungen. In <c>Production</c> ist <c>Demo:Enabled</c> ohne <c>Demo:AllowInProduction=true</c> ein Fehler:
    /// die Methode wirft eine <see cref="InvalidOperationException"/> mit klarer Meldung (der Start bricht ab), statt Demo-Daten
    /// oder Demo-Benutzer in ein Produktivsystem zu legen.
    /// </summary>
    public static DemoSettings From(IConfiguration config, IHostEnvironment environment)
    {
        var enabled = config.GetValue<bool>(EnabledKey);
        if (enabled && environment.IsProduction() && !config.GetValue<bool>(AllowInProductionKey))
            throw new InvalidOperationException(
                "Demo:Enabled (bzw. LAGER_DEMO=1) ist in der Umgebung Production nicht erlaubt: Demo-Daten und Demo-Benutzer gehören nicht in ein " +
                "Produktivsystem. Demo:Enabled ausschalten (Standard) oder, wenn es wirklich gewollt ist, Demo:AllowInProduction=true setzen.");
        return new DemoSettings(enabled, config.GetValue<bool>(LegacySeedKey));
    }

    /// <summary>
    /// Bildet die Umgebungsvariable <c>LAGER_DEMO</c> auf <c>Demo:Enabled</c> ab: "1"/"true"/"yes"/"on" schaltet ein, "0"/"false"/"no"/"off"
    /// aus (die Umgebungsvariable schlägt die Konfigurationsdateien); leer oder unbekannt ändert nichts. Liefert den gesetzten Wert
    /// oder null, wenn nichts abgebildet wurde.
    /// </summary>
    public static bool? ApplyEnvironmentShortcut(ConfigurationManager config, Func<string, string?> getEnvironmentVariable)
    {
        var raw = getEnvironmentVariable(EnvironmentVariable)?.Trim();
        if (string.IsNullOrEmpty(raw)) return null;

        bool? value = raw.ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => null,
        };
        if (value is bool enabled) config[EnabledKey] = enabled ? "true" : "false";
        return value;
    }
}

public static class DemoDataSeeder
{
    /// <summary>
    /// Der Demo-Modus (<c>Demo:Enabled</c>): legt den neutralen, realistischen Demo-Datensatz samt 60 Tagen Historie und Demo-Benutzern an,
    /// aber NUR in eine leere Fachdatenbank (nie Reseed, nie Ergänzen). Alles entsteht in einer Transaktion: scheitert etwas, bleibt die
    /// Datenbank leer. Der Datensatz kommt aus dem <see cref="DemoCatalog"/> und dem <see cref="DemoHistoryGenerator"/> (fester
    /// Zufallsstartwert, Bestand nur über <c>StockBooking</c>).
    ///
    /// Demo-Benutzer (picker, picker2, picker3, packer, receiver, viewer, manager) bekommen beim Seeding zufällige Passwörter (ohne
    /// Passwortwechsel-Zwang), die GENAU EINMAL als Warnung im Log stehen ("Demo-Zugangsdaten"; in der Konsole, nicht in der Logdatei).
    /// Es gibt keine festen Passwörter im Repository. Existiert ein Benutzername schon, bleibt der Benutzer unverändert. Der Admin ist
    /// der Bootstrap-Admin. Die Umgebungsprüfung (Production) macht der Aufrufer über <see cref="DemoSettings.From"/>.
    /// Liefert true, wenn Daten angelegt wurden.
    /// </summary>
    /// <param name="services">Dienste des Aufrufers (Scope): gebraucht wird <see cref="IPasswordHasher"/>.</param>
    /// <param name="nowUtc">Bezugszeitpunkt "jetzt" (Standard: die Uhr); für reproduzierbare Tests.</param>
    /// <param name="historyDays">Länge der Historie in Tagen (Standard 60, mindestens 20); kürzere Werte sind für Tests gedacht.</param>
    public static async Task<bool> SeedAsync(
        LagerDbContext db, IServiceProvider services, ILogger logger, CancellationToken ct = default, DateTime? nowUtc = null,
        int historyDays = DemoHistoryGenerator.DefaultHistoryDays)
    {
        if (!await IsEmptyAsync(db, ct))
        {
            logger.LogInformation("Demo:Enabled ist aktiv, die Datenbank enthält aber schon Daten: Es werden keine Demo-Daten angelegt (nie Reseed).");
            return false;
        }

        var hasher = services.GetRequiredService<IPasswordHasher>();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        List<(DemoUserSpec Spec, string Password)> created;
        try
        {
            await DemoHistoryGenerator.GenerateAsync(db, nowUtc, historyDays: historyDays, logger: logger, ct: ct);
            created = await CreateDemoUsersAsync(db, hasher, ct);
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }

        if (created.Count == 0)
        {
            logger.LogInformation("Demo-Benutzer existieren schon: Passwörter und Rollen bleiben unverändert.");
        }
        else
        {
            // Der Name der Eigenschaft "DemoCredentials" hält den Eintrag aus der Logdatei heraus (LagerLogging): nur die Konsole
            // zeigt ihn. Ein Test-Logger sieht ihn im Meldungstext.
            var text = string.Join(Environment.NewLine, created.Select(c => $"    {c.Spec.Username,-9} {c.Password}   ({c.Spec.Role})"));
            logger.LogWarning(
                "Demo-Zugangsdaten (wird nur jetzt angezeigt, kein Passwortwechsel nötig). Admin: der Bootstrap-Admin.{NewLine}{DemoCredentials}",
                Environment.NewLine, text);
        }
        return true;
    }

    /// <summary>Legt die Demo-Benutzer an (ohne Passwortwechsel-Zwang); vorhandene Benutzernamen werden übersprungen. Liefert Benutzer und Klartext-Passwort der neu angelegten.</summary>
    private static async Task<List<(DemoUserSpec Spec, string Password)>> CreateDemoUsersAsync(
        LagerDbContext db, IPasswordHasher hasher, CancellationToken ct)
    {
        var existing = (await db.Users.AsNoTracking().Select(u => u.Username).ToListAsync(ct)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var specs = DemoCatalog.Users.Where(u => !existing.Contains(u.Username)).ToList();
        var passwords = specs.Select(_ => BootstrapAdminService.GeneratePassword()).ToList();

        // BCrypt (Work-Factor 12) braucht je Hash rund eine Viertelsekunde: parallel spart es beim Start Zeit.
        var hashes = await Task.WhenAll(passwords.Select(p => Task.Run(() => hasher.Hash(p), ct)));

        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            db.Users.Add(new User(
                spec.Username, hashes[i], Enum.Parse<Role>(spec.Role),
                email: $"{spec.Username}@demo.example.com", displayName: spec.DisplayName, mustChangePassword: false));
        }
        await db.SaveChangesAsync(ct);
        return specs.Select((s, i) => (s, passwords[i])).ToList();
    }

    /// <summary>
    /// Alter Startup-Aufruf für <c>Database:Seed</c> (veraltet, siehe <see cref="DemoSettings"/>): legt den kleinen Altbestand-Datensatz
    /// an (20 Artikel, 25 Bestandszeilen ohne Ledger, 8 Bestellungen). Seedet nur, wenn die Umgebung es zulässt. In <c>Production</c>
    /// verweigert der Seeder ohne <c>Demo:AllowInProduction=true</c> (Demo-Daten gehören nicht in ein Produktivsystem); in jeder anderen
    /// Umgebung außerhalb von Development gibt es eine Warnung. Danach gilt die Regel von
    /// <see cref="SeedAsync(LagerDbContext, CancellationToken)"/>: nur in eine leere Datenbank.
    /// </summary>
    public static async Task<bool> SeedAsync(
        LagerDbContext db, IHostEnvironment env, IConfiguration config, ILogger logger, CancellationToken ct = default)
    {
        if (env.IsProduction() && !config.GetValue<bool>("Demo:AllowInProduction"))
        {
            logger.LogWarning(
                "Database:Seed ist aktiv, aber die Umgebung ist Production: Es werden KEINE Demo-Daten angelegt. " +
                "Für den Produktivbetrieb Database:Seed=false setzen; Demo-Daten dort nur mit Demo:AllowInProduction=true.");
            return false;
        }

        if (!env.IsDevelopment())
            logger.LogWarning("Database:Seed ist aktiv (Environment {Environment}): Demo-Daten werden angelegt. Für den Produktivbetrieb Database:Seed=false setzen.",
                env.EnvironmentName);

        var seeded = await SeedAsync(db, ct);
        if (!seeded)
            logger.LogInformation("Database:Seed ist aktiv, die Datenbank enthält aber schon Daten: Es werden keine Demo-Daten angelegt.");
        return seeded;
    }

    /// <summary>
    /// Die öffentliche Signatur für den Reseed-Endpunkt (und den veralteten Schalter <c>Database:Seed</c>): legt den kleinen
    /// Altbestand-Datensatz an (20 Artikel, Lager mit 36 Lagerplätzen, 25 Bestandszeilen, 8 Bestellungen; ohne Historie, Kunden
    /// und Benutzer), aber NUR in eine leere Datenbank (keine Artikel, Lager, Bestände, Aufträge, Kunden, Lieferanten): Der Seeder
    /// ergänzt oder repariert nie bestehende Daten und legt gelöschte Demo-Daten nicht bei jedem Start neu an. Alles entsteht frisch
    /// und wird über die Ids der neu angelegten Objekte verknüpft; es gibt keine Nachschlagetabellen über Codes (Zone/Aisle/Shelf-Codes
    /// sind nicht eindeutig, ein Duplikat ließ früher den Start scheitern). Den vollständigen Demo-Datensatz legt
    /// <see cref="SeedAsync(LagerDbContext, IServiceProvider, ILogger, CancellationToken, DateTime?, int)"/> an (Demo-Modus).
    /// Liefert true, wenn Daten angelegt wurden.
    /// </summary>
    public static async Task<bool> SeedAsync(LagerDbContext db, CancellationToken ct = default)
    {
        if (!await IsEmptyAsync(db, ct)) return false;

        var articles = SeedArticles(db);
        var (warehouse, bins) = SeedWarehouse(db);
        SeedStock(db, articles, bins);
        SeedOrders(db, articles);
        SeedWalls(db, warehouse);
        SeedPickPoints(db, warehouse);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Leer im Sinne des Seeders: keine Fach-Stammdaten und keine Bewegungsdaten (Benutzer zählen nicht).</summary>
    public static async Task<bool> IsEmptyAsync(LagerDbContext db, CancellationToken ct = default) =>
        !await db.Articles.AnyAsync(ct)
        && !await db.Warehouses.AnyAsync(ct)
        && !await db.StorageLocations.AnyAsync(ct)
        && !await db.StockItems.AnyAsync(ct)
        && !await db.Orders.AnyAsync(ct)
        && !await db.Customers.AnyAsync(ct)
        && !await db.Suppliers.AnyAsync(ct);

    private static Dictionary<string, Article> SeedArticles(LagerDbContext db)
    {
        var specs = new (string Sku, string Name, Dimensions Dim, int WeightG, StackingInfo Stack, string? Desc)[]
        {
            ("SKU-001", "Schraube M6x20", new Dimensions(20, 6, 6), 5, new StackingInfo(true, StackingAxis.Z, 0, 1000), "Sechskantschraube verzinkt"),
            ("SKU-002", "Karton 300x200x150", new Dimensions(300, 200, 150), 250, new StackingInfo(true, StackingAxis.Z, 140, 10), "Faltkarton braun"),
            ("SKU-003", "Reinigungsmittel 1L", new Dimensions(80, 80, 250), 1100, StackingInfo.NotStackable, "Flasche, nicht stapelbar"),
            ("SKU-004", "Mutter M6", new Dimensions(10, 10, 5), 2, new StackingInfo(true, StackingAxis.Z, 0, 2000), "Sechskantmutter verzinkt"),
            ("SKU-005", "Unterlegscheibe M6", new Dimensions(12, 12, 1), 1, new StackingInfo(true, StackingAxis.Z, 0, 5000), null),
            ("SKU-006", "Akku-Bohrer 18V", new Dimensions(280, 90, 240), 1800, new StackingInfo(true, StackingAxis.Z, 230, 3), "im Karton, mit Akku"),
            ("SKU-007", "Bohrer-Set HSS", new Dimensions(180, 120, 25), 350, new StackingInfo(true, StackingAxis.Z, 22, 20), "13-teilig"),
            ("SKU-008", "Klebeband transparent", new Dimensions(75, 75, 50), 80, new StackingInfo(true, StackingAxis.Z, 48, 8), "50m x 50mm"),
            ("SKU-009", "Schutzhandschuhe Gr. M", new Dimensions(150, 100, 20), 90, new StackingInfo(true, StackingAxis.Z, 15, 25), "1 Paar Nitril"),
            ("SKU-010", "Werkstattlampe LED", new Dimensions(420, 180, 90), 1200, new StackingInfo(true, StackingAxis.Z, 85, 4), "rechargeable"),
            ("SKU-011", "Kabeltrommel 25m", new Dimensions(300, 300, 320), 2400, StackingInfo.NotStackable, "rund, nicht stapelbar"),
            ("SKU-012", "Werkbank-Matte", new Dimensions(900, 600, 8), 1500, new StackingInfo(true, StackingAxis.Z, 7, 30), "Anti-Rutsch"),
            ("SKU-013", "Schraubendreher-Set", new Dimensions(280, 100, 35), 450, new StackingInfo(true, StackingAxis.Z, 32, 10), "8-teilig PH/SL"),
            ("SKU-014", "Akku-Pack 18V 4Ah", new Dimensions(150, 80, 90), 750, new StackingInfo(true, StackingAxis.Z, 85, 6), "Ersatzakku"),
            ("SKU-015", "Pinsel-Set 5tlg.", new Dimensions(250, 100, 30), 180, new StackingInfo(true, StackingAxis.Z, 28, 12), "Lackier-Pinsel"),
            ("SKU-016", "Acrylfarbe weiß 1L", new Dimensions(95, 95, 180), 1300, StackingInfo.NotStackable, "Dose"),
            ("SKU-017", "Maler-Krepp 50mm", new Dimensions(60, 60, 50), 90, new StackingInfo(true, StackingAxis.Z, 48, 12), "Rolle"),
            ("SKU-018", "Schleifpapier P120 (10 Bg.)", new Dimensions(280, 230, 5), 120, new StackingInfo(true, StackingAxis.Z, 5, 50), null),
            ("SKU-019", "Säge Handsäge", new Dimensions(550, 130, 25), 480, new StackingInfo(true, StackingAxis.Z, 22, 6), "Universal"),
            ("SKU-020", "Spachtelmasse 1kg", new Dimensions(130, 130, 90), 1100, new StackingInfo(true, StackingAxis.Z, 85, 4), "Dose"),
        };

        var map = new Dictionary<string, Article>();
        foreach (var s in specs)
        {
            var a = new Article(s.Sku, s.Name, s.Dim, s.WeightG, s.Stack, s.Desc);
            db.Articles.Add(a);
            map[s.Sku] = a;
        }
        return map;
    }

    private static (WarehouseEntity warehouse, Dictionary<string, StorageLocation> bins) SeedWarehouse(LagerDbContext db)
    {
        var warehouse = new WarehouseEntity("WH01", "Hauptlager");
        db.Warehouses.Add(warehouse);

        var zoneA = new Zone(warehouse.Id, "Z-A", "Zone A", new Position(0, 0, 0));
        db.Zones.Add(zoneA);

        var aisleLayout = new (string Code, int Y, AisleOrientation Orient)[]
        {
            ("A1", 0, AisleOrientation.AlongX),
            ("A2", 2500, AisleOrientation.AlongX),
            ("A3", 5000, AisleOrientation.AlongX),
        };

        // Der Bin-Code (global eindeutig) ist der Schlüssel für die Bestandsvorgaben weiter unten; er entsteht hier
        // aus den eigenen, festen Codes des Seeders, nicht aus der Datenbank.
        var bins = new Dictionary<string, StorageLocation>();
        foreach (var (aCode, aY, orient) in aisleLayout)
        {
            var aisle = new Aisle(zoneA.Id, aCode, new Position(0, aY, 0), new Position(12_000, aY, 0), orient);
            db.Aisles.Add(aisle);

            for (var shelfIdx = 0; shelfIdx < 4; shelfIdx++)
            {
                var shelfCode = $"{aCode}-{shelfIdx + 1:D2}";
                var shelfX = 500 + shelfIdx * 2500;
                var shelf = new Shelf(aisle.Id, shelfCode, new Position(shelfX, aY + 200, 0), 2000, 600, 2000);
                db.Shelves.Add(shelf);

                for (var binIdx = 0; binIdx < 3; binIdx++)
                {
                    var binCode = $"{shelfCode}-{binIdx + 1:D2}";
                    var binX = shelfX + binIdx * 650;
                    var bin = new StorageLocation(shelf.Id, binCode, new Position(binX, aY + 200, 500), 600, 600, 500, 50_000);
                    db.StorageLocations.Add(bin);
                    bins[binCode] = bin;
                }
            }
        }

        return (warehouse, bins);
    }

    private static void SeedStock(LagerDbContext db, IReadOnlyDictionary<string, Article> articles, IReadOnlyDictionary<string, StorageLocation> bins)
    {
        var stockSpec = new (string Sku, string BinCode, int Qty)[]
        {
            ("SKU-001", "A1-01-01", 5000),
            ("SKU-001", "A2-03-02", 1200),
            ("SKU-002", "A1-02-01", 50),
            ("SKU-002", "A2-01-01", 30),
            ("SKU-003", "A1-03-01", 120),
            ("SKU-004", "A1-01-02", 8000),
            ("SKU-005", "A1-01-03", 12000),
            ("SKU-006", "A2-02-01", 25),
            ("SKU-007", "A2-02-02", 60),
            ("SKU-008", "A3-01-01", 200),
            ("SKU-009", "A3-01-02", 80),
            ("SKU-010", "A3-02-01", 15),
            ("SKU-011", "A3-03-01", 8),
            ("SKU-012", "A3-04-01", 40),
            ("SKU-013", "A2-04-01", 35),
            ("SKU-013", "A3-02-02", 20),
            ("SKU-014", "A2-04-02", 18),
            ("SKU-015", "A3-03-02", 45),
            ("SKU-016", "A3-04-02", 28),
            ("SKU-017", "A1-04-01", 90),
            ("SKU-018", "A1-04-02", 65),
            ("SKU-019", "A2-04-03", 12),
            ("SKU-020", "A3-04-03", 22),
            ("SKU-001", "A1-04-03", 3000),
            ("SKU-004", "A3-03-03", 4000),
        };

        foreach (var (sku, binCode, qty) in stockSpec)
        {
            if (!articles.TryGetValue(sku, out var article)) continue;
            if (!bins.TryGetValue(binCode, out var bin)) continue;

            db.StockItems.Add(new StockItem(article.Id, bin.Id, qty));
        }
    }

    private static void SeedOrders(LagerDbContext db, IReadOnlyDictionary<string, Article> articles)
    {
        var orderSpecs = new (string Number, string? CustomerRef, OrderSource Source, (string Sku, int Qty)[] Lines)[]
        {
            // Nur erfundene, neutrale Bezeichnungen (keine echten Personen, Firmen oder Marken).
            ("ORD-DEMO-01", "Kunde Beispiel",          OrderSource.Manual, new[] { ("SKU-001", 100), ("SKU-004", 100), ("SKU-005", 100) }),
            ("ORD-DEMO-02", "Testwerkstatt Nord",      OrderSource.Manual, new[] { ("SKU-006", 1),   ("SKU-007", 2),   ("SKU-008", 4)   }),
            ("ORD-DEMO-03", "WebShop-Bestellung 42",   OrderSource.Api,    new[] { ("SKU-009", 5),   ("SKU-010", 1)                     }),
            ("ORD-DEMO-04", "Malerbetrieb Muster",     OrderSource.Manual, new[] { ("SKU-015", 2),   ("SKU-016", 6),   ("SKU-017", 8), ("SKU-018", 4) }),
            ("ORD-DEMO-05", "WebShop-Bestellung 57",   OrderSource.Api,    new[] { ("SKU-013", 1),   ("SKU-014", 2)                     }),
            ("ORD-DEMO-06", "Probe Hausverwaltung",    OrderSource.Manual, new[] { ("SKU-011", 2),   ("SKU-012", 4),   ("SKU-008", 10)  }),
            ("ORD-DEMO-07", "WebShop-Bestellung 61",   OrderSource.Api,    new[] { ("SKU-019", 1),   ("SKU-020", 3),   ("SKU-018", 8)   }),
            ("ORD-DEMO-08", "Großauftrag Beispielholz", OrderSource.Manual, new[] { ("SKU-001", 500), ("SKU-004", 500), ("SKU-005", 500), ("SKU-007", 5), ("SKU-013", 3) }),
        };

        foreach (var (number, customerRef, source, lineSpecs) in orderSpecs)
        {
            var lines = lineSpecs
                .Where(l => articles.ContainsKey(l.Sku))
                .Select(l => new OrderLine(articles[l.Sku].Id, l.Qty))
                .ToList();

            if (lines.Count == 0) continue;
            db.Orders.Add(new Order(number, source, customerRef, lines));
        }
    }

    private static void SeedWalls(LagerDbContext db, WarehouseEntity warehouse)
    {
        // Outer hull, broken into segments to leave a 1.5 m door on the west wall.
        // Coordinates in mm. Aisles run y=0..5000, shelves depth 600 → inside footprint
        // is roughly (0,0) bottom-left to (13000, 6000) top-right with 500 mm border.
        const int xMin = -500, xMax = 13_500;
        const int yMin = -500, yMax = 6_500;
        const int doorYBottom = 2_500;
        const int doorYTop = 4_000;
        const int partitionX = 6_500;
        const int passageYBottom = 2_500;
        const int passageYTop = 4_000;

        var walls = new[]
        {
            // South wall + east wall + north wall, single polyline (continuous corner)
            new Wall(warehouse.Id, new[]
            {
                new Position(xMin, yMin, 0),
                new Position(xMax, yMin, 0),
                new Position(xMax, yMax, 0),
                new Position(xMin, yMax, 0),
            }, thicknessMm: 150, label: "Außenmauer S/O/N"),

            // West wall, lower half (south of the door)
            new Wall(warehouse.Id, new[]
            {
                new Position(xMin, yMin, 0),
                new Position(xMin, doorYBottom, 0),
            }, thicknessMm: 150, label: "Außenmauer West (unten)"),

            // West wall, upper half (north of the door) — the gap is the loading door
            new Wall(warehouse.Id, new[]
            {
                new Position(xMin, doorYTop, 0),
                new Position(xMin, yMax, 0),
            }, thicknessMm: 150, label: "Außenmauer West (oben)"),

            // Internal partition with a 1.5 m passage in the middle of the building
            new Wall(warehouse.Id, new[]
            {
                new Position(partitionX, yMin, 0),
                new Position(partitionX, passageYBottom, 0),
            }, thicknessMm: 120, label: "Trennwand (unten)"),
            new Wall(warehouse.Id, new[]
            {
                new Position(partitionX, passageYTop, 0),
                new Position(partitionX, yMax, 0),
            }, thicknessMm: 120, label: "Trennwand (oben)"),
        };

        foreach (var w in walls) db.Walls.Add(w);
    }

    private static void SeedPickPoints(LagerDbContext db, WarehouseEntity warehouse)
    {
        // Inside the building, near the door (left), the partition passage (middle)
        // and the back of the east wing (right) — covers Start, End, Both.
        var points = new[]
        {
            new PickPoint(warehouse.Id, "Wareneingang", new Position(   200, 3_200, 0), PickPointType.Start),
            new PickPoint(warehouse.Id, "Versand",      new Position(13_000, 3_200, 0), PickPointType.End),
            new PickPoint(warehouse.Id, "Verpackung",   new Position(12_500, 6_200, 0), PickPointType.Both),
        };

        foreach (var p in points) db.PickPoints.Add(p);
    }

    public record BulkSeedResult(int Articles, int StockItems, int Orders);

    /// <summary>
    /// Add up to <paramref name="totalCount"/> random demo rows on top of whatever
    /// is already in the database (additive). Roughly 5% articles, 10% stock items,
    /// 85% orders. Useful for stress-testing pick list generation and the cart packer.
    /// Each call uses a fresh batch tag so multiple runs don't collide on SKU/order number.
    /// </summary>
    public static async Task<BulkSeedResult> SeedBulkAsync(LagerDbContext db, int totalCount, CancellationToken ct = default)
    {
        if (totalCount <= 0) return new BulkSeedResult(0, 0, 0);

        var bins = await db.StorageLocations.ToListAsync(ct);
        if (bins.Count == 0)
            throw new InvalidOperationException("Keine Bins vorhanden — bitte zuerst die DB seeden (Reseed).");

        int articleCount = Math.Max(10, totalCount / 20);
        int stockCount = Math.Max(20, totalCount / 10);
        int orderCount = Math.Max(50, totalCount - articleCount - stockCount);

        var rng = new Random(unchecked((int)(DateTime.UtcNow.Ticks & 0x7FFFFFFF)));
        var batchTag = DateTime.UtcNow.ToString("yyMMddHHmmss");

        // 1. Articles
        var existingArticles = await db.Articles.ToListAsync(ct);
        string[] adjectives = { "Premium", "Standard", "Eco", "Profi", "Industrie", "Klein", "Groß", "Mini", "Maxi", "Robust" };
        string[] nouns = { "Schraube", "Mutter", "Werkzeug", "Karton", "Behälter", "Pinsel", "Bohrer", "Lampe", "Kabel", "Klemme", "Schalter", "Stecker", "Ventil", "Dichtung", "Filter" };

        var newArticles = new List<Article>(articleCount);
        for (int i = 0; i < articleCount; i++)
        {
            var sku = $"BULK-{batchTag}-A{i:D4}";
            var name = $"{adjectives[rng.Next(adjectives.Length)]} {nouns[rng.Next(nouns.Length)]} #{i + 1}";
            int l = 20 + rng.Next(280);
            int w = 20 + rng.Next(280);
            int h = 10 + rng.Next(200);
            int weight = 10 + rng.Next(2000);
            var stack = rng.Next(2) == 0
                ? new StackingInfo(true, StackingAxis.Z, Math.Max(1, h - rng.Next(20)), 1 + rng.Next(10))
                : StackingInfo.NotStackable;
            var article = new Article(sku, name, new Dimensions(l, w, h), weight, stack);
            await db.Articles.AddAsync(article, ct);
            newArticles.Add(article);
        }

        var allArticles = existingArticles.Concat(newArticles).ToList();

        // 2. Stock items — preload existing pairs to avoid N+1 lookups
        var existingPairs = await db.StockItems
            .Select(s => new { s.ArticleId, s.StorageLocationId })
            .ToListAsync(ct);
        var stockKeys = new HashSet<(Guid, Guid)>(existingPairs.Select(p => (p.ArticleId, p.StorageLocationId)));

        int stockAdded = 0;
        int attempts = 0;
        while (stockAdded < stockCount && attempts < stockCount * 4)
        {
            attempts++;
            var article = allArticles[rng.Next(allArticles.Count)];
            var bin = bins[rng.Next(bins.Count)];
            if (!stockKeys.Add((article.Id, bin.Id))) continue;
            await db.StockItems.AddAsync(new StockItem(article.Id, bin.Id, 10 + rng.Next(500)), ct);
            stockAdded++;
        }

        // 3. Orders
        var orderAdded = 0;
        for (int i = 0; i < orderCount; i++)
        {
            var orderNumber = $"BULK-{batchTag}-O{i:D5}";
            var lineCount = 1 + rng.Next(4);
            var usedArticles = new HashSet<Guid>();
            var lines = new List<OrderLine>();
            for (int j = 0; j < lineCount; j++)
            {
                var article = allArticles[rng.Next(allArticles.Count)];
                if (!usedArticles.Add(article.Id)) continue;
                var qty = 1 + rng.Next(10);
                lines.Add(new OrderLine(article.Id, qty));
            }
            if (lines.Count == 0) continue;
            var source = (i % 3 == 0) ? OrderSource.Api : OrderSource.Manual;
            await db.Orders.AddAsync(new Order(orderNumber, source, $"Bulk-Kunde #{i + 1}", lines), ct);
            orderAdded++;
        }

        await db.SaveChangesAsync(ct);
        return new BulkSeedResult(newArticles.Count, stockAdded, orderAdded);
    }
}
