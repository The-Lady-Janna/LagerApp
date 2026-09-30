using Lager.Application.Abstractions;
using Lager.Domain.Inventory;
using Lager.Domain.Stock;
using Lager.Domain.Warehouse;
using Microsoft.EntityFrameworkCore;
using WarehouseEntity = Lager.Domain.Warehouse.Warehouse;

namespace Lager.Infrastructure.Persistence.Repositories;

public class WarehouseRepository : IWarehouseRepository
{
    private readonly LagerDbContext _db;

    public WarehouseRepository(LagerDbContext db) => _db = db;

    public async Task<IReadOnlyList<WarehouseEntity>> ListWarehousesAsync(CancellationToken ct = default) =>
        await _db.Warehouses
            .AsNoTracking()
            .Include(w => w.Zones).ThenInclude(z => z.Aisles).ThenInclude(a => a.Shelves).ThenInclude(s => s.Locations)
            .AsSplitQuery()
            .ToListAsync(ct);

    public Task<WarehouseEntity?> GetWarehouseAsync(Guid id, CancellationToken ct = default) =>
        _db.Warehouses
            .Include(w => w.Zones).ThenInclude(z => z.Aisles).ThenInclude(a => a.Shelves).ThenInclude(s => s.Locations)
            .AsSplitQuery()
            .FirstOrDefaultAsync(w => w.Id == id, ct);

    public async Task AddWarehouseAsync(WarehouseEntity warehouse, CancellationToken ct = default) =>
        await _db.Warehouses.AddAsync(warehouse, ct);

    public async Task AddZoneAsync(Zone zone, CancellationToken ct = default) =>
        await _db.Zones.AddAsync(zone, ct);

    public async Task AddAisleAsync(Aisle aisle, CancellationToken ct = default) =>
        await _db.Aisles.AddAsync(aisle, ct);

    public async Task AddShelfAsync(Shelf shelf, CancellationToken ct = default) =>
        await _db.Shelves.AddAsync(shelf, ct);

    public async Task AddStorageLocationAsync(StorageLocation location, CancellationToken ct = default) =>
        await _db.StorageLocations.AddAsync(location, ct);

    public Task<StorageLocation?> GetStorageLocationAsync(Guid id, CancellationToken ct = default) =>
        _db.StorageLocations.FirstOrDefaultAsync(s => s.Id == id, ct);

