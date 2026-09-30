using Lager.Application.Abstractions;
using Lager.Domain.Customers;
using Microsoft.EntityFrameworkCore;

namespace Lager.Infrastructure.Persistence.Repositories;

public class CustomerRepository : ICustomerRepository
{
    private readonly LagerDbContext _db;
    public CustomerRepository(LagerDbContext db) => _db = db;

    public Task<Customer?> GetAsync(Guid id, CancellationToken ct = default) =>
        GetWithAddressesAsync(id, ct);

    public Task<Customer?> GetWithAddressesAsync(Guid id, CancellationToken ct = default) =>
        _db.Customers.Include(c => c.Addresses).FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<IReadOnlyList<Customer>> ListAsync(CancellationToken ct = default) =>
        ListAsync(includeInactive: false, ct);

    public async Task<IReadOnlyList<Customer>> ListAsync(bool includeInactive, CancellationToken ct = default) =>
        await _db.Customers.AsNoTracking().Include(c => c.Addresses)
            .Where(c => includeInactive || c.IsActive)
            .OrderBy(c => c.Name)
            .ToListAsync(ct);

    public Task<bool> CodeExistsAsync(string code, CancellationToken ct = default) =>
        _db.Customers.AnyAsync(c => c.Code == code, ct);

    public async Task<IReadOnlyDictionary<Guid, Customer>> GetManyAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var idSet = ids.ToHashSet();
        if (idSet.Count == 0) return new Dictionary<Guid, Customer>();
        var items = await _db.Customers.AsNoTracking().Include(c => c.Addresses)
            .Where(c => idSet.Contains(c.Id)).ToListAsync(ct);
        return items.ToDictionary(c => c.Id);
    }

    public async Task AddAddressAsync(CustomerAddress address, CancellationToken ct = default) =>
        await _db.CustomerAddresses.AddAsync(address, ct);

    public async Task AddAsync(Customer entity, CancellationToken ct = default) =>
        await _db.Customers.AddAsync(entity, ct);

    public void Remove(Customer entity) => _db.Customers.Remove(entity);
}
