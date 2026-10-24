using Lager.Application.Abstractions;
using Lager.Contracts.Warehouse;
using Lager.Domain.Warehouse;
using WarehouseEntity = Lager.Domain.Warehouse.Warehouse;

namespace Lager.Application.Warehouse;

/// <summary>
/// Ein Feld lässt sich nicht ändern: Behälter darin haben Bestand, offene Aufgaben oder Belege. Code <c>grid_cells_in_use</c>
/// (409); <see cref="Cells"/> nennt die Felder, damit der Editor sie markieren kann.
/// </summary>
public sealed class GridCellsInUseException : InvalidOperationException
{
    public IReadOnlyList<GridBlockedCellDto> Cells { get; }

    public GridCellsInUseException(string message, IReadOnlyList<GridBlockedCellDto> cells) : base(message)
    {
        Cells = cells;
        Data["code"] = GridLayoutService.CellsInUseError;
    }
}

/// <summary>
/// Einfaches Raster-Layout: speichert die Felder eines Lagers und erzeugt daraus die gewohnten Objekte, damit Wegberechnung,
/// Einlagerung und Bestand unverändert weiterarbeiten:
/// <list type="bullet">
/// <item>Lagerfeld (Regal, Packpuffer, Warenausgang, Retouren) = ein Regal mit X Behältern (Lagerplätzen), übereinander in der
/// Feldmitte. Zone je Art, Gang je Reihe; Codes gangbasiert: Gang "A01", Regal "A01-03", Behälter "A01-03-2" (Präfix PK, WA, RT
/// für Packpuffer, Warenausgang, Retouren). Ein einmal vergebener Code bleibt, auch wenn sich die Reihen später ändern.</item>
/// <item>Wand und Hindernis = Wände für die Wegberechnung (Dicke = Feldgröße); Tür, Wareneingang und Laufweg bleiben begehbar.</item>
/// <item>Start, Ende, Verpackung = je zusammenhängendem Bereich ein Pickpunkt (Start, End, Both).</item>
/// </list>
/// Objekte aus dem erweiterten Editor bleiben unangetastet. Entfernt werden nur Behälter ohne Bestand, offene Aufgaben und Belege;
/// sonst lehnt das Speichern als Ganzes ab (409 <c>grid_cells_in_use</c>) und nichts ändert sich.
/// </summary>
public class GridLayoutService
{
    public const string ConflictError = "grid_conflict";
    public const string CellsInUseError = "grid_cells_in_use";

    private sealed record Category(string ZoneCode, string ZoneName, string AislePrefix);

    private static readonly IReadOnlyDictionary<GridCellType, Category> Categories = new Dictionary<GridCellType, Category>
    {
        [GridCellType.Shelf] = new("LAGER", "Raster: Lagerregale", "A"),
        [GridCellType.PackingStorage] = new("PACKPUFFER", "Raster: Packpuffer", "PK"),
        [GridCellType.OutgoingStorage] = new("AUSGANG", "Raster: Warenausgang", "WA"),
        [GridCellType.Returns] = new("RETOUREN", "Raster: Retouren", "RT"),
    };

    private static readonly IReadOnlyDictionary<GridCellType, string> PickPointLabels = new Dictionary<GridCellType, string>
    {
        [GridCellType.Start] = "Start",
        [GridCellType.End] = "Ende",
        [GridCellType.Packing] = "Verpackung",
    };

    private readonly IWarehouseRepository _repo;
    private readonly IUnitOfWork _uow;

    public GridLayoutService(IWarehouseRepository repo, IUnitOfWork uow)
    {
        _repo = repo;
        _uow = uow;
    }

    /// <summary>Raster eines Lagers; ohne gespeichertes Raster die Vorgaben für ein neues. <c>null</c> = Lager unbekannt.</summary>
    public async Task<GridLayoutDto?> GetAsync(Guid warehouseId, CancellationToken ct = default)
    {
        var warehouse = await _repo.GetWarehouseAsync(warehouseId, ct);
        if (warehouse is null) return null;
        var grid = await _repo.GetGridLayoutAsync(warehouseId, ct);
        return await ToDtoAsync(warehouse, grid, ct);
    }

