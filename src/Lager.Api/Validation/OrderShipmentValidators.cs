using FluentValidation;
using Lager.Contracts.Customers;
using Lager.Contracts.Shipping;
using Lager.Domain.Customers;
using Lager.Domain.Shipping;

namespace Lager.Api.Validation;

// Validatoren der Requests zu Sendungen und Kunden (Bestellungen: CreateOrderRequestValidator). Sie fangen falsche
// Eingaben mit einer feldgenauen 400-Antwort ab, bevor die Domain eine ArgumentException wirft. Die Längen entsprechen
// den Spalten (ShipmentConfiguration, CustomerConfiguration).

/// <summary>Neue Sendung: Maße und Gewicht sind Pflicht (keine Platzhalter) mit Obergrenzen.</summary>
public class CreateShipmentRequestValidator : AbstractValidator<CreateShipmentRequest>
{
    /// <summary>Größte Seitenlänge eines Pakets in mm (10 m).</summary>
    public const int MaxDimensionMm = 10_000;

    /// <summary>Höchstgewicht in g (1000 kg).</summary>
    public const int MaxWeightGrams = 1_000_000;

    public CreateShipmentRequestValidator()
    {
        RuleFor(x => x.OrderId).NotEmpty();
        RuleFor(x => x.PickListId).NotEqual(Guid.Empty).When(x => x.PickListId.HasValue);
        RuleFor(x => x.CarrierCode)
            .NotEmpty().WithMessage("CarrierCode darf nicht leer sein.")
            .MaximumLength(16);

        RuleFor(x => x.LengthMm).InclusiveBetween(1, MaxDimensionMm).WithMessage($"Die Länge muss zwischen 1 und {MaxDimensionMm} mm liegen.");
        RuleFor(x => x.WidthMm).InclusiveBetween(1, MaxDimensionMm).WithMessage($"Die Breite muss zwischen 1 und {MaxDimensionMm} mm liegen.");
        RuleFor(x => x.HeightMm).InclusiveBetween(1, MaxDimensionMm).WithMessage($"Die Höhe muss zwischen 1 und {MaxDimensionMm} mm liegen.");
        RuleFor(x => x.WeightGrams).InclusiveBetween(1, MaxWeightGrams).WithMessage($"Das Gewicht muss zwischen 1 und {MaxWeightGrams} g liegen.");

        RuleFor(x => x.Notes).MaximumLength(1000).When(x => !string.IsNullOrEmpty(x.Notes));
    }
}

/// <summary>Tracking zuweisen: URL nur als absolute http(s)-Adresse bis 500 Zeichen (sie wird als Link angezeigt).</summary>
public class AssignTrackingRequestValidator : AbstractValidator<AssignTrackingRequest>
{
    public AssignTrackingRequestValidator()
    {
        RuleFor(x => x.TrackingNumber).MaximumLength(128).When(x => !string.IsNullOrEmpty(x.TrackingNumber));
        RuleFor(x => x.TrackingUrl)
            .Must(url => Shipment.IsHttpUrl(url))
            .When(x => !string.IsNullOrWhiteSpace(x.TrackingUrl))
            .WithMessage($"Die Tracking-URL muss eine absolute http(s)-Adresse mit höchstens {Shipment.MaxTrackingUrlLength} Zeichen sein.");
        RuleFor(x => x.CostCents).InclusiveBetween(0, 100_000_000);
    }
}

public class CreateCustomerRequestValidator : AbstractValidator<CreateCustomerRequest>
{
    public CreateCustomerRequestValidator()
    {
        RuleFor(x => x.Code)
            .NotEmpty().WithMessage("Code darf nicht leer sein.")
            .MaximumLength(32);
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name darf nicht leer sein.")
            .MaximumLength(256);
        RuleFor(x => x.Currency).MaximumLength(3);
    }
}

public class UpdateCustomerRequestValidator : AbstractValidator<UpdateCustomerRequest>
{
    public UpdateCustomerRequestValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Name darf nicht leer sein.")
            .MaximumLength(256);
        RuleFor(x => x.Email).MaximumLength(256);
        RuleFor(x => x.Phone).MaximumLength(64);
        RuleFor(x => x.Notes).MaximumLength(1000);
        RuleFor(x => x.Currency).MaximumLength(3);
        RuleFor(x => x.DefaultDiscountPercent)
            .InclusiveBetween(0, 100).WithMessage("Der Rabatt muss zwischen 0 und 100 Prozent liegen.");
    }
}

public class AddAddressRequestValidator : AbstractValidator<AddAddressRequest>
{
    public AddAddressRequestValidator()
    {
        RuleFor(x => x.Kind)
            .Must(kind => Enum.TryParse<AddressKind>(kind, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            .WithMessage("Kind muss Shipping, Billing oder Both sein.");
        RuleFor(x => x.Label)
            .NotEmpty().WithMessage("Label darf nicht leer sein.")
            .MaximumLength(64);
        RuleFor(x => x.Street)
            .NotEmpty().WithMessage("Straße darf nicht leer sein.")
            .MaximumLength(256);
        RuleFor(x => x.Street2).MaximumLength(256);
        RuleFor(x => x.Zip).MaximumLength(16);
        RuleFor(x => x.City).MaximumLength(128);
        RuleFor(x => x.Country).MaximumLength(3);
    }
}
