using Lager.Contracts.Warehouse;
using Lager.Domain.Warehouse;

namespace Lager.Application.Warehouse;

internal static class WarehouseMapper
{
    public static PositionDto ToDto(Position p) => new(p.XMm, p.YMm, p.ZMm);
    public static Position ToPosition(PositionDto p) => new(p.XMm, p.YMm, p.ZMm);

    public static StorageLocationDto ToDto(StorageLocation s) => new(
        s.Id, s.ShelfId, s.Code, ToDto(s.Position), s.WidthMm, s.DepthMm, s.HeightMm, s.MaxWeightGrams,
        s.BinType.ToString(), s.ReplenishmentThreshold);

    public static ShelfDto ToDto(Shelf s) => new(
        s.Id, s.AisleId, s.Code, ToDto(s.Position), s.WidthMm, s.DepthMm, s.HeightMm,
        s.Locations.Select(ToDto).ToList());

    public static AisleDto ToDto(Aisle a) => new(
        a.Id, a.ZoneId, a.Code, ToDto(a.StartPosition), ToDto(a.EndPosition), a.Orientation.ToString(),
        a.Shelves.Select(ToDto).ToList());

    public static ZoneDto ToDto(Zone z) => new(
        z.Id, z.WarehouseId, z.Code, z.Name, ToDto(z.Origin),
        z.Aisles.Select(ToDto).ToList());

    public static WarehouseDto ToDto(Domain.Warehouse.Warehouse w) => new(
        w.Id, w.Code, w.Name, w.Zones.Select(ToDto).ToList());

    public static WallDto ToDto(Wall w) => new(
        w.Id, w.WarehouseId, w.Label, w.Points.Select(ToDto).ToList(), w.ThicknessMm);

    public static PickPointDto ToDto(PickPoint p) => new(
        p.Id, p.WarehouseId, p.Label, p.Type.ToString(), ToDto(p.Position));

    /// <summary>Ausrichtung eines Gangs aus dem Namen ("AlongX"/"AlongY", ohne Groß-/Kleinschreibung); fehlend = <paramref name="fallback"/>.</summary>
    public static AisleOrientation ToOrientation(string? s, AisleOrientation fallback)
    {
        if (string.IsNullOrWhiteSpace(s)) return fallback;
        if (!Enum.TryParse<AisleOrientation>(s.Trim(), ignoreCase: true, out var o) || !Enum.IsDefined(o))
            throw new ArgumentException($"Ungültige Ausrichtung '{s}'. Erlaubt: AlongX, AlongY.");
        return o;
    }

    public static PickPointType ToPickPointType(string s)
    {
        if (!Enum.TryParse<PickPointType>(s, ignoreCase: true, out var t))
            throw new ArgumentException($"Invalid PickPointType '{s}'. Use Start, End, or Both.");
        return t;
    }
}
