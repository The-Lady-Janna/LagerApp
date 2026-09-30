using FluentValidation;
using Lager.Application.Returns;
using Lager.Contracts.Inbound;
using Lager.Contracts.Inventory;
using Lager.Contracts.Purchasing;
using Lager.Contracts.Returns;
using Lager.Contracts.Stock;

namespace Lager.Api.Validation;

/// <summary>
/// Gemeinsame Regeln der Validatoren für die bestandsbuchenden Vorgänge (Wareneingang, Inventur, Retouren, Einkauf,
/// Nachschub). Grenzen: Mengen 1 bis 1.000.000, Listen höchstens 500 Einträge, Textlängen wie die Spaltenbreiten.
/// Die Validierung läuft vor der Action (400 mit Feldfehlern); die Fachregeln prüfen zusätzlich Domain und Dienste.
/// (Für <see cref="AdjustStockRequest"/> gibt es den Validator in AdjustStockRequestValidator; die Mengengrenze
/// prüft dort der Buchungsweg.)
/// </summary>
internal static class StockFlowRules
{
    public const int MaxQuantity = 1_000_000;
    public const int MaxListItems = 500;
    public const int MaxLotLength = 64;

    public static IRuleBuilderOptions<T, int> ValidQuantity<T>(this IRuleBuilder<T, int> rule) =>
        rule.InclusiveBetween(1, MaxQuantity).WithMessage($"Menge muss zwischen 1 und {MaxQuantity} liegen.");

    public static IRuleBuilderOptions<T, string?> ValidLot<T>(this IRuleBuilder<T, string?> rule) =>
        rule.MaximumLength(MaxLotLength).WithMessage($"Chargennummer darf höchstens {MaxLotLength} Zeichen haben.");

    /// <summary>MHD ohne Zukunftsbezug (Abverkauf von Restbeständen), aber kein offensichtlich kaputtes Datum.</summary>
    public static IRuleBuilderOptions<T, DateTime?> ValidExpiry<T>(this IRuleBuilder<T, DateTime?> rule) =>
        rule.Must(d => d is null || d.Value.Year >= 2000).WithMessage("MHD muss nach dem Jahr 2000 liegen.");
}

// ---- Wareneingang ----------------------------------------------------------------------------------------------

public class CreateInboundShipmentRequestValidator : AbstractValidator<CreateInboundShipmentRequest>
{
    public CreateInboundShipmentRequestValidator()
    {
        RuleFor(x => x.ShipmentNumber).NotEmpty().WithMessage("Lieferschein-Nummer darf nicht leer sein.").MaximumLength(64);
        RuleFor(x => x.SupplierReference).MaximumLength(128);
        RuleFor(x => x.Notes).MaximumLength(1000);
    }
}

public class AddInboundLineRequestValidator : AbstractValidator<AddInboundLineRequest>
{
    public AddInboundLineRequestValidator()
    {
        RuleFor(x => x.ArticleId).NotEmpty();
        RuleFor(x => x.TargetBinId).NotEmpty();
        RuleFor(x => x.Quantity).ValidQuantity();
        RuleFor(x => x.LotNumber).ValidLot();
        RuleFor(x => x.ExpiryDate).ValidExpiry();
        RuleFor(x => x.UnitCostCents!.Value).GreaterThanOrEqualTo(0)
            .When(x => x.UnitCostCents.HasValue)
            .WithMessage("Einkaufspreis darf nicht negativ sein.");
        RuleFor(x => x.PurchaseOrderLineId!.Value).NotEmpty().When(x => x.PurchaseOrderLineId.HasValue);
    }
}

public class CreateInboundFromPurchaseOrderRequestValidator : AbstractValidator<CreateInboundFromPurchaseOrderRequest>
{
    public CreateInboundFromPurchaseOrderRequestValidator()
    {
        RuleFor(x => x.TargetBinId).NotEmpty().WithMessage("Ziel-Lagerplatz erforderlich.");
        RuleFor(x => x.Notes).MaximumLength(1000);
    }
}

// ---- Inventur --------------------------------------------------------------------------------------------------

public class StartInventoryRequestValidator : AbstractValidator<StartInventoryRequest>
{
    public StartInventoryRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithMessage("Name darf nicht leer sein.").MaximumLength(128);
    }
}

public class SetCountRequestValidator : AbstractValidator<SetCountRequest>
{
    public SetCountRequestValidator()
    {
        RuleFor(x => x.CountedQty).InclusiveBetween(0, StockFlowRules.MaxQuantity)
            .WithMessage($"Gezählte Menge muss zwischen 0 und {StockFlowRules.MaxQuantity} liegen.");
        RuleFor(x => x.Reason).MaximumLength(500);
    }
}

// ---- Retouren --------------------------------------------------------------------------------------------------

