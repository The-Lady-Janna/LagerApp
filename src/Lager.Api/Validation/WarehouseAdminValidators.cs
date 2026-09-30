using FluentValidation;
using Lager.Contracts.Warehouse;
using Lager.Domain.Warehouse;

namespace Lager.Api.Validation;

/// <summary>
/// Grenzen für die Pflege der Lager-Stammdaten (Lager, Zonen, Gänge, Regale, Lagerplätze). Alle Maße in Millimetern.
/// Sie sind enger als die allgemeinen Grenzen der Anlage-Requests (<see cref="ValidationLimits"/>): ein Regal oder Lagerplatz
/// ist höchstens 100 m lang, ein Höchstgewicht höchstens 100 t. Die Grenzen der Anlage aus WP12 (500 Fächer je neuem Regal,
/// Code-Längen) bleiben unverändert; der Server begrenzt zusätzlich die Fächer je Regal auf <see cref="Shelf.MaxLocations"/>.
/// </summary>
public static class WarehouseAdminLimits
{
    /// <summary>Abmessung eines Regals oder Lagerplatzes in mm: größer 0, höchstens 100 m.</summary>
    public const int MaxDimensionMm = 100_000;
}

internal static class WarehouseAdminRules
{
    /// <summary>Abmessung in mm: 1..<see cref="WarehouseAdminLimits.MaxDimensionMm"/>.</summary>
    public static IRuleBuilderOptions<T, int> ShelfDimensionMm<T>(this IRuleBuilder<T, int> rule) =>
        rule.InclusiveBetween(1, WarehouseAdminLimits.MaxDimensionMm)
            .WithMessage("{PropertyPath} muss größer als 0 und höchstens " + WarehouseAdminLimits.MaxDimensionMm + " mm sein.");

    /// <summary>Optionale Ausrichtung: fehlend oder der Name eines <see cref="AisleOrientation"/>-Werts.</summary>
    public static IRuleBuilderOptions<T, string?> OptionalOrientation<T>(this IRuleBuilder<T, string?> rule) =>
        rule.Must(value => string.IsNullOrWhiteSpace(value) || EnumNames.IsDefinedName<AisleOrientation>(value))
            .WithMessage(EnumNames.Message<AisleOrientation>());
}

public class CreateWarehouseRequestValidator : AbstractValidator<CreateWarehouseRequest>
{
    public CreateWarehouseRequestValidator()
    {
        RuleFor(x => x.Code).RequiredText(ValidationLimits.MaxCodeLength);
        RuleFor(x => x.Name).RequiredText(ValidationLimits.MaxNameLength);
    }
}

public class UpdateWarehouseRequestValidator : AbstractValidator<UpdateWarehouseRequest>
{
    public UpdateWarehouseRequestValidator()
    {
        RuleFor(x => x.Code).RequiredText(ValidationLimits.MaxCodeLength);
        RuleFor(x => x.Name).RequiredText(ValidationLimits.MaxNameLength);
    }
}

public class CreateZoneRequestValidator : AbstractValidator<CreateZoneRequest>
{
    public CreateZoneRequestValidator()
    {
        RuleFor(x => x.WarehouseId).NotEmpty();
        RuleFor(x => x.Code).RequiredText(ValidationLimits.MaxCodeLength);
        RuleFor(x => x.Name).RequiredText(ValidationLimits.MaxNameLength);
        RuleFor(x => x.Origin!).SetValidator(new PositionDtoValidator()).When(x => x.Origin is not null);
    }
}

public class UpdateZoneRequestValidator : AbstractValidator<UpdateZoneRequest>
{
    public UpdateZoneRequestValidator()
    {
        RuleFor(x => x.Code).RequiredText(ValidationLimits.MaxCodeLength);
        RuleFor(x => x.Name).RequiredText(ValidationLimits.MaxNameLength);
        RuleFor(x => x.Origin!).SetValidator(new PositionDtoValidator()).When(x => x.Origin is not null);
    }
}

public class CreateAisleRequestValidator : AbstractValidator<CreateAisleRequest>
{
    public CreateAisleRequestValidator()
    {
        RuleFor(x => x.ZoneId).NotEmpty();
        RuleFor(x => x.Code).RequiredText(ValidationLimits.MaxCodeLength);
        RuleFor(x => x.StartPosition!).SetValidator(new PositionDtoValidator()).When(x => x.StartPosition is not null);
        RuleFor(x => x.EndPosition!).SetValidator(new PositionDtoValidator()).When(x => x.EndPosition is not null);
        RuleFor(x => x.Orientation).OptionalOrientation();
    }
}

public class UpdateAisleRequestValidator : AbstractValidator<UpdateAisleRequest>
{
    public UpdateAisleRequestValidator()
    {
        RuleFor(x => x.Code).RequiredText(ValidationLimits.MaxCodeLength);
        RuleFor(x => x.StartPosition!).SetValidator(new PositionDtoValidator()).When(x => x.StartPosition is not null);
        RuleFor(x => x.EndPosition!).SetValidator(new PositionDtoValidator()).When(x => x.EndPosition is not null);
        RuleFor(x => x.Orientation).OptionalOrientation();
    }
}

/// <summary>Regal ändern: Code, Abmessungen > 0 und höchstens 100 m. Regeln der Anlage (Fächer je Regal höchstens 500) bleiben bei <see cref="CreateShelfRequestValidator"/>.</summary>
public class UpdateShelfRequestValidator : AbstractValidator<UpdateShelfRequest>
{
    public UpdateShelfRequestValidator()
    {
        RuleFor(x => x.Code).RequiredText(ValidationLimits.MaxCodeLength);
        RuleFor(x => x.WidthMm).ShelfDimensionMm();
        RuleFor(x => x.DepthMm).ShelfDimensionMm();
        RuleFor(x => x.HeightMm).ShelfDimensionMm();
    }
}

/// <summary>Lagerplatz ändern: Code, Abmessungen > 0 und höchstens 100 m, Höchstgewicht >= 0 (Gramm, höchstens 100 t).</summary>
public class UpdateStorageLocationRequestValidator : AbstractValidator<UpdateStorageLocationRequest>
{
    public UpdateStorageLocationRequestValidator()
    {
        RuleFor(x => x.Code).RequiredText(ValidationLimits.MaxCodeLength);
        RuleFor(x => x.WidthMm).ShelfDimensionMm();
        RuleFor(x => x.DepthMm).ShelfDimensionMm();
        RuleFor(x => x.HeightMm).ShelfDimensionMm();
        RuleFor(x => x.MaxWeightGrams).WeightGrams();
    }
}
