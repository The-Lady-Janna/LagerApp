using Lager.Application.Abstractions;
using Lager.Contracts.Suppliers;
using Lager.Domain.Suppliers;

namespace Lager.Application.Suppliers;

public class SupplierService
{
    private readonly ISupplierRepository _repo;
    private readonly IUnitOfWork _uow;

    public SupplierService(ISupplierRepository repo, IUnitOfWork uow)
    {
        _repo = repo;
        _uow = uow;
    }

    public async Task<IReadOnlyList<SupplierDto>> ListAsync(bool includeInactive, CancellationToken ct = default) =>
        (await _repo.ListAsync(includeInactive, ct)).Select(ToDto).ToList();

    public async Task<SupplierDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var s = await _repo.GetAsync(id, ct);
        return s is null ? null : ToDto(s);
    }

    public async Task<SupplierDto> CreateAsync(CreateSupplierRequest req, CancellationToken ct = default)
    {
        if (await _repo.CodeExistsAsync(req.Code.Trim(), ct))
            throw new InvalidOperationException("Lieferanten-Code bereits vergeben");
        var s = new Supplier(req.Code, req.Name, req.LeadTimeDays, req.Currency);
        s.UpdateProfile(req.Name, req.ContactEmail, req.ContactPhone, req.Notes,
            req.LeadTimeDays, req.MinOrderValueCents, req.Currency);
        await _repo.AddAsync(s, ct);
        await _uow.SaveChangesAsync(ct);
        return ToDto(s);
    }

    public async Task<SupplierDto?> UpdateAsync(Guid id, UpdateSupplierRequest req, CancellationToken ct = default)
    {
        var s = await _repo.GetAsync(id, ct);
        if (s is null) return null;
        s.UpdateProfile(req.Name, req.ContactEmail, req.ContactPhone, req.Notes,
            req.LeadTimeDays, req.MinOrderValueCents, req.Currency);
        await _uow.SaveChangesAsync(ct);
        return ToDto(s);
    }

    public async Task<SupplierDto?> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        var s = await _repo.GetAsync(id, ct);
        if (s is null) return null;
        if (active) s.Activate(); else s.Deactivate();
        await _uow.SaveChangesAsync(ct);
        return ToDto(s);
    }

    internal static SupplierDto ToDto(Supplier s) => new(
        s.Id, s.Code, s.Name, s.ContactEmail, s.ContactPhone, s.Notes,
        s.LeadTimeDays, s.MinOrderValueCents, s.Currency, s.IsActive, s.CreatedAt);
}
