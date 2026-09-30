using FluentValidation;
using Lager.Contracts.PickLists;

namespace Lager.Api.Validation;

/// <summary>Grenzen für Pickliste, Welle, Kommissionierwagen und Packen.</summary>
public static class PickingLimits
{
    /// <summary>Bestellungen, die eine Pickliste auf einmal aufnimmt (die Wegeberechnung läuft über alle Positionen).</summary>
    public const int MaxOrdersPerPickList = 200;
    /// <summary>Positionen im Pack-Request: eine Pickliste mit 200 Bestellungen hat leicht mehrere hundert Positionen.</summary>
    public const int MaxPackItems = 5000;
    /// <summary>Kommissionierwagen: Ebenen und Maße einer Ebene (mm). Das Volumen (Ebenen x B x T x H) passt so sicher in long.</summary>
    public const int MaxCartLevels = 100;
    public const int MaxCartLevelDimensionMm = 10_000;
}

public class GeneratePickListRequestValidator : AbstractValidator<GeneratePickListRequest>
{
    public GeneratePickListRequestValidator()
    {
        RuleFor(x => x.OrderIds)
            .UniqueIdList(PickingLimits.MaxOrdersPerPickList)
            .Must(ids => ids is null || ids.Count > 0).WithMessage("Es muss mindestens eine Bestellung angegeben werden.");
        RuleFor(x => x.StartPickPointId).OptionalId();
        RuleFor(x => x.EndPickPointId).OptionalId();
    }
}

public class RecalculatePickListRequestValidator : AbstractValidator<RecalculatePickListRequest>
{
    public RecalculatePickListRequestValidator()
    {
        RuleFor(x => x.StartPickPointId).OptionalId();
        RuleFor(x => x.EndPickPointId).OptionalId();
    }
}

public class GenerateCartPickListRequestValidator : AbstractValidator<GenerateCartPickListRequest>
{
    public GenerateCartPickListRequestValidator()
    {
        RuleFor(x => x.PickCartConfigId).NotEmpty();
        RuleFor(x => x.StartPickPointId).OptionalId();
        RuleFor(x => x.EndPickPointId).OptionalId();
    }
}

public class PackPickListRequestValidator : AbstractValidator<PackPickListRequest>
{
    public PackPickListRequestValidator()
    {
        RuleFor(x => x.Items)
            .NotNull().WithMessage("{PropertyPath} fehlt.")
            .Must(items => items is null || items.Count <= PickingLimits.MaxPackItems)
            .WithMessage("{PropertyPath} darf höchstens " + PickingLimits.MaxPackItems + " Einträge enthalten.");

        // Die Einzelprüfungen nur bei vernünftiger Listengröße (bei einer Riesenliste genügt die Meldung oben).
        RuleForEach(x => x.Items)
            .NotNull().WithMessage("{PropertyPath} darf kein leerer Eintrag sein.")
            .SetValidator(new ConfirmPackedItemRequestValidator())
            .When(x => x.Items is { Count: <= PickingLimits.MaxPackItems });

        RuleFor(x => x.Items)
            .Must(items => items.Where(i => i is not null).Select(i => i.PickItemId).Distinct().Count() == items.Count(i => i is not null))
            .WithMessage("{PropertyPath} enthält dieselbe Position mehrfach.")
            .When(x => x.Items is { Count: <= PickingLimits.MaxPackItems });
    }
}

public class ConfirmPackedItemRequestValidator : AbstractValidator<ConfirmPackedItemRequest>
{
    public ConfirmPackedItemRequestValidator()
    {
        RuleFor(x => x.PickItemId).NotEmpty();
        // 0 = nichts gepackt; nach oben begrenzt der Service zusätzlich auf die geplante Menge.
        RuleFor(x => x.ActualQuantity).NonNegativeQuantity();
    }
}

public class CreatePickWaveRequestValidator : AbstractValidator<CreatePickWaveRequest>
{
    public CreatePickWaveRequestValidator()
    {
        RuleFor(x => x.Description).OptionalText(500);
        RuleFor(x => x.OrderIds).IdList(ValidationLimits.MaxListItems);
    }
}

public class AddOrdersToWaveRequestValidator : AbstractValidator<AddOrdersToWaveRequest>
{
    public AddOrdersToWaveRequestValidator()
    {
        RuleFor(x => x.OrderIds).IdList(ValidationLimits.MaxListItems);
    }
}

public class ReleaseWaveRequestValidator : AbstractValidator<ReleaseWaveRequest>
{
    public ReleaseWaveRequestValidator()
    {
        RuleFor(x => x.StartPickPointId).OptionalId();
        RuleFor(x => x.EndPickPointId).OptionalId();
    }
}

public class CreatePickCartConfigRequestValidator : AbstractValidator<CreatePickCartConfigRequest>
{
    public CreatePickCartConfigRequestValidator()
    {
        RuleFor(x => x.Name).RequiredText(ValidationLimits.MaxLabelLength);
        RuleFor(x => x.LevelCount).CartLevels();
        RuleFor(x => x.LevelWidthMm).CartLevelDimension();
        RuleFor(x => x.LevelDepthMm).CartLevelDimension();
        RuleFor(x => x.LevelHeightMm).CartLevelDimension();
        RuleFor(x => x.MaxWeightGrams).CartMaxWeight();
    }
}

public class UpdatePickCartConfigRequestValidator : AbstractValidator<UpdatePickCartConfigRequest>
{
    public UpdatePickCartConfigRequestValidator()
    {
        RuleFor(x => x.Name).RequiredText(ValidationLimits.MaxLabelLength);
        RuleFor(x => x.LevelCount).CartLevels();
        RuleFor(x => x.LevelWidthMm).CartLevelDimension();
        RuleFor(x => x.LevelDepthMm).CartLevelDimension();
        RuleFor(x => x.LevelHeightMm).CartLevelDimension();
        RuleFor(x => x.MaxWeightGrams).CartMaxWeight();
    }
}

internal static class PickCartRules
{
    public static IRuleBuilderOptions<T, int> CartLevels<T>(this IRuleBuilder<T, int> rule) =>
        rule.InclusiveBetween(1, PickingLimits.MaxCartLevels)
            .WithMessage("{PropertyPath} muss zwischen 1 und " + PickingLimits.MaxCartLevels + " liegen.");

    public static IRuleBuilderOptions<T, int> CartLevelDimension<T>(this IRuleBuilder<T, int> rule) =>
        rule.InclusiveBetween(1, PickingLimits.MaxCartLevelDimensionMm)
            .WithMessage("{PropertyPath} muss zwischen 1 und " + PickingLimits.MaxCartLevelDimensionMm + " mm liegen.");

    public static IRuleBuilderOptions<T, int> CartMaxWeight<T>(this IRuleBuilder<T, int> rule) =>
        rule.InclusiveBetween(1, ValidationLimits.MaxWeightGrams)
            .WithMessage("{PropertyPath} muss zwischen 1 und " + ValidationLimits.MaxWeightGrams + " g liegen.");
}
