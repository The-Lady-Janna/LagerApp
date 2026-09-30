namespace Lager.Domain.Orders;

/// <summary>
/// Lebenszyklus einer Bestellung (siehe <see cref="Order.Transition"/>):
/// New -> Picking -> Picked -> Packed -> Shipped, Stornierung (Cancelled) aus New, Picking und Picked
/// (<see cref="Order.Cancel"/>; ab Packed ist der Bestand gebucht, dann gibt es nur noch die Retoure).
/// Shipped und Cancelled sind endgültig.
/// </summary>
public enum OrderStatus
{
    /// <summary>Angelegt, noch auf keiner Pickliste.</summary>
    New = 0,
    /// <summary>Steht auf einer offenen Pickliste.</summary>
    Picking = 1,
    /// <summary>Picken abgeschlossen (mark-picked), wartet am Packplatz.</summary>
    Picked = 2,
    /// <summary>Verpackt, Bestand ist gebucht.</summary>
    Packed = 3,
    Shipped = 4,
    Cancelled = 9
}

public enum OrderSource
{
    Manual = 0,
    Api = 1
}
