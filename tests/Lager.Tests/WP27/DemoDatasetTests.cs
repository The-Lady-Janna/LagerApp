using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Lager.Api.Seeding;
using Lager.Contracts.Auth;
using Lager.Contracts.Reports;
using Lager.Contracts.Stock;
using Lager.Contracts.Warehouse;
using Lager.Domain.Articles;
using Lager.Domain.Inbound;
using Lager.Domain.Inventory;
using Lager.Domain.Orders;
using Lager.Domain.PickLists;
using Lager.Domain.Purchasing;
using Lager.Domain.Returns;
using Lager.Domain.Shipping;
using Lager.Domain.Stock;
using Lager.Domain.Warehouse;
using Lager.Infrastructure.Persistence;
using Lager.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lager.Tests.WP27;

/// <summary>Ein Demo-Benutzer samt dem Passwort, das der Seeder im Log ausgegeben hat.</summary>
public sealed record DemoLogin(string Username, string Password, string Role);

/// <summary>
/// Eine laufende API (leere Datenbank, Bootstrap-Admin), in die der Demo-Seeder mit einem Test-Logger als Log-Senke einmal den
/// Demo-Datensatz legt. Die Passwörter der Demo-Benutzer stehen nur in diesem Log: der Fixture liest sie von dort.
/// Ein Seeding pro Testklasse reicht - alle Tests darauf lesen nur.
/// </summary>
public sealed class DemoDataFixture : IAsyncLifetime
{
    private static readonly Regex CredentialLine = new(@"^\s+(?<user>\S+)\s+(?<password>\S+)\s+\((?<role>\w+)\)\s*$", RegexOptions.Multiline);

    public LagerApiFactory Factory { get; } = new();
    public ListLogger Log { get; } = new();

    /// <summary>Bezugszeitpunkt "jetzt" des Datensatzes (vor dem Seeding gemessen).</summary>
    public DateTime Now { get; } = DateTime.UtcNow;

    public bool Seeded { get; private set; }
    public HttpClient Admin { get; private set; } = null!;
    public IReadOnlyList<DemoLogin> Logins { get; private set; } = Array.Empty<DemoLogin>();

    public async Task InitializeAsync()
    {
        using (var scope = Factory.Services.CreateScope())   // startet die App: leere Datenbank, Bootstrap-Admin
        {
            var db = scope.ServiceProvider.GetRequiredService<LagerDbContext>();
            Seeded = await DemoDataSeeder.SeedAsync(db, scope.ServiceProvider, Log, nowUtc: Now);
        }

        Logins = Log.Entries
            .Where(e => e.Message.StartsWith("Demo-Zugangsdaten", StringComparison.Ordinal))
            .SelectMany(e => CredentialLine.Matches(e.Message).Select(m =>
                new DemoLogin(m.Groups["user"].Value, m.Groups["password"].Value, m.Groups["role"].Value)))
            .ToList();
        Admin = await Factory.CreateClient().AsReadyAdminAsync();
    }

