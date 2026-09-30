using Lager.Domain.Common;

namespace Lager.Domain.Customers;

/// <summary>
/// Kunden-Stammsatz. Bisher gab es im Order nur ein freitext-Feld
/// <c>CustomerReference</c> — das bleibt für Backward-Compat, neu kann der
/// Order zusätzlich an einen <see cref="Customer"/> hängen.
/// Adressen sind eine eigene Sammlung, weil ein Kunde typischerweise mehrere
/// Lieferadressen hat (HQ, Filialen, Privatadressen, …).
/// </summary>
public class Customer : Entity
{
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? Email { get; private set; }
    public string? Phone { get; private set; }
    public string? Notes { get; private set; }
    public string Currency { get; private set; } = "EUR";
    public int DefaultDiscountPercent { get; private set; }  // 0..100
    public bool IsActive { get; private set; } = true;

    private readonly List<CustomerAddress> _addresses = new();
    public IReadOnlyCollection<CustomerAddress> Addresses => _addresses.AsReadOnly();

    private Customer() { }

    public Customer(string code, string name, string currency = "EUR")
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Code required", nameof(code));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required", nameof(name));
        Code = code.Trim();
        Name = name.Trim();
        Currency = string.IsNullOrWhiteSpace(currency) ? "EUR" : currency.Trim().ToUpperInvariant();
    }

    public void UpdateProfile(string name, string? email, string? phone, string? notes, string currency, int defaultDiscountPercent)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required", nameof(name));
        if (defaultDiscountPercent < 0 || defaultDiscountPercent > 100)
            throw new ArgumentOutOfRangeException(nameof(defaultDiscountPercent), "0..100");
        Name = name.Trim();
        Email = email?.Trim();
        Phone = phone?.Trim();
        Notes = notes?.Trim();
        Currency = string.IsNullOrWhiteSpace(currency) ? "EUR" : currency.Trim().ToUpperInvariant();
        DefaultDiscountPercent = defaultDiscountPercent;
        Touch();
    }

    public CustomerAddress AddAddress(AddressKind kind, string label, string street, string? street2,
        string zip, string city, string country)
    {
        var a = new CustomerAddress(kind, label, street, street2, zip, city, country);
        a.AttachTo(Id);
        _addresses.Add(a);
        Touch();
        return a;
    }

    public void RemoveAddress(Guid addressId)
    {
        var a = _addresses.FirstOrDefault(x => x.Id == addressId);
        if (a is null) return;
        _addresses.Remove(a);
        Touch();
    }

    public void Activate() { IsActive = true; Touch(); }
    public void Deactivate() { IsActive = false; Touch(); }
}

public enum AddressKind
{
    Shipping = 0,
    Billing = 1,
    Both = 2
}

public class CustomerAddress : Entity
{
    public Guid CustomerId { get; private set; }
    public AddressKind Kind { get; private set; }
    public string Label { get; private set; } = string.Empty;
    public string Street { get; private set; } = string.Empty;
    public string? Street2 { get; private set; }
    public string Zip { get; private set; } = string.Empty;
    public string City { get; private set; } = string.Empty;
    public string Country { get; private set; } = "DE";

    private CustomerAddress() { }

    public CustomerAddress(AddressKind kind, string label, string street, string? street2, string zip, string city, string country)
    {
        if (string.IsNullOrWhiteSpace(label)) throw new ArgumentException("Label required", nameof(label));
        if (string.IsNullOrWhiteSpace(street)) throw new ArgumentException("Street required", nameof(street));
        Kind = kind;
        Label = label.Trim();
        Street = street.Trim();
        Street2 = street2?.Trim();
        Zip = zip?.Trim() ?? string.Empty;
        City = city?.Trim() ?? string.Empty;
        Country = string.IsNullOrWhiteSpace(country) ? "DE" : country.Trim().ToUpperInvariant();
    }

    internal void AttachTo(Guid customerId) => CustomerId = customerId;
}
