namespace Lager.Contracts.Suppliers;

public record SupplierDto(
    Guid Id,
    string Code,
    string Name,
    string? ContactEmail,
    string? ContactPhone,
    string? Notes,
    int LeadTimeDays,
    int MinOrderValueCents,
    string Currency,
    bool IsActive,
    DateTime CreatedAt);

public record CreateSupplierRequest(
    string Code,
    string Name,
    string? ContactEmail = null,
    string? ContactPhone = null,
    string? Notes = null,
    int LeadTimeDays = 7,
    int MinOrderValueCents = 0,
    string Currency = "EUR");

public record UpdateSupplierRequest(
    string Name,
    string? ContactEmail,
    string? ContactPhone,
    string? Notes,
    int LeadTimeDays,
    int MinOrderValueCents,
    string Currency);
