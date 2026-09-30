using FluentValidation;
using Lager.Contracts.Auth;
using Lager.Contracts.Suppliers;
using Lager.Contracts.Warehouse;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace Lager.Api.Validation;

/// <summary>
/// Zentrale Obergrenzen der API-Eingaben. Sie schützen vor Missbrauch und Tippfehlern (Überläufe, Endlosschleifen,
/// riesige Listen), nicht vor fachlich falschen Werten - die prüft die Domain. Alle Validatoren nutzen diese
/// Konstanten, damit ein Limit an genau einer Stelle steht.
/// </summary>
public static class ValidationLimits
{
    /// <summary>Codes (SKU, Regal, Lagerplatz ...): passt zur Spaltenlänge in der Datenbank.</summary>
    public const int MaxCodeLength = 64;
    public const int MaxLabelLength = 128;
    public const int MaxNameLength = 256;
    public const int MaxTextLength = 2000;

    /// <summary>Menge je Zeile/Buchung: 1..1.000.000.</summary>
    public const int MaxQuantity = 1_000_000;
    /// <summary>Obergrenze für Bestandsschwellen (Mindest-/Meldebestand, Maximalbestand).</summary>
    public const int MaxStockLevel = 100_000_000;
    /// <summary>Allgemeine Obergrenze für Listen in Requests (Positionen, Punkte, Bestellungen ...).</summary>
    public const int MaxListItems = 500;
    /// <summary>Koordinaten und Maße in Millimetern: höchstens +-1.000.000 (1 km).</summary>
    public const int MaxCoordinateMm = 1_000_000;
    public const int MaxDimensionMm = 1_000_000;
    /// <summary>Gewicht in Gramm (100 t).</summary>
    public const int MaxWeightGrams = 100_000_000;
    /// <summary>Zeitraum-Parameter in Tagen (etwa zehn Jahre).</summary>
    public const int MaxDays = 3660;
    /// <summary>Anzahl Ergebniszeilen (top/take/pageSize).</summary>
    public const int MaxPageSize = 1000;
}

/// <summary>Wiederverwendbare Regeln für die Request-Validatoren (auch für die Validatoren der anderen Bereiche gedacht).</summary>
public static class CommonRules
{
    /// <summary>Menge 1..<see cref="ValidationLimits.MaxQuantity"/>.</summary>
    public static IRuleBuilderOptions<T, int> PositiveQuantity<T>(this IRuleBuilder<T, int> rule) =>
        rule.InclusiveBetween(1, ValidationLimits.MaxQuantity)
            .WithMessage("{PropertyPath} muss zwischen 1 und " + ValidationLimits.MaxQuantity + " liegen.");

    /// <summary>Menge 0..<see cref="ValidationLimits.MaxQuantity"/> (0 = keine Menge).</summary>
    public static IRuleBuilderOptions<T, int> NonNegativeQuantity<T>(this IRuleBuilder<T, int> rule) =>
        rule.InclusiveBetween(0, ValidationLimits.MaxQuantity)
            .WithMessage("{PropertyPath} muss zwischen 0 und " + ValidationLimits.MaxQuantity + " liegen.");

    /// <summary>Koordinate in mm: +-<see cref="ValidationLimits.MaxCoordinateMm"/>.</summary>
    public static IRuleBuilderOptions<T, int> CoordinateMm<T>(this IRuleBuilder<T, int> rule) =>
        rule.InclusiveBetween(-ValidationLimits.MaxCoordinateMm, ValidationLimits.MaxCoordinateMm)
            .WithMessage("{PropertyPath} muss zwischen -" + ValidationLimits.MaxCoordinateMm + " und " + ValidationLimits.MaxCoordinateMm + " mm liegen.");

