using Lager.Application.Abstractions;
using Lager.Application.PickLists;
using Lager.Application.Stock;
using Lager.Domain.Articles;
using Lager.Domain.Customers;
using Lager.Domain.Inbound;
using Lager.Domain.Inventory;
using Lager.Domain.Orders;
using Lager.Domain.PickLists;
using Lager.Domain.Purchasing;
using Lager.Domain.Returns;
using Lager.Domain.Shipping;
using Lager.Domain.Stock;
using Lager.Domain.Suppliers;
using Lager.Domain.Warehouse;
using Lager.Infrastructure.Persistence;
using Lager.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using WarehouseEntity = Lager.Domain.Warehouse.Warehouse;

namespace Lager.Api.Seeding;

/// <summary>Was der Demo-Datensatz angelegt hat (Zählung aus der Datenbank nach dem Seeding).</summary>
public sealed record DemoSeedSummary(
    int Articles, int Suppliers, int Customers, int Bins, int StockRows, int StockMovements,
    int Orders, int PickLists, int PickWaves, int PurchaseOrders, int InboundShipments, int Shipments,
    int Returns, int ReplenishmentTasks, int InventoryCounts);

/// <summary>
/// Erzeugt den Demo-Datensatz samt Historie: Stammdaten aus dem <see cref="DemoCatalog"/> und rund 60 Tage Betrieb (Wareneingänge
/// über Einkaufsbestellungen, Bestellungen in allen Status, Picklisten, Wellen, Sendungen, Retouren, Nachschub, Inventur).
///
/// Grundsätze:
///  - Deterministisch: fester Zufallsstartwert, die Simulation hängt nur vom Katalog, vom Startwert und vom Bezugsdatum ab
///    (zwei Läufe mit demselben Bezugsdatum ergeben dieselben Artikel- und Bestandssummen; nur die Guids sind neu).
///  - Der Bestand entsteht ausschließlich über <see cref="StockBooking"/> (Wareneingang, Kommissionierung, Nachschub, Retoure,
///    Inventur): Ledger und Bestand stimmen daher überein. Die Domain-Objekte laufen durch ihre echten Statusmethoden.
///  - Vergangenheits-Zeitstempel: die Domain setzt immer <c>DateTime.UtcNow</c>. Nach jeder Fachaktion überschreibt
///    <see cref="Stamp"/> diese "jetzt"-Werte über den EF-Property-Zugriff (<c>Entry(e).Property(name).CurrentValue</c>) mit
///    dem simulierten Zeitpunkt - ohne einen Domain-Setter zu öffnen. Ersetzt wird nur, was nach dem Start des Generators
///    entstanden ist; die Simulation selbst liegt vollständig davor.
///  - Alles läuft in der Transaktion des Aufrufers (der Seeder öffnet sie): scheitert etwas, bleibt die Datenbank leer.
/// </summary>
public sealed class DemoHistoryGenerator
{
    public const int DefaultSeed = 2701;
    public const int DefaultHistoryDays = 60;

    /// <summary>Datumsfelder der Domain, die beim Anlegen/Ändern "jetzt" sind und für die Vergangenheit umgeschrieben werden.</summary>
    private static readonly string[] TimestampProperties =
    {
        "CreatedAt", "UpdatedAt", "At", "ReceivedAt", "ConfirmedAt", "CompletedAt", "SentAt", "LabeledAt", "ShippedAt",
        "DeliveredAt", "ProcessedAt", "ReconciledAt", "ReleasedAt",
    };

    // ------------------------------------------------------------------------------------------------------------------
    // Simulationszustand
    // ------------------------------------------------------------------------------------------------------------------

    private sealed class Bin
    {
        public Guid Id;
        public string Code = "";
        public StorageLocation Entity = null!;
        public string ZoneCode = "";
    }

    private sealed class Art
    {
        public DemoArticleSpec Spec = null!;
        public Guid Id;
        public Article Entity = null!;
        public Bin Primary = null!;   // Kommissionierplatz: Hot-Pick-, Standard- oder Reserveplatz
        public Bin? Reserve;          // Reserveplatz hinter einem Hot-Pick-Platz
        public int OnHand;            // Gesamtbestand (Spiegel des Bestands, führt jede Buchung mit)
        public int OpenPoQty;         // bestellt, noch nicht geliefert
        public int Committed;         // in offenen Bestellungen (noch nicht gepackt) gebunden
        public Bin ReceiveBin => Reserve ?? Primary;
    }

    private sealed record SimLine(Art Art, int Quantity, Guid LineId);

    private sealed class SimOrder
    {
        public Guid Id;
        public string Number = "";
        public DateTime CreatedAt;
        public int Priority;
        public DateTime? Due;
        public DateTime? CancelAt;
        public Guid? CustomerId;
        public string? CustomerName;
        public string? CustomerReference;
        public List<SimLine> Lines = new();
        public Guid? PickListId;
        public DateTime? PackedAt;
        public DateTime? ShippedAt;
        public bool Returned;
    }

    private sealed class SimPickList
    {
        public Guid Id;
        public string Number = "";
        public List<SimOrder> Orders = new();
        public Guid? WaveId;
        public DateTime PickedAt;
        public int DistanceMm;
    }

    private sealed class PoLine
    {
        public Guid LineId;
        public Art Art = null!;
        public int Ordered;
        public int Remaining;
        public int PriceCents;
    }

    private sealed class SimPo
    {
        public Guid Id;
        public string Number = "";
        public int SupplierIndex;
        public List<PoLine> Lines = new();
        public int Deliveries;
    }

    private sealed class Delivery
    {
        public SimPo Po = null!;
        public DateTime Day;
        public double Fraction = 1.0;
        public bool Final = true;
        public bool CancelInstead;
    }

    private sealed class OpenTask
    {
        public Guid Id;
        public Art Art = null!;
        public int Suggested;
        public DateTime CreatedOn;
    }

    private sealed record PendingDelivery(Guid ShipmentId, DateTime Day);

    private enum PickStage { Pending, InProgress, Picked, Packed }

    // ------------------------------------------------------------------------------------------------------------------
    // Felder
    // ------------------------------------------------------------------------------------------------------------------

    private readonly LagerDbContext _db;
    private readonly ILogger? _logger;
    private readonly CancellationToken _ct;
    private readonly Random _rng;
    private readonly int _historyDays;
    private readonly DateTime _now;
    private readonly DateTime _today;
    private readonly DateTime _realStart;
    private readonly IStockRepository _stockRepo;
    private readonly IStockMovementRepository _moveRepo;
    private readonly WallAwarePickRouteOptimizer _router = new();

    private readonly List<Art> _artList = new();
    private readonly Dictionary<string, Art> _arts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Bin> _bins = new();
    private readonly Dictionary<Guid, StorageLocation> _binEntities = new();
    private readonly List<Wall> _walls = new();
    private readonly List<PickPoint> _pickPoints = new();
    private readonly List<Guid> _cartIds = new();
    private readonly List<Guid> _supplierIds = new();
    private readonly List<(Guid Id, string Name, List<Guid> ShippingAddressIds)> _customers = new();
    private readonly List<Art> _orderable = new();

    private readonly List<SimOrder> _waiting = new();
    private readonly List<SimOrder> _packed = new();
    private readonly List<SimOrder> _shipped = new();
    private readonly Dictionary<DateTime, int> _orderCounter = new();
    private readonly List<Delivery> _deliveries = new();
    private readonly List<OpenTask> _openTasks = new();
    private readonly List<PendingDelivery> _pendingDeliveries = new();
    private readonly HashSet<string> _scriptedDone = new();
    private int _shopNumber = 1041;
    private int _trackingNumber = 4_100_000;

    private DemoHistoryGenerator(LagerDbContext db, DateTime nowUtc, int seed, int historyDays, ILogger? logger, CancellationToken ct)
    {
        _db = db;
        _logger = logger;
        _ct = ct;
        _rng = new Random(seed);
        _historyDays = historyDays;
        // Die Simulation darf nicht hinter der Uhr liegen: alle Zeitstempel müssen VOR den echten "jetzt"-Werten der Domain liegen.
        var real = DateTime.UtcNow;
        _now = DateTime.SpecifyKind(nowUtc > real ? real : nowUtc, DateTimeKind.Utc);
        _today = _now.Date;
        _realStart = real.AddSeconds(-2);
        _stockRepo = new StockRepository(db);
        _moveRepo = new StockMovementRepository(db);
    }

    /// <summary>
    /// Legt den kompletten Demo-Datensatz an und speichert ihn (mehrere SaveChanges; der Aufrufer hält die Transaktion).
    /// <paramref name="nowUtc"/> ist das Bezugsdatum "jetzt" (Standard: die Uhr); die Historie reicht <paramref name="historyDays"/>
    /// Tage davor bis zu diesem Zeitpunkt.
    /// </summary>
    public static async Task<DemoSeedSummary> GenerateAsync(
        LagerDbContext db, DateTime? nowUtc = null, int seed = DefaultSeed, int historyDays = DefaultHistoryDays,
        ILogger? logger = null, CancellationToken ct = default)
    {
        if (historyDays < 20) throw new ArgumentOutOfRangeException(nameof(historyDays), "Die Historie braucht mindestens 20 Tage.");
        var generator = new DemoHistoryGenerator(db, nowUtc ?? DateTime.UtcNow, seed, historyDays, logger, ct);
        return await generator.RunAsync();
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Ablauf
    // ------------------------------------------------------------------------------------------------------------------

    private async Task<DemoSeedSummary> RunAsync()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        _logger?.LogInformation("Demo-Daten: Stammdaten, {Days} Tage Historie und Ist-Stand werden angelegt (kann einige Sekunden dauern).", _historyDays);

        await BuildMasterDataAsync();
        await PrehistoryAsync();
        await OpeningReceiptsAsync();

        for (var offset = -_historyDays; offset <= -1; offset++)
            await RunDayAsync(_today.AddDays(offset), isLastDay: offset == -1);

        await BuildTailAsync();
        await EngineerAlertsAsync();

        _db.ChangeTracker.Clear();
        var summary = new DemoSeedSummary(
            await _db.Articles.CountAsync(_ct), await _db.Suppliers.CountAsync(_ct), await _db.Customers.CountAsync(_ct),
            await _db.StorageLocations.CountAsync(_ct), await _db.StockItems.CountAsync(_ct), await _db.StockMovements.CountAsync(_ct),
            await _db.Orders.CountAsync(_ct), await _db.PickLists.CountAsync(_ct), await _db.PickWaves.CountAsync(_ct),
            await _db.PurchaseOrders.CountAsync(_ct), await _db.InboundShipments.CountAsync(_ct), await _db.Shipments.CountAsync(_ct),
            await _db.ReturnShipments.CountAsync(_ct), await _db.ReplenishmentTasks.CountAsync(_ct), await _db.InventoryCounts.CountAsync(_ct));
        _logger?.LogInformation(
            "Demo-Daten angelegt in {Seconds:0.0} s: {Articles} Artikel, {Orders} Bestellungen, {PickLists} Picklisten, {Movements} Lagerbewegungen, {PurchaseOrders} Einkaufsbestellungen.",
            watch.Elapsed.TotalSeconds, summary.Articles, summary.Orders, summary.PickLists, summary.StockMovements, summary.PurchaseOrders);
        return summary;
    }

