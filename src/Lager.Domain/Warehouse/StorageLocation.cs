using Lager.Domain.Common;

namespace Lager.Domain.Warehouse;

public class StorageLocation : Entity
{
    public Guid ShelfId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public Position Position { get; private set; } = Position.Origin;
    public int WidthMm { get; private set; }
    public int DepthMm { get; private set; }
    public int HeightMm { get; private set; }
    public int MaxWeightGrams { get; private set; }

    /// <summary>
    /// Hot-Pick bins are the small, fast-access positions at the front of the
    /// pick-walk. Reserve bins hold the bulk stock used to refill Hot-Pick
    /// bins. Standard bins are everything in between (default).
    /// </summary>
    public BinType BinType { get; private set; } = BinType.Standard;

    /// <summary>
    /// Below this quantity (for any article in the bin) the replenishment
    /// scanner will create a refill task. Only meaningful for Hot-Pick bins.
    /// </summary>
    public int ReplenishmentThreshold { get; private set; }

    private StorageLocation() { }

    public StorageLocation(Guid shelfId, string code, Position position, int widthMm, int depthMm, int heightMm, int maxWeightGrams)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Code is required", nameof(code));

        ShelfId = shelfId;
        Code = code.Trim();
        Position = position;
        WidthMm = widthMm;
        DepthMm = depthMm;
        HeightMm = heightMm;
        MaxWeightGrams = maxWeightGrams;
    }

    public long VolumeMm3 => (long)WidthMm * DepthMm * HeightMm;

    public void MoveTo(Position newPosition)
    {
        Position = newPosition;
        Touch();
    }

    /// <summary>Code, Abmessungen und Höchstgewicht ändern. Die Position bleibt; die globale Eindeutigkeit des Codes prüft der Service.</summary>
    public void Update(string code, int widthMm, int depthMm, int heightMm, int maxWeightGrams)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Code ist erforderlich", nameof(code));
        if (widthMm <= 0) throw new ArgumentOutOfRangeException(nameof(widthMm), "Breite muss größer als 0 sein");
        if (depthMm <= 0) throw new ArgumentOutOfRangeException(nameof(depthMm), "Tiefe muss größer als 0 sein");
        if (heightMm <= 0) throw new ArgumentOutOfRangeException(nameof(heightMm), "Höhe muss größer als 0 sein");
        if (maxWeightGrams < 0) throw new ArgumentOutOfRangeException(nameof(maxWeightGrams), "Höchstgewicht darf nicht negativ sein");
        Code = code.Trim();
        WidthMm = widthMm;
        DepthMm = depthMm;
        HeightMm = heightMm;
        MaxWeightGrams = maxWeightGrams;
        Touch();
    }

    public void SetBinType(BinType type, int replenishmentThreshold)
    {
        if (replenishmentThreshold < 0)
            throw new ArgumentException("Schwelle darf nicht negativ sein", nameof(replenishmentThreshold));
        BinType = type;
        ReplenishmentThreshold = replenishmentThreshold;
        Touch();
    }

    internal void ShiftBy(int dxMm, int dyMm, int dzMm)
    {
        Position = new Position(Position.XMm + dxMm, Position.YMm + dyMm, Position.ZMm + dzMm);
        Touch();
    }
}

public enum BinType
{
    Standard = 0,
    HotPick = 1,
    Reserve = 2
}
