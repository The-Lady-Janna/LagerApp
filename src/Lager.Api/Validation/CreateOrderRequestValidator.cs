using FluentValidation;
using Lager.Application.Orders;
using Lager.Contracts.Orders;
using Lager.Domain.Orders;

namespace Lager.Api.Validation;

public class CreateOrderRequestValidator : AbstractValidator<CreateOrderRequest>
{
    public CreateOrderRequestValidator()
    {
        RuleFor(x => x.OrderNumber)
            .NotEmpty().WithMessage("OrderNumber darf nicht leer sein.")
            .MaximumLength(64);

        RuleFor(x => x.CustomerReference)
            .MaximumLength(128)
            .When(x => !string.IsNullOrEmpty(x.CustomerReference));

        // Kennung im Quellsystem (auch der Idempotency-Key): eindeutig, deshalb mit fester Obergrenze.
        RuleFor(x => x.ExternalReference)
            .MaximumLength(Order.MaxExternalReferenceLength)
            .When(x => !string.IsNullOrEmpty(x.ExternalReference));

        RuleFor(x => x.Priority)
            .InclusiveBetween(0, Order.MaxPriority)
            .WithMessage($"Die Priorität muss zwischen 0 und {Order.MaxPriority} liegen.");

        RuleFor(x => x.CustomerId).NotEqual(Guid.Empty).When(x => x.CustomerId.HasValue);
        RuleFor(x => x.ShippingAddressId).NotEqual(Guid.Empty).When(x => x.ShippingAddressId.HasValue);
        RuleFor(x => x.ShippingAddressId)
            .Null().When(x => x.CustomerId is null)
            .WithMessage("Eine Lieferadresse setzt einen Kunden voraus.");

        RuleFor(x => x.Lines)
            .NotEmpty().WithMessage("Bestellung braucht mindestens eine Position.")
            .Must(lines => lines is null || lines.Count <= OrderService.MaxLines)
            .WithMessage($"Eine Bestellung darf höchstens {OrderService.MaxLines} Positionen haben.");

        RuleForEach(x => x.Lines).NotNull().SetValidator(new CreateOrderLineRequestValidator());
    }
}

public class CreateOrderLineRequestValidator : AbstractValidator<CreateOrderLineRequest>
{
    public CreateOrderLineRequestValidator()
    {
        // Artikel wahlweise per Id oder SKU (eines von beiden genügt).
        RuleFor(x => x)
            .Must(line => (line.ArticleId.HasValue && line.ArticleId.Value != Guid.Empty) || !string.IsNullOrWhiteSpace(line.Sku))
            .WithName("ArticleId")
            .WithMessage("Artikel-Id oder SKU muss angegeben werden.");
        RuleFor(x => x.ArticleId).NotEqual(Guid.Empty).When(x => x.ArticleId.HasValue);
        RuleFor(x => x.Sku).MaximumLength(64).When(x => !string.IsNullOrEmpty(x.Sku));
        RuleFor(x => x.Quantity)
            .InclusiveBetween(1, OrderService.MaxQuantity)
            .WithMessage($"Quantity muss zwischen 1 und {OrderService.MaxQuantity} liegen.");
    }
}
