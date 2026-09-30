using Lager.Domain.Common;

namespace Lager.Domain.Warehouse;

public class Warehouse : Entity
{
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;

    private readonly List<Zone> _zones = new();
    public IReadOnlyCollection<Zone> Zones => _zones.AsReadOnly();

    private Warehouse() { }

    public Warehouse(string code, string name)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Code is required", nameof(code));
        Code = code.Trim();
        Name = name.Trim();
    }

    /// <summary>Code und Name ändern (Umbenennen). Beides ist Pflicht; die Eindeutigkeit des Codes prüft der Service.</summary>
    public void Update(string code, string name)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Code ist erforderlich", nameof(code));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name ist erforderlich", nameof(name));
        Code = code.Trim();
        Name = name.Trim();
        Touch();
    }
}
