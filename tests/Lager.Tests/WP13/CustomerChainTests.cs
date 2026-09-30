using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Orders;
using Lager.Contracts.Shipping;
using Lager.Domain.Orders;

namespace Lager.Tests.WP13;

/// <summary>Die Kette Kunde -> Lieferadresse -> Bestellung -> Sendung (Order.LinkCustomer war bisher nie verdrahtet).</summary>
public class CustomerChainTests : IClassFixture<Wp13Fixture>
{
    private readonly Wp13Fixture _fx;
    private Wp13World W => _fx.World;

    public CustomerChainTests(Wp13Fixture fixture) => _fx = fixture;

    private Task<OrderDto> CreateAsync(Guid article, Guid? customer, Guid? address) =>
        W.OrdersAsync(s => s.CreateAsync(
            Wp13World.OrderRequest(Wp13World.Unique("CUST"), article, 1, customerId: customer, addressId: address), OrderSource.Manual));

    [Fact]
    public async Task The_order_carries_customer_name_and_shipping_address_everywhere_it_is_read()
    {
        var article = await W.AddArticleAsync();
        var (customer, shipping, _) = await W.AddCustomerAsync();
        var admin = await _fx.AdminAsync();

        var response = await admin.PostAsJsonAsync("/api/orders/manual",
            Wp13World.OrderRequest(Wp13World.Unique("CUST"), article, 1, customerId: customer, addressId: shipping));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<OrderDto>())!;
        var byId = (await admin.GetFromJsonAsync<OrderDto>($"/api/orders/{created.Id}"))!;
        var listed = (await admin.GetFromJsonAsync<List<OrderDto>>("/api/orders"))!.Single(o => o.Id == created.Id);

        foreach (var dto in new[] { created, byId, listed })
        {
            Assert.Equal(customer, dto.CustomerId);
            Assert.StartsWith("Kunde ", dto.CustomerName);
            Assert.Equal(shipping, dto.ShippingAddressId);
            var address = dto.ShippingAddress!;
            Assert.Equal(("Hafenstr. 5", "Halle 2", "20457", "Hamburg", "DE"),
                (address.Street, address.Street2, address.Zip, address.City, address.Country));
        }
    }

    [Fact]
    public async Task An_order_without_a_customer_stays_valid_and_shows_no_address()
    {
        var article = await W.AddArticleAsync();

        var order = await CreateAsync(article, null, null);

        Assert.Null(order.CustomerId);
        Assert.Null(order.CustomerName);
        Assert.Null(order.ShippingAddress);
    }

    [Fact]
    public async Task Customer_and_address_are_checked_before_the_order_is_created()
    {
        var article = await W.AddArticleAsync();
        var (customer, shipping, billing) = await W.AddCustomerAsync();
        var (inactive, inactiveAddress, _) = await W.AddCustomerAsync(active: false);
        var (_, foreignAddress, _) = await W.AddCustomerAsync();

        async Task<Exception> Fails(Guid? c, Guid? a) => await Assert.ThrowsAnyAsync<Exception>(() => CreateAsync(article, c, a));

        var deactivated = Assert.IsType<InvalidOperationException>(await Fails(inactive, inactiveAddress));
        Assert.Equal("customer_inactive", deactivated.Data["code"]);

        var unknown = Assert.IsType<ArgumentException>(await Fails(Guid.NewGuid(), null));
        Assert.Equal("unknown_customer", unknown.Data["code"]);

        var foreign = Assert.IsType<ArgumentException>(await Fails(customer, foreignAddress));
        Assert.Equal("address_not_of_customer", foreign.Data["code"]);

        var invoiceOnly = Assert.IsType<ArgumentException>(await Fails(customer, billing));
        Assert.Equal("address_not_shipping", invoiceOnly.Data["code"]);

        var withoutCustomer = Assert.IsType<ArgumentException>(await Fails(null, shipping));
        Assert.Equal("shipping_address_requires_customer", withoutCustomer.Data["code"]);
    }

    [Fact]
    public async Task The_shipment_takes_the_orders_shipping_address_as_its_recipient()
    {
        var (article, _) = await W.AddStockedArticleAsync(20);
        var (customer, shipping, _) = await W.AddCustomerAsync();
        var packed = await W.AddPackedOrderAsync(article, 2, customer, shipping);

        var created = await W.ShipmentsAsync(s => s.CreateAsync(new CreateShipmentRequest(packed, null, "MANUAL", 300, 200, 100, 1500)));
        var admin = await _fx.AdminAsync();
        var listed = (await admin.GetFromJsonAsync<List<ShipmentDto>>("/api/shipments"))!.Single(s => s.Id == created.Id);

        foreach (var shipment in new[] { created, listed })
        {
            Assert.StartsWith("Kunde ", shipment.RecipientName);
            Assert.Equal(("Hafenstr. 5", "Halle 2", "20457", "Hamburg", "DE"),
                (shipment.RecipientStreet, shipment.RecipientStreet2, shipment.RecipientZip, shipment.RecipientCity, shipment.RecipientCountry));
        }
    }

    [Fact]
    public async Task Without_a_customer_the_shipment_has_no_recipient_which_a_manual_carrier_accepts()
    {
        var (article, _) = await W.AddStockedArticleAsync(20);
        var packed = await W.AddPackedOrderAsync(article, 1);

        var shipment = await W.ShipmentsAsync(s => s.CreateAsync(new CreateShipmentRequest(packed, null, "MANUAL", 300, 200, 100, 1500)));

        Assert.Null(shipment.RecipientName);
        Assert.Null(shipment.RecipientStreet);
    }
}
