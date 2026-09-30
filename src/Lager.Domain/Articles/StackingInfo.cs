namespace Lager.Domain.Articles;

public record StackingInfo(
    bool IsStackable,
    StackingAxis StackingAxis,
    int StackingIncrementMm,
    int? MaxStackCount)
{
    public static StackingInfo NotStackable => new(false, StackingAxis.Z, 0, 1);

    public int StackedSizeMm(int unitCount, int baseSizeMm)
    {
        if (unitCount <= 0) return 0;
        if (!IsStackable || unitCount == 1) return baseSizeMm;
        return baseSizeMm + (unitCount - 1) * StackingIncrementMm;
    }
}
