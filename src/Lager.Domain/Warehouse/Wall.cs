using System.Text.Json;
using Lager.Domain.Common;

namespace Lager.Domain.Warehouse;

public class Wall : Entity
{
    public Guid WarehouseId { get; private set; }
    public string? Label { get; private set; }
    public int ThicknessMm { get; private set; }

    private List<Position> _points = new();
    public IReadOnlyList<Position> Points => _points;

    /// <summary>
    /// JSON serialization of <see cref="Points"/>. EF maps this property to the PointsJson column.
    /// Nullable so EF tolerates DB rows where the backfill hasn't run yet.
    /// Not for direct application use — go through <see cref="Points"/> and <see cref="UpdatePoints"/>.
    /// </summary>
    public string? PointsJson
    {
        get => JsonSerializer.Serialize(_points);
        private set => _points = string.IsNullOrEmpty(value)
            ? new List<Position>()
            : JsonSerializer.Deserialize<List<Position>>(value) ?? new List<Position>();
    }

    public Position Start => _points.Count > 0 ? _points[0] : Position.Origin;
    public Position End => _points.Count > 0 ? _points[^1] : Position.Origin;

    private Wall() { }

    public Wall(Guid warehouseId, IEnumerable<Position> points, int thicknessMm, string? label = null)
    {
        if (thicknessMm <= 0) throw new ArgumentOutOfRangeException(nameof(thicknessMm));
        var pts = points.ToList();
        if (pts.Count < 2) throw new ArgumentException("Wall must have at least 2 points", nameof(points));

        WarehouseId = warehouseId;
        ThicknessMm = thicknessMm;
        Label = label;
        _points = pts;
    }

    public void UpdatePoints(IEnumerable<Position> points)
    {
        var pts = points.ToList();
        if (pts.Count < 2) throw new ArgumentException("Wall must have at least 2 points", nameof(points));
        _points = pts;
        Touch();
    }

    public void Rename(string? label)
    {
        Label = label;
        Touch();
    }
}
