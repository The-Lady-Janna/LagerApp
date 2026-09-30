namespace Lager.Domain.Packing;

public record PackedItem(Guid ArticleId, string ArticleSku, int Quantity);

/// <summary>
/// Ein gepackter Karton. <paramref name="UsedVolumeMm3"/> ist das tatsächlich belegte Volumen (die Blöcke, die
/// im Karton platziert wurden), nicht die Summe der Artikelvolumen: gestapelte Einheiten teilen sich Platz.
/// </summary>
public record PackedCarton(
    int Index,
    CartonType CartonType,
    int TotalWeightGrams,
    long UsedVolumeMm3,
    IReadOnlyList<PackedItem> Items)
{
    public double FillRatio => CartonType.InnerVolumeMm3 == 0 ? 0 : (double)UsedVolumeMm3 / CartonType.InnerVolumeMm3;
}

public record PackingPlan(
    Guid OrderId,
    string OrderNumber,
    IReadOnlyList<PackedCarton> Cartons,
    IReadOnlyList<PackedItem> Unpacked)
{
    /// <summary>
    /// Hinweise zum Plan (deutsche Klartexte), z. B. Artikel ohne gültige Abmessungen. Leer, wenn nichts auffiel.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
