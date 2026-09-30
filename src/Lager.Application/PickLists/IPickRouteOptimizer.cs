using Lager.Domain.Warehouse;

namespace Lager.Application.PickLists;

public record PickCandidate(
    Guid OrderId,
    Guid OrderLineId,
    Guid ArticleId,
    Guid StorageLocationId,
    int Quantity);

public record OptimizedRoute(
    IReadOnlyList<PickCandidate> Ordered,
    int TotalDistanceMm,
    IReadOnlyList<Position> Waypoints)
{
    /// <summary>
    /// Hinweise zur Route (deutsche Klartexte), z. B. Luftlinie statt Wandumgehung, weil ein Ziel nicht
    /// erreichbar ist, oder ein in eine Wand verschobener Punkt. Leer, wenn alles regulär berechnet wurde.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public interface IPickRouteOptimizer
{
    OptimizedRoute Optimize(
        IReadOnlyCollection<PickCandidate> candidates,
        IReadOnlyDictionary<Guid, StorageLocation> locations,
        Position start,
        IReadOnlyCollection<Wall> walls,
        Position? end = null);
}
