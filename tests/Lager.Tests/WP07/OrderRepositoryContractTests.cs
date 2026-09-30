using Lager.Application.Abstractions;
using Lager.Domain.Orders;

namespace Lager.Tests.WP07;

/// <summary>
/// <see cref="IOrderRepository.ListByStatusAsync"/> hat eine Standardimplementierung, damit bestehende
/// Implementierungen der Schnittstelle (Test-Fakes anderer Pakete) ohne Änderung weiter kompilieren.
/// </summary>
public class OrderRepositoryContractTests
{
    /// <summary>Kennt nur die Mitglieder, die schon vor ListByStatusAsync existierten.</summary>
    private sealed class MinimalOrders : IOrderRepository
    {
        private readonly List<Order> _orders;
        public MinimalOrders(params Order[] orders) => _orders = orders.ToList();

        public Task<Order?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_orders.FirstOrDefault(o => o.Id == id));
        public Task<IReadOnlyList<Order>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Order>>(_orders);
        public Task AddAsync(Order entity, CancellationToken ct = default) { _orders.Add(entity); return Task.CompletedTask; }
        public void Remove(Order entity) => _orders.Remove(entity);
        public Task<Order?> GetByNumberAsync(string orderNumber, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<Order>> GetManyAsync(IEnumerable<Guid> ids, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static Order NewOrder(string number) =>
        new(number, OrderSource.Manual, null, new[] { new OrderLine(Guid.NewGuid(), 1) });

    [Fact]
    public async Task Default_implementation_filters_the_full_list_by_status()
    {
        var fresh = NewOrder("ORD-NEW");
        var picking = NewOrder("ORD-PICKING");
        picking.MarkPicking();
        IOrderRepository repository = new MinimalOrders(fresh, picking);

        var result = await repository.ListByStatusAsync(OrderStatus.New);

        Assert.Same(fresh, Assert.Single(result));
        Assert.Same(picking, Assert.Single(await repository.ListByStatusAsync(OrderStatus.Picking)));
        Assert.Empty(await repository.ListByStatusAsync(OrderStatus.Shipped));
    }
}
