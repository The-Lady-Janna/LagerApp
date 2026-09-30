using Lager.Domain.Common;

namespace Lager.Domain.Warehouse;

public class Zone : Entity
{
    public Guid WarehouseId { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public Position Origin { get; private set; } = Position.Origin;

    private readonly List<Aisle> _aisles = new();
    public IReadOnlyCollection<Aisle> Aisles => _aisles.AsReadOnly();

    private Zone() { }

    public Zone(Guid warehouseId, string code, string name, Position origin)
    {
        WarehouseId = warehouseId;
        Code = code.Trim();
        Name = name.Trim();
        Origin = origin;
    }

    /// <summary>Code, Name und Ursprung ändern. Code und Name sind Pflicht; die Eindeutigkeit des Codes im Lager prüft der Service.</summary>
    public void Update(string code, string name, Position origin)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Code ist erforderlich", nameof(code));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name ist erforderlich", nameof(name));
        Code = code.Trim();
        Name = name.Trim();
        Origin = origin;
        Touch();
    }
}
