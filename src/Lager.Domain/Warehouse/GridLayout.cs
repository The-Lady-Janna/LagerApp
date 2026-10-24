using System.Text.Json;
using Lager.Domain.Common;

namespace Lager.Domain.Warehouse;

/// <summary>
/// Art eines Rasterfelds im einfachen Layout-Editor. Die Zahlenwerte stehen im JSON der Zellen und bleiben stabil.
/// <see cref="Walkway"/> ist der Normalfall und wird nicht gespeichert (leeres Feld = Laufweg).
/// </summary>
public enum GridCellType
{
    Walkway = 0,
    Wall = 1,
    Door = 2,
    Obstacle = 3,
    Shelf = 4,
    Start = 5,
    End = 6,
    Packing = 7,
    PackingStorage = 8,
    OutgoingStorage = 9,
    GoodsReceipt = 10,
    Returns = 11
}

/// <summary>Regeln zu den Feldarten: was Behälter hat, was den Weg versperrt, welcher Lagerplatz-Typ entsteht.</summary>
public static class GridCellTypes
{
    /// <summary>Lagerfelder: aus jedem entsteht ein Regal mit X Behältern (Lagerplätzen).</summary>
    public static bool IsStorage(this GridCellType type) =>
        type is GridCellType.Shelf or GridCellType.PackingStorage or GridCellType.OutgoingStorage or GridCellType.Returns;

    /// <summary>Felder, die den Laufweg versperren (werden zu Wänden für die Wegberechnung).</summary>
    public static bool IsBlocking(this GridCellType type) => type is GridCellType.Wall or GridCellType.Obstacle;

    /// <summary>Lagerplatz-Typ der Behälter eines Lagerfelds.</summary>
    public static BinType BinTypeOf(GridCellType type) => type switch
    {
        GridCellType.PackingStorage => BinType.Packing,
        GridCellType.OutgoingStorage => BinType.Outgoing,
        GridCellType.Returns => BinType.Returns,
        _ => BinType.Standard,
    };
}

/// <summary>
/// Ein belegtes Rasterfeld (Spalte und Zeile ab 0). <see cref="Containers"/> = Zahl der Behälter eines Lagerfelds, sonst 0.
/// <see cref="ShelfId"/> verknüpft ein Lagerfeld mit dem erzeugten Regal; das setzt nur der Dienst beim Anwenden.
/// </summary>
public sealed record GridCell(int Col, int Row, GridCellType Type, int Containers = 0, Guid? ShelfId = null);

/// <summary>
/// Was das Raster im Lager erzeugt hat und beim nächsten Speichern wiederverwendet (Ids der Zonen je Lagerfeld-Art, Gänge,
/// Wände und Pickpunkte). Alles, was hier nicht steht, gehört dem erweiterten Editor und bleibt unangetastet.
/// </summary>
public sealed record GridManagedObjects(
    IReadOnlyDictionary<GridCellType, Guid> Zones,
    IReadOnlyList<Guid> Aisles,
    IReadOnlyList<Guid> Walls,
    IReadOnlyList<GridManagedPickPoint> PickPoints)
{
    public static GridManagedObjects Empty { get; } = new(
        new Dictionary<GridCellType, Guid>(), Array.Empty<Guid>(), Array.Empty<Guid>(), Array.Empty<GridManagedPickPoint>());
}

/// <summary>Ein vom Raster erzeugter Pickpunkt und die Feldart, aus der er stammt (Start, Ende, Verpackung).</summary>
public sealed record GridManagedPickPoint(Guid Id, GridCellType Kind);

