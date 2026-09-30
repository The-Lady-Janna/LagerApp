using Lager.Application.PickLists;

namespace Lager.Tests.WP08;

/// <summary>
/// Reine Unit-Tests (ohne Host) für den Sichtbarkeitstest (<see cref="WallRect.Blocks"/>) und den Graphen.
/// Ein Weg darf den Rand einer Wand und ihre Ecken berühren, aber nie durch ihr Inneres laufen.
/// </summary>
public class VisibilityGraphTests
{
    private static WallRect Wall(double ax, double ay, double bx, double by, double thickness) => new(ax, ay, bx, by, thickness);

    [Fact]
    public void Path_through_the_wall_is_blocked_but_paths_along_or_away_from_the_edge_are_free()
    {
        var wall = Wall(0, 0, 5000, 0, 100); // Rechteck [0,5000] x [-50,50]

        Assert.True(wall.Blocks(new Vec2(2500, 1000), new Vec2(2500, -1000)));      // quer durch
        Assert.True(wall.Blocks(new Vec2(-500, -500), new Vec2(5500, 500)));        // schräg durch
        Assert.False(wall.Blocks(new Vec2(-100, 50), new Vec2(5100, 50)));          // exakt auf der Oberkante entlang
        Assert.False(wall.Blocks(new Vec2(0, -50), new Vec2(0, 50)));               // entlang der Stirnseite
        Assert.False(wall.Blocks(new Vec2(2500, 50), new Vec2(2500, 1000)));        // vom Rand weg
        Assert.False(wall.Blocks(new Vec2(-1000, 50), new Vec2(0, 50)));            // Ecke berührt, dann weiter am Rand
        Assert.False(wall.Blocks(new Vec2(-500, 1000), new Vec2(5500, 1000)));      // parallel daneben
    }

    [Fact]
    public void Diagonal_through_two_opposite_corners_blocks_although_it_only_touches_the_edges_at_points()
    {
        var pillar = Wall(1000, 500, 1000, 1500, 1000); // Quadrat [500,1500]^2

        Assert.True(pillar.Blocks(new Vec2(0, 0), new Vec2(2000, 2000)));
        Assert.False(pillar.Blocks(new Vec2(0, 0), new Vec2(1500, 500)));      // tangential an der Ecke (1500,500) vorbei
        Assert.False(pillar.Blocks(new Vec2(500, 500), new Vec2(1500, 500)));  // Kante von Ecke zu Ecke
        Assert.True(pillar.Blocks(new Vec2(500, 500), new Vec2(1500, 1500)));  // Diagonale von Ecke zu Ecke
    }

    [Fact]
    public void Rotated_walls_behave_the_same_way()
    {
        var wall = Wall(0, 0, 1000, 1000, 100);
        var c = wall.Corners;

        Assert.False(wall.Blocks(c[0], c[1])); // Längskante
        Assert.False(wall.Blocks(c[1], c[2])); // Stirnseite
        Assert.True(wall.Blocks(c[0], c[2]));  // Diagonale des Rechtecks
        Assert.True(wall.Blocks(new Vec2(0, 1000), new Vec2(1000, 0)));
    }

    [Fact]
    public void Points_inside_walls_are_pushed_to_the_nearest_edge_on_the_side_of_the_hint()
    {
        var wall = Wall(0, 0, 5000, 0, 100);

        var moved = VisibilityGraph.PushOutOfWalls(new Vec2(2500, 0), new[] { wall }, hint: new Vec2(2500, 1000), out var wasMoved, out var resolved);
        Assert.True(wasMoved && resolved);
        Assert.Equal(2500, moved.X, 6);
        Assert.Equal(50, moved.Y, 6); // Gleichstand oben/unten: die Seite zum Hinweispunkt gewinnt

        var below = VisibilityGraph.PushOutOfWalls(new Vec2(2500, -10), new[] { wall }, hint: new Vec2(2500, 1000), out _, out _);
        Assert.Equal(-50, below.Y, 6); // näher an der Unterkante: die nächste Seite gewinnt vor dem Hinweis

        var outside = VisibilityGraph.PushOutOfWalls(new Vec2(2500, 500), new[] { wall }, default, out var stayed, out _);
        Assert.False(stayed);
        Assert.Equal(new Vec2(2500, 500), outside);
    }

    [Fact]
    public void Shortest_paths_match_a_naive_visibility_graph_on_random_layouts()
    {
        // Differenztest gegen eine Referenz ohne Raster-Vorfilter: alle Knotenpaare gegen alle Wände, Floyd-Warshall.
        var rnd = new Random(31337);
        int reachable = 0, detours = 0;
        for (var layout = 0; layout < 12; layout++)
        {
            var rects = new List<WallRect>();
            for (var i = 0; i < 14; i++)
            {
                double X() => rnd.Next(0, 40) * 250;
                rects.Add(Wall(X(), X(), X(), X(), rnd.Next(1, 5) * 100));
            }
            var terminals = Enumerable.Range(0, 8).Select(_ => new Vec2(rnd.Next(0, 40) * 250 + 125, rnd.Next(0, 40) * 250 + 125)).ToArray();

            var graph = new VisibilityGraph(rects, terminals);
            var reference = NaiveDistances(rects, terminals);

            for (var s = 0; s < terminals.Length; s++)
            {
                var (dist, _) = graph.ShortestPaths(s);
                for (var t = 0; t < terminals.Length; t++)
                {
                    var expected = reference[s, t];
                    if (double.IsPositiveInfinity(expected)) Assert.True(double.IsPositiveInfinity(dist[t]), $"Layout {layout}: {s}->{t} sollte unerreichbar sein");
                    else
                    {
                        Assert.True(Math.Abs(expected - dist[t]) < 1e-6, $"Layout {layout}: {s}->{t} erwartet {expected:F3}, war {dist[t]:F3}");
                        if (s == t) continue;
                        reachable++;
                        var straight = Math.Sqrt(Math.Pow(terminals[s].X - terminals[t].X, 2) + Math.Pow(terminals[s].Y - terminals[t].Y, 2));
                        if (dist[t] > straight + 1) detours++;
                    }
                }
            }
        }

        // Der Vergleich muss echte Umwege enthalten, sonst prüft er nichts.
        Assert.True(reachable >= 200, $"nur {reachable} erreichbare Paare");
        Assert.True(detours >= 20, $"nur {detours} Umwege");
    }

    private static double[,] NaiveDistances(IReadOnlyList<WallRect> rects, Vec2[] terminals)
    {
        var nodes = new List<Vec2>(terminals);
        foreach (var r in rects) nodes.AddRange(r.Corners);

        var n = nodes.Count;
        var d = new double[n, n];
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
        {
            if (i == j) { d[i, j] = 0; continue; }
            var blocked = rects.Any(r => r.Blocks(nodes[i], nodes[j]));
            d[i, j] = blocked ? double.PositiveInfinity : Math.Sqrt(Math.Pow(nodes[i].X - nodes[j].X, 2) + Math.Pow(nodes[i].Y - nodes[j].Y, 2));
        }

        for (var k = 0; k < n; k++)
        for (var i = 0; i < n; i++)
        for (var j = 0; j < n; j++)
            if (d[i, k] + d[k, j] < d[i, j]) d[i, j] = d[i, k] + d[k, j];
        return d;
    }
}
