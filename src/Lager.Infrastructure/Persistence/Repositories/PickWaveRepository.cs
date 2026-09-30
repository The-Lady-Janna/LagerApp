using Lager.Application.Abstractions;
using Lager.Domain.PickLists;
using Microsoft.EntityFrameworkCore;

namespace Lager.Infrastructure.Persistence.Repositories;

public class PickWaveRepository : IPickWaveRepository
{
    private readonly LagerDbContext _db;
    public PickWaveRepository(LagerDbContext db) => _db = db;

    public Task<PickWave?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.PickWaves.FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<PickWave>> ListAsync(CancellationToken ct = default) =>
        await _db.PickWaves.AsNoTracking().OrderByDescending(p => p.CreatedAt).ToListAsync(ct);

    public async Task AddAsync(PickWave entity, CancellationToken ct = default) =>
        await _db.PickWaves.AddAsync(entity, ct);

    public void Remove(PickWave entity) => _db.PickWaves.Remove(entity);

    /// <summary>
    /// Nutzt die Zähler-Tabelle PickListSequence (Zeile <see cref="NumberSequences.PickWave"/>) - Wellen-Nummern
    /// tragen ein `W-` als Präfix und kollidieren daher nie mit Picklisten-Nummern. Atomar, siehe
    /// <see cref="NumberSequences.NextAsync"/>.
    /// </summary>
    public Task<long> NextSequenceAsync(CancellationToken ct = default) =>
        NumberSequences.NextAsync(_db, NumberSequences.PickWave, ct);

    public async Task<PickWave?> FindByPickListAsync(Guid pickListId, CancellationToken ct = default)
    {
        // Die Listen-Ids liegen als CSV in einer Spalte; Guid.ToString() ist wie beim Schreiben klein und mit
        // Bindestrichen, der Teilstring-Vergleich ist deshalb eindeutig (Guids sind gleich lang).
        var needle = pickListId.ToString();
        return await _db.PickWaves.FirstOrDefaultAsync(w => w.PickListIdsCsv.Contains(needle), ct);
    }
}
