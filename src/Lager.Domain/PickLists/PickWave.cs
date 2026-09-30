using Lager.Domain.Common;

namespace Lager.Domain.PickLists;

/// <summary>
/// A pick wave groups multiple orders that should be released together for
/// picking. Two reasons to use waves:
///  1. <b>Cutoff batching</b> — collect all orders that arrive before X o'clock
///     and release them as one bundle (typical for shipping-cutoff workflows).
///  2. <b>Split by strategy</b> — the wave can be released as one big picklist
///     or split (one picklist per zone / per picker). Today we support "one
///     consolidated picklist"; zone-split is a future extension.
///
/// The wave itself doesn't hold the route — once released, it produces N
/// concrete <see cref="PickList"/> aggregates and stores their ids.
///
/// Lebenszyklus: Open -> Released -> Completed (sobald alle Picklisten verpackt sind);
/// Cancelled aus Open oder Released (Letzteres nur, solange keine Liste begonnen wurde).
/// </summary>
public class PickWave : Entity
{
    public string WaveNumber { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public DateTime? CutoffAt { get; private set; }
    public PickWaveStatus Status { get; private set; } = PickWaveStatus.Open;
    public DateTime? ReleasedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }

    private readonly List<Guid> _orderIds = new();
    public IReadOnlyList<Guid> OrderIds => _orderIds.AsReadOnly();

    private readonly List<Guid> _pickListIds = new();
    public IReadOnlyList<Guid> PickListIds => _pickListIds.AsReadOnly();

    /// <summary>Persisted as comma-separated for EF — keeps schema simple.</summary>
    public string OrderIdsCsv
    {
        get => string.Join(",", _orderIds);
        private set
        {
            _orderIds.Clear();
            if (string.IsNullOrWhiteSpace(value)) return;
            _orderIds.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse));
        }
    }

    public string PickListIdsCsv
    {
        get => string.Join(",", _pickListIds);
        private set
        {
            _pickListIds.Clear();
            if (string.IsNullOrWhiteSpace(value)) return;
            _pickListIds.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse));
        }
    }

    private PickWave() { }

    public PickWave(string waveNumber, string? description, DateTime? cutoffAt)
    {
        if (string.IsNullOrWhiteSpace(waveNumber))
            throw new ArgumentException("WaveNumber required", nameof(waveNumber));
        WaveNumber = waveNumber.Trim();
        Description = description;
        CutoffAt = cutoffAt;
    }

    public void AddOrders(IEnumerable<Guid> orderIds)
    {
        if (Status != PickWaveStatus.Open)
            throw new InvalidOperationException("Welle ist nicht mehr offen, keine Orders hinzufügbar");
        foreach (var id in orderIds)
        {
            if (id == Guid.Empty) continue;
            if (!_orderIds.Contains(id)) _orderIds.Add(id);
        }
        Touch();
    }

    public void RemoveOrder(Guid orderId)
    {
        if (Status != PickWaveStatus.Open)
            throw new InvalidOperationException("Welle ist nicht mehr offen");
        _orderIds.Remove(orderId);
        Touch();
    }

    /// <summary>
    /// Mark wave as released — the application service has already created the
    /// concrete picklists and passes their ids back here for record-keeping.
    /// </summary>
    public void MarkReleased(IEnumerable<Guid> generatedPickListIds)
    {
        if (Status != PickWaveStatus.Open)
            throw new InvalidOperationException("Welle bereits released oder abgeschlossen");
        if (_orderIds.Count == 0)
            throw new InvalidOperationException("Leere Welle kann nicht released werden");
        _pickListIds.Clear();
        _pickListIds.AddRange(generatedPickListIds);
        Status = PickWaveStatus.Released;
        ReleasedAt = DateTime.UtcNow;
        Touch();
    }

    /// <summary>
    /// Alle verknüpften Picklisten sind verpackt — der Aufrufer prüft das extern. Nur eine freigegebene
    /// (Released) Welle kann abgeschlossen werden; eine bereits abgeschlossene bleibt unverändert.
    /// </summary>
    public void MarkCompleted()
    {
        if (Status == PickWaveStatus.Completed) return;
        if (Status != PickWaveStatus.Released)
            throw new InvalidOperationException($"Welle im Status {Status} kann nicht abgeschlossen werden");
        Status = PickWaveStatus.Completed;
        CompletedAt = DateTime.UtcNow;
        Touch();
    }

    /// <summary>
    /// Bricht die Welle ab: nur aus Open oder Released. Eine abgeschlossene oder bereits abgebrochene Welle
    /// wirft. Bei Released kümmert sich der Aufrufer um die (noch unbegonnenen) Picklisten und Bestellungen.
    /// </summary>
    public void Cancel()
    {
        if (Status == PickWaveStatus.Completed)
            throw new InvalidOperationException("Welle ist bereits abgeschlossen und lässt sich nicht abbrechen");
        if (Status == PickWaveStatus.Cancelled)
            throw new InvalidOperationException("Welle ist bereits abgebrochen");
        Status = PickWaveStatus.Cancelled;
        Touch();
    }
}

public enum PickWaveStatus
{
    Open = 0,
    Released = 1,
    Completed = 2,
    Cancelled = 9
}
