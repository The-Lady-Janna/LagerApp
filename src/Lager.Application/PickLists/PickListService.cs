using Lager.Application.Abstractions;
using Lager.Application.Orders;
using Lager.Contracts.PickLists;
using Lager.Domain.Articles;
using Lager.Domain.Orders;
using Lager.Domain.PickLists;
using Lager.Domain.Stock;
using Lager.Domain.Warehouse;
using WarehouseEntity = Lager.Domain.Warehouse.Warehouse;

namespace Lager.Application.PickLists;

public class PickListService
{
    /// <summary>Wie tief Bundles (Bundle in Bundle) aufgelöst werden; tiefer bzw. zyklisch wird abgelehnt.</summary>
    private const int MaxBundleDepth = 5;

    private static readonly PickListStatus[] ResettableStatuses =
        { PickListStatus.Pending, PickListStatus.InProgress, PickListStatus.Picked };
    private static readonly PickListStatus[] FinalStatuses =
        { PickListStatus.Completed, PickListStatus.Cancelled };

    private readonly IOrderRepository _orders;
    private readonly IStockRepository _stock;
    private readonly IStockMovementRepository _movements;
    private readonly IWarehouseRepository _warehouse;
    private readonly IArticleRepository _articles;
    private readonly IPickListRepository _pickLists;
    private readonly IPickWaveRepository _waves;
    private readonly IPickCartConfigRepository _carts;
    private readonly IPickRouteOptimizer _optimizer;
    private readonly IUnitOfWork _uow;
    private readonly ICurrentUser? _currentUser;

    public PickListService(
        IOrderRepository orders,
        IStockRepository stock,
        IStockMovementRepository movements,
        IWarehouseRepository warehouse,
        IArticleRepository articles,
        IPickListRepository pickLists,
        IPickWaveRepository waves,
        IPickCartConfigRepository carts,
        IPickRouteOptimizer optimizer,
        IUnitOfWork uow,
        ICurrentUser? currentUser = null)
    {
        _orders = orders;
        _stock = stock;
        _movements = movements;
        _warehouse = warehouse;
        _articles = articles;
        _pickLists = pickLists;
        _waves = waves;
        _carts = carts;
        _optimizer = optimizer;
        _uow = uow;
        _currentUser = currentUser;
    }

    // ------------------------------------------------------------------
    // Generieren
    // ------------------------------------------------------------------

    /// <summary>
    /// Erzeugt eine Pickliste für die Bestellungen. Alle Bestellungen müssen den Status New haben (sonst
    /// <see cref="InvalidOperationException"/>: keine Doppel-Picklisten für laufende, versendete oder stornierte
    /// Bestellungen); doppelte Ids werden verworfen. Der Bestand wird über ALLE Anforderungen der Liste
    /// gemeinsam allokiert (<see cref="StockAllocator"/>) - eine Fehlmenge bricht ab, statt zu überzuteilen.
    /// </summary>
    public async Task<PickListDto> GenerateAsync(GeneratePickListRequest request, CancellationToken ct = default)
    {
        if (request.OrderIds.Count == 0) throw new ArgumentException("At least one order id is required", nameof(request));

        var staged = await StageAsync(request.OrderIds, request.StartPickPointId, request.EndPickPointId, null, ct);
        await _uow.SaveChangesAsync(ct);
        return await ToDtoAsync(staged.PickList, staged.Orders, ct);
    }

    /// <summary>Eine gebaute, aber noch nicht gespeicherte Pickliste samt ihrer (auf Picking gesetzten) Bestellungen.</summary>
    internal sealed record StagedPickList(PickList PickList, IReadOnlyList<Order> Orders);

    /// <summary>
    /// Baut die Pickliste (Validierung, Allokation, Route, Nummer) und setzt die Bestellungen auf Picking -
    /// OHNE zu speichern. Der Aufrufer (Generieren, Wagen, Wellen-Release) ergänzt, was zum selben Vorgang
    /// gehört, und committet dann alles mit EINEM SaveChanges.
    /// </summary>
    internal async Task<StagedPickList> StageAsync(
        IReadOnlyCollection<Guid> orderIds, Guid? startPickPointId, Guid? endPickPointId, Guid? pickCartConfigId, CancellationToken ct)
    {
        if (orderIds.Count == 0) throw new ArgumentException("At least one order id is required", nameof(orderIds));

        // Doppelte Ids verwerfen, Reihenfolge der Anfrage beibehalten (sie bestimmt, wer bei knappem Bestand zuerst bedient wird).
        var distinctIds = orderIds.Distinct().ToList();
        var loaded = await _orders.GetManyAsync(distinctIds, ct);
        if (loaded.Count != distinctIds.Count)
            throw new InvalidOperationException("Eine oder mehrere Bestellungen wurden nicht gefunden");
        var byId = loaded.ToDictionary(o => o.Id);
        var orders = distinctIds.Select(id => byId[id]).ToList();

        var notNew = orders.Where(o => o.Status != OrderStatus.New).ToList();
        if (notNew.Count > 0)
            throw new InvalidOperationException(
                "Nur neue Bestellungen können kommissioniert werden: " +
                string.Join(", ", notNew.Select(o => $"{o.OrderNumber} ({o.Status})")));

        // Bundles in Komponenten auflösen: ein Bundle hat keinen physischen Bestand, nur seine Komponenten.
        var articles = await LoadArticlesWithComponentsAsync(orders.SelectMany(o => o.Lines).Select(l => l.ArticleId), ct);
        var requests = orders.SelectMany(o => ExpandOrder(o, articles)).ToList();

        // Bestand aller zu pickenden Artikel und die zugehörigen Lagerplätze (BinType für die Auswahl) laden.
        var pickArticleIds = requests.Select(r => r.ArticleId).Distinct().ToList();
        var stock = await _stock.ListForArticlesAsync(pickArticleIds, ct);
        var locations = await _warehouse.GetStorageLocationsAsync(stock.Select(s => s.StorageLocationId).Distinct(), ct);

        var allocator = new StockAllocator(stock, locations);
        var candidates = allocator.Allocate(requests, articles.ToDictionary(kv => kv.Key, kv => kv.Value.Sku));

        var routed = await RouteAsync(candidates, locations, startPickPointId, endPickPointId, ct);

        var nextSeq = await _pickLists.NextSequenceAsync(ct);
        var pickListNumber = $"PL-{DateTime.UtcNow:yyyyMMdd}-{nextSeq:D5}";
        var pickList = new PickList(pickListNumber, ToPickItems(routed.Ordered), routed.TotalDistanceMm, routed.Waypoints);
        if (pickCartConfigId.HasValue) pickList.AssignToCart(pickCartConfigId.Value);

        await _pickLists.AddAsync(pickList, ct);
        foreach (var order in orders)
            order.MarkPicking();

        return new StagedPickList(pickList, orders);
    }

