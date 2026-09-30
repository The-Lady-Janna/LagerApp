using FluentValidation;
using Lager.Contracts.Warehouse;
using Lager.Domain.Warehouse;

namespace Lager.Api.Validation;

/// <summary>Grenzen für die Lager-Requests (Regale, Fächer, Wände, Pickpunkte). Alle Maße in Millimetern.</summary>
public static class WarehouseLimits
{
    /// <summary>Fächer, die ein Regal beim Anlegen sofort bekommt (jedes Fach kostet beim Anlegen Zeit und Speicher).</summary>
    public const int MaxInitialBins = 500;
    /// <summary>Punkte einer Wand: die Pickrouten-Berechnung wächst quadratisch mit der Zahl der Wandsegmente.</summary>
    public const int MaxWallPoints = 500;
    /// <summary>Bin-Code = Regalcode + "-" + laufende Nummer (bis 3 Stellen): der Regalcode muss dafür 4 Zeichen Platz lassen.</summary>
    public const int MaxShelfCodeWithBins = ValidationLimits.MaxCodeLength - 4;

    public static readonly string PointCountMessage = "Eine Wand braucht zwischen 2 und " + MaxWallPoints + " Punkte.";
}

internal static class WallPointCount
{
    /// <summary>true, wenn die Liste fehlt (das meldet NotNull) oder 2..<see cref="WarehouseLimits.MaxWallPoints"/> Punkte hat.</summary>
    public static bool IsValid(IReadOnlyList<PositionDto>? points) =>
        points is null || (points.Count >= 2 && points.Count <= WarehouseLimits.MaxWallPoints);
}

/// <summary>Namen der erlaubten Werte einer Enum, für Texteingaben wie BinType oder Pickpunkt-Typ.</summary>
internal static class EnumNames
{
    public static bool IsDefinedName<TEnum>(string? value) where TEnum : struct, Enum =>
        !string.IsNullOrWhiteSpace(value)
        && Enum.GetNames<TEnum>().Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    public static string Message<TEnum>() where TEnum : struct, Enum =>
        "{PropertyPath} muss einer der Werte " + string.Join(", ", Enum.GetNames<TEnum>()) + " sein.";
}

public class UpdatePositionRequestValidator : AbstractValidator<UpdatePositionRequest>
{
    public UpdatePositionRequestValidator()
    {
        RuleFor(x => x.Position).NotNull().SetValidator(new PositionDtoValidator());
    }
}

public class CreateStorageLocationRequestValidator : AbstractValidator<CreateStorageLocationRequest>
{
    public CreateStorageLocationRequestValidator()
    {
        RuleFor(x => x.ShelfId).NotEmpty();
        RuleFor(x => x.Code).RequiredText(ValidationLimits.MaxCodeLength);
        RuleFor(x => x.Position).NotNull().SetValidator(new PositionDtoValidator());
        RuleFor(x => x.WidthMm).DimensionMm();
        RuleFor(x => x.DepthMm).DimensionMm();
        RuleFor(x => x.HeightMm).DimensionMm();
        RuleFor(x => x.MaxWeightGrams).WeightGrams();
    }
}

/// <summary>
/// Regal anlegen. <c>InitialBinCount</c> ist auf 500 begrenzt: der Service legt die Fächer in einer Schleife an,
/// und jedes Anlegen prüft alle bisherigen Fächer (quadratischer Aufwand) - eine große Zahl würde den Server blockieren.
/// </summary>
public class CreateShelfRequestValidator : AbstractValidator<CreateShelfRequest>
{
    public CreateShelfRequestValidator()
    {
        RuleFor(x => x.AisleId).NotEmpty();
        RuleFor(x => x.Code).RequiredText(ValidationLimits.MaxCodeLength);
        RuleFor(x => x.Code)
            .MaximumLength(WarehouseLimits.MaxShelfCodeWithBins)
            .WithMessage("{PropertyPath} darf mit automatisch angelegten Fächern höchstens " + WarehouseLimits.MaxShelfCodeWithBins + " Zeichen lang sein.")
            .When(x => x.InitialBinCount > 0);
        RuleFor(x => x.Position).NotNull().SetValidator(new PositionDtoValidator());
        RuleFor(x => x.WidthMm).DimensionMm();
        RuleFor(x => x.DepthMm).DimensionMm();
        RuleFor(x => x.HeightMm).DimensionMm();

        RuleFor(x => x.InitialBinCount)
            .InclusiveBetween(0, WarehouseLimits.MaxInitialBins)
            .WithMessage("{PropertyPath} muss zwischen 0 und " + WarehouseLimits.MaxInitialBins + " liegen.");

        // Die Fach-Maße zählen nur, wenn wirklich Fächer entstehen.
        When(x => x.InitialBinCount > 0, () =>
        {
            RuleFor(x => x.BinWidthMm).DimensionMm();
            RuleFor(x => x.BinDepthMm).DimensionMm();
            RuleFor(x => x.BinHeightMm).DimensionMm();
            RuleFor(x => x.BinMaxWeightGrams).WeightGrams();
        });
    }
}