/// <summary>
/// Einfaches Lager-Layout als Raster (wie Zellen in einer Tabelle): jedes Feld ist Laufweg, Wand, Tür, Hindernis, Regal mit
/// X Behältern, Start, Ende, Verpackung, Pack- oder Ausgangspuffer, Wareneingang oder Retourenfläche. Ein Lager hat höchstens
/// ein Raster. Aus dem Raster erzeugt der GridLayoutService die gewohnten Objekte (Zonen, Gänge, Regale, Lagerplätze, Wände,
/// Pickpunkte), damit Wegberechnung, Einlagerung und Bestand unverändert weiterarbeiten.
/// </summary>
public class GridLayout : Entity
{
    public const int MinCellSizeMm = 250;
    public const int MaxCellSizeMm = 5_000;
    public const int MaxColumns = 100;
    public const int MaxRows = 100;
    public const int MaxContainersPerCell = 50;
    public const int DefaultCellSizeMm = 1_000;
    public const int DefaultColumns = 30;
    public const int DefaultRows = 20;
    public const int DefaultContainerWidthMm = 600;
    public const int DefaultContainerDepthMm = 600;
    public const int DefaultContainerHeightMm = 400;
    public const int DefaultContainerMaxWeightGrams = 50_000;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Guid WarehouseId { get; private set; }
    public int CellSizeMm { get; private set; } = DefaultCellSizeMm;
    public int Columns { get; private set; } = DefaultColumns;
    public int Rows { get; private set; } = DefaultRows;
    public int ContainerWidthMm { get; private set; } = DefaultContainerWidthMm;
    public int ContainerDepthMm { get; private set; } = DefaultContainerDepthMm;
    public int ContainerHeightMm { get; private set; } = DefaultContainerHeightMm;
    public int ContainerMaxWeightGrams { get; private set; } = DefaultContainerMaxWeightGrams;

    private List<GridCell> _cells = new();
    public IReadOnlyList<GridCell> Cells => _cells;

    private GridManagedObjects _managed = GridManagedObjects.Empty;
    public GridManagedObjects Managed => _managed;

    /// <summary>JSON der belegten Felder (EF-Spalte CellsJson). Nicht direkt benutzen, sondern über <see cref="Cells"/>.</summary>
    public string CellsJson
    {
        get => JsonSerializer.Serialize(_cells, Json);
        private set => _cells = string.IsNullOrEmpty(value) ? new() : JsonSerializer.Deserialize<List<GridCell>>(value, Json) ?? new();
    }

    /// <summary>JSON der erzeugten Objekte (EF-Spalte ManagedJson). Nicht direkt benutzen, sondern über <see cref="Managed"/>.</summary>
    public string ManagedJson
    {
        get => JsonSerializer.Serialize(_managed, Json);
        private set => _managed = string.IsNullOrEmpty(value)
            ? GridManagedObjects.Empty
            : JsonSerializer.Deserialize<GridManagedObjects>(value, Json) ?? GridManagedObjects.Empty;
    }

    private GridLayout() { }

    public GridLayout(Guid warehouseId)
    {
        if (warehouseId == Guid.Empty) throw new ArgumentException("Lager ist erforderlich", nameof(warehouseId));
        WarehouseId = warehouseId;
    }

