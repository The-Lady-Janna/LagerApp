namespace Lager.Contracts.Articles;

public record StackingInfoDto(
    bool IsStackable,
    string StackingAxis,
    int StackingIncrementMm,
    int? MaxStackCount);
