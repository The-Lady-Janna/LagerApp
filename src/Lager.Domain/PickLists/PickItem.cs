using Lager.Domain.Common;

namespace Lager.Domain.PickLists;

public class PickItem : Entity
{
    public Guid PickListId { get; private set; }
    public int SequenceNumber { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid OrderLineId { get; private set; }
    public Guid ArticleId { get; private set; }
    public Guid StorageLocationId { get; private set; }
    public int Quantity { get; private set; }
    public bool Picked { get; private set; }
    public int? ConfirmedQuantity { get; private set; }
    public DateTime? ConfirmedAt { get; private set; }

    private PickItem() { }

    public PickItem(int sequenceNumber, Guid orderId, Guid orderLineId, Guid articleId, Guid storageLocationId, int quantity)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        SequenceNumber = sequenceNumber;
        OrderId = orderId;
        OrderLineId = orderLineId;
        ArticleId = articleId;
        StorageLocationId = storageLocationId;
        Quantity = quantity;
    }

    internal void AttachToPickList(Guid pickListId) => PickListId = pickListId;

    public void MarkPicked()
    {
        Picked = true;
        Touch();
    }

    /// <summary>
    /// Ob die Position schon bestätigt (gepackt) wurde. Eine Bestätigung ist endgültig.
    /// </summary>
    public bool IsConfirmed => ConfirmedAt.HasValue;

    /// <summary>
    /// Trägt die tatsächlich gepackte Menge ein. Erlaubt sind 0 bis zur geplanten Menge
    /// (<see cref="Quantity"/>) - mehr als geplant wird nie gepackt, weniger ist ein Kurz-Pick.
    /// Eine bereits bestätigte Position lässt sich nicht erneut bestätigen.
    /// </summary>
    public void ConfirmPacked(int actualQuantity)
    {
        if (actualQuantity < 0 || actualQuantity > Quantity)
            throw new ArgumentOutOfRangeException(nameof(actualQuantity),
                $"Gepackte Menge {actualQuantity} liegt außerhalb von 0..{Quantity}");
        if (IsConfirmed)
            throw new InvalidOperationException("Die Position wurde bereits bestätigt");
        ConfirmedQuantity = actualQuantity;
        ConfirmedAt = DateTime.UtcNow;
        Picked = true;
        Touch();
    }
}
