using Lager.Application.Abstractions;
using Lager.Contracts.Warehouse;
using Lager.Domain.Warehouse;
using WarehouseEntity = Lager.Domain.Warehouse.Warehouse;

namespace Lager.Application.Warehouse;

/// <summary>Maschinenlesbare Fehlercodes der Lager-Stammdaten (<c>exception.Data["code"]</c>, Antwort 409).</summary>
public static class WarehouseErrorCodes
{
    /// <summary>Ein Code (Lager, Zone, Gang, Regal, Lagerplatz) ist im jeweiligen Geltungsbereich schon vergeben.</summary>
    public const string DuplicateCode = Shelf.DuplicateCodeError;

    /// <summary>Lager, Zone, Gang oder Regal lässt sich nicht löschen: darunter liegt ein Lagerplatz mit Bestand oder offenen Aufgaben.</summary>
    public const string NotEmpty = "warehouse_not_empty";

    /// <summary>Ein Lagerplatz wird noch verwendet (Bestand, offene Aufgaben) oder Belege verweisen darauf; Code wie der Datenbank-Fehler beim Löschen.</summary>
    public const string InUse = "in_use";
}

/// <summary>
/// Lager-Stammdaten: Lager, Zonen, Gänge, Regale und Lagerplätze anlegen, ändern und löschen.
/// Regeln: Codes sind je Elternteil eindeutig (Zone im Lager, Gang in der Zone, Regal im Gang, Lagerplatz im ganzen System;
/// ohne Groß-/Kleinschreibung), Konflikt = 409 <c>duplicate_code</c>. Gelöscht wird nur, wenn darunter kein Lagerplatz mit
/// Bestand (Menge über 0) oder offener Aufgabe (Nachschub, Inventur) liegt (409 <c>warehouse_not_empty</c>) und kein Beleg mehr
/// auf einen Lagerplatz verweist (409 <c>in_use</c>); leere Unterstrukturen werden mitgelöscht.
/// </summary>
public class WarehouseService
{
    private readonly IWarehouseRepository _repo;
    private readonly IUnitOfWork _uow;

    public WarehouseService(IWarehouseRepository repo, IUnitOfWork uow)
    {
        _repo = repo;
        _uow = uow;
    }

    public async Task<IReadOnlyList<WarehouseDto>> ListAsync(CancellationToken ct = default)
    {
        var items = await _repo.ListWarehousesAsync(ct);
        return items.Select(WarehouseMapper.ToDto).ToList();
    }