    /// <summary>Speichert das Raster und wendet es an. <c>null</c> = Lager unbekannt.</summary>
    public async Task<SaveGridLayoutResult?> SaveAsync(Guid warehouseId, SaveGridLayoutRequest request, CancellationToken ct = default)
    {
        var warehouse = await _repo.GetWarehouseAsync(warehouseId, ct);
        if (warehouse is null) return null;

        var grid = await _repo.GetGridLayoutAsync(warehouseId, ct);
        if (grid is not null && request.ExpectedVersion != grid.ConcurrencyToken)
            throw Rule(ConflictError, "Das Raster wurde zwischenzeitlich geändert. Bitte neu laden und die Änderungen erneut vornehmen.");
        var gridIsNew = grid is null;
        grid ??= new GridLayout(warehouseId);

        var previous = grid.Cells.ToDictionary(c => (c.Col, c.Row));
        var managedBefore = grid.Managed;
        grid.Define(request.CellSizeMm, request.Columns, request.Rows,
            request.ContainerWidthMm, request.ContainerDepthMm, request.ContainerHeightMm, request.ContainerMaxWeightGrams,
            request.Cells.Select(ParseCell));

        var shelvesById = warehouse.Zones.SelectMany(z => z.Aisles).SelectMany(a => a.Shelves).ToDictionary(s => s.Id);
        var aislesById = warehouse.Zones.SelectMany(z => z.Aisles).ToDictionary(a => a.Id);

        // 1. Welche Lagerfelder behalten ihr Regal? Gleiche Stelle, gleiche Art, Regal existiert noch.
        var cells = new List<GridCell>();
        foreach (var cell in grid.Cells)
        {
            var keep = cell.Type.IsStorage()
                && previous.TryGetValue((cell.Col, cell.Row), out var before)
                && before.Type == cell.Type
                && before.ShelfId is { } id
                && shelvesById.ContainsKey(id);
            cells.Add(keep ? cell with { ShelfId = previous[(cell.Col, cell.Row)].ShelfId } : cell);
        }

        var keptShelfIds = cells.Where(c => c.ShelfId is not null).Select(c => c.ShelfId!.Value).ToHashSet();
        var removedShelves = previous.Values
            .Where(c => c.ShelfId is { } id && shelvesById.ContainsKey(id) && !keptShelfIds.Contains(id))
            .Select(c => (Cell: c, Shelf: shelvesById[c.ShelfId!.Value]))
            .ToList();

        // 2. Zu entfernende Behälter: ganze Regale weggefallener Felder, bei weniger Behältern die obersten.
        var removedBins = new List<(GridCell Cell, StorageLocation Bin)>();
        foreach (var (cell, shelf) in removedShelves)
            removedBins.AddRange(shelf.Locations.Select(b => (cell, b)));
        foreach (var cell in cells.Where(c => c.ShelfId is not null))
        {
            var bins = OrderedBins(shelvesById[cell.ShelfId!.Value]);
            removedBins.AddRange(bins.Skip(cell.Containers).Select(b => (cell, b)));
        }
        await EnsureRemovableAsync(removedBins, ct);

        var summary = new SummaryCounter { ShelvesRemoved = removedShelves.Count, ContainersRemoved = removedBins.Count };
        var removedBinIds = removedBins.Select(r => r.Bin.Id).ToHashSet();
        await _repo.RemoveEmptyStockRowsAsync(removedBinIds.ToList(), ct);
        foreach (var (_, shelf) in removedShelves)
        {
            _repo.RemoveShelf(shelf);
            shelvesById.Remove(shelf.Id);
        }
        foreach (var (_, bin) in removedBins.Where(r => keptShelfIds.Contains(r.Bin.ShelfId)))
            _repo.RemoveStorageLocation(bin);

        // 3. Zonen je Lagerfeld-Art, Gänge je Reihe, neue Regale und Behälter.
        var zones = new Dictionary<GridCellType, Guid>();
        var managedAisles = managedBefore.Aisles.Where(aislesById.ContainsKey).ToHashSet();
        var shelvesOfAisle = shelvesById.Values.GroupBy(s => s.AisleId).ToDictionary(g => g.Key, g => g.ToList());
        var plannedShelves = new List<(GridCell Cell, Shelf Shelf, bool IsNew)>();

        foreach (var (type, category) in Categories)
        {
            var typeCells = cells.Where(c => c.Type == type).ToList();
            if (typeCells.Count == 0) continue;
            var zone = await EnsureZoneAsync(warehouse, managedBefore, type, category, ct);
            zones[type] = zone.Id;

            foreach (var run in GridLayoutPlanner.DetectRuns(typeCells))
            {
                var aisle = run.Cells
                    .Where(c => c.ShelfId is not null)
                    .Select(c => aislesById.GetValueOrDefault(shelvesById[c.ShelfId!.Value].AisleId))
                    .FirstOrDefault(a => a is not null);
                if (aisle is null)
                {
                    aisle = new Aisle(zone.Id, NextAisleCode(zone, aislesById.Values, category.AislePrefix), Position.Origin, Position.Origin, run.Orientation);
                    await _repo.AddAisleAsync(aisle, ct);
                    aislesById[aisle.Id] = aisle;
                }
                managedAisles.Add(aisle.Id);
                if (!shelvesOfAisle.TryGetValue(aisle.Id, out var aisleShelves))
                    shelvesOfAisle[aisle.Id] = aisleShelves = new List<Shelf>();
                // Auch die Codes gerade entfernter Regale gelten als vergeben: ihre Behälter-Codes stehen bis zum Speichern noch in der Datenbank.
                var reserved = aisle.Shelves.Concat(aisleShelves).ToList();

                for (var i = 0; i < run.Cells.Count; i++)
                {
                    var cell = run.Cells[i];
                    if (cell.ShelfId is { } existingId)
                    {
                        plannedShelves.Add((cell, shelvesById[existingId], false));
                        continue;
                    }
                    var code = NextShelfCode(aisle.Code, i + 1, reserved);
                    var shelf = new Shelf(aisle.Id, code, grid.CellOrigin(cell.Col, cell.Row), grid.CellSizeMm, grid.CellSizeMm, cell.Containers * grid.ContainerHeightMm);
                    aisleShelves.Add(shelf);
                    reserved.Add(shelf);
                    shelvesById[shelf.Id] = shelf;
                    plannedShelves.Add((cell, shelf, true));
                }
            }
        }

        // Codes der neuen Behälter vergeben (systemweit eindeutig), dann Regale und Behälter anlegen bzw. anpassen.
        var binCodes = await AllocateBinCodesAsync(warehouse, plannedShelves, ct);
        var finalCells = new List<GridCell>();
        foreach (var (cell, shelf, isNew) in plannedShelves)
        {
            await ApplyShelfAsync(grid, cell, shelf, isNew, binCodes, summary, ct);
            finalCells.Add(cell with { ShelfId = shelf.Id });
        }
        finalCells.AddRange(cells.Where(c => !c.Type.IsStorage()));

        // Gänge an ihre Regale anpassen, leere Raster-Gänge und -Zonen entfernen.
        foreach (var aisleId in managedAisles.ToList())
        {
            var aisle = aislesById[aisleId];
            var aisleShelves = shelvesOfAisle.GetValueOrDefault(aisleId) ?? new List<Shelf>();
            aisleShelves.RemoveAll(s => !shelvesById.ContainsKey(s.Id));
            if (aisleShelves.Count == 0)
            {
                _repo.RemoveAisle(aisle);
                managedAisles.Remove(aisleId);
                continue;
            }
            FitAisle(aisle, aisleShelves);
        }
        foreach (var (type, zoneId) in managedBefore.Zones)
        {
            if (zones.ContainsKey(type) || warehouse.Zones.FirstOrDefault(z => z.Id == zoneId) is not { } zone) continue;
            // Nur entfernen, wenn alle Gänge der Zone vom Raster stammten und jetzt weg sind (sonst hat jemand die Zone mitbenutzt).
            if (zone.Aisles.All(a => managedBefore.Aisles.Contains(a.Id) && !managedAisles.Contains(a.Id)))
                _repo.RemoveZone(zone);
            else
                zones[type] = zone.Id;
        }

        // 4. Wände und Pickpunkte neu aus dem Raster.
        var wallIds = await ApplyWallsAsync(grid, managedBefore, warehouseId, finalCells, ct);
        var pickPoints = await ApplyPickPointsAsync(grid, managedBefore, warehouseId, finalCells, ct);
        summary.Walls = wallIds.Count;
        summary.PickPoints = pickPoints.Count;

        grid.RecordApplied(finalCells, new GridManagedObjects(zones, managedAisles.ToList(), wallIds, pickPoints));
        if (gridIsNew) await _repo.AddGridLayoutAsync(grid, ct);
        await _uow.SaveChangesAsync(ct);

        var reloaded = await _repo.GetWarehouseAsync(warehouseId, ct);
        return new SaveGridLayoutResult(await ToDtoAsync(reloaded!, grid, ct), summary.ToDto());
    }