    /// <summary>Abmessung in mm: größer 0, höchstens <see cref="ValidationLimits.MaxDimensionMm"/>.</summary>
    public static IRuleBuilderOptions<T, int> DimensionMm<T>(this IRuleBuilder<T, int> rule) =>
        rule.InclusiveBetween(1, ValidationLimits.MaxDimensionMm)
            .WithMessage("{PropertyPath} muss größer als 0 und höchstens " + ValidationLimits.MaxDimensionMm + " mm sein.");

    /// <summary>Gewicht in Gramm: 0..<see cref="ValidationLimits.MaxWeightGrams"/>.</summary>
    public static IRuleBuilderOptions<T, int> WeightGrams<T>(this IRuleBuilder<T, int> rule) =>
        rule.InclusiveBetween(0, ValidationLimits.MaxWeightGrams)
            .WithMessage("{PropertyPath} muss zwischen 0 und " + ValidationLimits.MaxWeightGrams + " g liegen.");

    /// <summary>Pflichttext mit Höchstlänge (z. B. Code, Name).</summary>
    public static IRuleBuilderOptions<T, string?> RequiredText<T>(this IRuleBuilder<T, string?> rule, int maxLength) =>
        rule.NotEmpty().WithMessage("{PropertyPath} darf nicht leer sein.")
            .MaximumLength(maxLength).WithMessage("{PropertyPath} darf höchstens " + maxLength + " Zeichen lang sein.");

    /// <summary>Optionaler Text mit Höchstlänge.</summary>
    public static IRuleBuilderOptions<T, string?> OptionalText<T>(this IRuleBuilder<T, string?> rule, int maxLength) =>
        rule.MaximumLength(maxLength).WithMessage("{PropertyPath} darf höchstens " + maxLength + " Zeichen lang sein.");

    /// <summary>Id-Liste: nicht null, höchstens <paramref name="maxItems"/> Einträge, keine leeren Ids.</summary>
    public static IRuleBuilderOptions<T, IReadOnlyList<Guid>> IdList<T>(this IRuleBuilder<T, IReadOnlyList<Guid>> rule, int maxItems) =>
        rule.NotNull().WithMessage("{PropertyPath} fehlt.")
            .Must(ids => ids is null || ids.Count <= maxItems).WithMessage("{PropertyPath} darf höchstens " + maxItems + " Einträge enthalten.")
            .Must(ids => ids is null || ids.All(id => id != Guid.Empty)).WithMessage("{PropertyPath} enthält eine leere Id.");

    /// <summary>Wie <see cref="IdList{T}"/>, zusätzlich ohne doppelte Einträge.</summary>
    public static IRuleBuilderOptions<T, IReadOnlyList<Guid>> UniqueIdList<T>(this IRuleBuilder<T, IReadOnlyList<Guid>> rule, int maxItems) =>
        rule.IdList(maxItems)
            .Must(ids => ids is null || ids.Distinct().Count() == ids.Count).WithMessage("{PropertyPath} enthält doppelte Einträge.");

    /// <summary>Optionale Id: wenn angegeben, nicht die leere Guid.</summary>
    public static IRuleBuilderOptions<T, Guid?> OptionalId<T>(this IRuleBuilder<T, Guid?> rule) =>
        rule.Must(id => id is null || id != Guid.Empty).WithMessage("{PropertyPath} darf nicht leer sein, wenn angegeben.");
}

/// <summary>Eine Position im Lager (mm): jede Achse innerhalb von +-<see cref="ValidationLimits.MaxCoordinateMm"/>.</summary>
public class PositionDtoValidator : AbstractValidator<PositionDto>
{
    public PositionDtoValidator()
    {
        RuleFor(x => x.XMm).CoordinateMm();
        RuleFor(x => x.YMm).CoordinateMm();
        RuleFor(x => x.ZMm).CoordinateMm();
    }
}

// ---- Anmeldung, Benutzer, Lieferanten ------------------------------------------------------------------------------
// Nur Obergrenzen (und Pflichtangaben, wo die Domain sie ohnehin verlangt): Inhaltliche Regeln - Passwort-Richtlinie,
// Rollennamen, Benutzername-Länge, letzter Admin - prüfen die Services und liefern ihre eigenen Codes
// (password_policy, unknown_role ...). Leere Login-Angaben beantwortet der Login selbst mit der einheitlichen 401.

