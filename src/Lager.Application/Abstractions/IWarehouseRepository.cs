using Lager.Domain.Warehouse;
using WarehouseEntity = Lager.Domain.Warehouse.Warehouse;

namespace Lager.Application.Abstractions;

/// <summary>
/// Womit Lagerplätze noch verknüpft sind (Ergebnis von <see cref="IWarehouseRepository.GetBinUsageAsync"/>), jeweils als Menge
/// von Lagerplatz-Ids: <c>WithStock</c> = Bestand über 0, <c>WithOpenTasks</c> = offene Nachschub-Aufgabe oder offene Inventur,
/// <c>Referenced</c> = Belege verweisen noch darauf (Wareneingang, Retoure, Pickliste, abgeschlossene Aufgaben und Inventuren).
/// </summary>
public sealed record BinUsage(IReadOnlySet<Guid> WithStock, IReadOnlySet<Guid> WithOpenTasks, IReadOnlySet<Guid> Referenced);

public interface IWarehouseRepository
{
    Task<IReadOnlyList<WarehouseEntity>> ListWarehousesAsync(CancellationToken ct = default);
    Task<WarehouseEntity?> GetWarehouseAsync(Guid id, CancellationToken ct = default);
    Task AddWarehouseAsync(WarehouseEntity warehouse, CancellationToken ct = default);

    Task AddZoneAsync(Zone zone, CancellationToken ct = default);
    Task AddAisleAsync(Aisle aisle, CancellationToken ct = default);
    Task AddShelfAsync(Shelf shelf, CancellationToken ct = default);
    Task AddStorageLocationAsync(StorageLocation location, CancellationToken ct = default);

    Task<StorageLocation?> GetStorageLocationAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyDictionary<Guid, StorageLocation>> GetStorageLocationsAsync(IEnumerable<Guid> ids, CancellationToken ct = default);
    Task<IReadOnlyList<StorageLocation>> ListStorageLocationsAsync(CancellationToken ct = default);
    Task<Shelf?> GetShelfWithLocationsAsync(Guid id, CancellationToken ct = default);
    Task<Aisle?> GetAisleAsync(Guid id, CancellationToken ct = default);
    void RemoveStorageLocation(StorageLocation location);

    // ---- Lager-Stammdaten pflegen (WP22) ----
    Task<bool> WarehouseExistsAsync(Guid id, CancellationToken ct = default);
    Task<bool> ShelfExistsAsync(Guid id, CancellationToken ct = default);
    Task<Zone?> GetZoneAsync(Guid id, CancellationToken ct = default);
    /// <summary>Zone samt Gängen, Regalen und Lagerplätzen (verfolgt, damit sich ändern und löschen lässt).</summary>
    Task<Zone?> GetZoneWithTreeAsync(Guid id, CancellationToken ct = default);
    /// <summary>Gang samt Regalen und Lagerplätzen (verfolgt).</summary>
    Task<Aisle?> GetAisleWithTreeAsync(Guid id, CancellationToken ct = default);
    void RemoveWarehouse(WarehouseEntity warehouse);
    void RemoveZone(Zone zone);
    void RemoveAisle(Aisle aisle);
    void RemoveShelf(Shelf shelf);

    /// <summary>Gibt es schon ein Lager mit diesem Code (ohne Groß-/Kleinschreibung)? <paramref name="exceptId"/> = das Lager selbst beim Umbenennen.</summary>
    Task<bool> WarehouseCodeExistsAsync(string code, Guid? exceptId = null, CancellationToken ct = default);
    Task<bool> ZoneCodeExistsAsync(Guid warehouseId, string code, Guid? exceptId = null, CancellationToken ct = default);
    Task<bool> AisleCodeExistsAsync(Guid zoneId, string code, Guid? exceptId = null, CancellationToken ct = default);
    Task<bool> ShelfCodeExistsAsync(Guid aisleId, string code, Guid? exceptId = null, CancellationToken ct = default);
    /// <summary>Welche der Lagerplatz-Codes sind im ganzen System schon vergeben (ohne Groß-/Kleinschreibung)? Liefert die betroffenen Codes.</summary>
    Task<IReadOnlyList<string>> FindTakenBinCodesAsync(IReadOnlyCollection<string> codes, Guid? exceptBinId = null, CancellationToken ct = default);

    /// <summary>Verwendung der Lagerplätze durch Bestand, offene Aufgaben und Belege (Grundlage der Löschregeln).</summary>
    Task<BinUsage> GetBinUsageAsync(IReadOnlyCollection<Guid> binIds, CancellationToken ct = default);
    /// <summary>Entfernt die leeren Bestandszeilen (Menge 0) der Lagerplätze; sie blockieren sonst den Fremdschlüssel beim Löschen.</summary>
    Task RemoveEmptyStockRowsAsync(IReadOnlyCollection<Guid> binIds, CancellationToken ct = default);
    /// <summary>Wände und Pickpunkte eines Lagers (verfolgt); sie gehen mit dem Lager.</summary>
    Task<IReadOnlyList<Wall>> ListWallsOfWarehouseAsync(Guid warehouseId, CancellationToken ct = default);
    Task<IReadOnlyList<PickPoint>> ListPickPointsOfWarehouseAsync(Guid warehouseId, CancellationToken ct = default);

    Task<IReadOnlyList<Wall>> ListWallsAsync(CancellationToken ct = default);
    Task<Wall?> GetWallAsync(Guid id, CancellationToken ct = default);
    Task AddWallAsync(Wall wall, CancellationToken ct = default);
    void RemoveWall(Wall wall);

    Task<IReadOnlyList<PickPoint>> ListPickPointsAsync(CancellationToken ct = default);
    Task<PickPoint?> GetPickPointAsync(Guid id, CancellationToken ct = default);
    Task AddPickPointAsync(PickPoint pp, CancellationToken ct = default);
    void RemovePickPoint(PickPoint pp);
}
