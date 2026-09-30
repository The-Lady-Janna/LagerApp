namespace Lager.Contracts.Packing;

public record CartonAllocationDto(Guid ArticleId, string ArticleSku, int Quantity);

public record CartonDto(
    int Index,
    string CartonType,
    int InnerLengthMm,
    int InnerWidthMm,
    int InnerHeightMm,
    int TotalWeightGrams,
    double FillRatio,
    IReadOnlyList<CartonAllocationDto> Allocations);

/// <summary>
/// Verpackungsvorschlag. <paramref name="Warnings"/> nennt Besonderheiten in Klartext (z. B. fehlende Artikel oder
/// Artikel ohne Abmessungen, die nicht verpackt werden konnten); leer, wenn nichts auffiel.
/// </summary>
public record PackingPlanDto(
    Guid OrderId,
    string OrderNumber,
    IReadOnlyList<CartonDto> Cartons,
    IReadOnlyList<CartonAllocationDto> Unpacked,
    IReadOnlyList<string>? Warnings = null);