public class AddBinToShelfRequestValidator : AbstractValidator<AddBinToShelfRequest>
{
    public AddBinToShelfRequestValidator()
    {
        RuleFor(x => x.Code).RequiredText(ValidationLimits.MaxCodeLength);
        RuleFor(x => x.WidthMm).DimensionMm();
        RuleFor(x => x.DepthMm).DimensionMm();
        RuleFor(x => x.HeightMm).DimensionMm();
        RuleFor(x => x.MaxWeightGrams).WeightGrams();
    }
}

public class SetBinTypeRequestValidator : AbstractValidator<SetBinTypeRequest>
{
    public SetBinTypeRequestValidator()
    {
        RuleFor(x => x.BinType)
            .Must(EnumNames.IsDefinedName<BinType>).WithMessage(EnumNames.Message<BinType>());
        RuleFor(x => x.ReplenishmentThreshold).NonNegativeQuantity();
    }
}

/// <summary>
/// Wand anlegen. Die Punktzahl ist auf 500 begrenzt: die Wegeberechnung der Picklisten baut aus den Wandsegmenten einen
/// Sichtbarkeitsgraphen (Speicher und Zeit wachsen quadratisch) - eine einzige riesige Wand würde jede Pickliste blockieren.
/// </summary>
public class CreateWallRequestValidator : AbstractValidator<CreateWallRequest>
{
    public CreateWallRequestValidator()
    {
        RuleFor(x => x.WarehouseId).NotEmpty();
        RuleFor(x => x.Label).OptionalText(ValidationLimits.MaxLabelLength);
        RuleFor(x => x.Points)
            .NotNull().WithMessage("{PropertyPath} fehlt.")
            .Must(WallPointCount.IsValid).WithMessage(WarehouseLimits.PointCountMessage);
        // Die einzelnen Punkte werden nur geprüft, wenn die Anzahl stimmt (bei einer Riesenliste bricht schon die Zählung ab).
        RuleForEach(x => x.Points).NotNull().SetValidator(new PositionDtoValidator())
            .When(x => WallPointCount.IsValid(x.Points));
        RuleFor(x => x.ThicknessMm).DimensionMm();
    }
}

public class UpdateWallPointsRequestValidator : AbstractValidator<UpdateWallPointsRequest>
{
    public UpdateWallPointsRequestValidator()
    {
        RuleFor(x => x.Points)
            .NotNull().WithMessage("{PropertyPath} fehlt.")
            .Must(WallPointCount.IsValid).WithMessage(WarehouseLimits.PointCountMessage);
        RuleForEach(x => x.Points).NotNull().SetValidator(new PositionDtoValidator())
            .When(x => WallPointCount.IsValid(x.Points));
    }
}

public class CreatePickPointRequestValidator : AbstractValidator<CreatePickPointRequest>
{
    public CreatePickPointRequestValidator()
    {
        RuleFor(x => x.WarehouseId).NotEmpty();
        RuleFor(x => x.Label).RequiredText(ValidationLimits.MaxLabelLength);
        RuleFor(x => x.Type).Must(EnumNames.IsDefinedName<PickPointType>).WithMessage(EnumNames.Message<PickPointType>());
        RuleFor(x => x.Position).NotNull().SetValidator(new PositionDtoValidator());
    }
}

public class UpdatePickPointRequestValidator : AbstractValidator<UpdatePickPointRequest>
{
    public UpdatePickPointRequestValidator()
    {
        RuleFor(x => x.Label).RequiredText(ValidationLimits.MaxLabelLength);
        RuleFor(x => x.Type).Must(EnumNames.IsDefinedName<PickPointType>).WithMessage(EnumNames.Message<PickPointType>());
    }
}
