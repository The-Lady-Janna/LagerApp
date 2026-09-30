using FluentValidation;
using Lager.Contracts.Articles;
using Lager.Domain.Articles;

namespace Lager.Api.Validation;

/// <summary>
/// Catches bad input before it reaches the domain. The Domain entity already
/// validates a lot of this, but FluentValidation gives us a 400 with field-
/// level errors instead of a generic 500.
/// </summary>
public class CreateArticleRequestValidator : AbstractValidator<CreateArticleRequest>
{
    public CreateArticleRequestValidator()
    {
        RuleFor(x => x.Sku)
            .NotEmpty().WithMessage("SKU darf nicht leer sein.")
            .MaximumLength(64);

        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name darf nicht leer sein.")
            .MaximumLength(256);

        RuleFor(x => x.Description).OptionalText(ValidationLimits.MaxTextLength);

        RuleFor(x => x.WeightGrams)
            .GreaterThanOrEqualTo(0).WithMessage("Gewicht darf nicht negativ sein.")
            .LessThanOrEqualTo(ValidationLimits.MaxWeightGrams).WithMessage("Gewicht darf höchstens " + ValidationLimits.MaxWeightGrams + " g betragen.");

        RuleFor(x => x.Dimensions).NotNull();
        When(x => x.Dimensions is not null, () =>
        {
            RuleFor(x => x.Dimensions.LengthMm).DimensionMm();
            RuleFor(x => x.Dimensions.WidthMm).DimensionMm();
            RuleFor(x => x.Dimensions.HeightMm).DimensionMm();
        });

        RuleFor(x => x.Stacking).NotNull();
        When(x => x.Stacking is not null, () =>
        {
            RuleFor(x => x.Stacking.StackingAxis).Must(ArticleRules.IsStackingAxis).WithMessage(ArticleRules.StackingAxisMessage);
            RuleFor(x => x.Stacking.StackingIncrementMm).InclusiveBetween(0, ValidationLimits.MaxDimensionMm);
            RuleFor(x => x.Stacking.MaxStackCount!.Value)
                .InclusiveBetween(1, ValidationLimits.MaxQuantity)
                .When(x => x.Stacking.MaxStackCount.HasValue)
                .WithMessage("MaxStackCount muss zwischen 1 und " + ValidationLimits.MaxQuantity + " liegen, wenn gesetzt.");
        });

        RuleFor(x => x.MinStock).InclusiveBetween(0, ValidationLimits.MaxStockLevel);
        RuleFor(x => x.ReorderPoint).InclusiveBetween(0, ValidationLimits.MaxStockLevel);
        RuleFor(x => x.MaxStock).InclusiveBetween(0, ValidationLimits.MaxStockLevel);
        RuleFor(x => x.PurchasePriceCents).GreaterThanOrEqualTo(0).WithMessage("PurchasePriceCents darf nicht negativ sein.");

        RuleFor(x => x.AlternativeSkus).Must(ArticleRules.AreValidAlternativeSkus).WithMessage(ArticleRules.AlternativeSkusMessage);
        RuleFor(x => x.BundleComponents).Must(ArticleRules.AreValidBundleComponents).WithMessage(ArticleRules.BundleComponentsMessage);
        RuleFor(x => x.Gtin).MaximumLength(ValidationLimits.MaxCodeLength);
        // GTIN: Ziffern, 8/12/13/14 Stellen, Prüfziffer nach GS1. Kein Wert ist gültig (keine GTIN bzw. beim Ändern "unverändert"/"entfernen").
        RuleFor(x => x.Gtin).Must(g => Gtin.GetError(g) is null).WithMessage(x => Gtin.GetError(x.Gtin) ?? "GTIN ist ungültig.");

        // Logical ordering: Min ≤ Reorder ≤ Max (0 = ungesetzt erlaubt)
        RuleFor(x => x).Custom((req, ctx) =>
        {
            if (req.ReorderPoint > 0 && req.MinStock > req.ReorderPoint)
                ctx.AddFailure("ReorderPoint", "ReorderPoint darf nicht kleiner als MinStock sein.");
            if (req.MaxStock > 0 && req.ReorderPoint > req.MaxStock)
                ctx.AddFailure("MaxStock", "MaxStock darf nicht kleiner als ReorderPoint sein.");
            if (req.MaxStock > 0 && req.MinStock > req.MaxStock)
                ctx.AddFailure("MaxStock", "MaxStock darf nicht kleiner als MinStock sein.");
            if (ArticleRules.IsSeasonEndBeforeStart(req.ValidFrom, req.ValidUntil))
                ctx.AddFailure("ValidUntil", "ValidUntil muss am oder nach ValidFrom liegen.");
        });
    }
}

public class UpdateArticleRequestValidator : AbstractValidator<UpdateArticleRequest>
{
    public UpdateArticleRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(256);
        RuleFor(x => x.Description).OptionalText(ValidationLimits.MaxTextLength);
        RuleFor(x => x.WeightGrams).InclusiveBetween(0, ValidationLimits.MaxWeightGrams);

        RuleFor(x => x.Dimensions).NotNull();
        When(x => x.Dimensions is not null, () =>
        {
            RuleFor(x => x.Dimensions.LengthMm).DimensionMm();
            RuleFor(x => x.Dimensions.WidthMm).DimensionMm();
            RuleFor(x => x.Dimensions.HeightMm).DimensionMm();
        });