    private async Task RunDayAsync(DateTime day, bool isLastDay)
    {
        var business = day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

        if (business)
        {
            await CompleteReplenishmentsAsync(day);
            await ReceiveDeliveriesAsync(day);
            if (day.DayOfWeek is DayOfWeek.Monday or DayOfWeek.Thursday)
                await ReviewReordersAsync(day);
            await RunScriptedEventsAsync(day);
        }

        await CreateOrdersAsync(day);

        if (business)
        {
            await RunBatchAsync(day, 10, 0);
            await RunBatchAsync(day, isLastDay ? 16 : 14, 0);
            await ShipAsync(day, sameDayOnly: isLastDay);
            await ScanReplenishmentAsync(day, At(day, 16, 45));
        }

        await DeliverAsync(day);
        await ReturnsAsync(day);
        await InventoryAsync(day);
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Zeit und Speichern
    // ------------------------------------------------------------------------------------------------------------------

    private static DateTime At(DateTime day, int hour, int minute = 0) =>
        DateTime.SpecifyKind(day.Date.AddHours(hour).AddMinutes(minute), DateTimeKind.Utc);

    private DateTime DayOffset(int offset) => _today.AddDays(offset);

    /// <summary>Ersetzt jeden "jetzt" gesetzten Zeitstempel der noch nicht gespeicherten Änderungen durch <paramref name="when"/>.</summary>
    private void Stamp(DateTime when)
    {
        when = DateTime.SpecifyKind(when, DateTimeKind.Utc);
        foreach (var entry in _db.ChangeTracker.Entries().ToList())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            foreach (var name in TimestampProperties)
            {
                var metadata = entry.Metadata.FindProperty(name);
                if (metadata is null || (metadata.ClrType != typeof(DateTime) && metadata.ClrType != typeof(DateTime?))) continue;
                var property = entry.Property(name);
                if (property.CurrentValue is DateTime value && value >= _realStart)
                    property.CurrentValue = when;
            }
        }
    }

