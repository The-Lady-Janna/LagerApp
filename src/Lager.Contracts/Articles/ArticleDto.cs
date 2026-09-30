namespace Lager.Contracts.Articles;

public record BundleComponentDto(Guid ComponentArticleId, string ComponentSku, int Quantity);

/// <param name="Gtin">GTIN/EAN (nur Ziffern, 8/12/13/14 Stellen, gültige Prüfziffer) oder null.</param>
/// <param name="IsCurrentlyActive">
/// Vom Server berechnet: liegt heute innerhalb des Saison-Fensters (ValidFrom/ValidUntil, das Ende gilt als Kalendertag inklusive)?
/// false = außerhalb der Saison, nicht bestellbar.
/// </param>
public record ArticleDto(
    Guid Id,
    string Sku,
    string Name,
    string? Description,
    DimensionsDto Dimensions,
    int WeightGrams,
    StackingInfoDto Stacking,
    int MinStock = 0,
    int ReorderPoint = 0,
    int MaxStock = 0,
    Guid? PrimarySupplierId = null,
    int PurchasePriceCents = 0,
    string[]? AlternativeSkus = null,
    DateTime? ValidFrom = null,
    DateTime? ValidUntil = null,
    bool IsBundle = false,
    IReadOnlyList<BundleComponentDto>? BundleComponents = null,
    string? Gtin = null,
    bool IsCurrentlyActive = true);

/// <summary>
/// Neuer Artikel. <c>Gtin</c>: leer/fehlend = keine GTIN. <c>BundleComponents</c>: fehlend (null) = kein Bundle.
/// </summary>
public record CreateArticleRequest(
    string Sku,
    string Name,
    string? Description,
    DimensionsDto Dimensions,
    int WeightGrams,
    StackingInfoDto Stacking,
    int MinStock = 0,
    int ReorderPoint = 0,
    int MaxStock = 0,
    Guid? PrimarySupplierId = null,
    int PurchasePriceCents = 0,
    string[]? AlternativeSkus = null,
    DateTime? ValidFrom = null,
    DateTime? ValidUntil = null,
    IReadOnlyList<CreateBundleComponentRequest>? BundleComponents = null,
    string? Gtin = null);

/// <summary>
/// Artikel ändern (PUT = ganzer Stand). Semantik der Felder, die ein Client weglassen kann:
///  - <c>AlternativeSkus</c>, <c>ValidFrom</c>, <c>ValidUntil</c>: fehlend (null) = leeren/zurücksetzen (wie alle übrigen Felder).
///  - <c>BundleComponents</c>: fehlend (null) = UNVERÄNDERT; eine leere Liste = Bundle auflösen.
///  - <c>Gtin</c>: fehlend (null) = UNVERÄNDERT (ein Client, der das Feld nicht kennt, darf keine GTIN löschen);
///    leer oder nur Leerraum = GTIN entfernen.
/// </summary>
public record UpdateArticleRequest(
    string Name,
    string? Description,
    DimensionsDto Dimensions,
    int WeightGrams,
    StackingInfoDto Stacking,
    int MinStock = 0,
    int ReorderPoint = 0,
    int MaxStock = 0,
    Guid? PrimarySupplierId = null,
    int PurchasePriceCents = 0,
    string[]? AlternativeSkus = null,
    DateTime? ValidFrom = null,
    DateTime? ValidUntil = null,
    IReadOnlyList<CreateBundleComponentRequest>? BundleComponents = null,
    string? Gtin = null);

public record CreateBundleComponentRequest(Guid ComponentArticleId, int Quantity);
