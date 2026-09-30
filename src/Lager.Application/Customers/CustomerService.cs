using Lager.Application.Abstractions;
using Lager.Contracts.Customers;
using Lager.Domain.Customers;

namespace Lager.Application.Customers;

public class CustomerService
{
    private readonly ICustomerRepository _repo;
    private readonly IUnitOfWork _uow;

    public CustomerService(ICustomerRepository repo, IUnitOfWork uow)
    {
        _repo = repo;
        _uow = uow;
    }

    public async Task<IReadOnlyList<CustomerDto>> ListAsync(bool includeInactive, CancellationToken ct = default) =>
        (await _repo.ListAsync(includeInactive, ct)).Select(ToDto).ToList();

    public async Task<CustomerDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var c = await _repo.GetWithAddressesAsync(id, ct);
        return c is null ? null : ToDto(c);
    }

    public async Task<CustomerDto> CreateAsync(CreateCustomerRequest req, CancellationToken ct = default)
    {
        if (await _repo.CodeExistsAsync(req.Code.Trim(), ct))
        {
            var ex = new InvalidOperationException("Kunden-Code bereits vergeben");
            ex.Data["code"] = "duplicate_customer_code";
            throw ex;
        }
        var c = new Customer(req.Code, req.Name, req.Currency);
        await _repo.AddAsync(c, ct);
        await _uow.SaveChangesAsync(ct);
        return ToDto(c);
    }

    public async Task<CustomerDto?> UpdateAsync(Guid id, UpdateCustomerRequest req, CancellationToken ct = default)
    {
        var c = await _repo.GetAsync(id, ct);
        if (c is null) return null;
        c.UpdateProfile(req.Name, req.Email, req.Phone, req.Notes, req.Currency, req.DefaultDiscountPercent);
        await _uow.SaveChangesAsync(ct);
        return ToDto(c);
    }

    public async Task<CustomerDto?> AddAddressAsync(Guid id, AddAddressRequest req, CancellationToken ct = default)
    {
        var c = await _repo.GetWithAddressesAsync(id, ct);
        if (c is null) return null;
        if (!Enum.TryParse<AddressKind>(req.Kind, ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
        {
            var ex = new ArgumentException($"Unbekannter AddressKind: {req.Kind}");
            ex.Data["code"] = "invalid_address_kind";
            throw ex;
        }
        var address = c.AddAddress(kind, req.Label, req.Street, req.Street2, req.Zip, req.City, req.Country);
        await _repo.AddAddressAsync(address, ct);   // sonst hielte EF die Adresse (Guid schon gesetzt) für vorhanden
        await _uow.SaveChangesAsync(ct);
        return ToDto(c);
    }

    public async Task<CustomerDto?> RemoveAddressAsync(Guid id, Guid addressId, CancellationToken ct = default)
    {
        var c = await _repo.GetWithAddressesAsync(id, ct);
        if (c is null) return null;
        c.RemoveAddress(addressId);
        await _uow.SaveChangesAsync(ct);
        return ToDto(c);
    }

    public async Task<CustomerDto?> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var c = await _repo.GetAsync(id, ct);
        if (c is null) return null;
        if (active) c.Activate(); else c.Deactivate();
        await _uow.SaveChangesAsync(ct);
        return ToDto(c);
    }

    private static CustomerDto ToDto(Customer c) => new(
        c.Id, c.Code, c.Name, c.Email, c.Phone, c.Notes,
        c.Currency, c.DefaultDiscountPercent, c.IsActive, c.CreatedAt,
        c.Addresses.Select(a => new CustomerAddressDto(
            a.Id, a.Kind.ToString(), a.Label, a.Street, a.Street2,
            a.Zip, a.City, a.Country)).ToList());
}
