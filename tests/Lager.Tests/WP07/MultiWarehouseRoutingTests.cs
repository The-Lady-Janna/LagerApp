using Lager.Application.PickLists;
using Lager.Contracts.PickLists;
using Lager.Domain.Warehouse;
using Lager.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Lager.Tests.WP07;

/// <summary>
/// Jedes Lager hat sein eigenes Koordinatensystem: eine Route darf nur die Wände und Pick-Points ihres Lagers
/// sehen. Ein aufzeichnender Optimierer (delegiert an den echten) hält fest, womit er aufgerufen wurde.
/// </summary>
public class MultiWarehouseRoutingTests
{
    private sealed record Call(
        IReadOnlyList<PickCandidate> Candidates, IReadOnlyList<Wall> Walls, Position Start, Position? End);

    private sealed class RecordingOptimizer : IPickRouteOptimizer
    {
        private readonly IPickRouteOptimizer _inner = new WallAwarePickRouteOptimizer();
        public List<Call> Calls { get; } = new();

        public OptimizedRoute Optimize(
            IReadOnlyCollection<PickCandidate> candidates,
            IReadOnlyDictionary<Guid, StorageLocation> locations,
            Position start,
            IReadOnlyCollection<Wall> walls,
            Position? end = null)
        {
            lock (Calls) Calls.Add(new Call(candidates.ToList(), walls.ToList(), start, end));
            return _inner.Optimize(candidates, locations, start, walls, end);
        }
    }

    [Fact]
    public async Task Each_warehouse_is_routed_with_its_own_walls_and_pick_points_and_the_routes_are_appended()
    {
        var recorder = new RecordingOptimizer();
        using var baseFactory = new LagerApiFactory();
        using var factory = baseFactory.WithWebHostBuilder(b => b.ConfigureServices(services =>
        {
            services.RemoveAll<IPickRouteOptimizer>();
            services.AddSingleton<IPickRouteOptimizer>(recorder);
        }));
        var w = new PickWorld(factory.Services);

        var whA = await w.AddWarehouseAsync("WH-A");
        var whB = await w.AddWarehouseAsync("WH-B");
        var binA = await w.AddBinAsync(whA, "A-BIN", x: 1_000);
        var binB = await w.AddBinAsync(whB, "B-BIN", x: 1_000);      // gleiche Koordinaten wie in Lager A
        var wallA = await w.AddWallAsync(whA, new Position(-3_000, -3_000, 0), new Position(-3_000, 3_000, 0));
        var wallB = await w.AddWallAsync(whB, new Position(-4_000, -4_000, 0), new Position(-4_000, 4_000, 0));
        var startA = new Position(100, 100, 0);
        var startB = new Position(9_000, 100, 0);
        var endB = new Position(9_500, 100, 0);
        await w.AddPickPointAsync(whA, "Start A", startA, PickPointType.Start);
        var startBId = await w.AddPickPointAsync(whB, "Start B", startB, PickPointType.Start);
        var endBId = await w.AddPickPointAsync(whB, "Ende B", endB, PickPointType.End);

        var articleA = await w.AddArticleAsync();
        var articleB = await w.AddArticleAsync();
        await w.AddStockAsync(articleA, binA, 10);
        await w.AddStockAsync(articleB, binB, 10);
        var order = await w.AddOrderAsync((articleB, 1), (articleA, 2));   // absichtlich B zuerst

        // Start-/End-Pick-Point aus der Anfrage gehören zu Lager B und gelten nur dort.
        var list = await w.PickListsAsync(s => s.GenerateAsync(new GeneratePickListRequest(new[] { order }, startBId, endBId)));

        Assert.Equal(2, recorder.Calls.Count);
        var callA = Assert.Single(recorder.Calls, c => c.Candidates.Any(x => x.StorageLocationId == binA.Id));
        var callB = Assert.Single(recorder.Calls, c => c.Candidates.Any(x => x.StorageLocationId == binB.Id));
        Assert.All(callA.Candidates, c => Assert.Equal(binA.Id, c.StorageLocationId));   // nie gemischt
        Assert.All(callB.Candidates, c => Assert.Equal(binB.Id, c.StorageLocationId));
        Assert.Equal(new[] { wallA }, callA.Walls.Select(x => x.Id));                    // nur die eigene Wand
        Assert.Equal(new[] { wallB }, callB.Walls.Select(x => x.Id));
        Assert.Equal(startA, callA.Start);          // Lager A wählt seinen eigenen Start-Pick-Point
        Assert.Null(callA.End);
        Assert.Equal(startB, callB.Start);          // Lager B nimmt den angeforderten
        Assert.Equal(endB, callB.End);

        // Die Teilrouten hängen nacheinander (nach Lager-Code) mit fortlaufender Nummer an.
        Assert.Equal(new[] { binA.Id, binB.Id }, list.Items.OrderBy(i => i.SequenceNumber).Select(i => i.StorageLocationId));
        Assert.Equal(new[] { 1, 2 }, list.Items.Select(i => i.SequenceNumber).OrderBy(n => n));

        // Neuberechnen nutzt dieselbe Aufteilung.
        recorder.Calls.Clear();
        var recalculated = await w.PickListsAsync(s => s.RecalculateAsync(list.Id, new RecalculatePickListRequest()));
        Assert.Equal(2, recalculated!.Items.Count);
        Assert.Equal(2, recorder.Calls.Count);
        Assert.All(recorder.Calls, c =>
        {
            var warehouseWall = c.Candidates.Any(x => x.StorageLocationId == binA.Id) ? wallA : wallB;
            Assert.Equal(new[] { warehouseWall }, c.Walls.Select(x => x.Id));
        });
    }
}
