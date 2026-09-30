namespace Lager.Domain.Warehouse;

public record Position(int XMm, int YMm, int ZMm)
{
    public static Position Origin => new(0, 0, 0);

    public int ManhattanDistanceTo(Position other) =>
        Math.Abs(XMm - other.XMm) + Math.Abs(YMm - other.YMm) + Math.Abs(ZMm - other.ZMm);
}
