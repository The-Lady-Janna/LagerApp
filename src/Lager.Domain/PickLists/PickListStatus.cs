namespace Lager.Domain.PickLists;

public enum PickListStatus
{
    /// <summary>Angelegt, noch nicht begonnen.</summary>
    Pending = 0,
    /// <summary>Ein Picker ist zugewiesen (<see cref="PickList.Assign"/>).</summary>
    InProgress = 1,
    /// <summary>Verpackt: Bestand ist gebucht. Endgültig.</summary>
    Completed = 2,
    /// <summary>Picking finished — waiting on the packing station.</summary>
    Picked = 3,
    /// <summary>Storniert (<see cref="PickList.Cancel"/>), ohne Buchungswirkung. Endgültig.</summary>
    Cancelled = 9
}
