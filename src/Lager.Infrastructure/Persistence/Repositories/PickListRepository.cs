using Lager.Application.Abstractions;
using Lager.Domain.PickLists;
using Microsoft.EntityFrameworkCore;

namespace Lager.Infrastructure.Persistence.Repositories;

public class PickListRepository : IPickListRepository
{
    private readonly LagerDbContext _db;

    public PickListRepository(LagerDbContext db) => _db = db;

    public Task<PickList?> GetAsync(Guid id, CancellationToken ct = default) =>
        _db.PickLists.Include(p => p.Items).FirstOrDefaultAsync(p => p.Id == id, ct);

    public async Task<IReadOnlyList<PickList>> ListAsync(CancellationToken ct = default) =>
        await _db.PickLists.AsNoTracking().Include(p => p.Items).OrderByDescending(p => p.CreatedAt).ToListAsync(ct);

    public async Task AddAsync(PickList entity, CancellationToken ct = default) =>
        await _db.PickLists.AddAsync(entity, ct);

    public void Remove(PickList entity) => _db.PickLists.Remove(entity);

    /// <summary>Atomarer Zähler, siehe <see cref="NumberSequences.NextAsync"/>.</summary>
    public Task<long> NextSequenceAsync(CancellationToken ct = default) =>
        NumberSequences.NextAsync(_db, NumberSequences.PickList, ct);

    public async Task ResetSequenceAsync(CancellationToken ct = default) =>
        await NumberSequences.ResetAsync(_db, NumberSequences.PickList, ct);

    public async Task<IReadOnlyList<PickList>> ListByStatusAsync(IReadOnlyCollection<PickListStatus> statuses, CancellationToken ct = default)
    {
        if (statuses.Count == 0) return Array.Empty<PickList>();
        return await _db.PickLists.Include(p => p.Items)
            .Where(p => statuses.Contains(p.Status))
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<int> CountByStatusAsync(IReadOnlyCollection<PickListStatus> statuses, CancellationToken ct = default)
    {
        if (statuses.Count == 0) return 0;
        return await _db.PickLists.CountAsync(p => statuses.Contains(p.Status), ct);
    }

    public async Task<IReadOnlyDictionary<Guid, PickListStatus>> GetStatusesAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var idSet = ids.ToHashSet();
        if (idSet.Count == 0) return new Dictionary<Guid, PickListStatus>();
        var rows = await _db.PickLists.AsNoTracking()
            .Where(p => idSet.Contains(p.Id))
            .Select(p => new { p.Id, p.Status })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => r.Status);
    }

    public async Task<IReadOnlyList<PickList>> GetManyAsync(IEnumerable<Guid> ids, CancellationToken ct = default)
    {
        var idSet = ids.ToHashSet();
        if (idSet.Count == 0) return Array.Empty<PickList>();
        return await _db.PickLists.Include(p => p.Items).Where(p => idSet.Contains(p.Id)).ToListAsync(ct);
    }

    public async Task<IReadOnlyCollection<Guid>> ListOrderIdsAsync(IReadOnlyCollection<PickListStatus> statuses, CancellationToken ct = default)
    {
        if (statuses.Count == 0) return Array.Empty<Guid>();
        return await _db.PickItems.AsNoTracking()
            .Where(i => _db.PickLists.Any(p => p.Id == i.PickListId && statuses.Contains(p.Status)))
            .Select(i => i.OrderId)
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<PickList>> RemoveOrderItemsAsync(Guid orderId, CancellationToken ct = default)
    {
        var active = new[] { PickListStatus.Pending, PickListStatus.InProgress, PickListStatus.Picked };
        var lists = await _db.PickLists.Include(p => p.Items)
            .Where(p => active.Contains(p.Status) && p.Items.Any(i => i.OrderId == orderId))
            .ToListAsync(ct);

        // Nur vormerken (Deleted): geschrieben wird beim SaveChanges des Aufrufers.
        foreach (var pl in lists)
            _db.PickItems.RemoveRange(pl.Items.Where(i => i.OrderId == orderId).ToList());
        return lists;
    }

    public Task ReplaceItemsAsync(PickList pl, IEnumerable<PickItem> newItems, CancellationToken ct = default)
    {
        var newItemsList = newItems.ToList();
        var oldItems = pl.Items.ToList();

        // Die Domain tauscht die Items in der Liste aus (und prüft dabei Status und Mindestanzahl) ...
        pl.ReplaceItems(newItemsList);

        // ... der Change-Tracker bekommt die alten als gelöscht und die neuen als hinzugefügt. Add() ist hier
        // wichtig: die neuen Items haben schon eine Guid, ohne explizites Add würde EF sie beim Entdecken über
        // die Navigation als vorhanden (Modified) einstufen und ein UPDATE statt INSERT versuchen.
        // Nichts wird sofort ausgeführt - erst das SaveChanges des Aufrufers schreibt alles in EINER Transaktion,
        // sodass bei einem Fehler (z. B. Concurrency-Konflikt) die alten Items erhalten bleiben.
        _db.PickItems.RemoveRange(oldItems);
        _db.PickItems.AddRange(newItemsList);
        return Task.CompletedTask;
    }
}