    public async Task<IReadOnlyDictionary<Guid, StorageLocation>> GetStorageLocationsAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var idSet = ids.ToHashSet();
        var items = await _db.StorageLocations.Where(s => idSet.Contains(s.Id)).ToListAsync(ct);
        return items.ToDictionary(s => s.Id);
    }

    public async Task<IReadOnlyList<StorageLocation>> ListStorageLocationsAsync(CancellationToken ct = default) =>
        await _db.StorageLocations.AsNoTracking().OrderBy(s => s.Code).ToListAsync(ct);

    public Task<Shelf?> GetShelfWithLocationsAsync(Guid id, CancellationToken ct = default) =>
        _db.Shelves.Include(s => s.Locations).FirstOrDefaultAsync(s => s.Id == id, ct);

    public Task<Aisle?> GetAisleAsync(Guid id, CancellationToken ct = default) =>
        _db.Aisles.FirstOrDefaultAsync(a => a.Id == id, ct);

    public void RemoveStorageLocation(StorageLocation location) =>
        _db.StorageLocations.Remove(location);

    // ---- Lager-Stammdaten pflegen (WP22) ----

    /// <summary>So viele Ids/Codes je Abfrage: hält die IN-Listen klein (SQLite und MySQL haben Grenzen für Parameter).</summary>
    private const int ChunkSize = 500;

    public Task<bool> WarehouseExistsAsync(Guid id, CancellationToken ct = default) =>
        _db.Warehouses.AnyAsync(w => w.Id == id, ct);

    public Task<bool> ShelfExistsAsync(Guid id, CancellationToken ct = default) =>
        _db.Shelves.AnyAsync(s => s.Id == id, ct);

    public Task<Zone?> GetZoneAsync(Guid id, CancellationToken ct = default) =>
        _db.Zones.FirstOrDefaultAsync(z => z.Id == id, ct);

    public Task<Zone?> GetZoneWithTreeAsync(Guid id, CancellationToken ct = default) =>
        _db.Zones
            .Include(z => z.Aisles).ThenInclude(a => a.Shelves).ThenInclude(s => s.Locations)
            .AsSplitQuery()
            .FirstOrDefaultAsync(z => z.Id == id, ct);

    public Task<Aisle?> GetAisleWithTreeAsync(Guid id, CancellationToken ct = default) =>
        _db.Aisles
            .Include(a => a.Shelves).ThenInclude(s => s.Locations)
            .AsSplitQuery()
            .FirstOrDefaultAsync(a => a.Id == id, ct);

    public void RemoveWarehouse(WarehouseEntity warehouse) => _db.Warehouses.Remove(warehouse);

    public void RemoveZone(Zone zone) => _db.Zones.Remove(zone);

    public void RemoveAisle(Aisle aisle) => _db.Aisles.Remove(aisle);

    public void RemoveShelf(Shelf shelf) => _db.Shelves.Remove(shelf);

    // Die Codes der Geschwister werden geladen und im Speicher verglichen: so gilt "ohne Groß-/Kleinschreibung" auf SQLite
    // (vergleicht binär) und MySQL gleich, auch für Umlaute. Die Mengen sind klein (Zonen je Lager, Gänge je Zone ...).
    public async Task<bool> WarehouseCodeExistsAsync(string code, Guid? exceptId = null, CancellationToken ct = default)
    {
        var except = exceptId ?? Guid.Empty;
        var codes = await _db.Warehouses.AsNoTracking().Where(w => w.Id != except).Select(w => w.Code).ToListAsync(ct);
        return ContainsIgnoreCase(codes, code);
    }

    public async Task<bool> ZoneCodeExistsAsync(Guid warehouseId, string code, Guid? exceptId = null, CancellationToken ct = default)
    {
        var except = exceptId ?? Guid.Empty;
        var codes = await _db.Zones.AsNoTracking().Where(z => z.WarehouseId == warehouseId && z.Id != except).Select(z => z.Code).ToListAsync(ct);
        return ContainsIgnoreCase(codes, code);
    }

    public async Task<bool> AisleCodeExistsAsync(Guid zoneId, string code, Guid? exceptId = null, CancellationToken ct = default)
    {
        var except = exceptId ?? Guid.Empty;
        var codes = await _db.Aisles.AsNoTracking().Where(a => a.ZoneId == zoneId && a.Id != except).Select(a => a.Code).ToListAsync(ct);
        return ContainsIgnoreCase(codes, code);
    }

    public async Task<bool> ShelfCodeExistsAsync(Guid aisleId, string code, Guid? exceptId = null, CancellationToken ct = default)
    {
        var except = exceptId ?? Guid.Empty;
        var codes = await _db.Shelves.AsNoTracking().Where(s => s.AisleId == aisleId && s.Id != except).Select(s => s.Code).ToListAsync(ct);
        return ContainsIgnoreCase(codes, code);
    }

    // Lagerplatz-Codes gibt es viele: der Vergleich läuft in der Datenbank (lower() auf beiden Seiten). Sie kennt Groß-/Kleinschreibung
    // von Umlauten nicht überall (SQLite lower() nur ASCII); identische Texte fängt in jedem Fall der Unique-Index ab (409 duplicate).
    public async Task<IReadOnlyList<string>> FindTakenBinCodesAsync(IReadOnlyCollection<string> codes, Guid? exceptBinId = null, CancellationToken ct = default)
    {
        if (codes.Count == 0) return Array.Empty<string>();
        var except = exceptBinId ?? Guid.Empty;
        var taken = new List<string>();
        foreach (var chunk in codes.Select(c => c.Trim().ToLowerInvariant()).Distinct().Chunk(ChunkSize))
        {
            var lowered = chunk.ToList();
            var hits = await _db.StorageLocations.AsNoTracking()
                .Where(s => s.Id != except && lowered.Contains(s.Code.ToLower()))
                .Select(s => s.Code)
                .ToListAsync(ct);
            taken.AddRange(hits);
        }
        // Die Antwort nennt die angefragten Schreibweisen, nicht die gespeicherten.
        return codes.Where(c => ContainsIgnoreCase(taken, c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<BinUsage> GetBinUsageAsync(IReadOnlyCollection<Guid> binIds, CancellationToken ct = default)
    {
        var withStock = new HashSet<Guid>();
        var withOpenTasks = new HashSet<Guid>();
        var referenced = new HashSet<Guid>();

        foreach (var chunk in binIds.Distinct().Chunk(ChunkSize))
        {
            var ids = chunk.ToList();

            withStock.UnionWith(await _db.StockItems.AsNoTracking()
                .Where(s => ids.Contains(s.StorageLocationId) && s.Quantity > 0)
                .Select(s => s.StorageLocationId).Distinct().ToListAsync(ct));

            // Offene Aufgaben: Nachschub (Quelle oder Ziel) und Inventuren, die noch laufen.
            withOpenTasks.UnionWith(await _db.ReplenishmentTasks.AsNoTracking()
                .Where(t => t.Status == ReplenishmentStatus.Open && ids.Contains(t.SourceBinId))
                .Select(t => t.SourceBinId).Distinct().ToListAsync(ct));
            withOpenTasks.UnionWith(await _db.ReplenishmentTasks.AsNoTracking()
                .Where(t => t.Status == ReplenishmentStatus.Open && ids.Contains(t.TargetBinId))
                .Select(t => t.TargetBinId).Distinct().ToListAsync(ct));
            withOpenTasks.UnionWith(await _db.InventoryLines.AsNoTracking()
                .Where(l => ids.Contains(l.BinId)
                    && _db.InventoryCounts.Any(c => c.Id == l.InventoryCountId && c.Status == InventoryStatus.Open))
                .Select(l => l.BinId).Distinct().ToListAsync(ct));

            // Belege, die den Lagerplatz noch nennen (auch erledigte): Der Fremdschlüssel (Neu-Datenbanken) würde das Löschen ohnehin
            // verweigern, in älteren Datenbanken bliebe sonst ein Verweis ins Leere.
            referenced.UnionWith(await _db.ReplenishmentTasks.AsNoTracking()
                .Where(t => ids.Contains(t.SourceBinId)).Select(t => t.SourceBinId).Distinct().ToListAsync(ct));
            referenced.UnionWith(await _db.ReplenishmentTasks.AsNoTracking()
                .Where(t => ids.Contains(t.TargetBinId)).Select(t => t.TargetBinId).Distinct().ToListAsync(ct));
            referenced.UnionWith(await _db.InventoryLines.AsNoTracking()
                .Where(l => ids.Contains(l.BinId)).Select(l => l.BinId).Distinct().ToListAsync(ct));
            referenced.UnionWith(await _db.InboundLines.AsNoTracking()
                .Where(l => ids.Contains(l.TargetBinId)).Select(l => l.TargetBinId).Distinct().ToListAsync(ct));
            referenced.UnionWith(await _db.ReturnLines.AsNoTracking()
                .Where(l => l.TargetBinId != null && ids.Contains(l.TargetBinId.Value)).Select(l => l.TargetBinId!.Value).Distinct().ToListAsync(ct));
            referenced.UnionWith(await _db.PickItems.AsNoTracking()
                .Where(i => ids.Contains(i.StorageLocationId)).Select(i => i.StorageLocationId).Distinct().ToListAsync(ct));
        }

        return new BinUsage(withStock, withOpenTasks, referenced);
    }

    public async Task RemoveEmptyStockRowsAsync(IReadOnlyCollection<Guid> binIds, CancellationToken ct = default)
    {
        foreach (var chunk in binIds.Distinct().Chunk(ChunkSize))
        {
            var ids = chunk.ToList();
            var rows = await _db.StockItems.Where(s => ids.Contains(s.StorageLocationId) && s.Quantity == 0).ToListAsync(ct);
            _db.StockItems.RemoveRange(rows);
        }
    }

    public async Task<IReadOnlyList<Wall>> ListWallsOfWarehouseAsync(Guid warehouseId, CancellationToken ct = default) =>
        await _db.Walls.Where(w => w.WarehouseId == warehouseId).ToListAsync(ct);

    public async Task<IReadOnlyList<PickPoint>> ListPickPointsOfWarehouseAsync(Guid warehouseId, CancellationToken ct = default) =>
        await _db.PickPoints.Where(p => p.WarehouseId == warehouseId).ToListAsync(ct);

    private static bool ContainsIgnoreCase(IEnumerable<string> codes, string code)
    {
        var wanted = code.Trim();
        return codes.Any(c => c.Trim().Equals(wanted, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<IReadOnlyList<Wall>> ListWallsAsync(CancellationToken ct = default) =>
        await _db.Walls.AsNoTracking().OrderBy(w => w.CreatedAt).ToListAsync(ct);

    public Task<Wall?> GetWallAsync(Guid id, CancellationToken ct = default) =>
        _db.Walls.FirstOrDefaultAsync(w => w.Id == id, ct);

    public async Task AddWallAsync(Wall wall, CancellationToken ct = default) =>
        await _db.Walls.AddAsync(wall, ct);

    public void RemoveWall(Wall wall) => _db.Walls.Remove(wall);

    public async Task<IReadOnlyList<PickPoint>> ListPickPointsAsync(CancellationToken ct = default) =>
        await _db.PickPoints.AsNoTracking().OrderBy(p => p.Label).ToListAsync(ct);

    public Task<PickPoint?> GetPickPointAsync(Guid id, CancellationToken ct = default) =>
        _db.PickPoints.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task AddPickPointAsync(PickPoint pp, CancellationToken ct = default) =>
        await _db.PickPoints.AddAsync(pp, ct);

    public void RemovePickPoint(PickPoint pp) => _db.PickPoints.Remove(pp);
}
