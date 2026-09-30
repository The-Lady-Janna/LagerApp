using Lager.Domain.Orders;
using Lager.Domain.Shipping;

namespace Lager.Tests.WP13;

/// <summary>Domain-Unit-Tests (ohne Host): Storno-Regeln, Planung, externe Referenz, Sendungs-Eingaben.</summary>
public class DomainRuleTests
{
    private static Order InState(OrderStatus status)
    {
        var order = new Order("ORD-WP13", OrderSource.Manual, null, new[] { new OrderLine(Guid.NewGuid(), 1) });
        switch (status)
        {
            case OrderStatus.New: break;
            case OrderStatus.Picking: order.MarkPicking(); break;
            case OrderStatus.Picked: order.MarkPicking(); order.MarkPicked(); break;
            case OrderStatus.Packed: order.MarkPicking(); order.MarkPicked(); order.MarkPacked(); break;
            case OrderStatus.Shipped: order.MarkPicking(); order.MarkPicked(); order.MarkPacked(); order.MarkShipped(); break;
            case OrderStatus.Cancelled: order.Cancel(); break;
        }
        Assert.Equal(status, order.Status);
        return order;
    }

    // ---- Storno ---------------------------------------------------------

    [Theory]
    [InlineData(OrderStatus.New)]
    [InlineData(OrderStatus.Picking)]
    [InlineData(OrderStatus.Picked)]
    public void Cancel_is_allowed_while_nothing_is_booked(OrderStatus from)
    {
        var order = InState(from);
        var tokenBefore = order.ConcurrencyToken;

        order.Cancel();

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.NotEqual(tokenBefore, order.ConcurrencyToken);
    }

    [Theory]
    [InlineData(OrderStatus.Packed)]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.Cancelled)]
    public void Cancel_is_rejected_once_stock_is_booked_or_the_order_is_final(OrderStatus from)
    {
        var order = InState(from);

        var ex = Assert.Throws<InvalidOperationException>(() => order.Cancel());

        Assert.Equal("order_not_cancellable", ex.Data["code"]);
        Assert.Equal(from, order.Status);
    }

    [Fact]
    public void MarkShipped_is_only_possible_from_Packed()
    {
        Assert.Throws<InvalidOperationException>(() => InState(OrderStatus.Picked).MarkShipped());
        var packed = InState(OrderStatus.Packed);

        packed.MarkShipped();

        Assert.Equal(OrderStatus.Shipped, packed.Status);
    }

    // ---- Planung und Kunde ----------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void SetPlanning_accepts_priorities_zero_to_three(int priority)
    {
        var order = InState(OrderStatus.New);
        var due = new DateTime(2030, 1, 2, 0, 0, 0, DateTimeKind.Utc);

        order.SetPlanning(priority, due);

        Assert.Equal(priority, order.Priority);
        Assert.Equal(due, order.DueDate);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(4)]
    public void SetPlanning_rejects_priorities_outside_the_range(int priority)
    {
        var order = InState(OrderStatus.New);

        Assert.Throws<ArgumentOutOfRangeException>(() => order.SetPlanning(priority, null));
        Assert.Equal(0, order.Priority);
    }

    [Fact]
    public void ExternalReference_is_trimmed_blank_means_none_and_length_is_limited()
    {
        var order = InState(OrderStatus.New);

        order.SetExternalReference("  SHOP-42  ");
        Assert.Equal("SHOP-42", order.ExternalReference);

        order.SetExternalReference("   ");
        Assert.Null(order.ExternalReference);

        Assert.Throws<ArgumentException>(() => order.SetExternalReference(new string('x', Order.MaxExternalReferenceLength + 1)));
    }

    [Fact]
    public void A_shipping_address_needs_a_customer()
    {
        var order = InState(OrderStatus.New);

        Assert.Throws<ArgumentException>(() => order.LinkCustomer(null, Guid.NewGuid()));

        var customer = Guid.NewGuid();
        var address = Guid.NewGuid();
        order.LinkCustomer(customer, address);
        Assert.Equal((customer, address), (order.CustomerId!.Value, order.ShippingAddressId!.Value));
    }

    // ---- Sendung --------------------------------------------------------

    private static Shipment NewShipment() => new("SH-WP13", Guid.NewGuid(), null, "manual");

    [Theory]
    [InlineData(0, 200, 100, 500)]
    [InlineData(300, 0, 100, 500)]
    [InlineData(300, 200, 0, 500)]
    [InlineData(300, 200, 100, 0)]
    public void Placeholder_dimensions_of_zero_are_rejected(int length, int width, int height, int weight)
    {
        var shipment = NewShipment();

        Assert.Throws<ArgumentException>(() => shipment.SetDimensions(length, width, height, weight));
    }

    [Theory]
    [InlineData("https://tracking.example/abc?x=1", true)]
    [InlineData("http://tracking.example/abc", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ftp://tracking.example/abc", false)]
    [InlineData("data:text/html,<script>alert(1)</script>", false)]
    [InlineData("/relativ/pfad", false)]
    [InlineData("tracking.example/abc", false)]
    public void Tracking_url_must_be_an_absolute_http_or_https_address(string url, bool valid)
    {
        var shipment = NewShipment();

        if (valid)
        {
            shipment.AssignTracking("TRACK-1", url, 0);
            Assert.Equal(url, shipment.TrackingUrl);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => shipment.AssignTracking("TRACK-1", url, 0));
            Assert.Null(shipment.TrackingNumber);
            Assert.Equal(ShipmentStatus.Ready, shipment.Status);
        }
    }

    [Fact]
    public void Tracking_url_is_limited_to_the_column_width_and_blank_means_none()
    {
        var shipment = NewShipment();

        Assert.Throws<ArgumentException>(() =>
            shipment.AssignTracking("T", "https://x.example/" + new string('a', Shipment.MaxTrackingUrlLength), 0));

        shipment.AssignTracking("T", "   ", 0);
        Assert.Null(shipment.TrackingUrl);
    }
}