    /// <summary>
    /// Ersetzt Rastergröße, Behältermaße und Felder. Laufwege werden verworfen (ein leeres Feld ist Laufweg). Die Verknüpfung
    /// mit dem erzeugten Regal (<see cref="GridCell.ShelfId"/>) setzt erst der Dienst beim Anwenden.
    /// </summary>
    public void Define(int cellSizeMm, int columns, int rows,
        int containerWidthMm, int containerDepthMm, int containerHeightMm, int containerMaxWeightGrams,
        IEnumerable<GridCell> cells)
    {
        if (cellSizeMm is < MinCellSizeMm or > MaxCellSizeMm)
            throw new ArgumentOutOfRangeException(nameof(cellSizeMm), $"Feldgröße muss zwischen {MinCellSizeMm} und {MaxCellSizeMm} mm liegen");
        if (columns is < 1 or > MaxColumns)
            throw new ArgumentOutOfRangeException(nameof(columns), $"Spaltenzahl muss zwischen 1 und {MaxColumns} liegen");
        if (rows is < 1 or > MaxRows)
            throw new ArgumentOutOfRangeException(nameof(rows), $"Zeilenzahl muss zwischen 1 und {MaxRows} liegen");
        if (containerWidthMm <= 0 || containerWidthMm > cellSizeMm)
            throw new ArgumentOutOfRangeException(nameof(containerWidthMm), "Behälterbreite muss größer 0 und höchstens so groß wie ein Feld sein");
        if (containerDepthMm <= 0 || containerDepthMm > cellSizeMm)
            throw new ArgumentOutOfRangeException(nameof(containerDepthMm), "Behältertiefe muss größer 0 und höchstens so groß wie ein Feld sein");
        if (containerHeightMm <= 0)
            throw new ArgumentOutOfRangeException(nameof(containerHeightMm), "Behälterhöhe muss größer 0 sein");
        if (containerMaxWeightGrams < 0)
            throw new ArgumentOutOfRangeException(nameof(containerMaxWeightGrams), "Höchstgewicht darf nicht negativ sein");

        var list = new List<GridCell>();
        var seen = new HashSet<(int, int)>();
        foreach (var cell in cells)
        {
            if (cell.Type == GridCellType.Walkway) continue;
            if (!Enum.IsDefined(cell.Type))
                throw new ArgumentException($"Unbekannte Feldart {(int)cell.Type}", nameof(cells));
            if (cell.Col < 0 || cell.Col >= columns || cell.Row < 0 || cell.Row >= rows)
                throw new ArgumentException($"Feld {CellRef(cell.Col, cell.Row)} liegt außerhalb des Rasters ({columns} x {rows})", nameof(cells));
            if (!seen.Add((cell.Col, cell.Row)))
                throw new ArgumentException($"Feld {CellRef(cell.Col, cell.Row)} ist doppelt angegeben", nameof(cells));
            if (cell.Type.IsStorage())
            {
                if (cell.Containers is < 1 or > MaxContainersPerCell)
                    throw new ArgumentException($"Feld {CellRef(cell.Col, cell.Row)}: ein Lagerfeld braucht 1 bis {MaxContainersPerCell} Behälter", nameof(cells));
                list.Add(cell);
            }
            else
            {
                list.Add(cell with { Containers = 0, ShelfId = null });
            }
        }

        CellSizeMm = cellSizeMm;
        Columns = columns;
        Rows = rows;
        ContainerWidthMm = containerWidthMm;
        ContainerDepthMm = containerDepthMm;
        ContainerHeightMm = containerHeightMm;
        ContainerMaxWeightGrams = containerMaxWeightGrams;
        _cells = list.OrderBy(c => c.Row).ThenBy(c => c.Col).ToList();
        Touch();
    }

    /// <summary>Übernimmt das Ergebnis des Anwendens: Felder mit Regal-Verknüpfung und die erzeugten Objekte.</summary>
    public void RecordApplied(IEnumerable<GridCell> cells, GridManagedObjects managed)
    {
        _cells = cells.OrderBy(c => c.Row).ThenBy(c => c.Col).ToList();
        _managed = managed;
        Touch();
    }

    /// <summary>Linke obere Ecke eines Felds in mm.</summary>
    public Position CellOrigin(int col, int row) => new(col * CellSizeMm, row * CellSizeMm, 0);

    /// <summary>Mitte eines Felds in mm.</summary>
    public Position CellCenter(int col, int row) => new(col * CellSizeMm + CellSizeMm / 2, row * CellSizeMm + CellSizeMm / 2, 0);

    /// <summary>Feldbezeichnung wie in einer Tabelle: Spalte als Buchstaben (A, B, ..., Z, AA ...), Zeile ab 1, z. B. "C5".</summary>
    public static string CellRef(int col, int row) => ColumnName(col) + (row + 1);

    public static string ColumnName(int col)
    {
        var name = string.Empty;
        var n = col + 1;
        while (n > 0)
        {
            var rem = (n - 1) % 26;
            name = (char)('A' + rem) + name;
            n = (n - 1) / 26;
        }
        return name;
    }
}
