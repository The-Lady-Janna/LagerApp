using Lager.Domain.PickLists;

namespace Lager.Application.Abstractions;

public interface IPickListRepository : IRepository<PickList>
{
    /// <summary>
    /// Vergibt die nächste Picklisten-Nummer. Atomar: das Inkrement läuft direkt in der Datenbank
    /// (UPDATE ... SET NextValue = NextValue + 1), parallele Aufrufer bekommen nie denselben Wert.
    /// Die Nummer ist sofort vergeben - auch wenn der Aufrufer danach abbricht (dann bleibt eine Lücke).
    /// </summary>
    Task<long> NextSequenceAsync(CancellationToken ct = default);

    /// <summary>
    /// Setzt den Nummernkreis auf 1 zurück. Nur aufrufen, wenn keine Pickliste mehr existiert.
    /// Wirkt sofort (kein SaveChanges nötig).
    /// </summary>
    Task ResetSequenceAsync(CancellationToken ct = default);

    /// <summary>
    /// Tracked: Picklisten (samt Items) mit einem der angegebenen Status.
    /// </summary>
    Task<IReadOnlyList<PickList>> ListByStatusAsync(IReadOnlyCollection<PickListStatus> statuses, CancellationToken ct = default);

    /// <summary>Anzahl der Picklisten mit einem der angegebenen Status.</summary>
    Task<int> CountByStatusAsync(IReadOnlyCollection<PickListStatus> statuses, CancellationToken ct = default);

    /// <summary>Status der Picklisten mit den angegebenen Ids (unbekannte Ids fehlen im Ergebnis).</summary>
    Task<IReadOnlyDictionary<Guid, PickListStatus>> GetStatusesAsync(IEnumerable<Guid> ids, CancellationToken ct = default);

    /// <summary>Tracked: die Picklisten (samt Items) mit den angegebenen Ids.</summary>
    Task<IReadOnlyList<PickList>> GetManyAsync(IEnumerable<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// Ids aller Bestellungen, die auf Positionen einer Pickliste mit einem der angegebenen Status stehen.
    /// </summary>
    Task<IReadOnlyCollection<Guid>> ListOrderIdsAsync(IReadOnlyCollection<PickListStatus> statuses, CancellationToken ct = default);

    /// <summary>
    /// Tracked: entfernt alle Positionen der Bestellung aus den noch aktiven Picklisten (Pending, InProgress, Picked;
    /// verpackte und stornierte Listen bleiben unberührt) und liefert diese Picklisten. Die Positionen sind nur zum
    /// Löschen vorgemerkt - erst das SaveChanges des Aufrufers schreibt alles in einer Transaktion. Ob eine Liste
    /// danach leer ist (und storniert wird), entscheidet der Aufrufer.
    /// </summary>
    Task<IReadOnlyList<PickList>> RemoveOrderItemsAsync(Guid orderId, CancellationToken ct = default);

    /// <summary>
    /// Ersetzt alle PickItems der Pickliste durch die neuen: die alten Items werden vom Change-Tracker
    /// gelöscht, die neuen eingefügt (<see cref="PickList.ReplaceItems"/>). Es wird NICHTS sofort ausgeführt -
    /// erst das SaveChanges des Aufrufers schreibt alles in einer Transaktion. Scheitert es, bleibt die
    /// Pickliste unverändert (kein Datenverlust).
    /// </summary>
    Task ReplaceItemsAsync(PickList pl, IEnumerable<PickItem> newItems, CancellationToken ct = default);
}
