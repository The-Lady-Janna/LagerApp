using Lager.Application.PickLists;
using Lager.Domain.Warehouse;

namespace Lager.Tests.WP08;

/// <summary>Bausteine für die Routen-Tests: Bins mit bekannter Mitte, Wände, Kandidaten.</summary>
internal sealed class RouteFixtures
{
    private static readonly Guid WarehouseId = Guid.NewGuid();

    public Dictionary<Guid, StorageLocation> Locations { get; } = new();
    public List<PickCandidate> Candidates { get; } = new();

    /// <summary>Legt einen 100x100-Bin an, dessen MITTE bei (centerX, centerY) liegt, und einen Pick darauf.</summary>
    public StorageLocation Pick(string code, int centerX, int centerY, int quantity = 1)
    {
        var bin = new StorageLocation(Guid.NewGuid(), code, new Position(centerX - 50, centerY - 50, 0), 100, 100, 100, 10_000);
        Locations[bin.Id] = bin;
        Candidates.Add(new PickCandidate(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), bin.Id, quantity));
        return bin;
    }

    public OptimizedRoute Run(int startX, int startY, IReadOnlyCollection<Wall>? walls = null, (int X, int Y)? end = null,
        IReadOnlyCollection<PickCandidate>? candidates = null) =>
        new WallAwarePickRouteOptimizer().Optimize(
            candidates ?? Candidates,
            Locations,
            new Position(startX, startY, 0),
            walls ?? Array.Empty<Wall>(),
            end is null ? null : new Position(end.Value.X, end.Value.Y, 0));

    public static Wall Wall(int thicknessMm, params (int X, int Y)[] points) =>
        new(WarehouseId, points.Select(p => new Position(p.X, p.Y, 0)), thicknessMm);

    /// <summary>Wandzug als geschlossener Ring (Rechteck), z. B. ein eingeschlossener Raum.</summary>
    public static Wall Ring(int thicknessMm, int x1, int y1, int x2, int y2) =>
        Wall(thicknessMm, (x1, y1), (x2, y1), (x2, y2), (x1, y2), (x1, y1));

    /// <summary>Wegpunkte als (x, y)-Paare.</summary>
    public static (int X, int Y)[] Points(OptimizedRoute route) => route.Waypoints.Select(p => (p.XMm, p.YMm)).ToArray();

    /// <summary>Länge des Wegpunkt-Zuges (Summe der Luftlinien zwischen den Wegpunkten).</summary>
    public static double PolylineLength(OptimizedRoute route)
    {
        double sum = 0;
        for (var i = 0; i + 1 < route.Waypoints.Count; i++)
        {
            var a = route.Waypoints[i];
            var b = route.Waypoints[i + 1];
            sum += Math.Sqrt(Math.Pow(a.XMm - b.XMm, 2) + Math.Pow(a.YMm - b.YMm, 2));
        }
        return sum;
    }

    /// <summary>True, wenn ein Stück des Wegzuges durch das Innere einer Wand läuft (Berühren des Rands ist erlaubt).</summary>
    public static bool CrossesAnyWall(OptimizedRoute route, IEnumerable<Wall> walls)
    {
        var rects = new List<WallRect>();
        foreach (var w in walls)
            for (var i = 0; i + 1 < w.Points.Count; i++)
                rects.Add(new WallRect(w.Points[i].XMm, w.Points[i].YMm, w.Points[i + 1].XMm, w.Points[i + 1].YMm, w.ThicknessMm));

        for (var i = 0; i + 1 < route.Waypoints.Count; i++)
        {
            var a = new Vec2(route.Waypoints[i].XMm, route.Waypoints[i].YMm);
            var b = new Vec2(route.Waypoints[i + 1].XMm, route.Waypoints[i + 1].YMm);
            if (rects.Any(r => r.Blocks(a, b))) return true;
        }
        return false;
    }
}