        RuleFor(x => x.Stacking).NotNull();
        When(x => x.Stacking is not null, () =>
        {
            RuleFor(x => x.Stacking.StackingAxis).Must(ArticleRules.IsStackingAxis).WithMessage(ArticleRules.StackingAxisMessage);
            RuleFor(x => x.Stacking.StackingIncrementMm).InclusiveBetween(0, ValidationLimits.MaxDimensionMm);
            RuleFor(x => x.Stacking.MaxStackCount!.Value)
                .InclusiveBetween(1, ValidationLimits.MaxQuantity)
                .When(x => x.Stacking.MaxStackCount.HasValue);
        });

        RuleFor(x => x.MinStock).InclusiveBetween(0, ValidationLimits.MaxStockLevel);
        RuleFor(x => x.ReorderPoint).InclusiveBetween(0, ValidationLimits.MaxStockLevel);
        RuleFor(x => x.MaxStock).InclusiveBetween(0, ValidationLimits.MaxStockLevel);
        RuleFor(x => x.PurchasePriceCents).GreaterThanOrEqualTo(0);

        RuleFor(x => x.AlternativeSkus).Must(ArticleRules.AreValidAlternativeSkus).WithMessage(ArticleRules.AlternativeSkusMessage);
        RuleFor(x => x.BundleComponents).Must(ArticleRules.AreValidBundleComponents).WithMessage(ArticleRules.BundleComponentsMessage);
        RuleFor(x => x.Gtin).MaximumLength(ValidationLimits.MaxCodeLength);
        // GTIN: Ziffern, 8/12/13/14 Stellen, Prüfziffer nach GS1. Kein Wert ist gültig (keine GTIN bzw. beim Ändern "unverändert"/"entfernen").
        RuleFor(x => x.Gtin).Must(g => Gtin.GetError(g) is null).WithMessage(x => Gtin.GetError(x.Gtin) ?? "GTIN ist ungültig.");

        RuleFor(x => x).Custom((req, ctx) =>
        {
            if (req.ReorderPoint > 0 && req.MinStock > req.ReorderPoint)
                ctx.AddFailure("ReorderPoint", "ReorderPoint darf nicht kleiner als MinStock sein.");
            if (req.MaxStock > 0 && req.ReorderPoint > req.MaxStock)
                ctx.AddFailure("MaxStock", "MaxStock darf nicht kleiner als ReorderPoint sein.");
            if (req.MaxStock > 0 && req.MinStock > req.MaxStock)
                ctx.AddFailure("MaxStock", "MaxStock darf nicht kleiner als MinStock sein.");
            if (ArticleRules.IsSeasonEndBeforeStart(req.ValidFrom, req.ValidUntil))
                ctx.AddFailure("ValidUntil", "ValidUntil muss am oder nach ValidFrom liegen.");
        });
    }
}

/// <summary>Regeln, die Create- und Update-Validator gemeinsam brauchen (Listen mit Obergrenzen, Enum als Text).</summary>
internal static class ArticleRules
{
    public const string StackingAxisMessage = "StackingAxis muss X, Y oder Z sein.";
    /// <summary>Spaltenbreite von <c>Articles.AlternativeSkusCsv</c> (ArticleConfiguration): alle Alternativ-SKUs zusammen, mit Komma verbunden.</summary>
    public const int MaxAlternativeSkusStoredLength = 1000;

    public const string AlternativeSkusMessage =
        "AlternativeSkus: höchstens 500 Einträge, je höchstens 64 Zeichen, zusammen höchstens 1000 Zeichen und ohne Komma (das Komma trennt die Einträge beim Speichern).";
    public const string BundleComponentsMessage =
        "BundleComponents: höchstens 500 Einträge, je mit Artikel-Id und einer Menge von 1 bis 1.000.000.";

    /// <summary>
    /// Endet das Saison-Fenster vor seinem Beginn? Beide Grenzen sind Kalendertage (das Ende gilt inklusive): am selben Tag ist
    /// ein Ein-Tages-Fenster, keine Verletzung.
    /// </summary>
    public static bool IsSeasonEndBeforeStart(DateTime? validFrom, DateTime? validUntil) =>
        validFrom is not null && validUntil is not null && validUntil.Value.Date < validFrom.Value.Date;

    public static bool IsStackingAxis(string? axis) =>
        !string.IsNullOrWhiteSpace(axis) && Enum.GetNames<StackingAxis>().Contains(axis.Trim(), StringComparer.OrdinalIgnoreCase);

    public static bool AreValidAlternativeSkus(string[]? skus) =>
        skus is null
        || (skus.Length <= ValidationLimits.MaxListItems
            && skus.All(s => s is null || (s.Length <= ValidationLimits.MaxCodeLength && !s.Contains(',')))
            && StoredLength(skus) <= MaxAlternativeSkusStoredLength);

    /// <summary>
    /// Länge des gespeicherten CSV-Textes, so wie <c>Article.SetAlternatives</c> ihn bildet (ohne Leereinträge und
    /// Duplikate). SQLite erzwingt die Spaltenbreite nicht, MySQL bricht das Speichern mit "Data too long" ab (sonst ein 500).
    /// </summary>
    private static int StoredLength(IEnumerable<string?> skus) =>
        string.Join(",", skus
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)).Length;

    public static bool AreValidBundleComponents(IReadOnlyList<CreateBundleComponentRequest>? components) =>
        components is null
        || (components.Count <= ValidationLimits.MaxListItems
            && components.All(c => c is not null
                                   && c.ComponentArticleId != Guid.Empty
                                   && c.Quantity is >= 1 and <= ValidationLimits.MaxQuantity));
}
