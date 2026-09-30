namespace Lager.Contracts.Warehouse;

public record PositionDto(int XMm, int YMm, int ZMm);

public record StorageLocationDto(
    Guid Id,
    Guid ShelfId,
    string Code,
    PositionDto Position,
    int WidthMm,
    int DepthMm,
    int HeightMm,
    int MaxWeightGrams,
    string BinType = "Standard",
    int ReplenishmentThreshold = 0);

public record SetBinTypeRequest(string BinType, int ReplenishmentThreshold);

public record ShelfDto(
    Guid Id,
    Guid AisleId,
    string Code,
    PositionDto Position,
    int WidthMm,
    int DepthMm,
    int HeightMm,
    IReadOnlyList<StorageLocationDto> Locations);

public record AisleDto(
    Guid Id,
    Guid ZoneId,
    string Code,
    PositionDto StartPosition,
    PositionDto EndPosition,
    string Orientation,
    IReadOnlyList<ShelfDto> Shelves);

public record ZoneDto(
    Guid Id,
    Guid WarehouseId,
    string Code,
    string Name,
    PositionDto Origin,
    IReadOnlyList<AisleDto> Aisles);

public record WarehouseDto(
    Guid Id,
    string Code,
    string Name,
    IReadOnlyList<ZoneDto> Zones);

public record CreateStorageLocationRequest(
    Guid ShelfId,
    string Code,
    PositionDto Position,
    int WidthMm,
    int DepthMm,
    int HeightMm,
    int MaxWeightGrams);

public record UpdatePositionRequest(PositionDto Position);

public record CreateShelfRequest(
    Guid AisleId,
    string Code,
    PositionDto Position,
    int WidthMm,
    int DepthMm,
    int HeightMm,
    int InitialBinCount,
    int BinWidthMm,
    int BinDepthMm,
    int BinHeightMm,
    int BinMaxWeightGrams);

public record AddBinToShelfRequest(
    string Code,
    int WidthMm,
    int DepthMm,
    int HeightMm,
    int MaxWeightGrams);

public record WallDto(
    Guid Id,
    Guid WarehouseId,
    string? Label,
    IReadOnlyList<PositionDto> Points,
    int ThicknessMm);

public record CreateWallRequest(
    Guid WarehouseId,
    string? Label,
    IReadOnlyList<PositionDto> Points,
    int ThicknessMm);

public record UpdateWallPointsRequest(IReadOnlyList<PositionDto> Points);

public record PickPointDto(
    Guid Id,
    Guid WarehouseId,
    string Label,
    string Type,
    PositionDto Position);

public record CreatePickPointRequest(
    Guid WarehouseId,
    string Label,
    string Type,
    PositionDto Position);

public record UpdatePickPointRequest(string Label, string Type);


// ---- Lager-Stammdaten pflegen (WP22) -----------------------------------------------------------------------------

/// <summary>Neues Lager. Der Code ist im ganzen System eindeutig (ohne Groß-/Kleinschreibung).</summary>
public record CreateWarehouseRequest(string Code, string Name);

/// <summary>Lager umbenennen (Code und Name).</summary>
public record UpdateWarehouseRequest(string Code, string Name);

/// <summary>Neue Zone in einem Lager. <c>Origin</c> fehlend = Ursprung (0, 0, 0). Der Code ist im Lager eindeutig.</summary>
public record CreateZoneRequest(Guid WarehouseId, string Code, string Name, PositionDto? Origin = null);

/// <summary>Zone ändern. <c>Origin</c> fehlend = unverändert.</summary>
public record UpdateZoneRequest(string Code, string Name, PositionDto? Origin = null);

/// <summary>
/// Neuer Gang in einer Zone. Der Code ist in der Zone eindeutig. Fehlend: <c>Orientation</c> = "AlongX", <c>StartPosition</c> =
/// Ursprung, <c>EndPosition</c> = 10 m vom Start entlang der Ausrichtung.
/// </summary>
public record CreateAisleRequest(
    Guid ZoneId,
    string Code,
    PositionDto? StartPosition = null,
    PositionDto? EndPosition = null,
    string? Orientation = null);

/// <summary>Gang ändern. Lage und Ausrichtung fehlend = unverändert.</summary>
public record UpdateAisleRequest(
    string Code,
    PositionDto? StartPosition = null,
    PositionDto? EndPosition = null,
    string? Orientation = null);

/// <summary>Regal ändern (Code und Abmessungen). Die Position ändert <c>PUT shelves/{id}/position</c>.</summary>
public record UpdateShelfRequest(string Code, int WidthMm, int DepthMm, int HeightMm);

/// <summary>Lagerplatz ändern (Code, Abmessungen, Höchstgewicht). Bin-Typ und Schwelle ändert <c>PUT storage-locations/{id}/bin-type</c>.</summary>
public record UpdateStorageLocationRequest(string Code, int WidthMm, int DepthMm, int HeightMm, int MaxWeightGrams);
