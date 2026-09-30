using Lager.Application.Abstractions;
using Lager.Contracts.PickLists;
using Lager.Domain.PickLists;

namespace Lager.Application.PickLists;

/// <summary>
/// Orchestrates pick-waves: collect orders, then on release delegate to the
/// existing PickListService to generate the concrete picklists. Today we
/// always create one consolidated picklist; SplitByZone is reserved for a
/// future implementation (one picklist per zone of bins).
/// </summary>
public class PickWaveService
{
    private readonly IPickWaveRepository _waves;
    private readonly PickListService _pickLists;
    private readonly IUnitOfWork _uow;

    public PickWaveService(IPickWaveRepository waves, PickListService pickLists, IUnitOfWork uow)
    {
        _waves = waves;
        _pickLists = pickLists;
        _uow = uow;
    }

    public async Task<IReadOnlyList<PickWaveDto>> ListAsync(CancellationToken ct = default) =>
        (await _waves.ListAsync(ct)).Select(ToDto).ToList();

    public async Task<PickWaveDto?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var w = await _waves.GetAsync(id, ct);
        return w is null ? null : ToDto(w);
    }

    public async Task<PickWaveDto> CreateAsync(CreatePickWaveRequest request, CancellationToken ct = default)
    {
        var seq = await _waves.NextSequenceAsync(ct);
        var waveNumber = $"W-{DateTime.UtcNow:yyyyMMdd}-{seq:D4}";
        var wave = new PickWave(waveNumber, request.Description, request.CutoffAt);
        wave.AddOrders(request.OrderIds);
        await _waves.AddAsync(wave, ct);
        await _uow.SaveChangesAsync(ct);
        return ToDto(wave);
    }

    public async Task<PickWaveDto?> AddOrdersAsync(Guid waveId, AddOrdersToWaveRequest request, CancellationToken ct = default)
    {
        var wave = await _waves.GetAsync(waveId, ct);
        if (wave is null) return null;
        wave.AddOrders(request.OrderIds);
        await _uow.SaveChangesAsync(ct);
        return ToDto(wave);
    }

    public async Task<PickWaveDto?> RemoveOrderAsync(Guid waveId, Guid orderId, CancellationToken ct = default)
    {
        var wave = await _waves.GetAsync(waveId, ct);
        if (wave is null) return null;
        wave.RemoveOrder(orderId);
        await _uow.SaveChangesAsync(ct);
        return ToDto(wave);
    }

    /// <summary>
    /// Generate the concrete picklists from this wave's orders. Future: branch
    /// by request.SplitByZone — for now we always produce one consolidated list.
    /// Nur eine offene Welle (Open) lässt sich freigeben - das wird VOR dem Erzeugen der Pickliste geprüft, ein
    /// zweiter Release erzeugt also keine zweite Liste. Pickliste, Bestellstatus und Wellenstatus landen in
    /// EINEM SaveChanges.
    /// </summary>
    public async Task<PickWaveDto?> ReleaseAsync(Guid waveId, ReleaseWaveRequest request, CancellationToken ct = default)
    {
        var wave = await _waves.GetAsync(waveId, ct);
        if (wave is null) return null;

        if (wave.Status != PickWaveStatus.Open)
            throw new InvalidOperationException($"Welle {wave.WaveNumber} ist nicht offen ({wave.Status}) und kann nicht freigegeben werden");
        if (wave.OrderIds.Count == 0)
            throw new InvalidOperationException("Leere Welle kann nicht released werden");

        // One consolidated picklist. Reuses the full FEFO + route-optimizer pipeline - ohne eigenen Commit.
        var staged = await _pickLists.StageAsync(wave.OrderIds.ToList(), request.StartPickPointId, request.EndPickPointId, null, ct);

        wave.MarkReleased(new[] { staged.PickList.Id });
        await _uow.SaveChangesAsync(ct);
        return ToDto(wave);
    }

    /// <summary>
    /// Bricht die Welle ab: aus Open jederzeit, aus Released nur, solange keine ihrer Picklisten begonnen wurde -
    /// dann werden diese Listen storniert und die Bestellungen wieder freigegeben (New). Abgeschlossene oder
    /// bereits abgebrochene Wellen werfen <see cref="InvalidOperationException"/>.
    /// </summary>
    public async Task<bool> CancelAsync(Guid waveId, CancellationToken ct = default)
    {
        var wave = await _waves.GetAsync(waveId, ct);
        if (wave is null) return false;

        if (wave.Status == PickWaveStatus.Released)
            await _pickLists.CancelUnstartedAsync(wave.PickListIds.ToList(), ct);

        wave.Cancel();
        await _uow.SaveChangesAsync(ct);
        return true;
    }

    private static PickWaveDto ToDto(PickWave w) => new(
        w.Id, w.WaveNumber, w.Description, w.Status.ToString(),
        w.CutoffAt, w.CreatedAt, w.ReleasedAt, w.CompletedAt,
        w.OrderIds, w.PickListIds);
}
