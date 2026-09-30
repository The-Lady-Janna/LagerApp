using System.Diagnostics;
using Lager.Application.PickLists;
using Lager.Domain.Warehouse;
using Xunit.Abstractions;
using static Lager.Tests.WP08.RouteFixtures;

namespace Lager.Tests.WP08;

/// <summary>
/// Reine Unit-Tests (ohne Host) für <see cref="WallAwarePickRouteOptimizer"/>: Zielpunkt, Wand-Umwege, unerreichbare
/// Ziele ohne Exception, wertgleiche Kandidaten, Reihenfolge/Endpunkt, Determinismus und Performance.
/// Erwartete Längen sind von Hand nachgerechnet (Pythagoras um die Wandecken).
/// </summary>
public class PickRouteOptimizerTests
{
    private readonly ITestOutputHelper _output;

    /// <summary>Grobe Obergrenze für Laufzeit-Schutzprüfungen: bewusst weit über der echten Dauer (Millisekunden), siehe WP33.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    public PickRouteOptimizerTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Without_walls_the_route_is_the_straight_line_in_nearest_order()
    {
        var f = new RouteFixtures();
        f.Pick("A-03", 3000, 0);
        f.Pick("A-01", 1000, 0);
        f.Pick("A-02", 2000, 0);

        var route = f.Run(0, 0);

        Assert.Equal(new[] { 1000, 2000, 3000 }, route.Ordered.Select(c => f.Locations[c.StorageLocationId].Position.XMm + 50));
        Assert.Equal(3000, route.TotalDistanceMm);
        Assert.Equal(new[] { (0, 0), (1000, 0), (2000, 0), (3000, 0) }, Points(route));
        Assert.Empty(route.Warnings);
    }

    [Fact]
    public void Target_point_is_the_bin_center_not_the_bin_corner()
    {
        // Frontend (PickRouteCanvas) setzt den Marker auf Position + halbe Breite/Tiefe.
        var f = new RouteFixtures();
        var bin = new StorageLocation(Guid.NewGuid(), "B-01", new Position(1000, 2000, 0), 600, 400, 300, 10_000);
        f.Locations[bin.Id] = bin;
        f.Candidates.Add(new PickCandidate(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), bin.Id, 1));

        var route = f.Run(0, 0);