    public async Task<WarehouseDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var w = await _repo.GetWarehouseAsync(id, ct);
        return w is null ? null : WarehouseMapper.ToDto(w);
    }

    public async Task<WarehouseDto> CreateWarehouseAsync(CreateWarehouseRequest request, CancellationToken ct = default)
    {
        var code = request.Code.Trim();
        if (await _repo.WarehouseCodeExistsAsync(code, null, ct))
            throw DuplicateCode($"Der Lager-Code '{code}' ist schon vergeben.");

        var warehouse = new WarehouseEntity(code, request.Name);
        await _repo.AddWarehouseAsync(warehouse, ct);
        await _uow.SaveChangesAsync(ct);
        return WarehouseMapper.ToDto(warehouse);
    }

    public async Task<WarehouseDto?> UpdateWarehouseAsync(Guid id, UpdateWarehouseRequest request, CancellationToken ct = default)
    {
        var warehouse = await _repo.GetWarehouseAsync(id, ct);
        if (warehouse is null) return null;

        var code = request.Code.Trim();
        if (await _repo.WarehouseCodeExistsAsync(code, id, ct))
            throw DuplicateCode($"Der Lager-Code '{code}' ist schon vergeben.");

        warehouse.Update(code, request.Name);
        await _uow.SaveChangesAsync(ct);
        return WarehouseMapper.ToDto(warehouse);
    }

    /// <summary>Löscht das Lager samt Zonen, Gängen, Regalen, Lagerplätzen, Wänden und Pickpunkten - sofern nichts davon in Benutzung ist.</summary>
    public async Task<bool> DeleteWarehouseAsync(Guid id, CancellationToken ct = default)
    {
        var warehouse = await _repo.GetWarehouseAsync(id, ct);
        if (warehouse is null) return false;

        var bins = warehouse.Zones.SelectMany(z => z.Aisles).SelectMany(a => a.Shelves).SelectMany(s => s.Locations).ToList();
        await EnsureRemovableAsync(bins, $"Das Lager '{warehouse.Code}'", singleBin: false, ct);
        await _repo.RemoveEmptyStockRowsAsync(bins.Select(b => b.Id).ToList(), ct);

        // Wände und Pickpunkte hängen per Fremdschlüssel am Lager und gehen mit ihm.
        foreach (var wall in await _repo.ListWallsOfWarehouseAsync(id, ct)) _repo.RemoveWall(wall);
        foreach (var point in await _repo.ListPickPointsOfWarehouseAsync(id, ct)) _repo.RemovePickPoint(point);

        _repo.RemoveWarehouse(warehouse);
        await _uow.SaveChangesAsync(ct);
        return true;
    }

    // ---- Zone ----

    public async Task<ZoneDto> CreateZoneAsync(CreateZoneRequest request, CancellationToken ct = default)
    {
        if (!await _repo.WarehouseExistsAsync(request.WarehouseId, ct))
            throw new KeyNotFoundException("Das Lager wurde nicht gefunden.");

        var code = request.Code.Trim();
        if (await _repo.ZoneCodeExistsAsync(request.WarehouseId, code, null, ct))
            throw DuplicateCode($"Der Zonen-Code '{code}' ist in diesem Lager schon vergeben.");

        var origin = request.Origin is null ? Position.Origin : WarehouseMapper.ToPosition(request.Origin);
        var zone = new Zone(request.WarehouseId, code, request.Name, origin);
        await _repo.AddZoneAsync(zone, ct);
        await _uow.SaveChangesAsync(ct);
        return WarehouseMapper.ToDto(zone);
    }

    public async Task<ZoneDto?> UpdateZoneAsync(Guid id, UpdateZoneRequest request, CancellationToken ct = default)
    {
        var zone = await _repo.GetZoneWithTreeAsync(id, ct);
        if (zone is null) return null;

        var code = request.Code.Trim();
        if (await _repo.ZoneCodeExistsAsync(zone.WarehouseId, code, id, ct))
            throw DuplicateCode($"Der Zonen-Code '{code}' ist in diesem Lager schon vergeben.");

        var origin = request.Origin is null ? zone.Origin : WarehouseMapper.ToPosition(request.Origin);
        zone.Update(code, request.Name, origin);
        await _uow.SaveChangesAsync(ct);
        return WarehouseMapper.ToDto(zone);
    }

    public async Task<bool> DeleteZoneAsync(Guid id, CancellationToken ct = default)
    {
        var zone = await _repo.GetZoneWithTreeAsync(id, ct);
        if (zone is null) return false;

        var bins = zone.Aisles.SelectMany(a => a.Shelves).SelectMany(s => s.Locations).ToList();
        await EnsureRemovableAsync(bins, $"Die Zone '{zone.Code}'", singleBin: false, ct);
        await _repo.RemoveEmptyStockRowsAsync(bins.Select(b => b.Id).ToList(), ct);

        _repo.RemoveZone(zone);
        await _uow.SaveChangesAsync(ct);
        return true;
    }

    // ---- Gang ----

    /// <summary>Länge eines neuen Gangs ohne Angabe der Endposition (mm).</summary>
    private const int DefaultAisleLengthMm = 10_000;

    public async Task<AisleDto> CreateAisleAsync(CreateAisleRequest request, CancellationToken ct = default)
    {
        var zone = await _repo.GetZoneAsync(request.ZoneId, ct)
            ?? throw new KeyNotFoundException("Die Zone wurde nicht gefunden.");

        var code = request.Code.Trim();
        if (await _repo.AisleCodeExistsAsync(zone.Id, code, null, ct))
            throw DuplicateCode($"Der Gang-Code '{code}' ist in dieser Zone schon vergeben.");

        var orientation = WarehouseMapper.ToOrientation(request.Orientation, AisleOrientation.AlongX);
        var start = request.StartPosition is null ? Position.Origin : WarehouseMapper.ToPosition(request.StartPosition);
        var end = request.EndPosition is null
            ? new Position(
                start.XMm + (orientation == AisleOrientation.AlongX ? DefaultAisleLengthMm : 0),
                start.YMm + (orientation == AisleOrientation.AlongY ? DefaultAisleLengthMm : 0),
                start.ZMm)
            : WarehouseMapper.ToPosition(request.EndPosition);

        var aisle = new Aisle(zone.Id, code, start, end, orientation);
        await _repo.AddAisleAsync(aisle, ct);
        await _uow.SaveChangesAsync(ct);
        return WarehouseMapper.ToDto(aisle);
    }

    public async Task<AisleDto?> UpdateAisleAsync(Guid id, UpdateAisleRequest request, CancellationToken ct = default)
    {
        var aisle = await _repo.GetAisleWithTreeAsync(id, ct);
        if (aisle is null) return null;

        var code = request.Code.Trim();
        if (await _repo.AisleCodeExistsAsync(aisle.ZoneId, code, id, ct))
            throw DuplicateCode($"Der Gang-Code '{code}' ist in dieser Zone schon vergeben.");

        aisle.Update(
            code,
            request.StartPosition is null ? aisle.StartPosition : WarehouseMapper.ToPosition(request.StartPosition),
            request.EndPosition is null ? aisle.EndPosition : WarehouseMapper.ToPosition(request.EndPosition),
            WarehouseMapper.ToOrientation(request.Orientation, aisle.Orientation));
        await _uow.SaveChangesAsync(ct);
        return WarehouseMapper.ToDto(aisle);
    }

    public async Task<bool> DeleteAisleAsync(Guid id, CancellationToken ct = default)
    {
        var aisle = await _repo.GetAisleWithTreeAsync(id, ct);
        if (aisle is null) return false;

        var bins = aisle.Shelves.SelectMany(s => s.Locations).ToList();
        await EnsureRemovableAsync(bins, $"Der Gang '{aisle.Code}'", singleBin: false, ct);
        await _repo.RemoveEmptyStockRowsAsync(bins.Select(b => b.Id).ToList(), ct);

        _repo.RemoveAisle(aisle);
        await _uow.SaveChangesAsync(ct);
        return true;
    }

    // ---- Regal ----

    public async Task<ShelfDto?> UpdateShelfAsync(Guid id, UpdateShelfRequest request, CancellationToken ct = default)
    {
        var shelf = await _repo.GetShelfWithLocationsAsync(id, ct);
        if (shelf is null) return null;

        var code = request.Code.Trim();
        if (await _repo.ShelfCodeExistsAsync(shelf.AisleId, code, id, ct))
            throw DuplicateCode($"Der Regal-Code '{code}' ist in diesem Gang schon vergeben.");

        shelf.Update(code, request.WidthMm, request.DepthMm, request.HeightMm);
        await _uow.SaveChangesAsync(ct);
        return WarehouseMapper.ToDto(shelf);
    }

    public async Task<bool> DeleteShelfAsync(Guid id, CancellationToken ct = default)
    {
        var shelf = await _repo.GetShelfWithLocationsAsync(id, ct);
        if (shelf is null) return false;

        var bins = shelf.Locations.ToList();
        await EnsureRemovableAsync(bins, $"Das Regal '{shelf.Code}'", singleBin: false, ct);
        await _repo.RemoveEmptyStockRowsAsync(bins.Select(b => b.Id).ToList(), ct);

        _repo.RemoveShelf(shelf);
        await _uow.SaveChangesAsync(ct);
        return true;
    }

    // ---- Lagerplatz ----

    public async Task<StorageLocationDto> AddStorageLocationAsync(CreateStorageLocationRequest request, CancellationToken ct = default)
    {
        if (!await _repo.ShelfExistsAsync(request.ShelfId, ct))
            throw new KeyNotFoundException("Das Regal wurde nicht gefunden.");
        await EnsureBinCodesFreeAsync(new[] { request.Code }, null, ct);

        var loc = new StorageLocation(
            request.ShelfId,
            request.Code,
            WarehouseMapper.ToPosition(request.Position),
            request.WidthMm,
            request.DepthMm,
            request.HeightMm,
            request.MaxWeightGrams);

        await _repo.AddStorageLocationAsync(loc, ct);
        await _uow.SaveChangesAsync(ct);
        return WarehouseMapper.ToDto(loc);
    }

    public async Task<IReadOnlyList<StorageLocationDto>> ListStorageLocationsAsync(CancellationToken ct = default)
    {
        var items = await _repo.ListStorageLocationsAsync(ct);
        return items.Select(WarehouseMapper.ToDto).ToList();
    }

    public async Task<StorageLocationDto?> UpdateStorageLocationAsync(Guid id, UpdateStorageLocationRequest request, CancellationToken ct = default)
    {
        var loc = await _repo.GetStorageLocationAsync(id, ct);
        if (loc is null) return null;

        await EnsureBinCodesFreeAsync(new[] { request.Code }, id, ct);

        loc.Update(request.Code, request.WidthMm, request.DepthMm, request.HeightMm, request.MaxWeightGrams);
        await _uow.SaveChangesAsync(ct);
        return WarehouseMapper.ToDto(loc);
    }

    public async Task<StorageLocationDto?> MoveStorageLocationAsync(Guid id, PositionDto newPosition, CancellationToken ct = default)
    {
        var loc = await _repo.GetStorageLocationAsync(id, ct);
        if (loc is null) return null;
        loc.MoveTo(WarehouseMapper.ToPosition(newPosition));
        await _uow.SaveChangesAsync(ct);
        return WarehouseMapper.ToDto(loc);
    }

    public async Task<StorageLocationDto?> SetBinTypeAsync(Guid id, SetBinTypeRequest request, CancellationToken ct = default)
    {
        var loc = await _repo.GetStorageLocationAsync(id, ct);
        if (loc is null) return null;
        if (!Enum.TryParse<Domain.Warehouse.BinType>(request.BinType, ignoreCase: true, out var type))
            throw new ArgumentException($"Unbekannter BinType: {request.BinType}");
        loc.SetBinType(type, request.ReplenishmentThreshold);
        await _uow.SaveChangesAsync(ct);
        return WarehouseMapper.ToDto(loc);
    }

    public async Task<ShelfDto?> MoveShelfAsync(Guid id, PositionDto newPosition, CancellationToken ct = default)
    {
        var shelf = await _repo.GetShelfWithLocationsAsync(id, ct);
        if (shelf is null) return null;
        shelf.MoveTo(WarehouseMapper.ToPosition(newPosition));
        await _uow.SaveChangesAsync(ct);
        return WarehouseMapper.ToDto(shelf);
    }

    public async Task<ShelfDto> CreateShelfAsync(CreateShelfRequest request, CancellationToken ct = default)
    {
        var aisle = await _repo.GetAisleAsync(request.AisleId, ct)
            ?? throw new InvalidOperationException($"Aisle {request.AisleId} not found");

        var shelfCode = request.Code.Trim();
        if (await _repo.ShelfCodeExistsAsync(aisle.Id, shelfCode, null, ct))
            throw DuplicateCode($"Der Regal-Code '{shelfCode}' ist in diesem Gang schon vergeben.");
        // Die Codes der automatisch angelegten Fächer müssen im ganzen System frei sein (sonst bräche das Anlegen erst am Unique-Index ab).
        var binCodes = Enumerable.Range(1, Math.Max(0, request.InitialBinCount)).Select(i => $"{shelfCode}-{i:D2}").ToList();
        await EnsureBinCodesFreeAsync(binCodes, null, ct);

        var shelf = new Shelf(
            aisle.Id,
            shelfCode,
            WarehouseMapper.ToPosition(request.Position),
            request.WidthMm,
            request.DepthMm,
            request.HeightMm);

        foreach (var binCode in binCodes)
            shelf.AddLocation(binCode, request.BinWidthMm, request.BinDepthMm, request.BinHeightMm, request.BinMaxWeightGrams);

        await _repo.AddShelfAsync(shelf, ct);
        await _uow.SaveChangesAsync(ct);

        var reloaded = await _repo.GetShelfWithLocationsAsync(shelf.Id, ct);
        return WarehouseMapper.ToDto(reloaded!);
    }

    public async Task<StorageLocationDto?> AddBinToShelfAsync(Guid shelfId, AddBinToShelfRequest request, CancellationToken ct = default)
    {
        var shelf = await _repo.GetShelfWithLocationsAsync(shelfId, ct);
        if (shelf is null) return null;

        await EnsureBinCodesFreeAsync(new[] { request.Code }, null, ct);
        var bin = shelf.AddLocation(request.Code, request.WidthMm, request.DepthMm, request.HeightMm, request.MaxWeightGrams);
        // Das Fach hat seine Guid schon im Konstruktor. Hängt man es nur an das getrackte Regal, hält EF es für einen
        // bestehenden Datensatz (Modified): das UPDATE trifft 0 Zeilen -> DbUpdateConcurrencyException (409). Also
        // ausdrücklich als neu (Added) anlegen.
        await _repo.AddStorageLocationAsync(bin, ct);
        await _uow.SaveChangesAsync(ct);
        return WarehouseMapper.ToDto(bin);
    }

    public async Task<bool> DeleteBinAsync(Guid id, CancellationToken ct = default)
    {
        var bin = await _repo.GetStorageLocationAsync(id, ct);
        if (bin is null) return false;

        await EnsureRemovableAsync(new[] { bin }, $"Der Lagerplatz '{bin.Code}'", singleBin: true, ct);
        await _repo.RemoveEmptyStockRowsAsync(new[] { bin.Id }, ct);
        _repo.RemoveStorageLocation(bin);
        await _uow.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<WallDto>> ListWallsAsync(CancellationToken ct = default)
    {
        var items = await _repo.ListWallsAsync(ct);
        return items.Select(WarehouseMapper.ToDto).ToList();
    }

    public async Task<WallDto> CreateWallAsync(CreateWallRequest request, CancellationToken ct = default)
    {
        var wall = new Wall(
            request.WarehouseId,
            request.Points.Select(WarehouseMapper.ToPosition),
            request.ThicknessMm,
            request.Label);
        await _repo.AddWallAsync(wall, ct);
        await _uow.SaveChangesAsync(ct);
        return WarehouseMapper.ToDto(wall);
    }

    public async Task<WallDto?> UpdateWallPointsAsync(Guid id, UpdateWallPointsRequest request, CancellationToken ct = default)
    {
        var wall = await _repo.GetWallAsync(id, ct);
        if (wall is null) return null;
        wall.UpdatePoints(request.Points.Select(WarehouseMapper.ToPosition));
        await _uow.SaveChangesAsync(ct);
        return WarehouseMapper.ToDto(wall);
    }

    public async Task<bool> DeleteWallAsync(Guid id, CancellationToken ct = default)
    {
        var wall = await _repo.GetWallAsync(id, ct);
        if (wall is null) return false;
        _repo.RemoveWall(wall);
        await _uow.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<PickPointDto>> ListPickPointsAsync(CancellationToken ct = default)
    {
        var items = await _repo.ListPickPointsAsync(ct);
        return items.Select(WarehouseMapper.ToDto).ToList();
    }

    public async Task<PickPointDto> CreatePickPointAsync(CreatePickPointRequest request, CancellationToken ct = default)
    {
        var pp = new PickPoint(
            request.WarehouseId,
            request.Label,
            WarehouseMapper.ToPosition(request.Position),
            WarehouseMapper.ToPickPointType(request.Type));
        await _repo.AddPickPointAsync(pp, ct);
        await _uow.SaveChangesAsync(ct);
        return WarehouseMapper.ToDto(pp);
    }

    public async Task<PickPointDto?> UpdatePickPointAsync(Guid id, UpdatePickPointRequest request, CancellationToken ct = default)
    {
        var pp = await _repo.GetPickPointAsync(id, ct);
        if (pp is null) return null;
        pp.Update(request.Label, WarehouseMapper.ToPickPointType(request.Type));
        await _uow.SaveChangesAsync(ct);
        return WarehouseMapper.ToDto(pp);
    }

    public async Task<PickPointDto?> MovePickPointAsync(Guid id, PositionDto newPosition, CancellationToken ct = default)
    {
        var pp = await _repo.GetPickPointAsync(id, ct);
        if (pp is null) return null;
        pp.MoveTo(WarehouseMapper.ToPosition(newPosition));
        await _uow.SaveChangesAsync(ct);
        return WarehouseMapper.ToDto(pp);
    }

    public async Task<bool> DeletePickPointAsync(Guid id, CancellationToken ct = default)
    {
        var pp = await _repo.GetPickPointAsync(id, ct);
        if (pp is null) return false;
        _repo.RemovePickPoint(pp);
        await _uow.SaveChangesAsync(ct);
        return true;
    }

    // ---- Regeln für Codes und Löschen ------------------------------------------------------------------------------

    /// <summary>Wie viele Codes die Fehlermeldung höchstens nennt (der Rest steht als "und n weitere").</summary>
    private const int MaxCodesInMessage = 5;

    /// <summary>Lagerplatz-Codes sind im ganzen System eindeutig (ohne Groß-/Kleinschreibung). <paramref name="exceptBinId"/> = der Lagerplatz selbst beim Ändern.</summary>
    private async Task EnsureBinCodesFreeAsync(IReadOnlyCollection<string> codes, Guid? exceptBinId, CancellationToken ct)
    {
        var taken = await _repo.FindTakenBinCodesAsync(codes, exceptBinId, ct);
        if (taken.Count == 0) return;
        throw DuplicateCode(taken.Count == 1
            ? $"Der Lagerplatz-Code '{taken[0]}' ist schon vergeben."
            : $"Die Lagerplatz-Codes {ListCodes(taken)} sind schon vergeben.");
    }

    /// <summary>
    /// Prüft vorab (nicht erst am Fremdschlüssel), ob die Lagerplätze entfernt werden dürfen. Bestand über 0 oder eine offene Aufgabe
    /// (Nachschub, Inventur) lehnt ab: für Lager, Zone, Gang und Regal mit <c>warehouse_not_empty</c>, für einen einzelnen Lagerplatz
    /// mit <c>in_use</c>. Verweisen nur noch Belege auf einen Lagerplatz (Wareneingang, Retoure, Pickliste, erledigte Aufgaben),
    /// lehnt <c>in_use</c> ab. Leere Bestandszeilen (Menge 0) stören nicht, sie werden mit entfernt.
    /// </summary>
    private async Task EnsureRemovableAsync(IReadOnlyList<StorageLocation> bins, string subject, bool singleBin, CancellationToken ct)
    {
        if (bins.Count == 0) return;
        var usage = await _repo.GetBinUsageAsync(bins.Select(b => b.Id).ToList(), ct);

        var busy = bins.Where(b => usage.WithStock.Contains(b.Id) || usage.WithOpenTasks.Contains(b.Id)).Select(b => b.Code).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
        if (busy.Count > 0)
        {
            if (singleBin)
                throw Rule(WarehouseErrorCodes.InUse, $"{subject} kann nicht gelöscht werden, weil er noch verwendet wird (Bestand oder offene Aufgaben wie Nachschub und Inventur).");
            throw Rule(WarehouseErrorCodes.NotEmpty,
                $"{subject} kann nicht gelöscht werden: {DescribeBins(busy)} noch Bestand oder offene Aufgaben (Nachschub, Inventur). Bestand zuerst ausbuchen bzw. Aufgaben abschließen.");
        }

        var referenced = bins.Where(b => usage.Referenced.Contains(b.Id)).Select(b => b.Code).OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToList();
        if (referenced.Count > 0)
        {
            var what = referenced.Count == 1 ? $"Der Lagerplatz {ListCodes(referenced)} wird" : $"Die Lagerplätze {ListCodes(referenced)} werden";
            throw Rule(WarehouseErrorCodes.InUse,
                $"{subject} kann nicht gelöscht werden: {what} noch von Belegen verwendet (Wareneingang, Retoure, Pickliste, Inventur oder Nachschub).");
        }
    }

    private static string DescribeBins(IReadOnlyList<string> codes) =>
        codes.Count == 1 ? $"der Lagerplatz {ListCodes(codes)} hat" : $"die Lagerplätze {ListCodes(codes)} haben";

    private static string ListCodes(IReadOnlyList<string> codes)
    {
        var shown = string.Join(", ", codes.Take(MaxCodesInMessage).Select(c => $"'{c}'"));
        return codes.Count > MaxCodesInMessage ? $"{shown} und {codes.Count - MaxCodesInMessage} weitere" : shown;
    }

    private static InvalidOperationException DuplicateCode(string message) => Rule(WarehouseErrorCodes.DuplicateCode, message);

    private static InvalidOperationException Rule(string code, string message)
    {
        var ex = new InvalidOperationException(message);
        ex.Data["code"] = code;
        return ex;
    }
}
