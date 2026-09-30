using Lager.Domain.Common;

namespace Lager.Domain.Warehouse;

public enum AisleOrientation
{
    AlongX = 0,
    AlongY = 1
}

public class Aisle : Entity
{
    public Guid ZoneId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public Position StartPosition { get; private set; } = Position.Origin;
    public Position EndPosition { get; private set; } = Position.Origin;
    public AisleOrientation Orientation { get; private set; }

    private readonly List<Shelf> _shelves = new();
    public IReadOnlyCollection<Shelf> Shelves => _shelves.AsReadOnly();

    private Aisle() { }

    public Aisle(Guid zoneId, string code, Position start, Position end, AisleOrientation orientation)
    {
        ZoneId = zoneId;
        Code = code.Trim();
        StartPosition = start;
        EndPosition = end;
        Orientation = orientation;
    }

    /// <summary>Code, Lage und Ausrichtung ändern. Der Code ist Pflicht; seine Eindeutigkeit in der Zone prüft der Service.</summary>
    public void Update(string code, Position start, Position end, AisleOrientation orientation)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Code ist erforderlich", nameof(code));
        Code = code.Trim();
        StartPosition = start;
        EndPosition = end;
        Orientation = orientation;
        Touch();
    }
}
