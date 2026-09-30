using Lager.Domain.Orders;

namespace Lager.Tests.WP07;

/// <summary>Domain-Unit-Tests (ohne Host) für die Statusmaschine von <see cref="Order"/>.</summary>
public class OrderStateMachineTests
{
    private static readonly OrderStatus[] All =
    {
        OrderStatus.New, OrderStatus.Picking, OrderStatus.Picked,
        OrderStatus.Packed, OrderStatus.Shipped, OrderStatus.Cancelled,
    };

    // Erlaubte Übergänge über Transition() laut Spezifikation - alles andere muss werfen.
    private static readonly HashSet<(OrderStatus, OrderStatus)> Allowed = new()
    {
        (OrderStatus.New, OrderStatus.Picking),
        (OrderStatus.New, OrderStatus.Cancelled),
        (OrderStatus.Picking, OrderStatus.Picked),
        (OrderStatus.Picking, OrderStatus.Cancelled),
        (OrderStatus.Picked, OrderStatus.Packed),
        (OrderStatus.Packed, OrderStatus.Shipped),
    };

    private static Order InState(OrderStatus status)
    {
        var order = new Order("ORD-TEST", OrderSource.Manual, null, new[] { new OrderLine(Guid.NewGuid(), 1) });
        switch (status)
        {
            case OrderStatus.New: break;
            case OrderStatus.Picking: order.Transition(OrderStatus.Picking); break;
            case OrderStatus.Picked: order.Transition(OrderStatus.Picking); order.Transition(OrderStatus.Picked); break;
            case OrderStatus.Packed:
                order.Transition(OrderStatus.Picking); order.Transition(OrderStatus.Picked); order.Transition(OrderStatus.Packed); break;
            case OrderStatus.Shipped:
                order.Transition(OrderStatus.Picking); order.Transition(OrderStatus.Picked);
                order.Transition(OrderStatus.Packed); order.Transition(OrderStatus.Shipped); break;
            case OrderStatus.Cancelled: order.Transition(OrderStatus.Cancelled); break;
        }
        Assert.Equal(status, order.Status);
        return order;
    }

    public static TheoryData<OrderStatus, OrderStatus> AllPairs()
    {
        var data = new TheoryData<OrderStatus, OrderStatus>();
        foreach (var from in All)
            foreach (var to in All)
                data.Add(from, to);
        return data;
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Transition_allows_exactly_the_specified_edges(OrderStatus from, OrderStatus to)
    {
        var order = InState(from);
        var tokenBefore = order.ConcurrencyToken;

        if (Allowed.Contains((from, to)))
        {
            order.Transition(to);
            Assert.Equal(to, order.Status);
            Assert.NotEqual(tokenBefore, order.ConcurrencyToken);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => order.Transition(to));
            Assert.Equal(from, order.Status);                           // nichts hat sich geändert
            Assert.Equal(tokenBefore, order.ConcurrencyToken);
        }
    }

    [Fact]
    public void Packed_can_never_go_back_to_New()
    {
        var order = InState(OrderStatus.Packed);

        Assert.Throws<InvalidOperationException>(() => order.Transition(OrderStatus.New));
        Assert.Throws<InvalidOperationException>(() => order.ReleaseFromPicking());
        Assert.Equal(OrderStatus.Packed, order.Status);
    }

    [Fact]
    public void Picking_goes_back_to_New_only_through_ReleaseFromPicking()
    {
        var order = InState(OrderStatus.Picking);

        Assert.Throws<InvalidOperationException>(() => order.Transition(OrderStatus.New));

        order.ReleaseFromPicking();
        Assert.Equal(OrderStatus.New, order.Status);
    }

    [Theory]
    [InlineData(OrderStatus.New)]
    [InlineData(OrderStatus.Packed)]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Cancelled)]
    public void ReleaseFromPicking_is_rejected_outside_the_picking_phase(OrderStatus from)
    {
        var order = InState(from);

        Assert.Throws<InvalidOperationException>(() => order.ReleaseFromPicking());
        Assert.Equal(from, order.Status);
    }

    [Fact]
    public void Helper_methods_walk_the_happy_path()
    {
        var order = InState(OrderStatus.New);

        order.MarkPicking();
        Assert.Equal(OrderStatus.Picking, order.Status);
        order.MarkPicked();
        Assert.Equal(OrderStatus.Picked, order.Status);
        order.MarkPacked();
        Assert.Equal(OrderStatus.Packed, order.Status);

        // und sind selbst geschützt: kein Überspringen
        var fresh = InState(OrderStatus.New);
        Assert.Throws<InvalidOperationException>(() => fresh.MarkPacked());
        Assert.Throws<InvalidOperationException>(() => fresh.MarkPicked());
    }

    [Fact]
    public void Shipped_and_Cancelled_are_final()
    {
        foreach (var final in new[] { OrderStatus.Shipped, OrderStatus.Cancelled })
        {
            var order = InState(final);
            foreach (var to in All)
                Assert.Throws<InvalidOperationException>(() => order.Transition(to));
        }
    }
}