    // ---- Regale und Behälter -------------------------------------------------------------------------------------

    private async Task ApplyShelfAsync(GridLayout grid, GridCell cell, Shelf shelf, bool isNew,
        IReadOnlyDictionary<Guid, Queue<string>> binCodes, SummaryCounter summary, CancellationToken ct)
    {
        var origin = grid.CellOrigin(cell.Col, cell.Row);
        var binType = GridCellTypes.BinTypeOf(cell.Type);
        var height = cell.Containers * grid.ContainerHeightMm;

        if (!isNew)
        {
            if (shelf.WidthMm != grid.CellSizeMm || shelf.DepthMm != grid.CellSizeMm || shelf.HeightMm != height)
                shelf.Update(shelf.Code, grid.CellSizeMm, grid.CellSizeMm, height);
            if (shelf.Position != origin)
                shelf.MoveTo(origin);
        }

        var bins = OrderedBins(shelf).Take(cell.Containers).ToList();
        var missing = cell.Containers - bins.Count;
        var codes = binCodes.GetValueOrDefault(shelf.Id);
        for (var i = 0; i < missing; i++)
        {
            var bin = shelf.AddLocation(codes!.Dequeue(), grid.ContainerWidthMm, grid.ContainerDepthMm, grid.ContainerHeightMm, grid.ContainerMaxWeightGrams);
            if (!isNew) await _repo.AddStorageLocationAsync(bin, ct);
            bins.Add(bin);
        }
        summary.ContainersCreated += missing;
        if (isNew)
        {
            await _repo.AddShelfAsync(shelf, ct);
            summary.ShelvesCreated++;
        }

        for (var level = 0; level < bins.Count; level++)
        {
            var bin = bins[level];
            if (bin.WidthMm != grid.ContainerWidthMm || bin.DepthMm != grid.ContainerDepthMm
                || bin.HeightMm != grid.ContainerHeightMm || bin.MaxWeightGrams != grid.ContainerMaxWeightGrams)
                bin.Update(bin.Code, grid.ContainerWidthMm, grid.ContainerDepthMm, grid.ContainerHeightMm, grid.ContainerMaxWeightGrams);
            var position = new Position(
                origin.XMm + (grid.CellSizeMm - grid.ContainerWidthMm) / 2,
                origin.YMm + (grid.CellSizeMm - grid.ContainerDepthMm) / 2,
                level * grid.ContainerHeightMm);
            if (bin.Position != position) bin.MoveTo(position);
            // Normale Regale: Hot-Pick/Reserve aus dem erweiterten Editor bleiben. Pufferfelder bekommen immer ihren Typ.
            var wanted = cell.Type == GridCellType.Shelf ? (bin.BinType.IsBuffer() ? BinType.Standard : bin.BinType) : binType;
            if (bin.BinType != wanted) bin.SetBinType(wanted, wanted == BinType.HotPick ? bin.ReplenishmentThreshold : 0);
        }
    }