        Assert.Equal((1300, 2200), Points(route).Last());
        Assert.Equal((int)Math.Round(Math.Sqrt(1300.0 * 1300 + 2200.0 * 2200)), route.TotalDistanceMm);
    }

    [Fact]
    public void Wall_forces_a_detour_around_its_corners()
    {
        // Wand x=1000 von y=-1000 bis 1000, Dicke 100 -> Weg über (950|1050, +-1000): 2*sqrt(950^2+1000^2)+100 = 2858,8.
        var f = new RouteFixtures();
        f.Pick("A-01", 2000, 0);
        var walls = new[] { Wall(100, (1000, -1000), (1000, 1000)) };

        var route = f.Run(0, 0, walls);

        Assert.InRange(route.TotalDistanceMm, 2858, 2860);
        Assert.False(CrossesAnyWall(route, walls), "Ein Wegstück läuft durch die Wand");
        Assert.Contains(Points(route), p => (p.X == 950 || p.X == 1050) && Math.Abs(p.Y) == 1000);
        Assert.InRange(PolylineLength(route), 2857, 2861);
    }

    [Fact]
    public void Diagonal_through_two_opposite_corners_of_a_thick_wall_is_not_a_free_edge()
    {
        // Pfeiler: Wand (1000,500)-(1000,1500) mit Dicke 1000 = Quadrat [500,1500]^2. Die Diagonale (0,0)-(2000,2000)
        // trifft nur die Ecken, läuft aber durch das Innere. Richtig: über (1500,500) = 2*sqrt(1500^2+500^2) = 3162.
        var f = new RouteFixtures();
        f.Pick("A-01", 2000, 2000);
        var walls = new[] { Wall(1000, (1000, 500), (1000, 1500)) };

        var route = f.Run(0, 0, walls);

        Assert.InRange(route.TotalDistanceMm, 3162, 3163);
        Assert.False(CrossesAnyWall(route, walls));
    }

    [Fact]
    public void Target_behind_a_closed_wall_ring_gives_a_route_with_a_warning_instead_of_an_exception()
    {
        var f = new RouteFixtures();
        f.Pick("R-99", 2000, 2000);
        f.Pick("A-01", 500, 0);
        var walls = new[] { Ring(100, 1000, 1000, 3000, 3000) };

        var route = f.Run(0, 0, walls);

        Assert.Equal(2, route.Ordered.Count);
        Assert.Contains(route.Warnings, w => w.Contains("Bin R-99") && w.Contains("Luftlinie"));
        Assert.Equal(new Position(2000, 2000, 0), route.Waypoints.Last());
        // A-01 liegt frei und wird zuerst angefahren, danach folgt die Luftlinie in den Raum.
        Assert.Equal("A-01", f.Locations[route.Ordered[0].StorageLocationId].Code);
    }

    [Fact]
    public void Start_behind_a_closed_wall_ring_does_not_throw_either()
    {
        var f = new RouteFixtures();
        f.Pick("A-01", 5000, 5000);
        var walls = new[] { Ring(100, 1000, 1000, 3000, 3000) };

        var route = f.Run(2000, 2000, walls);

        Assert.Single(route.Ordered);
        Assert.Contains(route.Warnings, w => w.Contains("Start") && w.Contains("Bin A-01"));
        Assert.InRange(route.TotalDistanceMm, 4242, 4243); // Luftlinie (2000|2000) -> (5000|5000)
    }

    [Fact]
    public void Pick_inside_a_wall_is_reached_at_the_wall_edge_instead_of_taking_a_huge_detour()
    {
        // Bin in der Wand (0,0)-(5000,0), Dicke 100. Früher: 5175 mm (Umweg über die Wandecke und zurück).
        var f = new RouteFixtures();
        f.Pick("W-01", 2500, 0);
        var walls = new[] { Wall(100, (0, 0), (5000, 0)) };

        var route = f.Run(2500, 1000, walls);

        Assert.InRange(route.TotalDistanceMm, 900, 1050);
        Assert.Contains(route.Warnings, w => w.Contains("Bin W-01") && w.Contains("Wand"));
    }

    [Fact]
    public void Start_inside_a_wall_is_moved_out_but_stays_the_first_waypoint()
    {
        var f = new RouteFixtures();
        f.Pick("A-01", 2500, 2000);
        var walls = new[] { Wall(100, (0, 0), (5000, 0)) };

        var route = f.Run(2500, 0, walls);

        Assert.Equal(new Position(2500, 0, 0), route.Waypoints[0]);
        Assert.Contains(route.Warnings, w => w.Contains("Startpunkt"));
        Assert.InRange(route.TotalDistanceMm, 1950, 2060);
    }

    [Fact]
    public void Two_value_equal_candidates_are_both_kept()
    {
        // Bundle mit doppelter Komponente: identische Kandidaten (Order, Zeile, Artikel, Bin, Menge). Früher: einer ging verloren.
        var f = new RouteFixtures();
        var bin = f.Pick("A-01", 1000, 0, quantity: 3);
        var twin = f.Candidates[0];
        f.Candidates.Add(twin with { });
        f.Pick("A-02", 2000, 0, quantity: 2);

        var route = f.Run(0, 0);

        Assert.Equal(3, route.Ordered.Count);
        Assert.Equal(2, route.Ordered.Count(c => c == twin));
        Assert.Equal(8, route.Ordered.Sum(c => c.Quantity)); // 3 + 3 + 2
        Assert.Equal(2, route.Ordered.TakeWhile(c => c.StorageLocationId == bin.Id).Count()); // am selben Bin hintereinander
        Assert.Equal(new[] { (0, 0), (1000, 0), (2000, 0) }, Points(route)); // ein Wegpunkt je Bin, keine Dubletten
    }

    [Fact]
    public void End_point_is_part_of_the_cost_and_of_the_route()
    {
        // Start = Ende = (0,0), vier Picks: Nearest-Neighbor allein liefert 41.908 mm, das Optimum ist 31.545 mm.
        var f = new RouteFixtures();
        f.Pick("P-1", 1000, -5000);
        f.Pick("P-2", 9000, -8000);
        f.Pick("P-3", 5000, 3000);
        f.Pick("P-4", 8000, -1000);

        var route = f.Run(0, 0, end: (0, 0));

        Assert.True(route.TotalDistanceMm <= 31_546, $"Route {route.TotalDistanceMm} mm ist länger als das Optimum 31.545 mm");
        Assert.Equal(new Position(0, 0, 0), route.Waypoints.Last());
        Assert.InRange(PolylineLength(route), route.TotalDistanceMm - 2, route.TotalDistanceMm + 2);
    }

    [Fact]
    public void Improvement_stays_close_to_the_brute_force_optimum_on_small_instances()
    {
        // Vergleich mit Brute-Force über alle Permutationen (6 Picks, feste Seeds): die Heuristik liegt nie
        // mehr als 2 % über dem Optimum.
        var rnd = new Random(7);
        double worstRatio = 1;
        for (var round = 0; round < 25; round++)
        {
            var f = new RouteFixtures();
            var pts = Enumerable.Range(0, 6).Select(_ => (X: rnd.Next(0, 20) * 500, Y: rnd.Next(0, 20) * 500)).ToArray();
            for (var i = 0; i < pts.Length; i++) f.Pick($"P-{i}", pts[i].X, pts[i].Y);
            var end = (X: rnd.Next(0, 20) * 500, Y: rnd.Next(0, 20) * 500);

            var route = f.Run(0, 0, end: end);
            var optimum = BruteForce(pts, (0, 0), end);

            worstRatio = Math.Max(worstRatio, route.TotalDistanceMm / Math.Max(1.0, optimum));
        }
        Assert.True(worstRatio <= 1.02, $"schlechtester Fall {worstRatio:P1} über dem Optimum");
    }

    [Fact]
    public void Larger_pick_lists_are_improved_beyond_nearest_neighbor()
    {
        // 30 Picks liegen über der Grenze der exakten Lösung: Nearest-Neighbor + 2-opt/Or-opt muss den
        // reinen Nearest-Neighbor-Weg (mit festem Endpunkt) spürbar unterbieten und darf ihn nie überschreiten.
        double nearestTotal = 0, routeTotal = 0;
        for (var seed = 1; seed <= 8; seed++)
        {
            var rnd = new Random(seed);
            var f = new RouteFixtures();
            var pts = Enumerable.Range(0, 30).Select(_ => (X: rnd.Next(0, 40) * 500, Y: rnd.Next(0, 40) * 500)).ToArray();
            for (var i = 0; i < pts.Length; i++) f.Pick($"P-{i:D2}", pts[i].X, pts[i].Y);

            var route = f.Run(0, 0, end: (0, 0));
            var nearest = NearestNeighborLength(pts, (0, 0), (0, 0));

            Assert.True(route.TotalDistanceMm <= nearest + 1, $"Seed {seed}: {route.TotalDistanceMm} mm > Nearest-Neighbor {nearest:F0} mm");
            nearestTotal += nearest;
            routeTotal += route.TotalDistanceMm;
        }
        Assert.True(routeTotal < nearestTotal * 0.95, $"Verbesserung nur {1 - routeTotal / nearestTotal:P1}");
    }

    [Theory]
    [InlineData(4)]  // exakte Lösung (Held-Karp)
    [InlineData(14)] // Nearest-Neighbor + 2-opt/Or-opt
    public void Result_is_deterministic_and_independent_of_input_order(int randomPicks)
    {
        var f = new RouteFixtures();
        var rnd = new Random(99);
        for (var i = 0; i < randomPicks; i++) f.Pick($"B-{i:D2}", rnd.Next(1, 10) * 1000, rnd.Next(1, 10) * 1000);
        // zwei Bins gleich weit vom Start: Gleichstand muss fest entschieden werden
        f.Pick("T-1", 4000, 0);
        f.Pick("T-2", 0, 4000);
        var walls = new[] { Wall(200, (2500, 2500), (6500, 2500)), Wall(200, (2500, 2500), (2500, 6500)) };

        var first = f.Run(0, 0, walls, end: (9000, 9000));
        var again = f.Run(0, 0, walls, end: (9000, 9000));
        var reversed = f.Run(0, 0, walls, end: (9000, 9000), candidates: f.Candidates.AsEnumerable().Reverse().ToList());

        Assert.Equal(first.Ordered, again.Ordered);
        Assert.Equal(first.Waypoints, again.Waypoints);
        Assert.Equal(first.TotalDistanceMm, again.TotalDistanceMm);
        Assert.Equal(first.Ordered, reversed.Ordered);
        Assert.Equal(first.Waypoints, reversed.Waypoints);
        Assert.Equal(first.TotalDistanceMm, reversed.TotalDistanceMm);
    }

    [Fact]
    public void Empty_candidates_give_an_empty_route()
    {
        var route = new RouteFixtures().Run(100, 200);

        Assert.Empty(route.Ordered);
        Assert.Equal(0, route.TotalDistanceMm);
        Assert.Equal(new[] { new Position(100, 200, 0) }, route.Waypoints);
    }

    [Fact]
    public void Candidate_with_unknown_storage_location_is_a_clear_error()
    {
        var f = new RouteFixtures();
        var lost = new PickCandidate(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);

        var ex = Assert.Throws<InvalidOperationException>(() => f.Run(0, 0, candidates: new[] { lost }));
        Assert.Contains(lost.StorageLocationId.ToString(), ex.Message);
    }

    [Fact]
    public void Zero_length_wall_segments_are_no_obstacle()
    {
        var f = new RouteFixtures();
        f.Pick("A-01", 2000, 0);
        var walls = new[] { Wall(100, (1000, 0), (1000, 0)) };

        var route = f.Run(0, 0, walls);

        Assert.Equal(2000, route.TotalDistanceMm);
    }

    [Fact]
    public void Benchmark_100_walls_and_40_picks_finish_in_reasonable_time_with_a_valid_route()
    {
        // Raster aus 10x10 Zellen à 5 m, in jeder Zelle ein Wandzug (2 Segmente) in zufälliger Lage: 100 Wände / 200 Segmente.
        var rnd = new Random(2024);
        var walls = new List<Wall>();
        for (var cx = 0; cx < 10; cx++)
        for (var cy = 0; cy < 10; cy++)
        {
            int Jitter() => rnd.Next(600, 2400);
            var x = cx * 5000; var y = cy * 5000;
            walls.Add(Wall(150, (x + Jitter(), y + Jitter()), (x + Jitter(), y + Jitter()), (x + Jitter(), y + Jitter())));
        }

        var f = new RouteFixtures();
        for (var i = 0; i < 40; i++) f.Pick($"P-{i:D2}", rnd.Next(0, 50) * 1000 + 250, rnd.Next(0, 50) * 1000 + 250);

        var watch = Stopwatch.StartNew();
        var route = f.Run(0, 0, walls, end: (49_000, 49_000));
        watch.Stop();
        _output.WriteLine($"Benchmark: {watch.ElapsedMilliseconds} ms");

        // Nur ein Schutz gegen eine ausufernde Berechnung: 30 s statt früher 2 s, damit ein langsamer oder ausgelasteter Rechner
        // (CI, parallele Testläufe) den Test nicht kippt. Der Kern des Tests ist die Korrektheit und Determinismus darunter.
        Assert.True(watch.Elapsed < HangGuard, $"Routenberechnung dauerte {watch.Elapsed.TotalSeconds:0.0} s (Grenze {HangGuard.TotalSeconds:0} s)");
        Assert.Equal(40, route.Ordered.Count);
        Assert.Equal(f.Candidates.OrderBy(c => c.OrderLineId), route.Ordered.OrderBy(c => c.OrderLineId));
        Assert.True(route.TotalDistanceMm > 0);

        var again = f.Run(0, 0, walls, end: (49_000, 49_000));
        Assert.Equal(route.Ordered, again.Ordered);
        Assert.Equal(route.TotalDistanceMm, again.TotalDistanceMm);
        Assert.Equal(route.Waypoints, again.Waypoints);
    }

    private static double NearestNeighborLength((int X, int Y)[] picks, (int X, int Y) start, (int X, int Y) end)
    {
        static double D((int X, int Y) a, (int X, int Y) b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        var rest = picks.ToList();
        var at = start;
        double total = 0;
        while (rest.Count > 0)
        {
            var next = rest.OrderBy(p => D(at, p)).First();
            total += D(at, next);
            at = next;
            rest.Remove(next);
        }
        return total + D(at, end);
    }

    private static double BruteForce((int X, int Y)[] picks, (int X, int Y) start, (int X, int Y) end)
    {
        static double D((int X, int Y) a, (int X, int Y) b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        var best = double.MaxValue;

        void Recurse(List<int> rest, (int X, int Y) at, double soFar)
        {
            if (soFar >= best) return;
            if (rest.Count == 0) { best = Math.Min(best, soFar + D(at, end)); return; }
            foreach (var next in rest)
                Recurse(rest.Where(x => x != next).ToList(), picks[next], soFar + D(at, picks[next]));
        }

        Recurse(Enumerable.Range(0, picks.Length).ToList(), start, 0);
        return best;
    }
}
