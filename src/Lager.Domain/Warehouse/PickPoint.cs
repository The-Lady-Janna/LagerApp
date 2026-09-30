using Lager.Domain.Common;

namespace Lager.Domain.Warehouse;

public enum PickPointType
{
    Start = 0,
    End = 1,
    Both = 2
}

public class PickPoint : Entity
{
    public Guid WarehouseId { get; private set; }
    public string Label { get; private set; } = string.Empty;
    public Position Position { get; private set; } = Position.Origin;
    public PickPointType Type { get; private set; }

    private PickPoint() { }

    public PickPoint(Guid warehouseId, string label, Position position, PickPointType type)
    {
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("Label required", nameof(label));
        WarehouseId = warehouseId;
        Label = label.Trim();
        Position = position;
        Type = type;
    }

    public void MoveTo(Position newPosition)
    {
        Position = newPosition;
        Touch();
    }

    public void Update(string label, PickPointType type)
    {
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("Label required", nameof(label));
        Label = label.Trim();
        Type = type;
        Touch();
    }
}