/// <summary>Grenzen für Zugangsdaten: großzügig über der Passwort-Regel (72 Bytes), damit deren Meldung greift.</summary>
public static class CredentialLimits
{
    public const int MaxUsernameLength = 256;
    public const int MaxPasswordLength = 1024;
    public const int MaxRoles = 20;
}

public class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(x => x.Username).OptionalText(CredentialLimits.MaxUsernameLength);
        RuleFor(x => x.Password).OptionalText(CredentialLimits.MaxPasswordLength);
    }
}

public class ChangePasswordRequestValidator : AbstractValidator<ChangePasswordRequest>
{
    public ChangePasswordRequestValidator()
    {
        RuleFor(x => x.CurrentPassword).OptionalText(CredentialLimits.MaxPasswordLength);
        RuleFor(x => x.NewPassword).OptionalText(CredentialLimits.MaxPasswordLength);
    }
}

public class AdminResetPasswordRequestValidator : AbstractValidator<AdminResetPasswordRequest>
{
    public AdminResetPasswordRequestValidator() =>
        RuleFor(x => x.NewPassword).OptionalText(CredentialLimits.MaxPasswordLength);
}

public class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(x => x.Username).OptionalText(CredentialLimits.MaxUsernameLength);
        RuleFor(x => x.Password).OptionalText(CredentialLimits.MaxPasswordLength);
        RuleFor(x => x.Roles).RoleNames();
        RuleFor(x => x.Email).OptionalText(ValidationLimits.MaxNameLength);
        RuleFor(x => x.DisplayName).OptionalText(ValidationLimits.MaxLabelLength);
    }
}

public class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        RuleFor(x => x.Roles).RoleNames();
        RuleFor(x => x.Email).OptionalText(ValidationLimits.MaxNameLength);
        RuleFor(x => x.DisplayName).OptionalText(ValidationLimits.MaxLabelLength);
    }
}

internal static class UserRules
{
    /// <summary>Rollenliste: vorhanden, höchstens <see cref="CredentialLimits.MaxRoles"/> Einträge, jeder Name kurz. Ob die Namen gültig sind, prüft der Service.</summary>
    public static IRuleBuilderOptions<T, string[]> RoleNames<T>(this IRuleBuilder<T, string[]> rule) =>
        rule.NotNull().WithMessage("{PropertyPath} fehlt.")
            .Must(roles => roles is null || (roles.Length <= CredentialLimits.MaxRoles && roles.All(r => r is null || r.Length <= ValidationLimits.MaxCodeLength)))
            .WithMessage("{PropertyPath}: höchstens " + CredentialLimits.MaxRoles + " Rollen mit je höchstens " + ValidationLimits.MaxCodeLength + " Zeichen.");
}

/// <summary>Lieferanten: Längen entsprechen den Datenbankspalten (Code 32, Name 256, E-Mail 256, Telefon 64, Notiz 1000, Währung 3).</summary>
public static class SupplierLimits
{
    public const int MaxCodeLength = 32;
    public const int MaxNotesLength = 1000;
    public const int MaxPhoneLength = 64;
    public const int CurrencyLength = 3;
}

public class CreateSupplierRequestValidator : AbstractValidator<CreateSupplierRequest>
{
    public CreateSupplierRequestValidator()
    {
        RuleFor(x => x.Code).RequiredText(SupplierLimits.MaxCodeLength);
        RuleFor(x => x.Name).RequiredText(ValidationLimits.MaxNameLength);
        RuleFor(x => x.ContactEmail).OptionalText(ValidationLimits.MaxNameLength);
        RuleFor(x => x.ContactPhone).OptionalText(SupplierLimits.MaxPhoneLength);
        RuleFor(x => x.Notes).OptionalText(SupplierLimits.MaxNotesLength);
        RuleFor(x => x.LeadTimeDays).InclusiveBetween(0, ValidationLimits.MaxDays);
        RuleFor(x => x.MinOrderValueCents).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Currency).OptionalText(SupplierLimits.CurrencyLength);
    }
}

