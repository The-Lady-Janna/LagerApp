using Lager.Domain.Common;

namespace Lager.Domain.Warehouse;

public class Shelf : Entity
{
    /// <summary>Höchstzahl Lagerplätze je Regal (deckt sich mit der Grenze für neue Fächer in der API).</summary>
    public const int MaxLocations = 500;

    /// <summary>Fehlercode (<c>Data["code"]</c>), wenn ein Code schon vergeben ist.</summary>
    public const string DuplicateCodeError = "duplicate_code";

    /// <summary>Fehlercode (<c>Data["code"]</c>), wenn das Regal keine weiteren Lagerplätze aufnehmen darf.</summary>
    public const string LocationLimitError = "shelf_bin_limit";

    public Guid AisleId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public Position Position { get; private set; } = Position.Origin;
    public int WidthMm { get; private set; }
    public int DepthMm { get; private set; }
    public int HeightMm { get; private set; }

    private readonly List<StorageLocation> _locations = new();
    public IReadOnlyCollection<StorageLocation> Locations => _locations.AsReadOnly();

    private Shelf() { }

    public Shelf(Guid aisleId, string code, Position position, int widthMm, int depthMm, int heightMm)
    {
        AisleId = aisleId;
        Code = code.Trim();
        Position = position;
        WidthMm = widthMm;
        DepthMm = depthMm;
        HeightMm = heightMm;
    }

    public void MoveTo(Position newPosition)
    {
        var dx = newPosition.XMm - Position.XMm;
        var dy = newPosition.YMm - Position.YMm;
        var dz = newPosition.ZMm - Position.ZMm;
        Position = newPosition;
        foreach (var loc in _locations)
            loc.ShiftBy(dx, dy, dz);
        Touch();
    }

    /// <summary>Code und Abmessungen ändern. Die Fächer behalten ihre Codes und Positionen; die Eindeutigkeit des Codes im Gang prüft der Service.</summary>
    public void Update(string code, int widthMm, int depthMm, int heightMm)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Code ist erforderlich", nameof(code));
        if (widthMm <= 0) throw new ArgumentOutOfRangeException(nameof(widthMm), "Breite muss größer als 0 sein");
        if (depthMm <= 0) throw new ArgumentOutOfRangeException(nameof(depthMm), "Tiefe muss größer als 0 sein");
        if (heightMm <= 0) throw new ArgumentOutOfRangeException(nameof(heightMm), "Höhe muss größer als 0 sein");
        Code = code.Trim();
        WidthMm = widthMm;
        DepthMm = depthMm;
        HeightMm = heightMm;
        Touch();
    }

    public StorageLocation AddLocation(string code, int widthMm, int depthMm, int heightMm, int maxWeightGrams)
    {
        if (_locations.Any(l => l.Code.Equals(code.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw Rule(DuplicateCodeError, $"Der Lagerplatz-Code '{code.Trim()}' ist im Regal '{Code}' schon vergeben");
        if (_locations.Count >= MaxLocations)
            throw Rule(LocationLimitError, $"Ein Regal kann höchstens {MaxLocations} Lagerplätze haben");

        var nextX = NextBinXOffsetMm(widthMm);
        var position = new Position(Position.XMm + nextX, Position.YMm, Position.ZMm + 500);
        var loc = new StorageLocation(Id, code, position, widthMm, depthMm, heightMm, maxWeightGrams);
        _locations.Add(loc);
        Touch();
        return loc;
    }

    private static InvalidOperationException Rule(string code, string message)
    {
        var ex = new InvalidOperationException(message);
        ex.Data["code"] = code;
        return ex;
    }

    private int NextBinXOffsetMm(int incomingWidthMm)
    {
        if (_locations.Count == 0) return 0;
        var rightMostEnd = _locations.Max(l => (l.Position.XMm - Position.XMm) + l.WidthMm);
        var candidate = rightMostEnd;
        return candidate + incomingWidthMm <= WidthMm
            ? candidate
            : Math.Max(0, WidthMm - incomingWidthMm);
    }
}
