using Lager.Domain.Common;

namespace Lager.Domain.Articles;

/// <summary>
/// "1 Bundle X enthält N Stück Sub-Artikel Y". Wenn ein Bestell-Line einen
/// Bundle-Artikel referenziert, expandiert der PickList-Generator automatisch
/// in die Sub-Komponenten — der Bundle selbst hat keinen physischen Bestand.
/// </summary>
public class BundleComponent : Entity
{
    public Guid BundleArticleId { get; private set; }
    public Guid ComponentArticleId { get; private set; }
    public int Quantity { get; private set; }

    private BundleComponent() { }

    public BundleComponent(Guid componentArticleId, int quantity)
    {
        if (componentArticleId == Guid.Empty) throw new ArgumentException("ComponentArticleId", nameof(componentArticleId));
        if (quantity <= 0) throw new ArgumentException("Menge > 0", nameof(quantity));
        ComponentArticleId = componentArticleId;
        Quantity = quantity;
    }

    internal void AttachTo(Guid bundleId) => BundleArticleId = bundleId;
}