public class UpdateSupplierRequestValidator : AbstractValidator<UpdateSupplierRequest>
{
    public UpdateSupplierRequestValidator()
    {
        RuleFor(x => x.Name).RequiredText(ValidationLimits.MaxNameLength);
        RuleFor(x => x.ContactEmail).OptionalText(ValidationLimits.MaxNameLength);
        RuleFor(x => x.ContactPhone).OptionalText(SupplierLimits.MaxPhoneLength);
        RuleFor(x => x.Notes).OptionalText(SupplierLimits.MaxNotesLength);
        RuleFor(x => x.LeadTimeDays).InclusiveBetween(0, ValidationLimits.MaxDays);
        RuleFor(x => x.MinOrderValueCents).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Currency).OptionalText(SupplierLimits.CurrencyLength);
    }
}

/// <summary>
/// Grenzen für Zeitraum- und Paging-Parameter im Query-String. FluentValidation prüft nur Modelle, keine einzelnen
/// Query-Parameter - deshalb gilt die Tabelle im <see cref="QueryLimitsFilter"/> für jede Action mit einem
/// int-Parameter dieses Namens (ohne Groß-/Kleinschreibung): <c>days/range/rangeDays</c> 1..3660,
/// <c>top/take/limit/pageSize</c> 1..1000, <c>skip/offset/page</c> nicht negativ.
/// </summary>
public static class QueryLimits
{
    private static readonly Dictionary<string, (int Min, int Max)> Bounds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["days"] = (1, ValidationLimits.MaxDays),
        ["range"] = (1, ValidationLimits.MaxDays),
        ["rangeDays"] = (1, ValidationLimits.MaxDays),
        ["top"] = (1, ValidationLimits.MaxPageSize),
        ["take"] = (1, ValidationLimits.MaxPageSize),
        ["limit"] = (1, ValidationLimits.MaxPageSize),
        ["pageSize"] = (1, ValidationLimits.MaxPageSize),
        ["skip"] = (0, int.MaxValue),
        ["offset"] = (0, int.MaxValue),
        ["page"] = (0, int.MaxValue), // 0- und 1-basierte Zählung sind beide üblich
    };

    /// <summary>Fehlermeldung, wenn <paramref name="value"/> für den Parameter <paramref name="name"/> außerhalb der Grenzen liegt; sonst null.</summary>
    public static string? Validate(string name, int value)
    {
        if (!Bounds.TryGetValue(name, out var bound)) return null;
        if (value >= bound.Min && value <= bound.Max) return null;
        return bound.Max == int.MaxValue
            ? $"{name} muss mindestens {bound.Min} sein."
            : $"{name} muss zwischen {bound.Min} und {bound.Max} liegen.";
    }
}

/// <summary>
/// Globaler Action-Filter: prüft die Zeitraum-/Paging-Parameter (<see cref="QueryLimits"/>) vor der Action und antwortet
/// bei Verstoß mit demselben 400 (ValidationProblemDetails, <c>errors</c>) wie ein ungültiges Modell.
/// </summary>
public sealed class QueryLimitsFilter : IActionFilter
{
    private readonly IOptions<ApiBehaviorOptions> _behavior;

    public QueryLimitsFilter(IOptions<ApiBehaviorOptions> behavior) => _behavior = behavior;

    public void OnActionExecuting(ActionExecutingContext context)
    {
        foreach (var (name, value) in context.ActionArguments)
        {
            if (value is not int number) continue;
            if (QueryLimits.Validate(name, number) is { } message)
                context.ModelState.AddModelError(name, message);
        }

        if (context.ModelState.IsValid) return;
        context.Result = _behavior.Value.InvalidModelStateResponseFactory(context);
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}
