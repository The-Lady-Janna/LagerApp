using Lager.Domain.Common;

namespace Lager.Domain.Orders;

public class OrderLine : Entity
{
    public Guid OrderId { get; private set; }
    public Guid ArticleId { get; private set; }
    public int Quantity { get; private set; }

    private OrderLine() { }

    public OrderLine(Guid articleId, int quantity)
    {
        if (quantity <= 0) throw new ArgumentOutOfRangeException(nameof(quantity));
        ArticleId = articleId;
        Quantity = quantity;
    }

    internal void AttachToOrder(Guid orderId) => OrderId = orderId;
}