    // ------------------------------------------------------------------
    // Lesen
    // ------------------------------------------------------------------

    public async Task<PickListDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var pl = await _pickLists.GetAsync(id, ct);
        if (pl is null) return null;

        var orderIds = pl.Items.Select(i => i.OrderId).Distinct();
        var orders = await _orders.GetManyAsync(orderIds, ct);
        return await ToDtoAsync(pl, orders, ct);
    }

    public async Task<IReadOnlyList<PickListDto>> ListAsync(CancellationToken ct = default)
    {
        var lists = await _pickLists.ListAsync(ct);
        if (lists.Count == 0) return Array.Empty<PickListDto>();

        var allOrderIds = lists.SelectMany(l => l.Items.Select(i => i.OrderId)).Distinct();
        var orders = await _orders.GetManyAsync(allOrderIds, ct);

        // Artikel, Lagerplätze und Wagen einmal für ALLE Listen laden (statt je Liste 2-3 Abfragen).
        var lookups = await LoadLookupsAsync(lists, orders, ct);
        return lists.Select(pl => ToDto(pl, lookups)).ToList();
    }

    // ------------------------------------------------------------------
    // Neu berechnen
    // ------------------------------------------------------------------

    /// <summary>
    /// Berechnet die Route einer noch offenen Pickliste (Pending/InProgress) neu; die Positionen bleiben
    /// inhaltlich gleich. Wände und Pick-Points des jeweils betroffenen Lagers zählen. Die alten Items werden
    /// über den Change-Tracker entfernt und die neuen hinzugefügt, alles in EINEM SaveChanges - ein Fehler
    /// (z. B. Konflikt) lässt die Pickliste unverändert. Picked/Completed/Cancelled: <see cref="InvalidOperationException"/>.
    /// </summary>
    public async Task<PickListDto?> RecalculateAsync(Guid id, RecalculatePickListRequest request, CancellationToken ct = default)
    {
        var pl = await _pickLists.GetAsync(id, ct);
        if (pl is null) return null;
        if (!pl.IsOpenForChanges)
            throw new InvalidOperationException(
                $"Pickliste {pl.PickListNumber} ({pl.Status}) kann nicht mehr neu berechnet werden - nur offene Picklisten");

        var candidates = MergeCandidates(pl.Items
            .OrderBy(i => i.SequenceNumber)
            .Select(i => new PickCandidate(i.OrderId, i.OrderLineId, i.ArticleId, i.StorageLocationId, i.Quantity)));

        var locations = await _warehouse.GetStorageLocationsAsync(candidates.Select(c => c.StorageLocationId).Distinct(), ct);
        var routed = await RouteAsync(candidates, locations, request.StartPickPointId, request.EndPickPointId, ct);
        if (routed.Ordered.Count != candidates.Count)
            throw new InvalidOperationException("Die Routenberechnung hat Positionen verloren - die Pickliste bleibt unverändert");

        await _pickLists.ReplaceItemsAsync(pl, ToPickItems(routed.Ordered), ct);
        pl.UpdateRouteSummary(routed.TotalDistanceMm, routed.Waypoints);
        await _uow.SaveChangesAsync(ct);

        var orderIds = pl.Items.Select(i => i.OrderId).Distinct();
        var orders = await _orders.GetManyAsync(orderIds, ct);
        return await ToDtoAsync(pl, orders, ct);
    }

    // ------------------------------------------------------------------
    // Route je Lager
    // ------------------------------------------------------------------

    private sealed record RoutedPicks(IReadOnlyList<PickCandidate> Ordered, int TotalDistanceMm, IReadOnlyList<Position> Waypoints);

    /// <summary>
    /// Optimiert die Route je Lager: jedes Lager hat sein eigenes Koordinatensystem, also zählen nur dessen
    /// Wände und Pick-Points. Die Teilrouten werden nacheinander (nach Lager-Code) angehängt; die Reihenfolge
    /// der Ergebnisliste ergibt die fortlaufende SequenceNumber. Start-/End-PickPoint aus der Anfrage gelten
    /// für das Lager, zu dem sie gehören; die anderen Lager wählen automatisch ihren Start-Pick-Point.
    /// </summary>
    private async Task<RoutedPicks> RouteAsync(
        IReadOnlyList<PickCandidate> candidates,
        IReadOnlyDictionary<Guid, StorageLocation> locations,
        Guid? startPickPointId,
        Guid? endPickPointId,
        CancellationToken ct)
    {
        var warehouses = await _warehouse.ListWarehousesAsync(ct);
        var walls = await _warehouse.ListWallsAsync(ct);
        var pickPoints = await _warehouse.ListPickPointsAsync(ct);

        var warehouseOfLocation = MapLocationsToWarehouses(warehouses);
        var codeOfWarehouse = warehouses.ToDictionary(w => w.Id, w => w.Code);

        var groups = candidates
            .GroupBy(c => warehouseOfLocation.GetValueOrDefault(c.StorageLocationId))   // unbekannt -> Guid.Empty
            .OrderBy(g => g.Key == Guid.Empty ? 1 : 0)
            .ThenBy(g => codeOfWarehouse.GetValueOrDefault(g.Key) ?? string.Empty, StringComparer.Ordinal)
            .ToList();

        var ordered = new List<PickCandidate>();
        var waypoints = new List<Position>();
        var totalDistance = 0;
        foreach (var group in groups)
        {
            var warehouseId = group.Key;
            // Ohne zuordenbares Lager (sollte nicht vorkommen) zählen alle Wände, wie früher.
            var wallsHere = warehouseId == Guid.Empty ? walls : walls.Where(w => w.WarehouseId == warehouseId).ToList();
            var (start, end) = ResolveStartEnd(startPickPointId, endPickPointId, warehouseId, pickPoints);

            var route = _optimizer.Optimize(group.ToList(), locations, start, wallsHere, end);
            ordered.AddRange(route.Ordered);
            waypoints.AddRange(route.Waypoints);
            totalDistance += route.TotalDistanceMm;
        }

        return new RoutedPicks(ordered, totalDistance, waypoints);
    }

    /// <summary>Lagerplatz -> Lager über die Kette Lagerplatz, Regal, Gang, Zone, Lager.</summary>
    private static Dictionary<Guid, Guid> MapLocationsToWarehouses(IEnumerable<WarehouseEntity> warehouses)
    {
        var map = new Dictionary<Guid, Guid>();
        foreach (var warehouse in warehouses)
            foreach (var zone in warehouse.Zones)
                foreach (var aisle in zone.Aisles)
                    foreach (var shelf in aisle.Shelves)
                        foreach (var location in shelf.Locations)
                            map[location.Id] = warehouse.Id;
        return map;
    }

    private static (Position Start, Position? End) ResolveStartEnd(
        Guid? startId, Guid? endId, Guid warehouseId, IReadOnlyList<PickPoint> pickPoints)
    {
        bool BelongsHere(PickPoint p) => warehouseId == Guid.Empty || p.WarehouseId == warehouseId;

        var start = Position.Origin;
        var explicitStart = startId.HasValue
            ? pickPoints.FirstOrDefault(p => p.Id == startId.Value && BelongsHere(p))
            : null;
        if (explicitStart is not null)
        {
            start = explicitStart.Position;
        }
        else
        {
            // Automatisch: erster Start-/Both-PickPoint DIESES Lagers.
            var auto = pickPoints.FirstOrDefault(p => BelongsHere(p) && (p.Type == PickPointType.Start || p.Type == PickPointType.Both));
            if (auto is not null) start = auto.Position;
        }

        Position? end = null;
        if (endId.HasValue)
        {
            var explicitEnd = pickPoints.FirstOrDefault(p => p.Id == endId.Value && BelongsHere(p));
            if (explicitEnd is not null) end = explicitEnd.Position;
        }

        return (start, end);
    }

    private static List<PickItem> ToPickItems(IReadOnlyList<PickCandidate> ordered) =>
        ordered
            .Select((c, idx) => new PickItem(idx + 1, c.OrderId, c.OrderLineId, c.ArticleId, c.StorageLocationId, c.Quantity))
            .ToList();

    /// <summary>
    /// Fasst Kandidaten mit demselben (Bestellung, Zeile, Artikel, Lagerplatz) zusammen. Der Routenoptimierer
    /// führt Kandidaten in einem Dictionary; zwei gleiche Datensätze würden dort zu EINEM zusammenfallen und
    /// eine Position verlieren.
    /// </summary>
    private static List<PickCandidate> MergeCandidates(IEnumerable<PickCandidate> candidates)
    {
        var result = new List<PickCandidate>();
        var index = new Dictionary<(Guid, Guid, Guid, Guid), int>();
        foreach (var c in candidates)
        {
            var key = (c.OrderId, c.OrderLineId, c.ArticleId, c.StorageLocationId);
            if (index.TryGetValue(key, out var i))
                result[i] = result[i] with { Quantity = result[i].Quantity + c.Quantity };
            else
            {
                index[key] = result.Count;
                result.Add(c);
            }
        }
        return result;
    }

    // ------------------------------------------------------------------
    // Bundles
    // ------------------------------------------------------------------

    /// <summary>
    /// Lädt die Artikel der Bestellzeilen und - Ebene für Ebene - alle Bundle-Komponenten, soweit sie selbst
    /// wieder Bundles sind (bis <see cref="MaxBundleDepth"/>).
    /// </summary>
    private Task<IReadOnlyDictionary<Guid, Article>> LoadArticlesWithComponentsAsync(IEnumerable<Guid> articleIds, CancellationToken ct) =>
        LoadArticlesWithComponentsAsync(_articles, articleIds, ct);

    /// <summary>
    /// Wie oben, ohne Instanz: auch die Bestandsampel der Bestellliste (<see cref="Orders.OrderService"/>) braucht den
    /// physischen Bedarf einer Bestellung und nutzt dafür dieselbe Auflösung wie das Kommissionieren.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<Guid, Article>> LoadArticlesWithComponentsAsync(
        IArticleRepository articleRepository, IEnumerable<Guid> articleIds, CancellationToken ct)
    {
        var map = new Dictionary<Guid, Article>();
        var pending = articleIds.ToHashSet();
        for (var depth = 0; pending.Count > 0 && depth <= MaxBundleDepth; depth++)
        {
            var loaded = await articleRepository.GetManyAsync(pending, ct);
            foreach (var (id, article) in loaded) map[id] = article;

            pending = loaded.Values
                .Where(a => a.IsBundle)
                .SelectMany(a => a.BundleComponents)
                .Select(c => c.ComponentArticleId)
                .Where(id => !map.ContainsKey(id))
                .ToHashSet();
        }
        return map;
    }

    /// <summary>
    /// Die physischen Pick-Anforderungen einer Bestellung: Bundle-Zeilen werden (rekursiv) in ihre
    /// Komponenten aufgelöst - 1 Bundle x M Komponenten x Menge. Zyklische oder zu tief verschachtelte
    /// Bundles werfen <see cref="InvalidOperationException"/>.
    /// </summary>
    internal static List<PickRequest> ExpandOrder(Order order, IReadOnlyDictionary<Guid, Article> articles)
    {
        var result = new List<PickRequest>();
        foreach (var line in order.Lines)
            Expand(order.Id, line.Id, line.ArticleId, line.Quantity, articles, result, 0, new HashSet<Guid>());
        return result;
    }

    private static void Expand(
        Guid orderId, Guid lineId, Guid articleId, int quantity,
        IReadOnlyDictionary<Guid, Article> articles, List<PickRequest> result, int depth, HashSet<Guid> path)
    {
        if (articles.TryGetValue(articleId, out var article) && article.IsBundle)
        {
            if (depth >= MaxBundleDepth || !path.Add(articleId))
                throw new InvalidOperationException($"Bundle {article.Sku} ist zyklisch oder zu tief verschachtelt");
            foreach (var component in article.BundleComponents)
            {
                var componentQuantity = (long)component.Quantity * quantity;
                if (componentQuantity > int.MaxValue)
                    throw new InvalidOperationException($"Menge für Bundle {article.Sku} ist zu groß");
                Expand(orderId, lineId, component.ComponentArticleId, (int)componentQuantity, articles, result, depth + 1, path);
            }
            path.Remove(articleId);
            return;
        }

        result.Add(new PickRequest(orderId, lineId, articleId, quantity));
    }

    // ------------------------------------------------------------------
    // Pickwagen
    // ------------------------------------------------------------------

    /// <summary>
    /// Bundle as many open orders (Status=New) into a single "cart pick list"
    /// as the chosen cart configuration allows. Stops adding an order as soon
    /// as it would push volume or weight over the cart's capacity (Summe, kein 3D-Anspruch).
    /// Bestand und Bins werden wie bei <see cref="GenerateAsync"/> über die Bundle-Komponenten und mit
    /// derselben Reservierungslogik (<see cref="StockAllocator"/>, Trockenlauf) geprüft; Pickliste und
    /// Wagen-Zuordnung landen in einem einzigen SaveChanges.
    /// </summary>
    public async Task<PickListDto> GenerateCartAsync(GenerateCartPickListRequest request, CancellationToken ct = default)
    {
        var cart = await _carts.GetAsync(request.PickCartConfigId, ct)
            ?? throw new InvalidOperationException("Pickwagen-Konfiguration nicht gefunden");

        // Reihenfolge: Priorität absteigend, Fälligkeit aufsteigend (ohne Termin zuletzt), dann Eingang (FIFO).
        var openOrders = OrderService.InPickingOrder(await _orders.ListByStatusAsync(OrderStatus.New, ct)).ToList();

        if (openOrders.Count == 0)
            throw new InvalidOperationException("Keine offenen Bestellungen verfügbar");

        var articles = await LoadArticlesWithComponentsAsync(openOrders.SelectMany(o => o.Lines).Select(l => l.ArticleId), ct);

        int skippedCapacity = 0;
        int skippedStock = 0;

        // Je Bestellung die physischen Anforderungen (Bundles aufgelöst). Nicht auflösbare Bundles: die
        // Bestellung bleibt außen vor und zählt bei den wegen Bestand übersprungenen.
        var requestsByOrder = new Dictionary<Guid, List<PickRequest>>();
        var selectable = new List<Order>();
        foreach (var order in openOrders)
        {
            try
            {
                requestsByOrder[order.Id] = ExpandOrder(order, articles);
                selectable.Add(order);
            }
            catch (InvalidOperationException)
            {
                skippedStock++;
            }
        }

        // Bestand mit laufender Reservierung: jede angenommene Bestellung verbraucht ihren Bestand, sodass
        // dieselbe Einheit nie zweimal vergeben wird - auch nicht innerhalb einer Bestellung und je Bin.
        var pickArticleIds = requestsByOrder.Values.SelectMany(r => r).Select(r => r.ArticleId).Distinct().ToList();
        var stock = await _stock.ListForArticlesAsync(pickArticleIds, ct);
        var locations = await _warehouse.GetStorageLocationsAsync(stock.Select(s => s.StorageLocationId).Distinct(), ct);
        var allocator = new StockAllocator(stock, locations);

        // Volumen und Gewicht je Bestellung aus den physischen Artikeln (Komponenten, nicht das Bundle), als long.
        var orderLoad = new Dictionary<Guid, (long Volume, long Weight)>();
        foreach (var order in selectable)
        {
            long vol = 0;
            long wt = 0;
            foreach (var r in requestsByOrder[order.Id])
            {
                if (!articles.TryGetValue(r.ArticleId, out var a)) continue;
                vol += a.Dimensions.VolumeMm3 * r.Quantity;
                wt += (long)a.WeightGrams * r.Quantity;
            }
            orderLoad[order.Id] = (vol, wt);
        }

        long maxVolume = cart.TotalVolumeMm3;
        long maxWeight = cart.MaxWeightGrams;
        long usedVolume = 0;
        long usedWeight = 0;
        var visitedBins = new HashSet<Guid>();
        var chosenOrders = new List<Order>();

        bool FitsCapacity(Order o) =>
            usedVolume + orderLoad[o.Id].Volume <= maxVolume && usedWeight + orderLoad[o.Id].Weight <= maxWeight;

        void Take(Order o)
        {
            usedVolume += orderLoad[o.Id].Volume;
            usedWeight += orderLoad[o.Id].Weight;
            chosenOrders.Add(o);
        }

        var remaining = new List<Order>(selectable);

        if (request.OptimizeForBinReuse)
        {
            // Greedy: at each step, pick the still-fitting order that adds
            // the fewest NEW bins. Ties broken by smaller volume, then by the pick order (Priorität, Fälligkeit,
            // Eingang): `remaining` ist so sortiert und der erste Treffer bleibt bei Gleichstand stehen.
            // Die Bins sind die tatsächlich allokierten (Trockenlauf), nicht alle Bins mit Bestand des Artikels.
            while (remaining.Count > 0)
            {
                Order? best = null;
                int bestNew = int.MaxValue;
                long bestVol = long.MaxValue;

                foreach (var order in remaining)
                {
                    if (!FitsCapacity(order)) continue;
                    if (!allocator.TryPreview(requestsByOrder[order.Id], out var preview)) continue;

                    var vol = orderLoad[order.Id].Volume;
                    var bins = preview.Select(c => c.StorageLocationId).ToHashSet();
                    var newBins = bins.Count == 0 ? 0 : bins.Except(visitedBins).Count();
                    if (newBins < bestNew || (newBins == bestNew && vol < bestVol))
                    {
                        best = order;
                        bestNew = newBins;
                        bestVol = vol;
                    }
                }

                if (best is null) break;
                var reserved = allocator.Allocate(requestsByOrder[best.Id]);
                visitedBins.UnionWith(reserved.Select(c => c.StorageLocationId));
                Take(best);
                remaining.Remove(best);
            }

            // Anything still in `remaining` was rejected for some reason — count why.
            foreach (var order in remaining)
            {
                if (!allocator.TryPreview(requestsByOrder[order.Id], out _)) skippedStock++;
                else skippedCapacity++;
            }
        }
        else
        {
            foreach (var order in remaining)
            {
                if (!allocator.TryPreview(requestsByOrder[order.Id], out _)) { skippedStock++; continue; }
                if (!FitsCapacity(order)) { skippedCapacity++; continue; }
                allocator.Allocate(requestsByOrder[order.Id]);
                Take(order);
            }
        }

        if (chosenOrders.Count == 0)
            throw new InvalidOperationException(
                $"Keine offene Bestellung passt aktuell in den Wagen '{cart.Name}' " +
                $"(max {maxVolume / 1_000_000} dm³ / {maxWeight / 1000} kg). " +
                $"Übersprungen: {skippedCapacity} wegen Kapazität, {skippedStock} wegen Bestand.");

        // Pickliste, Wagen-Zuordnung und Bestellstatus in EINEM Commit (kein zweites SaveChanges).
        var staged = await StageAsync(
            chosenOrders.Select(o => o.Id).ToList(), request.StartPickPointId, request.EndPickPointId, cart.Id, ct);
        await _uow.SaveChangesAsync(ct);
        return await ToDtoAsync(staged.PickList, staged.Orders, ct);
    }

    // ------------------------------------------------------------------
    // Picken abschließen und Packen
    // ------------------------------------------------------------------

    /// <summary>
    /// Picking ist abgeschlossen: die Pickliste geht auf Picked (Guard: verpackte oder stornierte Listen
    /// werfen), die Bestellungen der Liste von Picking auf Picked. Der Picker wird festgehalten, falls
    /// noch keiner gesetzt ist.
    /// </summary>
    public async Task<PickListDto?> MarkPickingCompleteAsync(Guid pickListId, CancellationToken ct = default)
    {
        var pl = await _pickLists.GetAsync(pickListId, ct);
        if (pl is null) return null;
        pl.MarkPickingComplete();
        // Picker zuweisen wenn noch nicht gesetzt — Basis für den
        // Picker-Performance-Report. Stay-silent wenn anonym/system.
        var who = _currentUser?.IsAuthenticated == true ? _currentUser.Username : null;
        if (!string.IsNullOrEmpty(who))
            pl.RecordPicker(who!);

        var orderIds = pl.Items.Select(i => i.OrderId).Distinct();
        var orders = await _orders.GetManyAsync(orderIds, ct);
        foreach (var order in orders.Where(o => o.Status == OrderStatus.Picking))
            order.MarkPicked();

        await _uow.SaveChangesAsync(ct);
        return await ToDtoAsync(pl, orders, ct);
    }

    /// <summary>
    /// Bestätigt die gepackten Mengen und bucht den Bestand ab - genau einmal je Pickliste:
    ///  - Guard: nur Pending/InProgress/Picked; eine bereits verpackte oder stornierte Liste wirft
    ///    <see cref="InvalidOperationException"/> (kein zweites Abbuchen).
    ///  - Eingabe: keine doppelten oder unbekannten PickItemIds, Menge je Position 0..Planmenge
    ///    (<see cref="ArgumentException"/>); nicht genannte Positionen gelten als 0 gepackt.
    ///  - Buchung je (Artikel, Lagerplatz) FEFO über die Bestandszeilen des Lagerplatzes (mehrere Chargen), je
    ///    Bestandszeile eine StockMovement (Pick, mit Charge/MHD). Fehlbestand wirft
    ///    <see cref="InvalidOperationException"/>, statt still zu kappen.
    ///  - Pickliste, Items, Bestand, Bewegungen, Bestellstatus (Packed) und Wellenstatus in EINEM SaveChanges.
    /// </summary>
    public async Task<PickListDto?> PackAsync(Guid pickListId, PackPickListRequest request, CancellationToken ct = default)
    {
        var pl = await _pickLists.GetAsync(pickListId, ct);
        if (pl is null) return null;

        // Guard vor jeder Mutation: ein zweites Packen darf nichts mehr buchen.
        if (pl.Status == PickListStatus.Completed)
            throw new InvalidOperationException("Pickliste ist bereits verpackt");
        if (pl.Status == PickListStatus.Cancelled)
            throw new InvalidOperationException("Pickliste ist storniert");

        var items = pl.Items.OrderBy(i => i.SequenceNumber).ToList();
        var actualByItem = ValidatePackRequest(request, items);

        // Bedarf je (Artikel, Lagerplatz), damit derselbe Platz für mehrere Bestellungen nur einmal gebucht wird.
        var demand = new Dictionary<(Guid Article, Guid Location), long>();
        foreach (var item in items)
        {
            var actual = actualByItem[item.Id];
            if (actual <= 0) continue;
            var key = (item.ArticleId, item.StorageLocationId);
            demand[key] = demand.GetValueOrDefault(key) + actual;
        }

        // Buchung planen (noch nichts verändern): je Bestandszeile FEFO, Fehlbestand -> Fehler.
        var articleMap = await _articles.GetManyAsync(demand.Keys.Select(k => k.Article).Distinct(), ct);
        var locationMap = await _warehouse.GetStorageLocationsAsync(demand.Keys.Select(k => k.Location).Distinct(), ct);
        var today = DateTime.UtcNow.Date;
        var bookings = new List<(StockItem Row, int Quantity)>();
        foreach (var ((articleId, locationId), amount) in demand
                     .OrderBy(kv => kv.Key.Article).ThenBy(kv => kv.Key.Location))
        {
            var rows = StockAllocator.OrderForBooking(await _stock.ListForBinAsync(articleId, locationId, ct), today);
            var missing = amount;
            foreach (var row in rows)
            {
                if (missing == 0) break;
                var take = (int)Math.Min(missing, row.Quantity);
                bookings.Add((row, take));
                missing -= take;
            }

            if (missing > 0)
            {
                var sku = articleMap.GetValueOrDefault(articleId)?.Sku ?? articleId.ToString();
                var code = locationMap.GetValueOrDefault(locationId)?.Code ?? locationId.ToString();
                throw new InvalidOperationException(
                    $"Nicht genug Bestand für Artikel {sku} auf Lagerplatz {code}: {missing} Stück fehlen");
            }
        }

        // Ab hier verändern: Bestätigung, Abbuchung mit Bewegung je Bestandszeile, Status.
        foreach (var item in items)
            item.ConfirmPacked(actualByItem[item.Id]);

        foreach (var (row, quantity) in bookings)
        {
            row.Remove(quantity);
            var costCents = articleMap.GetValueOrDefault(row.ArticleId)?.PurchasePriceCents ?? 0;
            await _movements.AddAsync(new StockMovement(
                row.ArticleId, row.StorageLocationId, -quantity, costCents,
                StockMovementReason.Pick,
                "PickList", pl.Id,
                row.LotNumber, row.ExpiryDate), ct);
        }

        pl.MarkPacked();

        // Bestellungen: alle Positionen einer Bestellung bestätigt -> Packed (über Picked, falls mark-picked fehlte).
        var orders = await _orders.GetManyAsync(items.Select(i => i.OrderId).Distinct(), ct);
        foreach (var order in orders)
        {
            if (!items.Where(i => i.OrderId == order.Id).All(i => i.IsConfirmed)) continue;
            if (order.Status == OrderStatus.Picking) order.MarkPicked();
            if (order.Status == OrderStatus.Picked) order.MarkPacked();
        }

        // Welle: sind alle ihre Picklisten verpackt, ist auch sie abgeschlossen.
        var wave = await _waves.FindByPickListAsync(pl.Id, ct);
        if (wave is { Status: PickWaveStatus.Released })
        {
            var otherIds = wave.PickListIds.Where(id => id != pl.Id).ToList();
            var statuses = await _pickLists.GetStatusesAsync(otherIds, ct);
            // Eine inzwischen gelöschte Liste blockiert die Welle nicht.
            var allDone = otherIds.All(id =>
                !statuses.TryGetValue(id, out var status) || status is PickListStatus.Completed or PickListStatus.Cancelled);
            if (allDone) wave.MarkCompleted();
        }

        await _uow.SaveChangesAsync(ct);
        return await ToDtoAsync(pl, orders, ct);
    }

    /// <summary>
    /// Prüft die Eingabe des Packens vor jeder Änderung und liefert die gepackte Menge je PickItem
    /// (nicht genannte Positionen = 0).
    /// </summary>
    private static Dictionary<Guid, int> ValidatePackRequest(PackPickListRequest request, IReadOnlyCollection<PickItem> items)
    {
        if (request?.Items is null)
            throw new ArgumentException("Die Positionsliste (Items) fehlt", nameof(request));

        var byId = new Dictionary<Guid, int>();
        foreach (var entry in request.Items)
        {
            if (!byId.TryAdd(entry.PickItemId, entry.ActualQuantity))
                throw new ArgumentException($"PickItemId {entry.PickItemId} kommt mehrfach vor", nameof(request));
        }

        var plan = items.ToDictionary(i => i.Id, i => i.Quantity);
        foreach (var (pickItemId, actual) in byId)
        {
            if (!plan.TryGetValue(pickItemId, out var planned))
                throw new ArgumentException($"PickItemId {pickItemId} gehört nicht zu dieser Pickliste", nameof(request));
            if (actual < 0 || actual > planned)
                throw new ArgumentOutOfRangeException(nameof(request),
                    $"Gepackte Menge {actual} liegt außerhalb von 0..{planned} (PickItem {pickItemId})");
        }

        return items.ToDictionary(i => i.Id, i => byId.GetValueOrDefault(i.Id, 0));
    }

    // ------------------------------------------------------------------
    // Zurücksetzen und Stornieren
    // ------------------------------------------------------------------

    /// <summary>
    /// Löscht ausschließlich Picklisten OHNE Buchungswirkung (Pending, InProgress, Picked) - nie verpackte
    /// (Completed) oder stornierte. Die betroffenen Bestellungen gehen auf New zurück, es sei denn, sie stehen
    /// noch auf einer verpackten Liste. Der Nummernkreis wird nur zurückgesetzt, wenn keine Pickliste
    /// übrig bleibt.
    /// </summary>
    public async Task<ResetPickListsResult> ResetAllAsync(CancellationToken ct = default)
    {
        var deletable = await _pickLists.ListByStatusAsync(ResettableStatuses, ct);
        var skipped = await _pickLists.CountByStatusAsync(FinalStatuses, ct);

        var affectedOrderIds = deletable.SelectMany(p => p.Items.Select(i => i.OrderId)).Distinct().ToList();
        foreach (var pl in deletable)
            _pickLists.Remove(pl);

        if (affectedOrderIds.Count > 0)
        {
            // Bestellungen auf einer verpackten Liste haben Bestand gebucht - die bleiben, wie sie sind.
            var onCompletedLists = (await _pickLists.ListOrderIdsAsync(new[] { PickListStatus.Completed }, ct)).ToHashSet();
            var orders = await _orders.GetManyAsync(affectedOrderIds.Where(id => !onCompletedLists.Contains(id)).ToList(), ct);
            foreach (var order in orders.Where(o => o.Status is OrderStatus.Picking or OrderStatus.Picked))
                order.ReleaseFromPicking();
        }

        // Wellen, deren Picklisten hier gelöscht werden, dürfen nicht als verwaiste Released-Welle stehen bleiben.
        await SettleReleasedWavesAsync(deletable.Select(p => p.Id).ToList(), deletable.Select(p => p.Id).ToHashSet(), ct);

        await _uow.SaveChangesAsync(ct);

        // Erst nach dem erfolgreichen Löschen und nur, wenn wirklich keine Liste übrig ist (sonst Nummern-Kollision).
        if (skipped == 0)
            await _pickLists.ResetSequenceAsync(ct);

        return new ResetPickListsResult(deletable.Count, skipped);
    }

    /// <summary>
    /// Storniert die noch unbegonnenen (Pending) Picklisten und gibt deren Bestellungen wieder frei - ohne zu
    /// speichern (für den Wellen-Abbruch). Ist eine der Listen schon begonnen oder verpackt, wirft die Methode.
    /// </summary>
    internal async Task CancelUnstartedAsync(IEnumerable<Guid> pickListIds, CancellationToken ct)
    {
        var lists = await _pickLists.GetManyAsync(pickListIds, ct);
        var begun = lists.Where(l => l.Status is not (PickListStatus.Pending or PickListStatus.Cancelled)).ToList();
        if (begun.Count > 0)
            throw new InvalidOperationException(
                "Es gibt bereits begonnene Picklisten (" + string.Join(", ", begun.Select(l => $"{l.PickListNumber}: {l.Status}")) + ")");

        var toCancel = lists.Where(l => l.Status == PickListStatus.Pending).ToList();
        var orders = await _orders.GetManyAsync(toCancel.SelectMany(l => l.Items.Select(i => i.OrderId)).Distinct().ToList(), ct);
        foreach (var pl in toCancel)
            pl.Cancel();
        foreach (var order in orders.Where(o => o.Status == OrderStatus.Picking))
            order.ReleaseFromPicking();
    }

    /// <summary>
    /// Storno einer Bestellung: nimmt ihre Positionen aus allen noch aktiven Picklisten (Pending, InProgress,
    /// Picked). Das hat keine Buchungswirkung - der Bestand wird erst beim Verpacken abgebucht. Eine Liste, auf der
    /// danach nichts mehr steht, wird storniert; ist damit auch eine freigegebene Welle erledigt, wird sie
    /// abgeschlossen (mindestens eine Liste verpackt) bzw. abgebrochen. Die Route der verbleibenden Positionen wird
    /// nicht neu berechnet (offene Listen lassen sich über "Neu berechnen" nachziehen), die Positionsnummern behalten
    /// ihre Lücke. Speichert NICHT: der Aufrufer committet zusammen mit dem Statuswechsel der Bestellung.
    /// </summary>
    internal async Task RemoveOrderFromActiveListsAsync(Guid orderId, CancellationToken ct)
    {
        var lists = await _pickLists.RemoveOrderItemsAsync(orderId, ct);
        foreach (var pl in lists)
        {
            // Rotiert das Concurrency-Token: packt jemand die Liste gleichzeitig, scheitert eines der beiden SaveChanges (409).
            pl.Touch();
            if (pl.Items.All(i => i.OrderId == orderId))
                pl.Cancel();
        }

        await SettleReleasedWavesAsync(lists.Where(l => l.Status == PickListStatus.Cancelled).Select(l => l.Id).ToList(), new HashSet<Guid>(), ct);
    }

    /// <summary>
    /// Schließt freigegebene (Released) Wellen ab, deren Picklisten nicht mehr offen sind: <paramref name="pickListIds"/>
    /// sind die soeben gelöschten bzw. stornierten Listen, <paramref name="goneIds"/> die gelöschten (sie zählen nicht mehr mit).
    /// Bleibt keine offene Liste übrig, ist die Welle abgeschlossen, wenn mindestens eine Liste verpackt wurde (wie beim Packen -
    /// eine gelöschte Liste blockiert die Welle nicht), sonst abgebrochen. Bestellungen und Listen fasst die Methode nicht an.
    /// </summary>
    private async Task SettleReleasedWavesAsync(IReadOnlyCollection<Guid> pickListIds, ISet<Guid> goneIds, CancellationToken ct)
    {
        foreach (var pickListId in pickListIds)
        {
            var wave = await _waves.FindByPickListAsync(pickListId, ct);
            if (wave is not { Status: PickWaveStatus.Released }) continue;

            var remaining = await _pickLists.GetManyAsync(wave.PickListIds.Where(id => !goneIds.Contains(id)).ToList(), ct);
            if (remaining.Any(l => l.Status is PickListStatus.Pending or PickListStatus.InProgress or PickListStatus.Picked)) continue;

            if (remaining.Any(l => l.Status == PickListStatus.Completed)) wave.MarkCompleted();
            else wave.Cancel();
        }
    }

    // ------------------------------------------------------------------
    // DTO-Mapping
    // ------------------------------------------------------------------

    /// <summary>Vorgeladene Nachschlage-Tabellen, damit das Mapping selbst keine Datenbankzugriffe macht.</summary>
    private sealed record Lookups(
        IReadOnlyDictionary<Guid, string> OrderNumbers,
        IReadOnlyDictionary<Guid, Article> Articles,
        IReadOnlyDictionary<Guid, StorageLocation> Locations,
        IReadOnlyDictionary<Guid, string> CartNames);

    private async Task<Lookups> LoadLookupsAsync(IReadOnlyCollection<PickList> lists, IReadOnlyList<Order> orders, CancellationToken ct)
    {
        var items = lists.SelectMany(l => l.Items).ToList();
        var articles = await _articles.GetManyAsync(items.Select(i => i.ArticleId).Distinct(), ct);
        var locations = await _warehouse.GetStorageLocationsAsync(items.Select(i => i.StorageLocationId).Distinct(), ct);

        var cartNames = new Dictionary<Guid, string>();
        if (lists.Any(l => l.PickCartConfigId.HasValue))
            foreach (var cart in await _carts.ListAsync(ct))
                cartNames[cart.Id] = cart.Name;

        return new Lookups(orders.ToDictionary(o => o.Id, o => o.OrderNumber), articles, locations, cartNames);
    }

    private async Task<PickListDto> ToDtoAsync(PickList pl, IReadOnlyList<Order> orders, CancellationToken ct) =>
        ToDto(pl, await LoadLookupsAsync(new[] { pl }, orders, ct));

    private static PickListDto ToDto(PickList pl, Lookups lookups)
    {
        var items = pl.Items
            .OrderBy(i => i.SequenceNumber)
            .Select(i => new PickItemDto(
                i.Id,
                i.SequenceNumber,
                i.OrderId,
                lookups.OrderNumbers.GetValueOrDefault(i.OrderId, "?"),
                i.ArticleId,
                lookups.Articles[i.ArticleId].Sku,
                lookups.Articles[i.ArticleId].Name,
                i.StorageLocationId,
                lookups.Locations[i.StorageLocationId].Code,
                i.Quantity,
                i.Picked,
                i.ConfirmedQuantity,
                i.ConfirmedAt))
            .ToList();

        var waypoints = pl.Waypoints
            .Select(p => new Lager.Contracts.Warehouse.PositionDto(p.XMm, p.YMm, p.ZMm))
            .ToList();

        string? cartName = null;
        if (pl.PickCartConfigId.HasValue)
            cartName = lookups.CartNames.GetValueOrDefault(pl.PickCartConfigId.Value);

        return new PickListDto(
            pl.Id, pl.PickListNumber, pl.Status.ToString(), pl.AssignedTo,
            pl.TotalDistanceMm, pl.CreatedAt, items, waypoints,
            pl.PickCartConfigId, cartName);
    }
}