    /// <summary>
    /// Speichert und schreibt den Audit-Trail dieses Schritts auf Zeit und Akteur um (der Interceptor stempelt "jetzt" und
    /// "system"). Danach ist der Change-Tracker leer: der nächste Schritt lädt seine Daten frisch.
    /// </summary>
    private async Task SaveAsync(DateTime when, string actor)
    {
        when = DateTime.SpecifyKind(when, DateTimeKind.Utc);
        await _db.SaveChangesAsync(_ct);
        var since = _realStart;
        await _db.AuditEntries.Where(a => a.At >= since)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.At, when).SetProperty(a => a.User, actor), _ct);
        _db.ChangeTracker.Clear();
    }

    private int Rnd(int minInclusive, int maxInclusive) => _rng.Next(minInclusive, maxInclusive + 1);

    // ------------------------------------------------------------------------------------------------------------------
    // Stammdaten
    // ------------------------------------------------------------------------------------------------------------------

    private async Task BuildMasterDataAsync()
    {
        var t = At(DayOffset(-_historyDays - 90), 9);

        // Lieferanten
        foreach (var spec in DemoCatalog.Suppliers)
        {
            var supplier = new Supplier(spec.Code, spec.Name, spec.LeadTimeDays);
            supplier.UpdateProfile(spec.Name, spec.Email, spec.Phone, spec.Notes, spec.LeadTimeDays, spec.MinOrderValueCents, "EUR");
            _db.Suppliers.Add(supplier);
            _supplierIds.Add(supplier.Id);
        }

        // Kunden mit Adressen
        foreach (var spec in DemoCatalog.Customers)
        {
            var customer = new Customer(spec.Code, spec.Name);
            customer.UpdateProfile(spec.Name, spec.Email, spec.Phone, spec.Notes, "EUR", spec.DiscountPercent);
            var shipping = new List<Guid>();
            foreach (var a in spec.Addresses)
            {
                var address = customer.AddAddress(a.Kind, a.Label, a.Street, a.Street2, a.Zip, a.City, "DE");
                if (a.Kind != AddressKind.Billing) shipping.Add(address.Id);
            }
            _db.Customers.Add(customer);
            _customers.Add((customer.Id, spec.Name, shipping));
        }

        BuildWarehouse();
        BuildArticles();

        Stamp(t);
        await SaveAsync(t, "system");
    }

    private void BuildWarehouse()
    {
        var warehouse = new WarehouseEntity(DemoLayout.WarehouseCode, DemoLayout.WarehouseName);
        _db.Warehouses.Add(warehouse);

        var zones = new Dictionary<string, Zone>();
        foreach (var (code, name, originY) in DemoLayout.Zones)
        {
            var zone = new Zone(warehouse.Id, code, name, new Position(0, originY, 0));
            _db.Zones.Add(zone);
            zones[code] = zone;
        }

        foreach (var (zoneCode, aisleCode, y) in DemoLayout.Aisles)
        {
            var aisle = new Aisle(zones[zoneCode].Id, aisleCode, new Position(0, y, 0), new Position(12_000, y, 0), AisleOrientation.AlongX);
            _db.Aisles.Add(aisle);

            for (var s = 0; s < DemoLayout.ShelfXMm.Count; s++)
            {
                var shelfCode = $"{aisleCode}-{s + 1:D2}";
                var shelfX = DemoLayout.ShelfXMm[s];
                var shelf = new Shelf(aisle.Id, shelfCode, new Position(shelfX, y + 200, 0),
                    DemoLayout.ShelfWidthMm, DemoLayout.ShelfDepthMm, DemoLayout.ShelfHeightMm);
                _db.Shelves.Add(shelf);

                for (var b = 0; b < DemoLayout.BinsPerShelf; b++)
                {
                    var code = $"{shelfCode}-{b + 1:D2}";
                    var location = new StorageLocation(shelf.Id, code,
                        new Position(shelfX + b * DemoLayout.BinPitchMm, y + 200, 500),
                        DemoLayout.BinWidthMm, DemoLayout.BinDepthMm, DemoLayout.BinHeightMm, DemoLayout.BinMaxWeightGrams);
                    _db.StorageLocations.Add(location);
                    _bins.Add(new Bin { Id = location.Id, Code = code, Entity = location, ZoneCode = zoneCode });
                    _binEntities[location.Id] = location;
                }
            }
        }

        foreach (var (points, thickness, label) in DemoLayout.Walls)
        {
            var wall = new Wall(warehouse.Id, points.Select(p => new Position(p[0], p[1], 0)), thickness, label);
            _db.Walls.Add(wall);
            _walls.Add(wall);
        }

        foreach (var (label, x, y, type) in DemoLayout.PickPoints)
        {
            var point = new PickPoint(warehouse.Id, label, new Position(x, y, 0), type);
            _db.PickPoints.Add(point);
            _pickPoints.Add(point);
        }

        foreach (var (name, levels, w, d, h, maxWeight) in DemoLayout.Carts)
        {
            var cart = new PickCartConfig(name, levels, w, d, h, maxWeight);
            _db.PickCartConfigs.Add(cart);
            _cartIds.Add(cart.Id);
        }
    }

    private void BuildArticles()
    {
        // Lagerplätze verteilen: Hot-Pick-Plätze (Gang A1/A2, jeweils erster Platz eines Regals), Standardplätze (Rest der Zone A),
        // Reserveplätze (Zone B). Die Reihenfolge ist fest (Katalogreihenfolge), damit die Verteilung reproduzierbar bleibt.
        var hotBins = _bins.Where(b => b.ZoneCode == "Z-A" && (b.Code.StartsWith("A1-") || b.Code.StartsWith("A2-")) && b.Code.EndsWith("-01")).ToList();
        var standardBins = _bins.Where(b => b.ZoneCode == "Z-A" && !hotBins.Contains(b)).ToList();
        var reserveBins = _bins.Where(b => b.ZoneCode == "Z-B").ToList();
        foreach (var reserve in reserveBins) reserve.Entity.SetBinType(BinType.Reserve, 0);

        int hotIndex = 0, standardIndex = 0, reserveIndex = 0;

        foreach (var spec in DemoCatalog.Articles.Where(a => !a.IsBundle))
        {
            var article = CreateArticle(spec);
            var art = new Art { Spec = spec, Id = article.Id, Entity = article };

            switch (spec.Placement)
            {
                case DemoPlacement.Hot:
                    art.Primary = hotBins[hotIndex++];
                    art.Primary.Entity.SetBinType(BinType.HotPick, spec.ReplenishmentThreshold);
                    art.Reserve = reserveBins[reserveIndex++];
                    break;
                case DemoPlacement.Reserve:
                    art.Primary = reserveBins[reserveIndex++];
                    break;
                default:
                    art.Primary = standardBins[standardIndex++ % standardBins.Count];
                    break;
            }

            Register(art);
        }

        foreach (var spec in DemoCatalog.Articles.Where(a => a.IsBundle))
        {
            var article = CreateArticle(spec);
            article.ReplaceBundleComponents(spec.Components.Select(c => (_arts[c.Sku].Id, c.Quantity)));
            Register(new Art { Spec = spec, Id = article.Id, Entity = article });
        }

        // Alternativ-SKUs erst jetzt: sie verweisen auf andere Artikel des Katalogs (alle sind angelegt).
        foreach (var art in _artList.Where(a => a.Spec.AlternativeSkus.Count > 0))
            art.Entity.SetAlternatives(art.Spec.AlternativeSkus);

        _orderable.AddRange(_artList.Where(a => a.Spec.Demand != DemoDemand.None));

        void Register(Art art)
        {
            _artList.Add(art);
            _arts[art.Spec.Sku] = art;
        }
    }

    private Article CreateArticle(DemoArticleSpec spec)
    {
        var stacking = spec.Stackable
            ? new StackingInfo(true, StackingAxis.Z, Math.Max(1, spec.HeightMm - 3), 10)
            : StackingInfo.NotStackable;
        var article = new Article(spec.Sku, spec.Name,
            new Dimensions(spec.LengthMm, spec.WidthMm, spec.HeightMm), spec.WeightGrams, stacking, spec.Description);
        article.SetPurchasing(_supplierIds[spec.SupplierIndex], spec.PriceCents);
        article.SetStockThresholds(spec.MinStock, spec.ReorderPoint, spec.MaxStock);
        if (spec.Gtin is not null) article.SetGtin(spec.Gtin);
        if (spec.SeasonFromDays is not null || spec.SeasonUntilDays is not null)
            article.SetSeasonWindow(
                spec.SeasonFromDays is int from ? _today.AddDays(from) : null,
                spec.SeasonUntilDays is int until ? _today.AddDays(until) : null);
        _db.Articles.Add(article);
        return article;
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Vorgeschichte und Eröffnungsbestand
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>Altbestand vor dem Beobachtungsfenster: die Chargen mit MHD (auch die abgelaufene) und die Ladenhüter.</summary>
    private async Task PrehistoryAsync()
    {
        var byDay = DemoCatalog.OpeningLots
            .GroupBy(l => l.ReceivedDaysAgo)
            .OrderByDescending(g => g.Key)
            .ToList();

        // Ladenhüter ohne Charge: ein freier Wareneingang je Artikel, deutlich älter als 90 Tage.
        var dead = _artList.Where(a => a.Spec.Demand == DemoDemand.None && !a.Spec.IsBundle && a.Spec.ShelfLifeDays is null).ToList();
        var deadDay = 132;
        var events = new List<(int DaysAgo, Func<Task> Action)>();
        foreach (var group in byDay)
        {
            var lots = group.ToList();
            events.Add((group.Key, () => ReceiveFreeAsync(
                At(_today.AddDays(-group.Key), 8, 30), "Altbestand-Übernahme (Chargen)",
                lots.Select(l => (_arts[l.Sku], (int?)l.Quantity, (string?)l.LotNumber, (DateTime?)_today.AddDays(l.ExpiryInDays))).ToList())));
        }
        foreach (var art in dead)
        {
            var qty = Math.Max(1, art.Spec.MaxStock * 3 / 4);
            var daysAgo = deadDay;
            deadDay -= 7;
            events.Add((daysAgo, () => ReceiveFreeAsync(
                At(_today.AddDays(-daysAgo), 9), "Altbestand-Übernahme",
                new List<(Art, int?, string?, DateTime?)> { (art, qty, null, null) })));
        }

        foreach (var (_, action) in events.OrderByDescending(e => e.DaysAgo))
            await action();
    }

    /// <summary>Freier Wareneingang (ohne Bestellung) mit Buchung; Zeilen: Artikel, Menge, Charge, MHD.</summary>
    private async Task ReceiveFreeAsync(DateTime t, string note, List<(Art Art, int? Quantity, string? Lot, DateTime? Expiry)> lines)
    {
        var number = $"WE-ALT-{t:yyyyMMdd}";
        var shipment = new InboundShipment(number, "Altbestand", note);
        foreach (var (art, qty, lot, expiry) in lines)
            shipment.AddLine(art.Id, art.ReceiveBin.Id, qty ?? 1, lot, expiry, unitCostCents: art.Spec.PriceCents);
        _db.InboundShipments.Add(shipment);
        Stamp(t);

        shipment.MarkReceived();
        foreach (var line in shipment.Lines)
        {
            var art = _artList.First(a => a.Id == line.ArticleId);
            await StockBooking.BookAsync(_stockRepo, _moveRepo, line.ArticleId, line.TargetBinId, +line.Quantity,
                StockMovementReason.Inbound, "InboundShipment", shipment.Id, line.LotNumber, line.ExpiryDate, art.Spec.PriceCents, ct: _ct);
            art.OnHand += line.Quantity;
        }
        Stamp(t.AddMinutes(25));
        await SaveAsync(t.AddMinutes(25), "receiver");
    }

    /// <summary>
    /// Eröffnungsbestand am ersten Tag: je Lieferant eine Bestellung (versendet, geliefert) mit Wareneingang. Hot-Pick-Artikel
    /// gehen mit der Nachschubmenge in den Hot-Pick-Platz und mit dem Rest in den Reserveplatz (zwei Zeilen zur selben Bestellzeile).
    /// </summary>
    private async Task OpeningReceiptsAsync()
    {
        var day = DayOffset(-_historyDays);
        var lotArticles = DemoCatalog.OpeningLots.Select(l => l.Sku).ToHashSet(StringComparer.OrdinalIgnoreCase);

        for (var s = 0; s < DemoCatalog.Suppliers.Count; s++)
        {
            var lines = new List<(Art Art, int Qty)>();
            foreach (var art in _artList.Where(a => a.Spec.SupplierIndex == s && !a.Spec.IsBundle && a.Spec.Demand != DemoDemand.None
                                                    && !lotArticles.Contains(a.Spec.Sku)))
            {
                var qty = RoundUp(Math.Max(1, art.Spec.MaxStock * 3 / 4), art.Spec.OrderMultiple);
                lines.Add((art, qty));
            }
            if (lines.Count == 0) continue;

            var tOrder = At(day.AddDays(-6), 8);
            var po = await CreatePoAsync(s, lines, tOrder, day, send: true);

            var tArrive = At(day, 7, 30 + s * 5);
            var shipment = new InboundShipment($"WE-{po.Number}", po.Number, "Eröffnungsbestand", po.Id);
            foreach (var line in po.Lines)
            {
                var art = line.Art;
                var (lot, expiry) = NewLot(art, day);
                if (art.Reserve is not null)
                {
                    var hotQty = Math.Min(line.Ordered - 1, art.Spec.ReplenishmentThreshold * 2);
                    shipment.AddLine(art.Id, art.Primary.Id, hotQty, lot, expiry, line.LineId, line.PriceCents);
                    shipment.AddLine(art.Id, art.Reserve.Id, line.Ordered - hotQty, lot, expiry, line.LineId, line.PriceCents);
                }
                else
                {
                    shipment.AddLine(art.Id, art.Primary.Id, line.Ordered, lot, expiry, line.LineId, line.PriceCents);
                }
            }
            await BookShipmentAsync(shipment, po, tArrive, tArrive.AddMinutes(40));
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Einkauf und Wareneingang
    // ------------------------------------------------------------------------------------------------------------------

    private static int RoundUp(int value, int multiple) => multiple <= 1 ? value : (value + multiple - 1) / multiple * multiple;

    /// <summary>Charge und MHD einer Lieferung: nur für Ware mit Restlaufzeit; je Artikel und Liefertag eine Charge mit festem MHD.</summary>
    private (string? Lot, DateTime? Expiry) NewLot(Art art, DateTime day)
    {
        if (art.Spec.ShelfLifeDays is not int life) return (null, null);
        var lot = $"L{day:yyMMdd}-{art.Spec.Sku[^4..]}";
        return (lot, day.Date.AddDays(life - 45));
    }

    private async Task<SimPo> CreatePoAsync(int supplierIndex, List<(Art Art, int Qty)> lines, DateTime tCreate, DateTime? expected, bool send)
    {
        var seq = await NumberSequences.NextAsync(_db, NumberSequences.PurchaseOrder, _ct);
        var number = $"PO-{tCreate:yyyyMMdd}-{seq:D5}";
        var po = new PurchaseOrder(number, _supplierIds[supplierIndex], "EUR", expected,
            send ? null : "Entwurf, noch nicht an den Lieferanten gesendet.");
        var sim = new SimPo { Id = po.Id, Number = number, SupplierIndex = supplierIndex };
        foreach (var (art, qty) in lines)
        {
            var line = po.AddLine(art.Id, art.Spec.Sku, qty, art.Spec.PriceCents);
            sim.Lines.Add(new PoLine { LineId = line.Id, Art = art, Ordered = qty, Remaining = qty, PriceCents = art.Spec.PriceCents });
        }
        _db.PurchaseOrders.Add(po);
        Stamp(tCreate);

        var last = tCreate;
        if (send)
        {
            last = tCreate.AddMinutes(20);
            po.MarkSent(expected);
            Stamp(last);
        }
        await SaveAsync(last, "manager");

        foreach (var line in sim.Lines) line.Art.OpenPoQty += send ? line.Ordered : 0;
        return sim;
    }

    /// <summary>Bucht einen (noch nicht gespeicherten) Wareneingang samt Bestellfortschreibung: Entwurf um <paramref name="tCreate"/>, Buchung um <paramref name="tReceive"/>.</summary>
    private async Task BookShipmentAsync(InboundShipment shipment, SimPo? sim, DateTime tCreate, DateTime tReceive)
    {
        _db.InboundShipments.Add(shipment);
        Stamp(tCreate);

        PurchaseOrder? po = null;
        if (sim is not null)
            po = await _db.PurchaseOrders.Include(p => p.Lines).FirstAsync(p => p.Id == sim.Id, _ct);

        shipment.MarkReceived();
        foreach (var line in shipment.Lines)
        {
            var art = _artList.First(a => a.Id == line.ArticleId);
            await StockBooking.BookAsync(_stockRepo, _moveRepo, line.ArticleId, line.TargetBinId, +line.Quantity,
                StockMovementReason.Inbound, "InboundShipment", shipment.Id, line.LotNumber, line.ExpiryDate,
                line.UnitCostCents ?? art.Spec.PriceCents, ct: _ct);
            art.OnHand += line.Quantity;
            if (sim is not null) art.OpenPoQty -= line.Quantity;
        }

        if (po is not null && sim is not null)
        {
            foreach (var group in shipment.Lines.Where(l => l.PurchaseOrderLineId is not null).GroupBy(l => l.PurchaseOrderLineId!.Value))
            {
                var qty = group.Sum(l => l.Quantity);
                po.ReceiveLine(group.Key, qty);
                sim.Lines.First(l => l.LineId == group.Key).Remaining -= qty;
            }
            sim.Deliveries++;
        }

        Stamp(tReceive);
        await SaveAsync(tReceive, "receiver");
    }

    /// <summary>Zweimal pro Woche (Mo/Do): Artikel unter dem Meldebestand werden je Lieferant bis zum Höchstbestand nachbestellt.</summary>
    private async Task ReviewReordersAsync(DateTime day)
    {
        for (var s = 0; s < DemoCatalog.Suppliers.Count; s++)
        {
            var supplier = DemoCatalog.Suppliers[s];
            var arrival = NextBusinessDay(day.AddDays(supplier.LeadTimeDays));
            var lines = new List<(Art, int)>();
            foreach (var art in _artList.Where(a => a.Spec.SupplierIndex == s && !a.Spec.IsBundle && a.Spec.Demand != DemoDemand.None))
            {
                if (!art.Entity.IsCurrentlyActive(arrival)) continue;
                var position = art.OnHand + art.OpenPoQty;
                if (position >= art.Spec.ReorderPoint) continue;
                var qty = RoundUp(art.Spec.MaxStock - position, art.Spec.OrderMultiple);
                if (qty > 0) lines.Add((art, qty));
            }
            if (lines.Count == 0) continue;

            await OrderAsync(s, lines, At(day, 8, 10 + s * 7), arrival, allowSplitAndDelay: true);
        }
    }

    /// <summary>Legt eine versendete Bestellung an und plant ihre Lieferung(en): pünktlich, verspätet, geteilt oder storniert.</summary>
    private async Task OrderAsync(int supplierIndex, List<(Art Art, int Qty)> lines, DateTime t, DateTime expectedArrival, bool allowSplitAndDelay)
    {
        var po = await CreatePoAsync(supplierIndex, lines, t, expectedArrival, send: true);

        var roll = _rng.Next(100);
        var arrival = expectedArrival;
        if (allowSplitAndDelay)
            arrival = NextBusinessDay(expectedArrival.AddDays(roll < 60 ? 0 : roll < 85 ? 1 : 2));

        var cancel = allowSplitAndDelay && _rng.Next(100) < 4;
        var split = allowSplitAndDelay && !cancel && _rng.Next(100) < 22;

        if (cancel)
        {
            _deliveries.Add(new Delivery { Po = po, Day = NextBusinessDay(arrival.AddDays(-1)), CancelInstead = true });
            return;
        }

        if (split)
        {
            _deliveries.Add(new Delivery { Po = po, Day = arrival, Fraction = 0.6, Final = false });
            _deliveries.Add(new Delivery { Po = po, Day = NextBusinessDay(arrival.AddDays(2)), Final = true });
        }
        else
        {
            _deliveries.Add(new Delivery { Po = po, Day = arrival, Final = true });
        }
    }

    private static DateTime NextBusinessDay(DateTime day)
    {
        while (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) day = day.AddDays(1);
        return day.Date;
    }

    private async Task ReceiveDeliveriesAsync(DateTime day)
    {
        var due = _deliveries.Where(d => d.Day.Date == day.Date).OrderBy(d => d.Po.Number, StringComparer.Ordinal).ThenBy(d => d.Final).ToList();
        var index = 0;
        foreach (var delivery in due)
        {
            _deliveries.Remove(delivery);
            var t = At(day, 7, 40 + index * 12);
            index++;
            var po = delivery.Po;

            if (delivery.CancelInstead)
            {
                await CancelPoAsync(po, t);
                continue;
            }

            var number = po.Deliveries == 0 ? $"WE-{po.Number}" : $"WE-{po.Number}-{po.Deliveries + 1}";
            var shipment = new InboundShipment(number, po.Number, null, po.Id);
            foreach (var line in po.Lines)
            {
                var qty = delivery.Final ? line.Remaining : Math.Min(line.Remaining, (int)Math.Round(line.Ordered * delivery.Fraction));
                if (qty <= 0) continue;
                var (lot, expiry) = NewLot(line.Art, day);
                shipment.AddLine(line.Art.Id, line.Art.ReceiveBin.Id, qty, lot, expiry, line.LineId, line.PriceCents);
            }
            if (shipment.Lines.Count == 0) continue;
            await BookShipmentAsync(shipment, po, t, t.AddMinutes(35));
        }
    }

    /// <summary>Der Lieferant storniert die Bestellung vor der Lieferung (nichts wurde empfangen): die offene Menge entfällt.</summary>
    private async Task CancelPoAsync(SimPo sim, DateTime t)
    {
        var po = await _db.PurchaseOrders.Include(p => p.Lines).FirstAsync(p => p.Id == sim.Id, _ct);
        po.Cancel();
        Stamp(t);
        foreach (var line in sim.Lines)
        {
            line.Art.OpenPoQty -= line.Remaining;
            line.Remaining = 0;
        }
        await SaveAsync(t, "manager");
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Bestellungen
    // ------------------------------------------------------------------------------------------------------------------

    private Art? PickArticle(DateTime t, HashSet<Art> used)
    {
        var candidates = _orderable.Where(a => !used.Contains(a) && a.Entity.IsCurrentlyActive(t)).ToList();
        var total = candidates.Sum(a => (int)a.Spec.Demand);
        if (total == 0) return null;
        var r = _rng.Next(total);
        foreach (var a in candidates)
        {
            r -= (int)a.Spec.Demand;
            if (r < 0) return a;
        }
        return candidates[^1];
    }

    private bool IsAvailable(Art art, int qty) =>
        art.Spec.IsBundle
            ? art.Spec.Components.All(c => { var comp = _arts[c.Sku]; return comp.OnHand - comp.Committed >= c.Quantity * qty; })
            : art.OnHand - art.Committed >= qty;

    private void Commit(SimOrder order, int sign)
    {
        foreach (var line in order.Lines)
        {
            if (line.Art.Spec.IsBundle)
                foreach (var c in line.Art.Spec.Components) _arts[c.Sku].Committed += sign * c.Quantity * line.Quantity;
            else
                line.Art.Committed += sign * line.Quantity;
        }
    }

    private async Task CreateOrdersAsync(DateTime day)
    {
        var baseCount = day.DayOfWeek switch
        {
            DayOfWeek.Monday => 8,
            DayOfWeek.Tuesday => 7,
            DayOfWeek.Wednesday => 7,
            DayOfWeek.Thursday => 7,
            DayOfWeek.Friday => 6,
            DayOfWeek.Saturday => 2,
            _ => 0,
        };
        var count = baseCount == 0 ? 0 : Math.Max(0, baseCount + Rnd(-1, 1));
        var minutes = Enumerable.Range(0, count).Select(_ => Rnd(0, 7 * 60 + 30)).OrderBy(m => m).ToList();

        foreach (var m in minutes)
            await CreateOrderAsync(At(day, 8, 0).AddMinutes(m), allowCancel: true, saveNow: false);
        if (minutes.Count > 0)
            await SaveAsync(At(day, 8, 0).AddMinutes(minutes[^1]), "system");
    }

    /// <summary>Legt eine Bestellung zufälliger Positionen an (nur Artikel mit ausreichendem freiem Bestand). Null, wenn keine Position zustande kam.</summary>
    private async Task<SimOrder?> CreateOrderAsync(DateTime t, bool allowCancel, bool saveNow, int? priority = null, DateTime? due = null, int? forcedLineCount = null, string actor = "system")
    {
        var lineCount = forcedLineCount ?? PickLineCount();
        var used = new HashSet<Art>();
        var lines = new List<SimLine>();
        var entities = new List<OrderLine>();
        for (var attempt = 0; attempt < 30 && lines.Count < lineCount; attempt++)
        {
            var art = PickArticle(t, used);
            if (art is null) break;
            used.Add(art);
            var spec = art.Spec;
            var steps = (spec.QuantityMax - spec.QuantityMin) / spec.QuantityStep;
            var qty = spec.QuantityMin + spec.QuantityStep * Rnd(0, steps);
            if (!IsAvailable(art, qty)) continue;
            var entity = new OrderLine(art.Id, qty);
            entities.Add(entity);
            lines.Add(new SimLine(art, qty, entity.Id));
        }
        if (lines.Count == 0) return null;

        var counter = _orderCounter.GetValueOrDefault(t.Date) + 1;
        _orderCounter[t.Date] = counter;
        var number = $"ORD-{t:yyyyMMdd}-{counter:D3}";

        // Herkunft: Kunde aus dem Stamm (Handel), Webshop (API, mit externer Referenz) oder Telefonbestellung ohne Stammkunde.
        var kind = _rng.Next(100);
        Order order;
        var sim = new SimOrder { Number = number, CreatedAt = t, Lines = lines };
        if (kind < 55)
        {
            var (customerId, name, addresses) = PickCustomer();
            order = new Order(number, OrderSource.Manual, name, entities);
            order.LinkCustomer(customerId, addresses[_rng.Next(addresses.Count)]);
            sim.CustomerId = customerId;
            sim.CustomerName = name;
            sim.CustomerReference = name;
        }
        else if (kind < 85)
        {
            var shop = ++_shopNumber;
            order = new Order(number, OrderSource.Api, $"Webshop-Bestellung {shop}", entities);
            order.SetExternalReference($"SHOP-{shop}");
            sim.CustomerReference = $"Webshop-Bestellung {shop}";
        }
        else
        {
            order = new Order(number, OrderSource.Manual, "Telefonbestellung", entities);
            sim.CustomerReference = "Telefonbestellung";
        }

        var prio = priority ?? (_rng.Next(100) < 12 ? Rnd(1, 3) : 0);
        var dueDate = due ?? (_rng.Next(100) < 25 ? t.Date.AddDays(Rnd(1, 3)) : (DateTime?)null);
        order.SetPlanning(prio, dueDate);
        sim.Priority = prio;
        sim.Due = dueDate;
        sim.Id = order.Id;

        if (allowCancel && _rng.Next(100) < 6)
            sim.CancelAt = t.AddMinutes(Rnd(15, 180));

        _db.Orders.Add(order);
        Stamp(t);
        Commit(sim, +1);
        _waiting.Add(sim);

        if (saveNow) await SaveAsync(t, actor);
        return sim;
    }

    private int PickLineCount()
    {
        var r = _rng.Next(100);
        return r < 30 ? 1 : r < 60 ? 2 : r < 82 ? 3 : r < 94 ? 4 : 5;
    }

    private (Guid Id, string Name, List<Guid> ShippingAddressIds) PickCustomer()
    {
        // Gewichte: Beispiel Bau 30, Testwerkstatt 25, Demo Handwerk 20, Hausverwaltung 10, Schreinerei 15.
        var r = _rng.Next(100);
        var index = r < 30 ? 0 : r < 55 ? 1 : r < 75 ? 2 : r < 85 ? 3 : 4;
        return _customers[index];
    }

    private async Task CancelOrderAsync(SimOrder sim, DateTime t)
    {
        var order = await _db.Orders.Include(o => o.Lines).FirstAsync(o => o.Id == sim.Id, _ct);
        order.Cancel();
        Stamp(t);
        Commit(sim, -1);
        _waiting.Remove(sim);
        await SaveAsync(t, sim.CustomerId is null ? "system" : "manager");
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Kommissionierung und Packen
    // ------------------------------------------------------------------------------------------------------------------

    private string NextPicker() => DemoCatalog.Pickers[_rng.Next(DemoCatalog.Pickers.Count)];

    private Art ArtById(Guid id) => _artList.First(a => a.Id == id);

    private string BinCode(Guid id) => _bins.First(b => b.Id == id).Code;

    /// <summary>Ein Kommissionierlauf: offene Bestellungen werden zu Picklisten (teils als Welle), gepickt und gepackt.</summary>
    private async Task RunBatchAsync(DateTime day, int hour, int minute)
    {
        var tb = At(day, hour, minute);

        // Stornos: der Kunde zieht zurück, oder die Bestellung ist seit Tagen nicht lieferbar.
        foreach (var order in _waiting.OrderBy(o => o.CreatedAt).ToList())
        {
            if (order.CancelAt is DateTime cancelAt && cancelAt <= tb)
                await CancelOrderAsync(order, cancelAt);
            else if ((tb - order.CreatedAt).TotalDays > 4)
                await CancelOrderAsync(order, tb.AddMinutes(-30));
        }

        var eligible = _waiting.Where(o => o.CreatedAt <= tb.AddMinutes(-10))
            .OrderByDescending(o => o.Priority).ThenBy(o => o.Due ?? DateTime.MaxValue).ThenBy(o => o.CreatedAt).ToList();
        if (eligible.Count == 0) return;

        var daysBack = (_today - day).Days;

        // Einmalig: eine verworfene Welle und eine stornierte Pickliste (Zustände, die es im Betrieb auch gibt).
        if (!_scriptedDone.Contains("wave-cancel") && daysBack <= 30 && eligible.Count >= 2)
        {
            _scriptedDone.Add("wave-cancel");
            await CreateCancelledWaveAsync(eligible.Take(2).ToList(), tb.AddMinutes(-20));
        }
        if (!_scriptedDone.Contains("picklist-cancel") && daysBack <= 26 && eligible.Count >= 2 && hour >= 14)
        {
            _scriptedDone.Add("picklist-cancel");
            await CreateCancelledPickListAsync(eligible.Take(2).ToList(), tb.AddMinutes(-5));
        }

        var remaining = new List<SimOrder>(eligible);
        var chunks = new List<(List<SimOrder> Orders, bool Wave)>();
        if (remaining.Count >= 4 && hour < 12 && _rng.Next(100) < 30)
        {
            var first = remaining.Take(8).ToList();
            chunks.Add((first, true));
            remaining.RemoveRange(0, first.Count);
        }
        while (remaining.Count > 0)
        {
            var size = Math.Min(remaining.Count, Rnd(2, 4));
            chunks.Add((remaining.Take(size).ToList(), false));
            remaining.RemoveRange(0, size);
        }

        for (var j = 0; j < chunks.Count; j++)
        {
            var (orders, asWave) = chunks[j];
            var tCreate = tb.AddMinutes(j * 6 + Rnd(0, 3));
            var picker = NextPicker();
            var useCart = orders.Count >= 3 && _rng.Next(100) < 50;

            var list = await CreatePickListAsync(orders, tCreate, PickStage.Picked, picker, tCreate.AddMinutes(Rnd(3, 9)), useCart, asWave);
            if (list is not null)
            {
                await PackAsync(list, list.PickedAt.AddMinutes(Rnd(10, 25)), "packer");
                continue;
            }

            // Nicht alle zusammen lieferbar: jede Bestellung einzeln versuchen, der Rest bleibt New.
            foreach (var order in orders)
            {
                var single = await CreatePickListAsync(new List<SimOrder> { order }, tCreate.AddMinutes(1), PickStage.Picked, picker,
                    tCreate.AddMinutes(Rnd(4, 9)), false, false);
                if (single is not null)
                    await PackAsync(single, single.PickedAt.AddMinutes(Rnd(10, 25)), "packer");
            }
        }
    }

    /// <summary>
    /// Legt eine Pickliste an (Bestandsallokation mit <see cref="StockAllocator"/>, Route mit dem Wegoptimierer) und führt sie bis
    /// <paramref name="upTo"/> weiter. Null, wenn der Bestand für die Bestellungen nicht reicht (nichts wurde angelegt).
    /// </summary>
    private async Task<SimPickList?> CreatePickListAsync(
        List<SimOrder> orders, DateTime tCreate, PickStage upTo, string? picker, DateTime? tAssign,
        bool useCart, bool asWave, DateTime? tWave = null)
    {
        var ids = orders.Select(o => o.Id).ToList();

        var requests = new List<PickRequest>();
        foreach (var sim in orders)
        foreach (var line in sim.Lines)
        {
            if (line.Art.Spec.IsBundle)
            {
                foreach (var c in line.Art.Spec.Components)
                    requests.Add(new PickRequest(sim.Id, line.LineId, _arts[c.Sku].Id, c.Quantity * line.Quantity));
            }
            else
            {
                requests.Add(new PickRequest(sim.Id, line.LineId, line.Art.Id, line.Quantity));
            }
        }

        var articleIds = requests.Select(r => r.ArticleId).Distinct().ToList();
        var stock = await _db.StockItems.AsNoTracking().Where(s => articleIds.Contains(s.ArticleId) && s.Quantity > 0).ToListAsync(_ct);
        IReadOnlyList<PickCandidate> candidates;
        try
        {
            candidates = new StockAllocator(stock, _binEntities, tCreate.Date).Allocate(requests);
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var start = _pickPoints.First(p => p.Type is PickPointType.Start or PickPointType.Both).Position;
        Position? end = asWave || _rng.Next(100) < 40 ? _pickPoints.First(p => p.Type == PickPointType.End).Position : null;
        var route = _router.Optimize(candidates, _binEntities, start, _walls, end);

        var entities = await _db.Orders.Include(o => o.Lines).Where(o => ids.Contains(o.Id)).ToListAsync(_ct);

        var sequence = await NumberSequences.NextAsync(_db, NumberSequences.PickList, _ct);
        var number = $"PL-{tCreate:yyyyMMdd}-{sequence:D5}";
        var items = route.Ordered
            .Select((c, i) => new PickItem(i + 1, c.OrderId, c.OrderLineId, c.ArticleId, c.StorageLocationId, c.Quantity))
            .ToList();
        var pl = new PickList(number, items, route.TotalDistanceMm, route.Waypoints);
        if (useCart) pl.AssignToCart(_cartIds[_rng.Next(_cartIds.Count)]);

        PickWave? wave = null;
        if (asWave)
        {
            var waveSequence = await NumberSequences.NextAsync(_db, NumberSequences.PickWave, _ct);
            var created = tWave ?? tCreate.AddMinutes(-25);
            wave = new PickWave($"W-{created:yyyyMMdd}-{waveSequence:D4}", "Welle vor dem Versand-Cutoff", tCreate.AddHours(4));
            wave.AddOrders(ids);
            _db.PickWaves.Add(wave);
            Stamp(created);
        }

        _db.PickLists.Add(pl);
        foreach (var order in entities) order.MarkPicking();
        wave?.MarkReleased(new[] { pl.Id });
        Stamp(tCreate);

        var sim2 = new SimPickList { Id = pl.Id, Number = number, Orders = orders, WaveId = wave?.Id, DistanceMm = route.TotalDistanceMm };
        var last = tCreate;

        if (upTo >= PickStage.InProgress)
        {
            pl.Assign(picker ?? NextPicker());
            last = tAssign ?? tCreate.AddMinutes(5);
            Stamp(last);
        }

        if (upTo >= PickStage.Picked)
        {
            // Dauer: Rüstzeit, je Position etwas Handgriff, dazu der Laufweg (rund 45 m pro Minute).
            var minutes = 8 + items.Count * 2 + route.TotalDistanceMm / 1000 / 45 + Rnd(0, 8);
            var tPicked = last.AddMinutes(minutes);
            foreach (var item in items) item.MarkPicked();
            pl.MarkPickingComplete();
            foreach (var order in entities) order.MarkPicked();
            Stamp(tPicked);
            last = tPicked;
            sim2.PickedAt = tPicked;
        }
        else if (upTo == PickStage.InProgress)
        {
            // Der Picker ist unterwegs: ein Teil der Positionen ist schon abgehakt.
            for (var i = 0; i < items.Count; i++)
                if (i % 5 < 2) items[i].MarkPicked();
            Stamp(last);
        }

        await SaveAsync(last, upTo >= PickStage.InProgress ? (pl.AssignedTo ?? "system") : "system");

        foreach (var order in orders)
        {
            order.PickListId = pl.Id;
            _waiting.Remove(order);
        }
        return sim2;
    }

    /// <summary>Packt die Pickliste: bucht den Bestand (FEFO je Lagerplatz), bestätigt die Positionen und setzt die Bestellungen auf Packed.</summary>
    private async Task PackAsync(SimPickList sim, DateTime tPack, string actor)
    {
        var pl = await _db.PickLists.Include(p => p.Items).FirstAsync(p => p.Id == sim.Id, _ct);
        var items = pl.Items.OrderBy(i => i.SequenceNumber).ToList();

        // Kurz-Pick (rund 3 % der Positionen, eine Einheit weniger): die Entscheidung hängt nur an Bestellnummer und Artikel, nicht an der
        // Reihenfolge der Positionen (die Route bricht Gleichstände über Guids) - so bleibt der Datensatz reproduzierbar.
        var numberOfLine = sim.Orders.SelectMany(o => o.Lines.Select(l => (l.LineId, o.Number))).ToDictionary(x => x.LineId, x => x.Number);
        var actual = new Dictionary<Guid, int>();
        foreach (var item in items)
        {
            var quantity = item.Quantity;
            if (quantity > 1 && StableHash($"{numberOfLine[item.OrderLineId]}|{ArtById(item.ArticleId).Spec.Sku}") % 100 < 3) quantity--;
            actual[item.Id] = quantity;
        }

        var groups = items
            .GroupBy(i => (i.ArticleId, i.StorageLocationId))
            .Select(g => (Article: ArtById(g.Key.ArticleId), Bin: g.Key.StorageLocationId, Quantity: g.Sum(i => actual[i.Id])))
            .Where(g => g.Quantity > 0)
            .OrderBy(g => g.Article.Spec.Sku, StringComparer.Ordinal).ThenBy(g => BinCode(g.Bin), StringComparer.Ordinal)
            .ToList();
        foreach (var (art, bin, quantity) in groups)
        {
            await StockBooking.BookOutAsync(_stockRepo, _moveRepo, art.Id, bin, quantity, StockMovementReason.Pick,
                "PickList", pl.Id, art.Spec.PriceCents, ct: _ct);
            art.OnHand -= quantity;
        }

        foreach (var item in items) item.ConfirmPacked(actual[item.Id]);
        pl.MarkPacked();

        var orderIds = sim.Orders.Select(o => o.Id).ToList();
        foreach (var order in await _db.Orders.Include(o => o.Lines).Where(o => orderIds.Contains(o.Id)).ToListAsync(_ct))
        {
            if (order.Status == OrderStatus.Picking) order.MarkPicked();
            if (order.Status == OrderStatus.Picked) order.MarkPacked();
        }

        if (sim.WaveId is Guid waveId)
            (await _db.PickWaves.FirstAsync(w => w.Id == waveId, _ct)).MarkCompleted();

        Stamp(tPack);
        await SaveAsync(tPack, actor);

        foreach (var order in sim.Orders)
        {
            Commit(order, -1);
            order.PackedAt = tPack;
            _packed.Add(order);
        }
    }

    /// <summary>Stabiler Hash (FNV-1a): anders als string.GetHashCode in jedem Prozess gleich.</summary>
    private static uint StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(text))
            hash = (hash ^ b) * 16777619u;
        return hash;
    }

    private async Task CreateCancelledWaveAsync(List<SimOrder> orders, DateTime t)
    {
        var sequence = await NumberSequences.NextAsync(_db, NumberSequences.PickWave, _ct);
        var wave = new PickWave($"W-{t:yyyyMMdd}-{sequence:D4}", "Zusammenstellung verworfen (Kunde hat umdisponiert)", t.AddHours(1));
        wave.AddOrders(orders.Select(o => o.Id));
        _db.PickWaves.Add(wave);
        Stamp(t);
        wave.Cancel();
        Stamp(t.AddMinutes(12));
        await SaveAsync(t.AddMinutes(12), "manager");
    }

    private async Task CreateOpenWaveAsync(List<SimOrder> orders, DateTime t)
    {
        var sequence = await NumberSequences.NextAsync(_db, NumberSequences.PickWave, _ct);
        var wave = new PickWave($"W-{t:yyyyMMdd}-{sequence:D4}", "Nachmittagswelle, noch nicht freigegeben", _now.AddHours(1));
        wave.AddOrders(orders.Select(o => o.Id));
        _db.PickWaves.Add(wave);
        Stamp(t);
        await SaveAsync(t, "manager");
    }

    /// <summary>Eine Pickliste wird angelegt und wieder storniert: die Bestellungen gehen zurück auf New und laufen im nächsten Lauf normal weiter.</summary>
    private async Task CreateCancelledPickListAsync(List<SimOrder> orders, DateTime t)
    {
        var list = await CreatePickListAsync(orders, t, PickStage.Pending, null, null, false, false);
        if (list is null) return;

        var pl = await _db.PickLists.Include(p => p.Items).FirstAsync(p => p.Id == list.Id, _ct);
        pl.Cancel();
        var orderIds = orders.Select(o => o.Id).ToList();
        foreach (var order in await _db.Orders.Where(o => orderIds.Contains(o.Id)).ToListAsync(_ct))
            order.ReleaseFromPicking();
        var tCancel = t.AddMinutes(9);
        Stamp(tCancel);
        await SaveAsync(tCancel, "manager");

        foreach (var order in orders)
        {
            order.PickListId = null;
            _waiting.Add(order);
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Versand
    // ------------------------------------------------------------------------------------------------------------------

    private enum ShipStage { Ready, Labeled, Shipped }

    private (int L, int W, int H, int Grams, int CostCents) ParcelFor(SimOrder sim)
    {
        long grams = 250;
        long volume = 0;
        foreach (var line in sim.Lines)
        {
            var parts = line.Art.Spec.IsBundle
                ? line.Art.Spec.Components.Select(c => (_arts[c.Sku].Spec, c.Quantity * line.Quantity))
                : new[] { (line.Art.Spec, line.Quantity) };
            foreach (var (spec, quantity) in parts)
            {
                grams += (long)spec.WeightGrams * quantity;
                volume += (long)spec.LengthMm * spec.WidthMm * spec.HeightMm * quantity;
            }
        }
        volume = volume * 13 / 10;

        var (l, w, h) = volume <= 9_000_000 ? (300, 200, 150)
            : volume <= 24_000_000 ? (400, 300, 200)
            : volume <= 72_000_000 ? (600, 400, 300)
            : (800, 600, 500);
        var g = (int)Math.Min(grams, 60_000);
        var cost = g <= 2_000 ? 490 : g <= 10_000 ? 690 : g <= 31_500 ? 990 : 1_990;
        return (l, w, h, g, cost);
    }

    /// <summary>
    /// Legt Sendungen für gepackte Bestellungen an (Ready), vergibt das Tracking (Labeled) und übergibt sie an den Carrier (Shipped;
    /// die Bestellung wird dann Shipped). Der Zielzustand steuert, wie weit die Sendung läuft.
    /// </summary>
    private async Task ShipOrdersAsync(List<SimOrder> orders, DateTime tCreate, DateTime? tLabel, DateTime? tShip, ShipStage stage)
    {
        if (orders.Count == 0) return;
        var ids = orders.Select(o => o.Id).ToList();
        var entities = (await _db.Orders.Where(o => ids.Contains(o.Id)).ToListAsync(_ct)).ToDictionary(o => o.Id);

        var last = tCreate;
        foreach (var sim in orders)
        {
            var parcel = ParcelFor(sim);
            var sequence = await NumberSequences.NextAsync(_db, NumberSequences.Shipment, _ct);
            var shipment = new Shipment($"SH-{tCreate:yyyyMMdd}-{sequence:D5}", sim.Id, sim.PickListId, "MANUAL");
            shipment.SetDimensions(parcel.L, parcel.W, parcel.H, parcel.Grams);
            _db.Shipments.Add(shipment);
            Stamp(tCreate);

            if (stage >= ShipStage.Labeled)
            {
                var tracking = $"DEMO-{++_trackingNumber}";
                shipment.AssignTracking(tracking, $"https://tracking.example.com/t/{tracking}", parcel.CostCents);
                last = tLabel ?? tCreate.AddMinutes(6);
                Stamp(last);
            }

            if (stage >= ShipStage.Shipped)
            {
                last = tShip ?? tCreate.AddMinutes(30);
                shipment.MarkShipped();
                entities[sim.Id].MarkShipped();
                Stamp(last);
                sim.ShippedAt = last;
                _packed.Remove(sim);
                _shipped.Add(sim);
                if (_rng.Next(100) < 88)
                    _pendingDeliveries.Add(new PendingDelivery(shipment.Id, NextBusinessDay(last.Date.AddDays(Rnd(1, 3)))));
            }
        }
        await SaveAsync(last, "packer");
    }

    private async Task ShipAsync(DateTime day, bool sameDayOnly)
    {
        foreach (var sim in _packed.Where(o => o.PackedAt <= At(day, 23, 59)).OrderBy(o => o.PackedAt).ToList())
        {
            var packedAt = sim.PackedAt!.Value;
            DateTime tShip;
            if (packedAt.Date < day.Date) tShip = At(day, 9, Rnd(0, 40));                       // Rückstand vom Vortag
            else if (packedAt <= At(day, 16, 0)) tShip = At(day, 16, 30 + Rnd(0, 15));         // Abholung am Nachmittag
            else if (sameDayOnly) tShip = packedAt.AddMinutes(45);
            else continue;                                                                       // bleibt bis morgen gepackt

            var tCreate = packedAt.AddMinutes(Rnd(8, 20));
            if (tCreate > tShip.AddMinutes(-15)) tCreate = tShip.AddMinutes(-15);
            if (tCreate < packedAt) tCreate = packedAt.AddMinutes(1);
            await ShipOrdersAsync(new List<SimOrder> { sim }, tCreate, tCreate.AddMinutes(Rnd(5, 12)), tShip, ShipStage.Shipped);
        }
    }

    private async Task DeliverAsync(DateTime day)
    {
        var due = _pendingDeliveries.Where(d => d.Day.Date == day.Date).ToList();
        if (due.Count == 0) return;
        var ids = due.Select(d => d.ShipmentId).ToList();
        var shipments = await _db.Shipments.Where(s => ids.Contains(s.Id)).ToListAsync(_ct);
        var last = At(day, 10);
        foreach (var shipment in shipments.OrderBy(s => s.ShipmentNumber, StringComparer.Ordinal))
        {
            shipment.MarkDelivered();
            last = At(day, 10, Rnd(0, 420));
            Stamp(last);
        }
        _pendingDeliveries.RemoveAll(d => d.Day.Date == day.Date);
        await SaveAsync(last, "system");
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Nachschub
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>Abends: Hot-Pick-Plätze unter der Schwelle bekommen eine Nachschub-Aufgabe aus dem Reserveplatz (wie der Scan im Betrieb).</summary>
    private async Task ScanReplenishmentAsync(DateTime day, DateTime t)
    {
        var hotArticles = _artList.Where(a => a.Reserve is not null).OrderBy(a => a.Primary.Code, StringComparer.Ordinal).ToList();
        var binIds = hotArticles.SelectMany(a => new[] { a.Primary.Id, a.Reserve!.Id }).ToList();
        var rows = await _db.StockItems.AsNoTracking().Where(s => binIds.Contains(s.StorageLocationId)).ToListAsync(_ct);

        var created = 0;
        foreach (var art in hotArticles)
        {
            var threshold = art.Primary.Entity.ReplenishmentThreshold;
            var total = rows.Where(r => r.ArticleId == art.Id && r.StorageLocationId == art.Primary.Id).Sum(r => r.Quantity);
            if (total >= threshold) continue;
            if (_openTasks.Any(o => o.Art == art)) continue;

            var reserveQuantity = rows.Where(r => r.ArticleId == art.Id && r.StorageLocationId == art.Reserve!.Id).Sum(r => r.Quantity);
            if (reserveQuantity <= 0) continue;
            var suggested = Math.Min(Math.Max(threshold * 2 - total, 1), reserveQuantity);

            var task = new ReplenishmentTask(art.Id, art.Spec.Sku, art.Reserve!.Id, art.Reserve.Code, art.Primary.Id, art.Primary.Code, suggested);
            _db.ReplenishmentTasks.Add(task);
            Stamp(t.AddMinutes(created));
            _openTasks.Add(new OpenTask { Id = task.Id, Art = art, Suggested = suggested, CreatedOn = day.Date });
            created++;
        }
        if (created > 0) await SaveAsync(t.AddMinutes(created), "system");
    }

    /// <summary>Am nächsten Morgen: die Picker füllen die Hot-Pick-Plätze auf (Umlagerung mit Charge/MHD); selten wird eine Aufgabe verworfen.</summary>
    private async Task CompleteReplenishmentsAsync(DateTime day)
    {
        var due = _openTasks.Where(o => o.CreatedOn < day.Date).OrderBy(o => o.Art.Spec.Sku, StringComparer.Ordinal).ToList();
        var index = 0;
        foreach (var open in due)
        {
            _openTasks.Remove(open);
            var t = At(day, 7, 5 + index * 3);
            index++;
            var art = open.Art;
            var task = await _db.ReplenishmentTasks.FirstAsync(x => x.Id == open.Id, _ct);
            var available = await _db.StockItems.Where(s => s.ArticleId == art.Id && s.StorageLocationId == art.Reserve!.Id).SumAsync(s => s.Quantity, _ct);
            var actual = Math.Min(open.Suggested, available);

            if (actual <= 0 || _rng.Next(100) < 3)
            {
                task.Cancel();
                Stamp(t);
                await SaveAsync(t, "manager");
                continue;
            }

            task.Complete(actual);
            var parts = await StockBooking.BookOutAsync(_stockRepo, _moveRepo, art.Id, art.Reserve!.Id, actual,
                StockMovementReason.ReplenishmentOut, "ReplenishmentTask", task.Id, art.Spec.PriceCents, ct: _ct);
            foreach (var part in parts)
                await StockBooking.BookAsync(_stockRepo, _moveRepo, art.Id, art.Primary.Id, +part.Quantity,
                    StockMovementReason.ReplenishmentIn, "ReplenishmentTask", task.Id, part.Row.LotNumber, part.Row.ExpiryDate,
                    art.Spec.PriceCents, ct: _ct);
            Stamp(t);
            await SaveAsync(t, NextPicker());
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Retouren
    // ------------------------------------------------------------------------------------------------------------------

    private enum ReturnKind { Sellable, BGrade, Defect, Destroy, Cancelled, DraftPending, DraftQcDone }

    private static readonly (int Offset, ReturnKind Kind)[] ReturnPlan =
    {
        (-52, ReturnKind.Sellable), (-47, ReturnKind.Sellable), (-41, ReturnKind.BGrade), (-33, ReturnKind.Sellable),
        (-29, ReturnKind.Defect), (-24, ReturnKind.Sellable), (-20, ReturnKind.Cancelled), (-17, ReturnKind.Destroy),
        (-14, ReturnKind.Sellable), (-9, ReturnKind.Sellable), (-6, ReturnKind.BGrade),
    };

    private async Task ReturnsAsync(DateTime day)
    {
        var daysBack = (_today - day).Days;
        foreach (var (offset, kind) in ReturnPlan)
        {
            var key = $"return{offset}";
            if (_scriptedDone.Contains(key) || daysBack > -offset || day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) continue;
            _scriptedDone.Add(key);
            await CreateReturnAsync(At(day, 11), kind);
        }
    }

    private static readonly string[] ReturnReasons =
    {
        "Falsche Menge bestellt", "Beschädigt angekommen", "Artikel nicht benötigt", "Verpackung geöffnet, Ware unbenutzt", "Lieferung doppelt bestellt",
    };

    /// <summary>Legt eine Retoure zu einer versendeten Bestellung an und führt sie je nach Art bis zum Abschluss, zur Stornierung oder zum offenen Entwurf.</summary>
    private async Task CreateReturnAsync(DateTime t, ReturnKind kind)
    {
        var candidates = _shipped
            .Where(o => !o.Returned && o.ShippedAt <= t.AddDays(-3) && o.Lines.Any(l => !l.Art.Spec.IsBundle && l.Art.Spec.ShelfLifeDays is null))
            .OrderBy(o => o.Number, StringComparer.Ordinal)
            .ToList();
        if (candidates.Count == 0) return;

        var sim = candidates[_rng.Next(candidates.Count)];
        var line = sim.Lines.First(l => !l.Art.Spec.IsBundle && l.Art.Spec.ShelfLifeDays is null);
        var art = line.Art;
        var quantity = Math.Min(line.Quantity, Rnd(1, 3));
        sim.Returned = true;

        var sequence = await NumberSequences.NextAsync(_db, NumberSequences.ReturnShipment, _ct);
        var ret = new ReturnShipment($"RMA-{t:yyyyMMdd}-{sequence:D5}", sim.Id, sim.CustomerReference, ReturnReasons[_rng.Next(ReturnReasons.Length)]);
        var returnLine = ret.AddLine(art.Id, art.Spec.Sku, quantity);
        _db.ReturnShipments.Add(ret);
        Stamp(t);

        if (kind == ReturnKind.DraftPending)
        {
            await SaveAsync(t, "receiver");
            return;
        }

        var tQc = kind is ReturnKind.DraftQcDone ? t.AddMinutes(25) : NextBusinessDay(t.AddDays(1)).AddHours(10).AddMinutes(30);
        var result = kind switch
        {
            ReturnKind.BGrade => QcResult.BGrade,
            ReturnKind.Defect => QcResult.Defect,
            ReturnKind.Destroy => QcResult.Destroy,
            _ => QcResult.Sellable,
        };
        var note = result switch
        {
            QcResult.BGrade => "Verpackung beschädigt, Ware in Ordnung: als B-Ware gesperrt.",
            QcResult.Defect => "Funktionsfehler festgestellt.",
            QcResult.Destroy => "Nicht mehr verkäuflich, Entsorgung.",
            _ => "Ware einwandfrei, zurück in den Bestand.",
        };

        if (kind == ReturnKind.Cancelled)
        {
            ret.Cancel();
            Stamp(tQc);
            await SaveAsync(tQc, "receiver");
            return;
        }

        ret.SetLineQc(returnLine.Id, result, art.Primary.Id, note);
        Stamp(tQc);
        if (kind == ReturnKind.DraftQcDone)
        {
            await SaveAsync(tQc, "receiver");
            return;
        }

        ret.MarkProcessed();
        var cost = art.Spec.PriceCents;
        if (result == QcResult.Sellable)
        {
            await StockBooking.BookAsync(_stockRepo, _moveRepo, art.Id, art.Primary.Id, +quantity, StockMovementReason.Return,
                "ReturnShipment", ret.Id, null, null, cost, ct: _ct);
            art.OnHand += quantity;
        }
        else
        {
            await StockBooking.RecordBlockedAsync(_stockRepo, _moveRepo, art.Id, art.Primary.Id, quantity,
                result == QcResult.BGrade ? StockMovementReason.ReturnB : StockMovementReason.ReturnScrap,
                "ReturnShipment", ret.Id, null, null, cost, ct: _ct);
        }
        var tProcess = tQc.AddMinutes(40);
        Stamp(tProcess);
        await SaveAsync(tProcess, "manager");
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Inventur
    // ------------------------------------------------------------------------------------------------------------------

    private async Task InventoryAsync(DateTime day)
    {
        if (day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return;
        var daysBack = (_today - day).Days;

        if (!_scriptedDone.Contains("inventory-cancel") && daysBack <= 50)
        {
            _scriptedDone.Add("inventory-cancel");
            await RunInventoryAsync(day, "Testzählung Gang B2 (abgebrochen)", "B2-", At(day, 8), null, cancel: true);
        }
        if (!_scriptedDone.Contains("inventory-cycle") && daysBack <= 38)
        {
            _scriptedDone.Add("inventory-cycle");
            await RunInventoryAsync(day, "Zyklische Inventur Gang A3", "A3-", At(day, 7, 30), At(day, 16, 40), cancel: false);
        }
    }

    /// <summary>Eine Inventur über die Lagerplätze mit dem Präfix: Snapshot, Zählung (einige Abweichungen), Abgleich über die Buchung. Abgebrochen: nur Snapshot und Storno.</summary>
    private async Task RunInventoryAsync(DateTime day, string name, string binPrefix, DateTime tStart, DateTime? tReconcile, bool cancel)
    {
        var binIds = _bins.Where(b => b.Code.StartsWith(binPrefix, StringComparison.Ordinal)).Select(b => b.Id).ToList();
        var rows = (await _db.StockItems.AsNoTracking().Where(s => binIds.Contains(s.StorageLocationId) && s.Quantity > 0).ToListAsync(_ct))
            .OrderBy(s => BinCode(s.StorageLocationId), StringComparer.Ordinal)
            .ThenBy(s => ArtById(s.ArticleId).Spec.Sku, StringComparer.Ordinal)
            .ThenBy(s => s.LotNumber, StringComparer.Ordinal)
            .ToList();
        if (rows.Count == 0) return;
        if (cancel) rows = rows.Take(4).ToList();

        var count = new InventoryCount(name);
        var lines = new List<InventoryLine>();
        foreach (var row in rows)
            lines.Add(count.AddSnapshotLine(row.StorageLocationId, BinCode(row.StorageLocationId), row.ArticleId,
                ArtById(row.ArticleId).Spec.Sku, row.Quantity, row.LotNumber, row.ExpiryDate));
        _db.InventoryCounts.Add(count);
        Stamp(tStart);

        if (cancel)
        {
            count.Cancel();
            Stamp(tStart.AddMinutes(90));
            await SaveAsync(tStart.AddMinutes(90), "manager");
            return;
        }

        var reasons = new[] { "Zählfehler beim letzten Umlagern", "Fund beim Aufräumen", "Schwund, Ursache unbekannt" };
        var counted = new List<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var expected = lines[i].ExpectedQty;
            var value = expected;
            string? reason = null;
            if (i % 5 == 2)
            {
                value = Math.Max(0, expected + (i % 2 == 0 ? -1 : 2));
                reason = reasons[i % reasons.Length];
            }
            count.SetCount(lines[i].Id, value, reason);
            counted.Add(value);
        }

        var tEnd = tReconcile ?? tStart.AddHours(9);
        count.Reconcile();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var art = ArtById(line.ArticleId);
            var row = await StockBooking.FindAsync(_stockRepo, line.ArticleId, line.BinId, line.LotNumber, line.ExpiryDate, _ct);
            var delta = counted[i] - (row?.Quantity ?? 0);
            if (delta == 0 || row is null) continue;
            await StockBooking.BookOnAsync(_moveRepo, row, delta, StockMovementReason.Inventory, "InventoryCount", count.Id, art.Spec.PriceCents, _ct);
            art.OnHand += delta;
        }
        Stamp(tEnd);
        await SaveAsync(tEnd, "manager");
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Feste Ereignisse (damit jeder Zustand im Ist-Stand vorkommt)
    // ------------------------------------------------------------------------------------------------------------------

    private List<(Art, int)> ScriptedPoLines(int supplierIndex, int max)
    {
        var lines = new List<(Art, int)>();
        foreach (var art in _artList.Where(a => a.Spec.SupplierIndex == supplierIndex && !a.Spec.IsBundle
                                                && a.Spec.Demand is DemoDemand.Medium or DemoDemand.Fast
                                                && a.Spec.ShelfLifeDays is null).OrderBy(a => a.Spec.Sku, StringComparer.Ordinal))
        {
            if (lines.Count >= max) break;
            lines.Add((art, RoundUp(Math.Max(art.Spec.MaxStock / 3, 1), art.Spec.OrderMultiple)));
        }
        return lines;
    }

    private async Task RunScriptedEventsAsync(DateTime day)
    {
        var daysBack = (_today - day).Days;

        // Eine Bestellung, die teilweise geliefert ist (Rest kommt in den nächsten Tagen).
        if (!_scriptedDone.Contains("po-partial") && daysBack <= 9)
        {
            _scriptedDone.Add("po-partial");
            var lines = ScriptedPoLines(1, 3);
            if (lines.Count > 0)
            {
                var po = await CreatePoAsync(1, lines, At(day, 8, 45), NextBusinessDay(day.AddDays(7)), send: true);
                _deliveries.Add(new Delivery { Po = po, Day = NextBusinessDay(day.AddDays(3)), Fraction = 0.6, Final = false });
                _deliveries.Add(new Delivery { Po = po, Day = _today.AddDays(2), Final = true });
            }
        }

        // Eine versendete Bestellung, die noch unterwegs ist.
        if (!_scriptedDone.Contains("po-open") && daysBack <= 3)
        {
            _scriptedDone.Add("po-open");
            var lines = ScriptedPoLines(0, 3);
            if (lines.Count > 0)
            {
                var arrival = NextBusinessDay(_today.AddDays(2));
                var po = await CreatePoAsync(0, lines, At(day, 8, 30), arrival, send: true);
                _deliveries.Add(new Delivery { Po = po, Day = arrival, Final = true });
            }
        }

        // Ein Wareneingang im Entwurf, der wieder storniert wird (Falschlieferung).
        if (!_scriptedDone.Contains("inbound-cancel") && daysBack <= 44)
        {
            _scriptedDone.Add("inbound-cancel");
            var art = _arts["FRB-4006"];
            var shipment = new InboundShipment($"WE-FALSCH-{day:yyyyMMdd}", "Lieferschein ohne Bestellbezug", "Falschlieferung, Ware ging zurück an den Lieferanten");
            shipment.AddLine(art.Id, art.ReceiveBin.Id, 3, null, null, unitCostCents: art.Spec.PriceCents);
            _db.InboundShipments.Add(shipment);
            Stamp(At(day, 9, 30));
            shipment.Cancel();
            Stamp(At(day, 11));
            await SaveAsync(At(day, 11), "receiver");
        }
    }

    // ------------------------------------------------------------------------------------------------------------------
    // Ist-Stand (die letzten Stunden vor "jetzt")
    // ------------------------------------------------------------------------------------------------------------------

    /// <summary>Zeitpunkt im Fenster der letzten fünf Stunden: 0 = vor knapp 5 Stunden, 1 = vor 5 Minuten.</summary>
    private DateTime Tail(double fraction) =>
        DateTime.SpecifyKind(_now.AddMinutes(-295 + fraction * 290), DateTimeKind.Utc);

    private async Task<List<SimOrder>> MakeOrdersAsync(int count, double fraction, int lineCount = 2)
    {
        var result = new List<SimOrder>();
        for (var i = 0; i < count; i++)
        {
            SimOrder? order = null;
            for (var attempt = 0; attempt < 8 && order is null; attempt++)
                order = await CreateOrderAsync(Tail(fraction + i * 0.01), allowCancel: false, saveNow: true, forcedLineCount: lineCount);
            if (order is not null) result.Add(order);
        }
        return result;
    }

    /// <summary>
    /// Der Zustand "jetzt": Bestellungen in jedem Status, dazu offene Picklisten, Sendungen in jedem Zustand, offene Retouren, eine
    /// offene Inventur, offene Nachschub-Aufgaben, ein Bestellentwurf und ein Wareneingang im Entwurf. Alle Zeitpunkte liegen in den
    /// letzten fünf Stunden vor dem Bezugszeitpunkt, also nach der Historie.
    /// </summary>
    private async Task BuildTailAsync()
    {
        // Erst, was noch Bestand bucht (Packen), dann die Listen, die nur angelegt oder gepickt werden: so überlagern sich die Allokationen nicht.
        var groupE = await MakeOrdersAsync(2, 0.00);
        var listE = await CreatePickListAsync(groupE, Tail(0.05), PickStage.Picked, "picker3", Tail(0.06), false, false);
        if (listE is not null) await PackAsync(listE, Tail(0.28), "packer");
        await ShipOrdersAsync(groupE, Tail(0.30), Tail(0.32), Tail(0.40), ShipStage.Shipped);

        var groupP = await MakeOrdersAsync(3, 0.03);
        var listP = await CreatePickListAsync(groupP, Tail(0.10), PickStage.Picked, "picker", Tail(0.11), true, false);
        if (listP is not null) await PackAsync(listP, Tail(0.45), "packer");
        if (groupP.Count > 0) await ShipOrdersAsync(groupP.Take(1).ToList(), Tail(0.47), null, null, ShipStage.Ready);
        if (groupP.Count > 1) await ShipOrdersAsync(groupP.Skip(1).Take(1).ToList(), Tail(0.48), Tail(0.50), null, ShipStage.Labeled);

        await CreateReturnAsync(Tail(0.25), ReturnKind.DraftPending);
        await CreateReturnAsync(Tail(0.35), ReturnKind.DraftQcDone);
        await RunInventoryOpenAsync(Tail(0.15));

        var groupR = await MakeOrdersAsync(2, 0.20);
        await CreatePickListAsync(groupR, Tail(0.30), PickStage.Picked, "picker2", Tail(0.31), false, false);

        var groupI = await MakeOrdersAsync(2, 0.40);
        await CreatePickListAsync(groupI, Tail(0.55), PickStage.InProgress, "picker", Tail(0.57), false, false);

        var groupQ = await MakeOrdersAsync(2, 0.50);
        await CreatePickListAsync(groupQ, Tail(0.70), PickStage.Pending, null, null, false, asWave: true, tWave: Tail(0.66));

        var cancelled = (await MakeOrdersAsync(1, 0.52, 1)).FirstOrDefault();
        if (cancelled is not null) await CancelOrderAsync(cancelled, Tail(0.62));

        var newOrders = new List<SimOrder>();
        foreach (var (fraction, priority) in new[] { (0.60, 3), (0.75, 1), (0.88, 0), (0.96, 0) })
        {
            SimOrder? order = null;
            for (var attempt = 0; attempt < 8 && order is null; attempt++)
                order = await CreateOrderAsync(Tail(fraction), allowCancel: false, saveNow: true, priority: priority,
                    due: priority == 3 ? _today.AddDays(1) : null, forcedLineCount: 3);
            if (order is not null) newOrders.Add(order);
        }
        if (newOrders.Count >= 2) await CreateOpenWaveAsync(newOrders.Take(2).ToList(), Tail(0.90));

        await CreateDraftPoAsync(Tail(0.20));
        await CreateDraftInboundAsync(Tail(0.80));
        await ScanReplenishmentAsync(_today, Tail(0.95));
    }

    /// <summary>Eine laufende Inventur: Snapshot der Reserveplätze im Gang B1, erst ein Teil ist gezählt.</summary>
    private async Task RunInventoryOpenAsync(DateTime t)
    {
        var binIds = _bins.Where(b => b.Code.StartsWith("B1-01-", StringComparison.Ordinal)).Select(b => b.Id).ToList();
        var rows = (await _db.StockItems.AsNoTracking().Where(s => binIds.Contains(s.StorageLocationId) && s.Quantity > 0).ToListAsync(_ct))
            .OrderBy(s => BinCode(s.StorageLocationId), StringComparer.Ordinal)
            .ThenBy(s => ArtById(s.ArticleId).Spec.Sku, StringComparer.Ordinal)
            .ToList();
        if (rows.Count == 0) return;

        var count = new InventoryCount("Stichtagsinventur Reservelager Gang B1 (laufend)");
        var lines = rows
            .Select(r => count.AddSnapshotLine(r.StorageLocationId, BinCode(r.StorageLocationId), r.ArticleId,
                ArtById(r.ArticleId).Spec.Sku, r.Quantity, r.LotNumber, r.ExpiryDate))
            .ToList();
        _db.InventoryCounts.Add(count);
        Stamp(t);
        for (var i = 0; i < lines.Count / 2; i++)
            count.SetCount(lines[i].Id, lines[i].ExpectedQty, null);
        Stamp(t.AddMinutes(30));
        await SaveAsync(t.AddMinutes(30), "manager");
    }

    private async Task CreateDraftPoAsync(DateTime t)
    {
        var lines = new List<(Art, int)>();
        foreach (var art in _artList.Where(a => a.Spec.SupplierIndex == 2 && !a.Spec.IsBundle && a.Spec.Demand != DemoDemand.None)
                     .OrderBy(a => a.Spec.Sku, StringComparer.Ordinal).Take(3))
            lines.Add((art, RoundUp(Math.Max(art.Spec.MaxStock / 4, 1), art.Spec.OrderMultiple)));
        if (lines.Count == 0) return;
        await CreatePoAsync(2, lines, t, null, send: false);
    }

    /// <summary>Wareneingang aus einer offenen Bestellung im Entwurf: Charge und MHD ergänzt der Empfänger erst beim Buchen.</summary>
    private async Task CreateDraftInboundAsync(DateTime t)
    {
        var open = _deliveries.Where(d => !d.CancelInstead && d.Po.Deliveries == 0).OrderBy(d => d.Day).ThenBy(d => d.Po.Number, StringComparer.Ordinal).FirstOrDefault();
        if (open is null) return;

        var shipment = new InboundShipment($"WE-{open.Po.Number}", open.Po.Number, "Anlieferung angekündigt, noch nicht gebucht", open.Po.Id);
        foreach (var line in open.Po.Lines.Where(l => l.Remaining > 0))
            shipment.AddLine(line.Art.Id, line.Art.ReceiveBin.Id, line.Remaining, null, null, line.LineId, line.PriceCents);
        if (shipment.Lines.Count == 0) return;
        _db.InboundShipments.Add(shipment);
        Stamp(t);
        await SaveAsync(t, "receiver");
    }

    /// <summary>
    /// Damit Bestandsalarme und Bestellvorschläge sichtbar sind: fehlen kritische bzw. Warnmeldungen, hebt der Generator die Schwellen
    /// einiger Artikel mit niedrigem Bestand knapp über ihren aktuellen Bestand (ein Disponent hat Mindestbestände angepasst).
    /// Betroffen sind nur Artikel mit Nachfrage, ohne offene Bestellung und ohne Hot-Pick-Platz; die Höchstgrenzen bleiben stimmig.
    /// </summary>
    private async Task EngineerAlertsAsync()
    {
        int Critical() => _artList.Count(a => a.Entity.MinStock > 0 && a.OnHand < a.Entity.MinStock);
        int Warning() => _artList.Count(a => a.Entity.MinStock > 0 && a.OnHand >= a.Entity.MinStock && a.OnHand < a.Entity.ReorderPoint);

        var candidates = _artList
            .Where(a => !a.Spec.IsBundle && a.Spec.Demand is DemoDemand.Slow or DemoDemand.Medium && a.Spec.Placement != DemoPlacement.Hot
                        && a.OpenPoQty == 0 && a.OnHand >= 4 && a.OnHand <= a.Spec.ReorderPoint * 2 && a.Entity.IsCurrentlyActive(_now)
                        && a.OnHand >= a.Entity.ReorderPoint)   // noch kein Alarm
            .OrderBy(a => a.OnHand - a.Spec.MinStock).ThenBy(a => a.Spec.Sku, StringComparer.Ordinal)
            .ToList();

        var needCritical = Math.Max(0, 2 - Critical());
        var needWarning = Math.Max(0, 3 - Warning());
        var t = Tail(0.98);

        var changed = false;
        foreach (var art in candidates)
        {
            if (needCritical == 0 && needWarning == 0) break;

            int min, reorder;
            if (needCritical > 0)
            {
                min = Math.Max(art.Spec.MinStock, art.OnHand + 2);
                reorder = Math.Max(art.Spec.ReorderPoint, min + 4);
                needCritical--;
            }
            else
            {
                min = Math.Max(1, Math.Min(art.Spec.MinStock, art.OnHand - 1));
                reorder = Math.Max(art.Spec.ReorderPoint, art.OnHand + 3);
                needWarning--;
            }
            var max = Math.Max(art.Spec.MaxStock, reorder + 5);

            var entity = await _db.Articles.FirstAsync(a => a.Id == art.Id, _ct);
            entity.SetStockThresholds(min, reorder, max);
            art.Entity.SetStockThresholds(min, reorder, max);
            changed = true;
        }

        if (changed)
        {
            Stamp(t);
            await SaveAsync(t, "manager");
        }
    }
}
