using Lager.Domain.Common;

namespace Lager.Domain.Suppliers;

/// <summary>
/// External vendor we buy stock from. Soft-deactivated rather than deleted
/// (historical PurchaseOrders keep referring to inactive suppliers).
/// </summary>
public class Supplier : Entity
{
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string? ContactEmail { get; private set; }
    public string? ContactPhone { get; private set; }
    public string? Notes { get; private set; }

    /// <summary>Typische Lieferzeit ab Bestellung — fließt in Bestellvorschläge ein.</summary>
    public int LeadTimeDays { get; private set; }

    /// <summary>Mindestbestellwert in Cent. 0 = keine Vorgabe.</summary>
    public int MinOrderValueCents { get; private set; }

    /// <summary>ISO-4217 Währungscode, z. B. "EUR".</summary>
    public string Currency { get; private set; } = "EUR";

    public bool IsActive { get; private set; } = true;

    private Supplier() { }

    public Supplier(string code, string name, int leadTimeDays = 7, string currency = "EUR")
    {
        if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("Code required", nameof(code));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required", nameof(name));
        Code = code.Trim();
        Name = name.Trim();
        LeadTimeDays = Math.Max(0, leadTimeDays);
        Currency = string.IsNullOrWhiteSpace(currency) ? "EUR" : currency.Trim().ToUpperInvariant();
    }

    public void UpdateProfile(string name, string? contactEmail, string? contactPhone, string? notes,
        int leadTimeDays, int minOrderValueCents, string currency)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name required", nameof(name));
        Name = name.Trim();
        ContactEmail = contactEmail?.Trim();
        ContactPhone = contactPhone?.Trim();
        Notes = notes?.Trim();
        LeadTimeDays = Math.Max(0, leadTimeDays);
        MinOrderValueCents = Math.Max(0, minOrderValueCents);
        Currency = string.IsNullOrWhiteSpace(currency) ? "EUR" : currency.Trim().ToUpperInvariant();
        Touch();
    }

    public void Activate() { IsActive = true; Touch(); }
    public void Deactivate() { IsActive = false; Touch(); }
}
