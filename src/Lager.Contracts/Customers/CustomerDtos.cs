namespace Lager.Contracts.Customers;

public record CustomerAddressDto(
    Guid Id,
    string Kind,
    string Label,
    string Street,
    string? Street2,
    string Zip,
    string City,
    string Country);

public record CustomerDto(
    Guid Id,
    string Code,
    string Name,
    string? Email,
    string? Phone,
    string? Notes,
    string Currency,
    int DefaultDiscountPercent,
    bool IsActive,
    DateTime CreatedAt,
    IReadOnlyList<CustomerAddressDto> Addresses);

public record CreateCustomerRequest(string Code, string Name, string Currency = "EUR");

public record UpdateCustomerRequest(
    string Name,
    string? Email,
    string? Phone,
    string? Notes,
    string Currency,
    int DefaultDiscountPercent);

public record AddAddressRequest(
    string Kind,           // "Shipping" | "Billing" | "Both"
    string Label,
    string Street,
    string? Street2,
    string Zip,
    string City,
    string Country = "DE");