public class CreateReturnShipmentRequestValidator : AbstractValidator<CreateReturnShipmentRequest>
{
    public CreateReturnShipmentRequestValidator()
    {
        RuleFor(x => x.CustomerReference).MaximumLength(128);
        RuleFor(x => x.Notes).MaximumLength(1000);
        RuleFor(x => x.Lines).NotNull().WithMessage("Positionsliste (Lines) fehlt.");
        RuleFor(x => x.Lines!.Count).LessThanOrEqualTo(StockFlowRules.MaxListItems)
            .When(x => x.Lines is not null)
            .WithMessage($"Höchstens {StockFlowRules.MaxListItems} Positionen je Retoure.");
        RuleForEach(x => x.Lines).SetValidator(new CreateReturnLineRequestValidator());
    }
}

public class CreateReturnLineRequestValidator : AbstractValidator<CreateReturnLineRequest>
{
    public CreateReturnLineRequestValidator()
    {
        RuleFor(x => x.ArticleId).NotEmpty();
        RuleFor(x => x.Quantity).ValidQuantity();
        RuleFor(x => x.LotNumber).ValidLot();
    }
}

public class AddReturnLineRequestValidator : AbstractValidator<AddReturnLineRequest>
{
    public AddReturnLineRequestValidator()
    {
        RuleFor(x => x.ArticleId).NotEmpty();
        RuleFor(x => x.Quantity).ValidQuantity();
        RuleFor(x => x.LotNumber).ValidLot();
    }
}

public class SetQcRequestValidator : AbstractValidator<SetQcRequest>
{
    public SetQcRequestValidator()
    {
        // Nur benannte Werte: Zahlen ("99") wären für Enum.TryParse gültig und ergäben einen undefinierten QC-Wert.
        RuleFor(x => x.Result)
            .NotEmpty().WithMessage("QC-Ergebnis fehlt.")
            .Must(value => ReturnService.TryParseQcResult(value, out _))
            .WithMessage($"Unbekanntes QC-Ergebnis. Erlaubt: {string.Join(", ", Enum.GetNames<Lager.Domain.Returns.QcResult>())}.");
        RuleFor(x => x.Notes).MaximumLength(500);
    }
}

// ---- Einkauf ---------------------------------------------------------------------------------------------------

public class CreatePurchaseOrderRequestValidator : AbstractValidator<CreatePurchaseOrderRequest>
{
    public CreatePurchaseOrderRequestValidator()
    {
        RuleFor(x => x.SupplierId).NotEmpty();
        RuleFor(x => x.Notes).MaximumLength(1000);
        RuleFor(x => x.Lines).NotNull().WithMessage("Positionsliste (Lines) fehlt.");
        RuleFor(x => x.Lines!.Count).LessThanOrEqualTo(StockFlowRules.MaxListItems)
            .When(x => x.Lines is not null)
            .WithMessage($"Höchstens {StockFlowRules.MaxListItems} Positionen je Bestellung.");
        RuleForEach(x => x.Lines).SetValidator(new CreatePurchaseOrderLineRequestValidator());
    }
}

public class CreatePurchaseOrderLineRequestValidator : AbstractValidator<CreatePurchaseOrderLineRequest>
{
    public CreatePurchaseOrderLineRequestValidator()
    {
        RuleFor(x => x.ArticleId).NotEmpty();
        RuleFor(x => x.OrderedQty).ValidQuantity();
        RuleFor(x => x.UnitPriceCents!.Value).GreaterThanOrEqualTo(0)
            .When(x => x.UnitPriceCents.HasValue)
            .WithMessage("Einkaufspreis darf nicht negativ sein.");
    }
}

public class AddPurchaseOrderLineRequestValidator : AbstractValidator<AddPurchaseOrderLineRequest>
{
    public AddPurchaseOrderLineRequestValidator()
    {
        RuleFor(x => x.ArticleId).NotEmpty();
        RuleFor(x => x.OrderedQty).ValidQuantity();
        RuleFor(x => x.UnitPriceCents!.Value).GreaterThanOrEqualTo(0)
            .When(x => x.UnitPriceCents.HasValue)
            .WithMessage("Einkaufspreis darf nicht negativ sein.");
    }
}

public class ReceivePurchaseOrderLineRequestValidator : AbstractValidator<ReceivePurchaseOrderLineRequest>
{
    public ReceivePurchaseOrderLineRequestValidator()
    {
        RuleFor(x => x.ReceivedQty).ValidQuantity();
    }
}

// ---- Nachschub -------------------------------------------------------------------------------------------------

public class CompleteReplenishmentRequestValidator : AbstractValidator<CompleteReplenishmentRequest>
{
    public CompleteReplenishmentRequestValidator()
    {
        RuleFor(x => x.ActualQty).ValidQuantity();
    }
}
