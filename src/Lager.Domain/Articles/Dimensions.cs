namespace Lager.Domain.Articles;

public record Dimensions(int LengthMm, int WidthMm, int HeightMm)
{
    public long VolumeMm3 => (long)LengthMm * WidthMm * HeightMm;

    public static Dimensions Zero => new(0, 0, 0);
}