    public async Task<T> DbAsync<T>(Func<LagerDbContext, Task<T>> action)
    {
        using var scope = Factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<LagerDbContext>());
    }

    public Task DisposeAsync()
    {
        Admin?.Dispose();
        Factory.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// Der Demo-Datensatz (Seeder <c>SeedAsync(db, IServiceProvider, ILogger)</c>): Umfang, Konsistenz von Bestand und Ledger, Historie mit
/// Vergangenheits-Zeitstempeln, Zustände in allen Bereichen, Reports mit sofortigen Daten und die Demo-Benutzer.
/// </summary>
public class DemoDatasetTests : IClassFixture<DemoDataFixture>
{
    private readonly DemoDataFixture _demo;

    public DemoDatasetTests(DemoDataFixture demo) => _demo = demo;

    // ---- Umfang ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void Der_Seeder_hat_in_die_leere_Datenbank_Daten_angelegt()
    {
        Assert.True(_demo.Seeded);
        Assert.Contains(_demo.Log.Entries, e => e.Message.StartsWith("Demo-Daten angelegt", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stammdaten_Artikel_Lieferanten_Kunden_und_Lager_sind_vollstaendig()
    {
        var counts = await _demo.DbAsync(async db => new
        {
            Articles = await db.Articles.CountAsync(),
            Suppliers = await db.Suppliers.CountAsync(),
            Customers = await db.Customers.CountAsync(),
            CustomersWithAddress = await db.Customers.CountAsync(c => c.Addresses.Any()),
            Warehouses = await db.Warehouses.CountAsync(),
            Zones = await db.Zones.CountAsync(),
            Aisles = await db.Aisles.CountAsync(),
            Shelves = await db.Shelves.CountAsync(),
            Bins = await db.StorageLocations.CountAsync(),
            HotPick = await db.StorageLocations.CountAsync(b => b.BinType == BinType.HotPick && b.ReplenishmentThreshold > 0),
            Reserve = await db.StorageLocations.CountAsync(b => b.BinType == BinType.Reserve),
            Walls = await db.Walls.CountAsync(),
            Start = await db.PickPoints.CountAsync(p => p.Type == PickPointType.Start || p.Type == PickPointType.Both),
            End = await db.PickPoints.CountAsync(p => p.Type == PickPointType.End || p.Type == PickPointType.Both),
            Carts = await db.PickCartConfigs.CountAsync(),
        });

        Assert.True(counts.Articles >= 40);
        Assert.Equal(3, counts.Suppliers);
        Assert.True(counts.Customers >= 5);
        Assert.Equal(counts.Customers, counts.CustomersWithAddress);
        Assert.Equal(1, counts.Warehouses);
        Assert.Equal(2, counts.Zones);
        Assert.True(counts.Aisles >= 3 && counts.Shelves >= 8 && counts.Bins >= 24);
        Assert.True(counts.HotPick >= 4);
        Assert.True(counts.Reserve >= 8);
        Assert.True(counts.Walls >= 2);
        Assert.True(counts.Start >= 1 && counts.End >= 1);
        Assert.True(counts.Carts >= 1);
    }

    [Fact]
    public async Task Das_Lager_kommt_ueber_die_API_mit_zwei_Zonen_Waenden_und_Pickpunkten()
    {
        var layout = (await _demo.Admin.GetFromJsonAsync<List<WarehouseDto>>("/api/warehouse/layout"))!;
        var warehouse = Assert.Single(layout);
        Assert.Equal(2, warehouse.Zones.Count);
        Assert.All(warehouse.Zones, z => Assert.NotEmpty(z.Aisles));

        var walls = (await _demo.Admin.GetFromJsonAsync<List<WallDto>>("/api/warehouse/walls"))!;
        Assert.True(walls.Count >= 2);
        var points = (await _demo.Admin.GetFromJsonAsync<List<PickPointDto>>("/api/warehouse/pick-points"))!;
        Assert.Contains(points, p => p.Type == "Start");
        Assert.Contains(points, p => p.Type == "End");
    }

    [Fact]
    public async Task Artikel_tragen_Preise_Schwellwerte_GTIN_Bundle_Saison_und_Alt_SKUs()
    {
        var (articles, bundleComponents) = await _demo.DbAsync(async db =>
            (await db.Articles.Include(a => a.BundleComponents).AsNoTracking().ToListAsync(), await db.BundleComponents.CountAsync()));

        var physical = articles.Where(a => !a.IsBundle).ToList();
        Assert.All(physical, a =>
        {
            Assert.True(a.PurchasePriceCents > 0, $"{a.Sku}: Einkaufspreis");
            Assert.True(a.MinStock > 0 && a.MinStock <= a.ReorderPoint && a.ReorderPoint <= a.MaxStock, $"{a.Sku}: Min/Reorder/Max");
            Assert.NotNull(a.PrimarySupplierId);
            Assert.True(a.Dimensions.VolumeMm3 > 0 && a.WeightGrams > 0);
        });

        var bundle = Assert.Single(articles, a => a.Sku == "SET-8001");
        Assert.True(bundle.IsBundle);
        Assert.True(bundleComponents >= bundle.BundleComponents.Count);
        Assert.All(bundle.BundleComponents, c => Assert.Contains(articles, a => a.Id == c.ComponentArticleId));

        var gtins = articles.Where(a => a.Gtin is not null).Select(a => a.Gtin!).ToList();
        Assert.True(gtins.Count >= 5);
        Assert.All(gtins, g => Assert.True(Gtin.IsValid(g)));

        Assert.Contains(articles, a => a.ValidFrom is not null || a.ValidUntil is not null);
        Assert.Contains(articles, a => a.ValidUntil is not null && a.IsCurrentlyActive(_demo.Now));            // Sommerartikel: bestellbar
        Assert.Contains(articles, a => (a.ValidFrom is not null || a.ValidUntil is not null) && !a.IsCurrentlyActive(_demo.Now));   // Saison vorbei/noch nicht da
        foreach (var article in articles.Where(a => a.AlternativeSkus.Count > 0))
            Assert.All(article.AlternativeSkus, alt => Assert.Contains(articles, x => x.HasSku(alt)));
        Assert.Contains(articles, a => a.AlternativeSkus.Count > 0);
    }

    [Fact]
    public async Task Ein_Hot_Pick_Platz_hat_seinen_Artikel_und_einen_Reserveplatz_mit_Bestand()
    {
        var rows = await _demo.DbAsync(async db =>
        {
            var hot = await db.StorageLocations.AsNoTracking().Where(b => b.BinType == BinType.HotPick).Select(b => b.Id).ToListAsync();
            var hotArticles = await db.StockItems.AsNoTracking().Where(s => hot.Contains(s.StorageLocationId) && s.Quantity > 0)
                .Select(s => s.ArticleId).Distinct().ToListAsync();
            var reserveIds = await db.StorageLocations.AsNoTracking().Where(b => b.BinType == BinType.Reserve).Select(b => b.Id).ToListAsync();
            var withReserve = await db.StockItems.AsNoTracking().Where(s => reserveIds.Contains(s.StorageLocationId) && s.Quantity > 0
                                                                              && hotArticles.Contains(s.ArticleId))
                .Select(s => s.ArticleId).Distinct().ToListAsync();
            return (hotArticles, withReserve);
        });

        Assert.True(rows.hotArticles.Count >= 4);
        Assert.True(rows.withReserve.Count >= 2, "Hinter den Hot-Pick-Plätzen liegt Reservebestand");
    }

    // ---- Chargen --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Mindestens_drei_Chargen_mit_Bestand_laufen_in_30_Tagen_ab_und_eine_ist_abgelaufen()
    {
        var today = _demo.Now.Date;
        var stock = (await _demo.Admin.GetFromJsonAsync<List<StockItemDto>>("/api/stock"))!;

        var expiring = stock.Where(s => s.ExpiryDate is DateTime e && e.Date >= today && e.Date <= today.AddDays(30)).ToList();
        Assert.True(expiring.Count >= 3, $"Nur {expiring.Count} Bestandszeilen laufen in den nächsten 30 Tagen ab");
        Assert.All(expiring, s => Assert.False(string.IsNullOrEmpty(s.LotNumber)));
        Assert.Contains(stock, s => s.ExpiryDate is DateTime e && e.Date < today && s.Quantity > 0);
    }

    [Fact]
    public async Task Die_Chargenrueckverfolgung_zeigt_Wareneingang_Bestand_und_Bewegungen()
    {
        var trace = await _demo.Admin.GetFromJsonAsync<ChargeTraceDto>("/api/reports/charge/MK-2411-A");

        Assert.NotNull(trace);
        Assert.NotEmpty(trace!.Inbounds);
        Assert.NotEmpty(trace.CurrentStock);
        Assert.NotEmpty(trace.Movements);
        Assert.Equal(trace.CurrentStockTotal, trace.CurrentStock.Sum(s => s.Quantity));
        Assert.Equal(30, trace.InboundTotal);   // eine Lieferung des Altbestands über 30 Stück
        Assert.Equal(trace.CurrentStockTotal, trace.Movements.Sum(m => m.QuantityDelta));   // Ledger der Charge = Bestand der Charge
    }

    // ---- Bestand und Ledger ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Bestand_und_Ledger_stimmen_je_Artikel_ueberein()
    {
        var (stock, ledger, ledgerRows) = await _demo.DbAsync(async db => (
            await db.StockItems.AsNoTracking().GroupBy(s => s.ArticleId).Select(g => new { g.Key, Q = g.Sum(s => s.Quantity) }).ToDictionaryAsync(x => x.Key, x => x.Q),
            await db.StockMovements.AsNoTracking().GroupBy(m => m.ArticleId).Select(g => new { g.Key, Q = g.Sum(m => m.QuantityDelta) }).ToDictionaryAsync(x => x.Key, x => x.Q),
            await db.StockMovements.CountAsync()));

        Assert.True(ledgerRows > 300, "Das Ledger soll viele Bewegungen enthalten");
        Assert.Equal(stock.Keys.OrderBy(k => k), ledger.Keys.OrderBy(k => k));
        foreach (var (articleId, quantity) in stock)
            Assert.Equal(quantity, ledger[articleId]);
        Assert.All(stock.Values, q => Assert.True(q >= 0));
    }

    [Fact]
    public async Task Der_Bestand_entsteht_ueber_das_Ledger_jede_Bestandszeile_hat_einen_Zugang()
    {
        var withoutInbound = await _demo.DbAsync(async db =>
        {
            var received = await db.StockMovements.AsNoTracking()
                .Where(m => m.Reason == StockMovementReason.Inbound)
                .Select(m => new { m.ArticleId, m.BinId }).Distinct().ToListAsync();
            var rows = await db.StockItems.AsNoTracking().Where(s => s.Quantity > 0).Select(s => new { s.ArticleId, s.StorageLocationId }).ToListAsync();
            // Ware in einem Platz stammt aus Wareneingang oder wurde per Nachschub/Retoure dorthin gebucht.
            var moved = await db.StockMovements.AsNoTracking()
                .Where(m => m.Reason == StockMovementReason.ReplenishmentIn || m.Reason == StockMovementReason.Return)
                .Select(m => new { m.ArticleId, m.BinId }).Distinct().ToListAsync();
            return rows.Where(r => !received.Any(x => x.ArticleId == r.ArticleId && x.BinId == r.StorageLocationId)
                                   && !moved.Any(x => x.ArticleId == r.ArticleId && x.BinId == r.StorageLocationId)).ToList();
        });

        Assert.Empty(withoutInbound);
    }

    [Fact]
    public async Task Das_Ledger_kennt_alle_Buchungsgruende_der_Prozesse()
    {
        var reasons = await _demo.DbAsync(db => db.StockMovements.AsNoTracking().Select(m => m.Reason).Distinct().ToListAsync());

        foreach (var expected in new[]
                 {
                     StockMovementReason.Inbound, StockMovementReason.Pick, StockMovementReason.Return, StockMovementReason.Inventory,
                     StockMovementReason.ReplenishmentOut, StockMovementReason.ReplenishmentIn, StockMovementReason.ReturnB, StockMovementReason.ReturnScrap,
                 })
            Assert.Contains(expected, reasons);
    }

    // ---- Historie -------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Die_Historie_reicht_rund_60_Tage_zurueck_und_liegt_ganz_in_der_Vergangenheit()
    {
        var (firstOrder, lastOrder, firstMovement, lastMovement) = await _demo.DbAsync(async db => (
            await db.Orders.MinAsync(o => o.CreatedAt), await db.Orders.MaxAsync(o => o.CreatedAt),
            await db.StockMovements.MinAsync(m => m.At), await db.StockMovements.MaxAsync(m => m.At)));

        Assert.True(firstOrder <= _demo.Now.Date.AddDays(-58), $"Erste Bestellung {firstOrder:u}");
        Assert.True(firstMovement <= _demo.Now.Date.AddDays(-60), $"Erste Bewegung {firstMovement:u}");
        Assert.True(lastOrder <= _demo.Now, $"Letzte Bestellung {lastOrder:u} liegt nach dem Bezugszeitpunkt");
        Assert.True(lastMovement <= _demo.Now, $"Letzte Bewegung {lastMovement:u} liegt nach dem Bezugszeitpunkt");
        Assert.True(lastOrder >= _demo.Now.AddHours(-6), "Der Ist-Stand liegt in den letzten Stunden");
    }

    [Fact]
    public async Task Kein_Zeitstempel_der_Fachdaten_stammt_vom_Zeitpunkt_des_Seedings()
    {
        // Die Domain setzt "jetzt"; der Generator schreibt jeden dieser Werte auf den simulierten Zeitpunkt um. Fehlte eine
        // Stelle, läge ein Zeitstempel bei der Startzeit statt in der Vergangenheit.
        var names = new HashSet<string>
        {
            "CreatedAt", "UpdatedAt", "At", "ReceivedAt", "ConfirmedAt", "CompletedAt", "SentAt", "LabeledAt", "ShippedAt",
            "DeliveredAt", "ProcessedAt", "ReconciledAt", "ReleasedAt",
        };
        var limit = _demo.Now.AddMinutes(-5);

        var offenders = await _demo.DbAsync(async db =>
        {
            var found = new List<string>();
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            try
            {
                foreach (var entityType in db.Model.GetEntityTypes().Where(t => !t.IsOwned() && t.GetTableName() is not null))
                {
                    var table = entityType.GetTableName()!;
                    if (table is "Users" or "AuditEntries") continue;   // Benutzer entstehen zur Startzeit; das Audit prüft ein eigener Test
                    foreach (var property in entityType.GetProperties().Where(p => names.Contains(p.Name)))
                    {
                        await using var command = connection.CreateCommand();
                        command.CommandText = $"SELECT MAX(\"{property.GetColumnName()}\") FROM \"{table}\"";
                        var value = await command.ExecuteScalarAsync();
                        if (value is string text && DateTime.Parse(text, null, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal) > limit)
                            found.Add($"{table}.{property.Name} = {text}");
                    }
                }
            }
            finally
            {
                await connection.CloseAsync();
            }
            return found;
        });

        Assert.Empty(offenders);
    }

    [Fact]
    public async Task Der_Audit_Trail_ist_gefuellt_und_auf_die_Historie_zurueckdatiert()
    {
        var (count, latest, users) = await _demo.DbAsync(async db => (
            await db.AuditEntries.CountAsync(a => a.EntityType != "User"),
            await db.AuditEntries.Where(a => a.EntityType != "User").MaxAsync(a => a.At),
            await db.AuditEntries.Where(a => a.EntityType != "User").Select(a => a.User).Distinct().ToListAsync()));

        Assert.True(count > 200, $"Nur {count} Audit-Einträge");
        Assert.True(latest <= _demo.Now, $"Letzter Audit-Eintrag {latest:u} liegt nach dem Bezugszeitpunkt");
        Assert.Contains("picker", users);
        Assert.Contains("packer", users);
        Assert.Contains("receiver", users);
        Assert.Contains("manager", users);
    }

    // ---- Zustände in allen Bereichen -----------------------------------------------------------------------------------

    [Fact]
    public async Task Bestellungen_gibt_es_in_allen_Status()
    {
        var orders = await _demo.DbAsync(db => db.Orders.AsNoTracking().Include(o => o.Lines).ToListAsync());
        var statuses = orders.Select(o => o.Status).ToHashSet();

        foreach (var status in new[] { OrderStatus.New, OrderStatus.Picking, OrderStatus.Picked, OrderStatus.Packed, OrderStatus.Shipped, OrderStatus.Cancelled })
            Assert.Contains(status, statuses);
        Assert.True(orders.Count >= 200);
        Assert.Contains(orders, o => o.CustomerId is not null && o.ShippingAddressId is not null);
        Assert.Contains(orders, o => o.Source == OrderSource.Api && o.ExternalReference is not null);
        Assert.Contains(orders, o => o.Priority > 0);
        Assert.Contains(orders, o => o.DueDate is not null);
        Assert.Contains(orders, o => o.Lines.Any(l => l.ArticleId != Guid.Empty) && o.Lines.Count >= 3);
    }

    [Fact]
    public async Task Picklisten_gibt_es_in_allen_Status_und_die_abgeschlossenen_haben_Picker()
    {
        var lists = await _demo.DbAsync(db => db.PickLists.AsNoTracking().Include(p => p.Items).ToListAsync());

        foreach (var status in new[] { PickListStatus.Pending, PickListStatus.InProgress, PickListStatus.Picked, PickListStatus.Completed, PickListStatus.Cancelled })
            Assert.Contains(lists, l => l.Status == status);

        var completed = lists.Where(l => l.Status == PickListStatus.Completed).ToList();
        Assert.True(completed.Count >= 60);
        Assert.All(completed, l =>
        {
            Assert.False(string.IsNullOrEmpty(l.AssignedTo));
            Assert.True(l.TotalDistanceMm > 0);
            Assert.All(l.Items, i => Assert.True(i.IsConfirmed && i.Picked));
            Assert.True(l.UpdatedAt > l.CreatedAt, "Dauer der Kommissionierung");
        });
        Assert.True(completed.Select(l => l.AssignedTo).Distinct().Count() >= 3, "Mehrere Picker");
        Assert.Contains(lists, l => l.PickCartConfigId is not null);
    }

    [Fact]
    public async Task Einkauf_Wareneingang_Retouren_Nachschub_Inventur_Sendungen_und_Wellen_haben_Daten_in_jedem_Zustand()
    {
        var snapshot = await _demo.DbAsync(async db => new
        {
            Pos = await db.PurchaseOrders.AsNoTracking().Select(p => p.Status).Distinct().ToListAsync(),
            Inbound = await db.InboundShipments.AsNoTracking().Select(p => p.Status).Distinct().ToListAsync(),
            Returns = await db.ReturnShipments.AsNoTracking().Select(p => p.Status).Distinct().ToListAsync(),
            Replenishment = await db.ReplenishmentTasks.AsNoTracking().Select(p => p.Status).Distinct().ToListAsync(),
            Inventory = await db.InventoryCounts.AsNoTracking().Select(p => p.Status).Distinct().ToListAsync(),
            Shipments = await db.Shipments.AsNoTracking().Select(p => p.Status).Distinct().ToListAsync(),
            Waves = await db.PickWaves.AsNoTracking().Select(p => p.Status).Distinct().ToListAsync(),
            QcResults = await db.ReturnLines.AsNoTracking().Select(l => l.QcResult).Distinct().ToListAsync(),
        });

        foreach (var s in new[] { PurchaseOrderStatus.Draft, PurchaseOrderStatus.Sent, PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.Received, PurchaseOrderStatus.Cancelled })
            Assert.Contains(s, snapshot.Pos);
        foreach (var s in new[] { InboundShipmentStatus.Draft, InboundShipmentStatus.Received, InboundShipmentStatus.Cancelled })
            Assert.Contains(s, snapshot.Inbound);
        foreach (var s in new[] { ReturnStatus.Draft, ReturnStatus.Processed, ReturnStatus.Cancelled })
            Assert.Contains(s, snapshot.Returns);
        foreach (var s in new[] { ReplenishmentStatus.Open, ReplenishmentStatus.Completed, ReplenishmentStatus.Cancelled })
            Assert.Contains(s, snapshot.Replenishment);
        foreach (var s in new[] { InventoryStatus.Open, InventoryStatus.Reconciled, InventoryStatus.Cancelled })
            Assert.Contains(s, snapshot.Inventory);
        foreach (var s in new[] { ShipmentStatus.Ready, ShipmentStatus.Labeled, ShipmentStatus.Shipped, ShipmentStatus.Delivered })
            Assert.Contains(s, snapshot.Shipments);
        foreach (var s in new[] { PickWaveStatus.Open, PickWaveStatus.Released, PickWaveStatus.Completed, PickWaveStatus.Cancelled })
            Assert.Contains(s, snapshot.Waves);
        foreach (var s in new[] { QcResult.Pending, QcResult.Sellable, QcResult.BGrade, QcResult.Defect, QcResult.Destroy })
            Assert.Contains(s, snapshot.QcResults);
    }

    [Fact]
    public async Task Die_Wareneingaenge_aus_Bestellungen_verweisen_auf_die_Bestellzeilen_und_schreiben_die_Menge_fort()
    {
        await _demo.DbAsync(async db =>
        {
            var pos = await db.PurchaseOrders.AsNoTracking().Include(p => p.Lines).ToListAsync();
            var inbounds = await db.InboundShipments.AsNoTracking().Include(s => s.Lines).Where(s => s.Status == InboundShipmentStatus.Received).ToListAsync();

            var receivedByLine = inbounds.SelectMany(s => s.Lines).Where(l => l.PurchaseOrderLineId is not null)
                .GroupBy(l => l.PurchaseOrderLineId!.Value).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
            foreach (var line in pos.SelectMany(p => p.Lines))
                Assert.Equal(line.ReceivedQty, receivedByLine.GetValueOrDefault(line.Id));
            Assert.All(pos.Where(p => p.Status == PurchaseOrderStatus.Received), p => Assert.All(p.Lines, l => Assert.Equal(l.OrderedQty, l.ReceivedQty)));
            Assert.All(pos.Where(p => p.Status == PurchaseOrderStatus.Draft), p => Assert.All(p.Lines, l => Assert.Equal(0, l.ReceivedQty)));
            return 0;
        });
    }

    [Fact]
    public async Task Bestellstatus_und_Sendungen_gehoeren_zusammen()
    {
        await _demo.DbAsync(async db =>
        {
            var orders = await db.Orders.AsNoTracking().ToListAsync();
            var shipments = await db.Shipments.AsNoTracking().ToListAsync();

            // Versendet heißt: mindestens eine Sendung ist raus; gepackte Bestellungen haben höchstens offene Sendungen.
            foreach (var order in orders.Where(o => o.Status == OrderStatus.Shipped))
                Assert.Contains(shipments, s => s.OrderId == order.Id && s.Status is ShipmentStatus.Shipped or ShipmentStatus.Delivered);
            foreach (var order in orders.Where(o => o.Status is OrderStatus.New or OrderStatus.Picking or OrderStatus.Picked or OrderStatus.Cancelled))
                Assert.DoesNotContain(shipments, s => s.OrderId == order.Id);
            foreach (var order in orders.Where(o => o.Status == OrderStatus.Packed))
                Assert.DoesNotContain(shipments, s => s.OrderId == order.Id && s.Status is ShipmentStatus.Shipped or ShipmentStatus.Delivered);
            Assert.All(shipments, s => Assert.True(Shipment.IsHttpUrl(s.TrackingUrl) || s.TrackingUrl is null));
            return 0;
        });
    }

    [Fact]
    public async Task Bestellungen_auf_Picklisten_haben_den_passenden_Status()
    {
        await _demo.DbAsync(async db =>
        {
            var orders = (await db.Orders.AsNoTracking().ToListAsync()).ToDictionary(o => o.Id);
            var items = await db.PickItems.AsNoTracking().Join(db.PickLists.AsNoTracking(), i => i.PickListId, p => p.Id, (i, p) => new { i.OrderId, p.Status }).ToListAsync();

            foreach (var g in items.GroupBy(x => x.OrderId))
            {
                var status = orders[g.Key].Status;
                var listStatuses = g.Select(x => x.Status).ToHashSet();
                if (status == OrderStatus.Picking) Assert.Subset(new HashSet<PickListStatus> { PickListStatus.Pending, PickListStatus.InProgress }, listStatuses);
                if (status == OrderStatus.Picked) Assert.Equal(new HashSet<PickListStatus> { PickListStatus.Picked }, listStatuses);
                if (status is OrderStatus.Packed or OrderStatus.Shipped) Assert.Contains(PickListStatus.Completed, listStatuses);
            }
            return 0;
        });
    }

    // ---- Reports zeigen sofort Daten -----------------------------------------------------------------------------------

    [Fact]
    public async Task Das_Dashboard_zeigt_Kennzahlen_Top_Artikel_und_Reihen()
    {
        var dashboard = (await _demo.Admin.GetFromJsonAsync<ReportDashboardDto>("/api/reports/dashboard?range=30"))!;

        Assert.All(dashboard.Headline, k => Assert.False(string.IsNullOrWhiteSpace(k.Value)));
        Assert.True(int.Parse(dashboard.Headline[0].Value) > 50, "Bestellungen im Zeitraum");
        Assert.True(dashboard.TopArticles.Count >= 5);
        Assert.True(dashboard.TopBins.Count >= 5);
        Assert.True(dashboard.OrderStatusBreakdown.Count >= 4);
        Assert.Contains(dashboard.OrdersPerDay, p => p.Count > 0);
        Assert.Contains(dashboard.PickListsPerDay, p => p.Count > 0);
    }

    [Fact]
    public async Task Heatmap_ABC_Dead_Stock_Lagerwert_und_Picker_Performance_haben_Daten()
    {
        var heatmap = (await _demo.Admin.GetFromJsonAsync<List<BinHeatPointDto>>("/api/reports/bin-heatmap?range=60"))!;
        Assert.True(heatmap.Count >= 10);

        var abc = (await _demo.Admin.GetFromJsonAsync<List<AbcArticleDto>>("/api/reports/abc-analysis?range=60"))!;
        Assert.Equal(new[] { "A", "B", "C" }, abc.Select(a => a.Class).Distinct().Order().ToArray());

        var dead = (await _demo.Admin.GetFromJsonAsync<List<DeadStockArticleDto>>("/api/reports/dead-stock?days=90"))!;
        Assert.True(dead.Count >= 3, "Ladenhüter (90 Tage ohne Bewegung)");
        Assert.All(dead, d => Assert.True(d.TotalQuantity > 0 && d.DaysSinceLastMovement >= 90));

        var valuation = (await _demo.Admin.GetFromJsonAsync<StockValuationDto>("/api/reports/stock-valuation"))!;
        Assert.True(valuation.TotalValueCents > 0);
        Assert.True(valuation.ArticleCount >= 40);
        Assert.Equal(0, valuation.FallbackValueCents);   // der Bestand entstand über das Ledger, nichts wird zum Stammpreis geschätzt

        var pickers = (await _demo.Admin.GetFromJsonAsync<PickerPerformanceDto>("/api/reports/picker-performance?range=60"))!;
        Assert.True(pickers.Rows.Count >= 3);
        Assert.All(pickers.Rows, r => Assert.True(r.PickListsCompleted > 0 && r.ItemsPicked > 0 && r.AvgDurationMinutes > 0));
        Assert.Contains(pickers.Rows, r => r.Picker == "picker");
    }

    [Fact]
    public async Task Der_Live_Status_und_die_Bestandsalarme_zeigen_offene_Vorgaenge()
    {
        var live = (await _demo.Admin.GetFromJsonAsync<LiveStatusDto>("/api/reports/live-status"))!;
        Assert.True(live.OrdersOpen > 0 && live.OrdersInPicking > 0 && live.OrdersPicked > 0 && live.OrdersPacked > 0);
        Assert.True(live.PicklistsPending > 0 && live.PicklistsInProgress > 0 && live.PicklistsPickedReadyToPack > 0);
        Assert.True(live.InventoryCountsOpen >= 1);
        Assert.True(live.ReplenishmentTasksOpen >= 1);
        Assert.True(live.StockAlertsCritical >= 1);
        Assert.True(live.StockAlertsWarning >= 1);

        var alerts = (await _demo.Admin.GetFromJsonAsync<List<StockAlertDto>>("/api/stock/alerts"))!;
        Assert.Contains(alerts, a => a.Severity == "critical");
        Assert.Contains(alerts, a => a.Severity == "warning");
    }

    [Fact]
    public async Task Der_Bestandsverlauf_eines_Artikels_hat_Punkte()
    {
        var articleId = await _demo.DbAsync(db => db.Articles.Where(a => a.Sku == "BEF-1001").Select(a => a.Id).SingleAsync());

        var trend = (await _demo.Admin.GetFromJsonAsync<StockTrendDto>($"/api/reports/stock-trend/{articleId}?days=60"))!;

        Assert.True(trend.Series.Count > 20);
        Assert.Equal(trend.CurrentQuantity, trend.Series[^1].Quantity);
        Assert.All(trend.Series, p => Assert.True(p.Quantity >= 0));
    }

    // ---- Demo-Benutzer -------------------------------------------------------------------------------------------------

    [Fact]
    public void Die_Zugangsdaten_stehen_genau_einmal_im_Log_und_das_Passwort_sonst_nirgends()
    {
        Assert.Single(_demo.Log.Entries, e => e.Message.StartsWith("Demo-Zugangsdaten", StringComparison.Ordinal));
        Assert.Equal(DemoCatalog.Users.Count, _demo.Logins.Count);
        foreach (var login in _demo.Logins)
        {
            Assert.True(login.Password.Length >= 16, "Zufallspasswort");
            Assert.Single(_demo.Log.Entries, e => e.Message.Contains(login.Password, StringComparison.Ordinal));
        }
        Assert.Equal(_demo.Logins.Count, _demo.Logins.Select(l => l.Password).Distinct().Count());
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, LogLevelOf("Demo-Zugangsdaten"));
    }

    private Microsoft.Extensions.Logging.LogLevel LogLevelOf(string prefix) =>
        _demo.Log.Entries.Single(e => e.Message.StartsWith(prefix, StringComparison.Ordinal)).Level;

    [Fact]
    public async Task Die_Demo_Benutzer_existieren_je_Rolle_ohne_Passwortwechsel_Zwang()
    {
        var users = (await _demo.Admin.GetFromJsonAsync<List<UserDto>>("/api/users"))!;

        foreach (var spec in DemoCatalog.Users)
        {
            var user = Assert.Single(users, u => u.Username == spec.Username);
            Assert.Equal(new[] { spec.Role }, user.Roles);
            Assert.True(user.IsActive);
            Assert.False(user.MustChangePassword);
        }
        // Der Admin bleibt der Bootstrap-Admin, es gibt keinen zweiten.
        Assert.Single(users, u => u.Roles.Contains("Admin"));
    }

    [Fact]
    public async Task Die_Demo_Benutzer_koennen_sich_mit_den_ausgegebenen_Passwoertern_anmelden()
    {
        foreach (var login in _demo.Logins)
        {
            using var client = _demo.Factory.CreateClient();
            var response = await client.PostAsJsonAsync("/api/auth/login", new LoginRequest(login.Username, login.Password));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = (await response.Content.ReadFromJsonAsync<LoginResponse>())!;
            Assert.Equal(login.Username, body.User.Username);
            Assert.Equal(new[] { login.Role }, body.User.Roles);
            Assert.False(body.User.MustChangePassword);
        }
    }

    [Fact]
    public async Task Ein_falsches_Passwort_wird_abgelehnt_und_die_Rollen_gelten()
    {
        var picker = _demo.Logins.Single(l => l.Username == "picker");
        using var wrong = _demo.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await wrong.PostAsJsonAsync("/api/auth/login", new LoginRequest("picker", picker.Password + "x"))).StatusCode);

        // Der Betrachter darf lesen, aber nichts schreiben.
        var viewerLogin = _demo.Logins.Single(l => l.Username == "viewer");
        using var viewer = await _demo.Factory.CreateClient().LoginAsync(viewerLogin.Username, viewerLogin.Password);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/orders/manual", new { })).StatusCode);
    }

    // ---- Nie Reseed ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Ein_zweiter_Seeding_Aufruf_aendert_nichts()
    {
        Func<LagerDbContext, Task<(int, int, int, int, int)>> count = async db =>
            (await db.Articles.CountAsync(), await db.Orders.CountAsync(), await db.StockMovements.CountAsync(),
             await db.Users.CountAsync(), await db.AuditEntries.CountAsync(a => a.EntityType != "User"));
        var before = await _demo.DbAsync(count);
        var logger = new ListLogger();

        var seededAgain = await _demo.DbAsync(async db =>
        {
            using var scope = _demo.Factory.Services.CreateScope();
            return await DemoDataSeeder.SeedAsync(db, scope.ServiceProvider, logger);
        });

        Assert.False(seededAgain);
        Assert.Equal(before, await _demo.DbAsync(count));
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("Demo-Zugangsdaten"));
        Assert.Contains(logger.Entries, e => e.Message.Contains("schon Daten"));
    }
}
