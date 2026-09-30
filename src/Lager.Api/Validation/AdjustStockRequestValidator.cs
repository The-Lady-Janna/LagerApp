using FluentValidation;
using Lager.Contracts.Stock;

namespace Lager.Api.Validation;

public class AdjustStockRequestValidator : AbstractValidator<AdjustStockRequest>
{
    public AdjustStockRequestValidator()
    {
        RuleFor(x => x.ArticleId).NotEmpty();
        RuleFor(x => x.StorageLocationId).NotEmpty();
        RuleFor(x => x.Delta)
            .NotEqual(0).WithMessage("Delta darf nicht 0 sein – nichts zu buchen.")
            // Obergrenze: zwei Buchungen mit int.MaxValue liefen sonst über (negativer Bestand), -int.MinValue ebenfalls.
            .InclusiveBetween(-ValidationLimits.MaxQuantity, ValidationLimits.MaxQuantity)
            .WithMessage("Delta muss zwischen -" + ValidationLimits.MaxQuantity + " und " + ValidationLimits.MaxQuantity + " liegen.");

        RuleFor(x => x.LotNumber)
            .MaximumLength(64)
            .When(x => !string.IsNullOrEmpty(x.LotNumber));

        // ExpiryDate ohne Zukunftsbezug erlaubt (Abverkauf von Restbeständen),
        // aber kein offensichtlich kaputtes Datum wie Jahr < 2000.
        RuleFor(x => x.ExpiryDate!.Value.Year)
            .GreaterThanOrEqualTo(2000)
            .When(x => x.ExpiryDate.HasValue)
            .WithMessage("ExpiryDate muss nach dem Jahr 2000 liegen.");
    }
}
