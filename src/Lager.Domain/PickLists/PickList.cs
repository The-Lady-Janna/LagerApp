using System.Text.Json;
using Lager.Domain.Common;
using Lager.Domain.Warehouse;

namespace Lager.Domain.PickLists;

/// <summary>
/// Kommissionierauftrag. Lebenszyklus:
/// Pending -> (InProgress) -> Picked -> Completed (= verpackt, Bestand gebucht);
/// Cancelled aus Pending/InProgress/Picked. Completed und Cancelled sind endgültig.
/// </summary>
public class PickList : Entity
{
    public string PickListNumber { get; private set; } = string.Empty;
    public PickListStatus Status { get; private set; } = PickListStatus.Pending;
    public string? AssignedTo { get; private set; }
    public int TotalDistanceMm { get; private set; }
    public Guid? PickCartConfigId { get; private set; }

    private List<Position> _waypoints = new();
    public IReadOnlyList<Position> Waypoints => _waypoints;

    /// <summary>
    /// JSON form of <see cref="Waypoints"/>. Nullable so EF tolerates legacy
    /// rows persisted before this column existed.
    /// </summary>
    public string? WaypointsJson
    {
        get => JsonSerializer.Serialize(_waypoints);
        private set => _waypoints = string.IsNullOrEmpty(value)
            ? new List<Position>()
            : JsonSerializer.Deserialize<List<Position>>(value) ?? new List<Position>();
    }

    private readonly List<PickItem> _items = new();
    public IReadOnlyCollection<PickItem> Items => _items.AsReadOnly();

    private PickList() { }

    public PickList(string pickListNumber, IEnumerable<PickItem> orderedItems, int totalDistanceMm, IEnumerable<Position>? waypoints = null)
    {
        if (string.IsNullOrWhiteSpace(pickListNumber)) throw new ArgumentException("Pick list number required", nameof(pickListNumber));
        PickListNumber = pickListNumber.Trim();
        TotalDistanceMm = totalDistanceMm;
        if (waypoints is not null) _waypoints = waypoints.ToList();

        foreach (var item in orderedItems)
        {
            item.AttachToPickList(Id);
            _items.Add(item);
        }

        if (_items.Count == 0) throw new ArgumentException("Pick list must have at least one item", nameof(orderedItems));
    }

    /// <summary>Noch nicht abgeschlossen: Pending oder InProgress (Items und Route dürfen sich noch ändern).</summary>
    public bool IsOpenForChanges => Status is PickListStatus.Pending or PickListStatus.InProgress;

    /// <summary>Weist einen Picker zu und setzt den Status auf InProgress (nur vor dem Abschluss des Pickens).</summary>
    public void Assign(string user)
    {
        if (string.IsNullOrWhiteSpace(user)) throw new ArgumentException("Picker required", nameof(user));
        EnsureOpenForChanges("zugewiesen");
        AssignedTo = user;
        Status = PickListStatus.InProgress;
        Touch();
    }

    /// <summary>
    /// Nur den Picker zuweisen ohne den Status zu ändern — wird beim
    /// MarkPickingComplete aufgerufen wenn AssignedTo noch leer ist. Bildet
    /// die Basis für den Picker-Performance-Report.
    /// </summary>
    public void RecordPicker(string user)
    {
        if (string.IsNullOrWhiteSpace(user)) return;
        if (!string.IsNullOrEmpty(AssignedTo)) return;  // Erster Picker gewinnt
        AssignedTo = user;
        Touch();
    }

    /// <summary>
    /// Verpackt: die Pickliste ist abgeschlossen (Completed). Erlaubt aus Pending, InProgress und Picked;
    /// eine bereits abgeschlossene oder stornierte Liste wirft - so bucht ein zweites Packen nie erneut.
    /// </summary>
    public void MarkPacked()
    {
        if (Status == PickListStatus.Completed)
            throw new InvalidOperationException("Pickliste ist bereits verpackt");
        if (Status == PickListStatus.Cancelled)
            throw new InvalidOperationException("Pickliste ist storniert");
        Status = PickListStatus.Completed;
        Touch();
    }

    /// <summary>
    /// Picking finished — the cart is now sitting at the packing station,
    /// waiting for the actual confirmation/packaging step.
    /// </summary>
    public void MarkPickingComplete()
    {
        if (Status == PickListStatus.Completed)
            throw new InvalidOperationException("Pickliste ist bereits verpackt");
        if (Status == PickListStatus.Cancelled)
            throw new InvalidOperationException("Pickliste ist storniert");
        Status = PickListStatus.Picked;
        Touch();
    }

    /// <summary>
    /// Storniert die Pickliste. Nur ohne Buchungswirkung (Pending, InProgress, Picked); eine verpackte
    /// (Completed) oder bereits stornierte Liste wirft. Die zugehörigen Bestellungen gibt der Aufrufer frei.
    /// </summary>
    public void Cancel()
    {
        if (Status == PickListStatus.Completed)
            throw new InvalidOperationException("Pickliste ist bereits verpackt und lässt sich nicht stornieren");
        if (Status == PickListStatus.Cancelled)
            throw new InvalidOperationException("Pickliste ist bereits storniert");
        Status = PickListStatus.Cancelled;
        Touch();
    }

    public void AssignToCart(Guid cartConfigId)
    {
        PickCartConfigId = cartConfigId;
        Touch();
    }

    /// <summary>
    /// Replaces the items collection with a fresh ordered set. Nur vor dem Abschluss des Pickens
    /// (Pending/InProgress) erlaubt - bestätigte Mengen einer verpackten Liste dürfen nie überschrieben
    /// werden. Das Repository sorgt dafür, dass die alten Items vom Change-Tracker gelöscht und die neuen
    /// eingefügt werden (ein SaveChanges).
    /// </summary>
    public void ReplaceItems(IEnumerable<PickItem> newItems)
    {
        EnsureOpenForChanges("neu berechnet");
        var fresh = newItems.ToList();
        if (fresh.Count == 0)
            throw new ArgumentException("Pick list still needs at least one item", nameof(newItems));

        _items.Clear();
        foreach (var item in fresh)
        {
            item.AttachToPickList(Id);
            _items.Add(item);
        }
        Touch();
    }

    public void UpdateRouteSummary(int totalDistanceMm, IEnumerable<Position> waypoints)
    {
        EnsureOpenForChanges("neu berechnet");
        TotalDistanceMm = totalDistanceMm;
        _waypoints = waypoints.ToList();
        Touch();
    }

    private void EnsureOpenForChanges(string action)
    {
        if (!IsOpenForChanges)
            throw new InvalidOperationException($"Pickliste {PickListNumber} ({Status}) kann nicht mehr {action} werden");
    }
}
