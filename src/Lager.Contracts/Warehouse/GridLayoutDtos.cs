namespace Lager.Contracts.Warehouse;

// ---- Einfaches Raster-Layout ----------------------------------------------------------------------------------------

/// <summary>
/// Ein belegtes Rasterfeld. <c>Col</c>/<c>Row</c> ab 0, <c>Ref</c> wie in einer Tabelle ("C5"). <c>Type</c>: Wall, Door,
/// Obstacle, Shelf, Start, End, Packing, PackingStorage, OutgoingStorage, GoodsReceipt, Returns (Laufwege stehen nicht in der
/// Liste). Nur Lagerfelder haben <c>Containers</c> (Behälter), <c>ShelfId</c>/<c>ShelfCode</c> (erzeugtes Regal) und
/// <c>OccupiedContainers</c> (Behälter mit Bestand oder offenen Aufgaben).
/// </summary>
public record GridCellDto(
    int Col,
    int Row,
    string Ref,
    string Type,
    int Containers = 0,
    Guid? ShelfId = null,
    string? ShelfCode = null,
    int OccupiedContainers = 0);

/// <summary>
/// Raster-Layout eines Lagers. <c>Exists</c> = false: das Lager hat noch kein Raster, die Werte sind die Vorgaben für ein neues.
/// <c>Version</c> beim Speichern als <c>ExpectedVersion</c> zurückschicken (Schutz vor gleichzeitigem Bearbeiten).
/// <c>Managed*</c> = Ids der Objekte, die das Raster erzeugt hat; alle übrigen Regale, Wände und Pickpunkte des Lagers stammen
/// aus dem erweiterten Editor und bleiben beim Speichern unangetastet.
/// </summary>
public record GridLayoutDto(
    Guid WarehouseId,
    bool Exists,
    Guid? Version,
    int CellSizeMm,
    int Columns,
    int Rows,
    int ContainerWidthMm,
    int ContainerDepthMm,
    int ContainerHeightMm,
    int ContainerMaxWeightGrams,
    IReadOnlyList<GridCellDto> Cells,
    IReadOnlyList<Guid> ManagedShelfIds,
    IReadOnlyList<Guid> ManagedWallIds,
    IReadOnlyList<Guid> ManagedPickPointIds);

/// <summary>Ein Feld beim Speichern. <c>Containers</c> nur für Lagerfelder (1 bis 50).</summary>
public record GridCellInput(int Col, int Row, string Type, int Containers = 0);

/// <summary>
/// Raster speichern und anwenden: ersetzt alle Felder. Die Behältermaße gelten für alle Lagerfelder; Breite und Tiefe höchstens
/// eine Feldgröße. <c>ExpectedVersion</c> = <c>Version</c> aus dem letzten Abruf (fehlend nur beim ersten Speichern).
/// </summary>
public record SaveGridLayoutRequest(
    int CellSizeMm,
    int Columns,
    int Rows,
    int ContainerWidthMm,
    int ContainerDepthMm,
    int ContainerHeightMm,
    int ContainerMaxWeightGrams,
    IReadOnlyList<GridCellInput> Cells,
    Guid? ExpectedVersion = null);

/// <summary>Was das Speichern im Lager geändert hat.</summary>
public record GridApplySummary(
    int ShelvesCreated,
    int ShelvesRemoved,
    int ContainersCreated,
    int ContainersRemoved,
    int Walls,
    int PickPoints);

public record SaveGridLayoutResult(GridLayoutDto Layout, GridApplySummary Summary);

/// <summary>Ein Feld, das sich nicht ändern lässt, weil Behälter darin noch verwendet werden (Bestand, Aufgaben, Belege).</summary>
public record GridBlockedCellDto(int Col, int Row, string Ref, IReadOnlyList<string> ContainerCodes);
