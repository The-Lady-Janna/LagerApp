using Lager.Contracts.Articles;
using Lager.Domain.Articles;

namespace Lager.Application.Articles;

internal static class ArticleMapper
{
    /// <summary>
    /// <paramref name="componentSkus"/> (Artikel-Id -> SKU) füllt <c>BundleComponents[].ComponentSku</c>; die Zuordnung kennt der
    /// Aufrufer (die Komponenten sind andere Artikel). Fehlt eine Id, bleibt die SKU leer.
    /// </summary>
    public static ArticleDto ToDto(Article a, IReadOnlyDictionary<Guid, string>? componentSkus = null, DateTime? now = null) => new(
        a.Id,
        a.Sku,
        a.Name,
        a.Description,
        new DimensionsDto(a.Dimensions.LengthMm, a.Dimensions.WidthMm, a.Dimensions.HeightMm),
        a.WeightGrams,
        new StackingInfoDto(a.Stacking.IsStackable, a.Stacking.StackingAxis.ToString(), a.Stacking.StackingIncrementMm, a.Stacking.MaxStackCount),
        a.MinStock,
        a.ReorderPoint,
        a.MaxStock,
        a.PrimarySupplierId,
        a.PurchasePriceCents,
        a.AlternativeSkus.ToArray(),
        a.ValidFrom,
        a.ValidUntil,
        a.IsBundle,
        a.BundleComponents
            .Select(c => new BundleComponentDto(
                c.ComponentArticleId,
                componentSkus is not null && componentSkus.TryGetValue(c.ComponentArticleId, out var sku) ? sku : "",
                c.Quantity))
            .ToList(),
        a.Gtin,
        a.IsCurrentlyActive(now));

    public static Dimensions ToDimensions(DimensionsDto d) => new(d.LengthMm, d.WidthMm, d.HeightMm);

    public static StackingInfo ToStacking(StackingInfoDto s)
    {
        if (!Enum.TryParse<StackingAxis>(s.StackingAxis, ignoreCase: true, out var axis))
            throw new ArgumentException($"Invalid StackingAxis '{s.StackingAxis}'. Use X, Y, or Z.");
        return new StackingInfo(s.IsStackable, axis, s.StackingIncrementMm, s.MaxStackCount);
    }
}