    /// <summary>Behälter eines Regals von unten nach oben (gleiche Höhe: nach Code).</summary>
    private static IEnumerable<StorageLocation> OrderedBins(Shelf shelf) =>
        shelf.Locations.OrderBy(b => b.Position.ZMm).ThenBy(b => b.Code, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Codes für alle fehlenden Behälter: "{Regal}-{n}" mit dem kleinsten freien n. Ist ein Code systemweit vergeben (etwa in
    /// einem anderen Lager), bekommen alle neuen Regale des Lagers den Lager-Code als Präfix ("WH01-A01-03"), damit die Codes eines
    /// Lagers einheitlich bleiben; trägt schon ein Raster-Regal das Präfix, gilt es für jedes neue. Bestehende Regale überspringen
    /// vergebene Nummern.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, Queue<string>>> AllocateBinCodesAsync(
        WarehouseEntity warehouse, IReadOnlyList<(GridCell Cell, Shelf Shelf, bool IsNew)> planned, CancellationToken ct)
    {
        var prefix = warehouse.Code + "-";
        var needing = planned.Where(p => p.Cell.Containers > p.Shelf.Locations.Count).ToList();
        var newShelves = planned.Where(p => p.IsNew).Select(p => p.Shelf).ToList();

        void PrefixNewShelves()
        {
            foreach (var shelf in newShelves.Where(s => !s.Code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                shelf.Update(prefix + shelf.Code, shelf.WidthMm, shelf.DepthMm, shelf.HeightMm);
        }

        var prefixed = planned.Any(p => !p.IsNew && p.Shelf.Code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (prefixed) PrefixNewShelves();

        var takenElsewhere = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var attempt = 0; ; attempt++)
        {
            if (attempt >= 10)
                throw Rule(WarehouseErrorCodes.DuplicateCode, "Für die neuen Behälter ließen sich keine freien Lagerplatz-Codes finden.");

            var proposals = needing.ToDictionary(p => p.Shelf.Id, p =>
            {
                var used = p.Shelf.Locations.Select(b => b.Code).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var codes = new List<string>();
                for (var n = 1; codes.Count < p.Cell.Containers - p.Shelf.Locations.Count; n++)
                {
                    var code = $"{p.Shelf.Code}-{n}";
                    if (!used.Contains(code) && !takenElsewhere.Contains(code)) codes.Add(code);
                }
                return new Queue<string>(codes);
            });

            var taken = await _repo.FindTakenBinCodesAsync(proposals.Values.SelectMany(c => c).ToList(), null, ct);
            if (taken.Count == 0) return proposals;
            takenElsewhere.UnionWith(taken);

            // Ein neues Regal kollidiert: Präfix für alle neuen Regale, dann noch einmal (bestehende zählen einfach weiter).
            var newCollides = needing.Any(p => p.IsNew && proposals[p.Shelf.Id].Any(takenElsewhere.Contains));
            if (newCollides && !prefixed)
            {
                PrefixNewShelves();
                prefixed = true;
            }
        }
    }

    private async Task EnsureRemovableAsync(IReadOnlyList<(GridCell Cell, StorageLocation Bin)> bins, CancellationToken ct)
    {
        if (bins.Count == 0) return;
        var usage = await _repo.GetBinUsageAsync(bins.Select(b => b.Bin.Id).ToList(), ct);
        var blocked = bins
            .Where(b => usage.WithStock.Contains(b.Bin.Id) || usage.WithOpenTasks.Contains(b.Bin.Id) || usage.Referenced.Contains(b.Bin.Id))
            .GroupBy(b => (b.Cell.Col, b.Cell.Row))
            .OrderBy(g => g.Key.Row).ThenBy(g => g.Key.Col)
            .Select(g => new GridBlockedCellDto(g.Key.Col, g.Key.Row, GridLayout.CellRef(g.Key.Col, g.Key.Row),
                g.Select(b => b.Bin.Code).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();
        if (blocked.Count == 0) return;

        var refs = string.Join(", ", blocked.Take(8).Select(b => b.Ref)) + (blocked.Count > 8 ? $" und {blocked.Count - 8} weitere" : "");
        throw new GridCellsInUseException(
            $"Das Raster wurde nicht gespeichert: In den Feldern {refs} liegen Behälter, die noch verwendet werden (Bestand, offene " +
            "Aufgaben oder Belege). Diese Felder bleiben, bis der Bestand umgelagert bzw. die Aufgaben abgeschlossen sind.", blocked);
    }

    // ---- Zonen und Gänge ------------------------------------------------------------------------------------------

    private async Task<Zone> EnsureZoneAsync(WarehouseEntity warehouse, GridManagedObjects managed, GridCellType type, Category category, CancellationToken ct)
    {
        if (managed.Zones.TryGetValue(type, out var id) && warehouse.Zones.FirstOrDefault(z => z.Id == id) is { } existing)
            return existing;

        var code = category.ZoneCode;
        for (var n = 2; warehouse.Zones.Any(z => z.Code.Equals(code, StringComparison.OrdinalIgnoreCase)); n++)
            code = $"{category.ZoneCode}-{n}";
        var zone = new Zone(warehouse.Id, code, category.ZoneName, Position.Origin);
        await _repo.AddZoneAsync(zone, ct);
        return zone;
    }

    private static string NextAisleCode(Zone zone, IEnumerable<Aisle> allAisles, string prefix)
    {
        var max = allAisles
            .Where(a => a.ZoneId == zone.Id)
            .Select(a => NumberAfter(a.Code, prefix))
            .DefaultIfEmpty(0)
            .Max();
        return $"{prefix}{max + 1:D2}";
    }

    private static string NextShelfCode(string aisleCode, int wanted, IReadOnlyCollection<Shelf> aisleShelves)
    {
        bool Taken(string code) => aisleShelves.Any(s => s.Code.Equals(code, StringComparison.OrdinalIgnoreCase)
                                                        || s.Code.EndsWith("-" + code, StringComparison.OrdinalIgnoreCase));
        var candidate = $"{aisleCode}-{wanted:D2}";
        if (!Taken(candidate)) return candidate;
        var max = aisleShelves.Select(s => NumberAfter(s.Code, aisleCode + "-")).DefaultIfEmpty(0).Max();
        for (var n = Math.Max(max, wanted) + 1; ; n++)
        {
            candidate = $"{aisleCode}-{n:D2}";
            if (!Taken(candidate)) return candidate;
        }
    }

    /// <summary>Zahl hinter einem Präfix am Ende eines Codes ("A07" mit "A" = 7, "WH01-A01-03" mit "A01-" = 3), sonst 0.</summary>
    private static int NumberAfter(string code, string prefix)
    {
        var idx = code.LastIndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (idx < 0 || (idx > 0 && code[idx - 1] != '-')) return 0;
        var rest = code[(idx + prefix.Length)..];
        return rest.Length > 0 && rest.All(char.IsDigit) && int.TryParse(rest, out var n) ? n : 0;
    }

    private static void FitAisle(Aisle aisle, IReadOnlyCollection<Shelf> shelves)
    {
        var minX = shelves.Min(s => s.Position.XMm);
        var minY = shelves.Min(s => s.Position.YMm);
        var maxX = shelves.Max(s => s.Position.XMm + s.WidthMm);
        var maxY = shelves.Max(s => s.Position.YMm + s.DepthMm);
        var orientation = maxX - minX >= maxY - minY ? AisleOrientation.AlongX : AisleOrientation.AlongY;
        var start = orientation == AisleOrientation.AlongX ? new Position(minX, (minY + maxY) / 2, 0) : new Position((minX + maxX) / 2, minY, 0);
        var end = orientation == AisleOrientation.AlongX ? new Position(maxX, (minY + maxY) / 2, 0) : new Position((minX + maxX) / 2, maxY, 0);
        if (aisle.StartPosition != start || aisle.EndPosition != end || aisle.Orientation != orientation)
            aisle.Update(aisle.Code, start, end, orientation);
    }

    // ---- Wände und Pickpunkte -------------------------------------------------------------------------------------

    private async Task<List<Guid>> ApplyWallsAsync(GridLayout grid, GridManagedObjects managed, Guid warehouseId, IReadOnlyCollection<GridCell> cells, CancellationToken ct)
    {
        var old = managed.Walls.ToHashSet();
        foreach (var wall in (await _repo.ListWallsOfWarehouseAsync(warehouseId, ct)).Where(w => old.Contains(w.Id)))
            _repo.RemoveWall(wall);

        var ids = new List<Guid>();
        foreach (var segment in GridLayoutPlanner.PlanWalls(cells, grid.CellSizeMm))
        {
            var wall = new Wall(warehouseId, new[] { segment.From, segment.To }, grid.CellSizeMm,
                segment.Type == GridCellType.Obstacle ? "Hindernis" : "Wand");
            await _repo.AddWallAsync(wall, ct);
            ids.Add(wall.Id);
        }
        return ids;
    }

    private async Task<List<GridManagedPickPoint>> ApplyPickPointsAsync(GridLayout grid, GridManagedObjects managed, Guid warehouseId, IReadOnlyCollection<GridCell> cells, CancellationToken ct)
    {
        var existing = (await _repo.ListPickPointsOfWarehouseAsync(warehouseId, ct)).ToDictionary(p => p.Id);
        var reusable = managed.PickPoints
            .Where(p => existing.ContainsKey(p.Id))
            .GroupBy(p => p.Kind)
            .ToDictionary(g => g.Key, g => new Queue<PickPoint>(g.Select(p => existing[p.Id])));

        var result = new List<GridManagedPickPoint>();
        foreach (var group in GridLayoutPlanner.PlanPickAreas(cells).GroupBy(a => a.Kind))
        {
            var areas = group.ToList();
            for (var i = 0; i < areas.Count; i++)
            {
                var area = areas[i];
                var label = areas.Count > 1 ? $"{PickPointLabels[area.Kind]} {i + 1}" : PickPointLabels[area.Kind];
                var type = GridLayoutPlanner.PickPointKinds[area.Kind];
                var position = grid.CellCenter(area.Col, area.Row);

                PickPoint point;
                if (reusable.TryGetValue(area.Kind, out var queue) && queue.Count > 0)
                {
                    point = queue.Dequeue();
                    if (point.Label != label || point.Type != type) point.Update(label, type);
                    if (point.Position != position) point.MoveTo(position);
                }
                else
                {
                    point = new PickPoint(warehouseId, label, position, type);
                    await _repo.AddPickPointAsync(point, ct);
                }
                result.Add(new GridManagedPickPoint(point.Id, area.Kind));
            }
        }

        foreach (var leftover in reusable.Values.SelectMany(q => q))
            _repo.RemovePickPoint(leftover);
        return result;
    }

    // ---- Abbildung -------------------------------------------------------------------------------------------------

    private async Task<GridLayoutDto> ToDtoAsync(WarehouseEntity warehouse, GridLayout? grid, CancellationToken ct)
    {
        if (grid is null)
            return new GridLayoutDto(warehouse.Id, false, null, GridLayout.DefaultCellSizeMm, GridLayout.DefaultColumns, GridLayout.DefaultRows,
                GridLayout.DefaultContainerWidthMm, GridLayout.DefaultContainerDepthMm, GridLayout.DefaultContainerHeightMm,
                GridLayout.DefaultContainerMaxWeightGrams, Array.Empty<GridCellDto>(), Array.Empty<Guid>(), Array.Empty<Guid>(), Array.Empty<Guid>());

        var shelves = warehouse.Zones.SelectMany(z => z.Aisles).SelectMany(a => a.Shelves).ToDictionary(s => s.Id);
        var managedShelves = grid.Cells.Where(c => c.ShelfId is { } id && shelves.ContainsKey(id)).Select(c => shelves[c.ShelfId!.Value]).ToList();
        var bins = managedShelves.SelectMany(s => s.Locations).ToList();
        var usage = bins.Count == 0 ? null : await _repo.GetBinUsageAsync(bins.Select(b => b.Id).ToList(), ct);

        var cells = grid.Cells.Select(c =>
        {
            var shelf = c.ShelfId is { } id ? shelves.GetValueOrDefault(id) : null;
            var occupied = shelf is null || usage is null
                ? 0
                : shelf.Locations.Count(b => usage.WithStock.Contains(b.Id) || usage.WithOpenTasks.Contains(b.Id));
            return new GridCellDto(c.Col, c.Row, GridLayout.CellRef(c.Col, c.Row), c.Type.ToString(), c.Containers,
                shelf?.Id, shelf?.Code, occupied);
        }).ToList();

        var walls = (await _repo.ListWallsOfWarehouseAsync(warehouse.Id, ct)).Select(w => w.Id).ToHashSet();
        var points = (await _repo.ListPickPointsOfWarehouseAsync(warehouse.Id, ct)).Select(p => p.Id).ToHashSet();
        return new GridLayoutDto(warehouse.Id, true, grid.ConcurrencyToken, grid.CellSizeMm, grid.Columns, grid.Rows,
            grid.ContainerWidthMm, grid.ContainerDepthMm, grid.ContainerHeightMm, grid.ContainerMaxWeightGrams,
            cells,
            managedShelves.Select(s => s.Id).ToList(),
            grid.Managed.Walls.Where(walls.Contains).ToList(),
            grid.Managed.PickPoints.Select(p => p.Id).Where(points.Contains).ToList());
    }

    private static GridCell ParseCell(GridCellInput input)
    {
        if (!Enum.TryParse<GridCellType>(input.Type, ignoreCase: true, out var type) || !Enum.IsDefined(type) || int.TryParse(input.Type, out _))
            throw new ArgumentException($"Unbekannte Feldart '{input.Type}'.");
        return new GridCell(input.Col, input.Row, type, type.IsStorage() ? input.Containers : 0);
    }

    private static InvalidOperationException Rule(string code, string message)
    {
        var ex = new InvalidOperationException(message);
        ex.Data["code"] = code;
        return ex;
    }

    private sealed class SummaryCounter
    {
        public int ShelvesCreated;
        public int ShelvesRemoved;
        public int ContainersCreated;
        public int ContainersRemoved;
        public int Walls;
        public int PickPoints;

        public GridApplySummary ToDto() => new(ShelvesCreated, ShelvesRemoved, ContainersCreated, ContainersRemoved, Walls, PickPoints);
    }
}
