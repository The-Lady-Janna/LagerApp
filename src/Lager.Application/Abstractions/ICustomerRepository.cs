using Lager.Domain.Customers;

namespace Lager.Application.Abstractions;

public interface ICustomerRepository : IRepository<Customer>
{
    Task<Customer?> GetWithAddressesAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Customer>> ListAsync(bool includeInactive, CancellationToken ct = default);
    Task<bool> CodeExistsAsync(string code, CancellationToken ct = default);

    /// <summary>
    /// Merkt eine neue Adresse eines bereits geladenen Kunden zum Einfügen vor (erst SaveChanges schreibt). Nötig, weil die
    /// Adresse ihre Guid schon trägt: nur über die Navigation des Kunden entdeckt, hielte EF sie für eine vorhandene
    /// und versuchte ein UPDATE statt INSERT (Concurrency-Fehler).
    /// </summary>
    Task AddAddressAsync(CustomerAddress address, CancellationToken ct = default);

    /// <summary>
    /// Read-only: die Kunden (samt Adressen) mit den angegebenen Ids in einer Abfrage, aktive und inaktive.
    /// Unbekannte Ids fehlen im Ergebnis. Für die Anzeige von Kundenname und Lieferadresse in Bestell-Listen.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Customer>> GetManyAsync(IEnumerable<Guid> ids, CancellationToken ct = default);
}
