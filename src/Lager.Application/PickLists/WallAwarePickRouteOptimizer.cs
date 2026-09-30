using Lager.Domain.Warehouse;

namespace Lager.Application.PickLists;

/// <summary>
/// Wegoptimierung um Wände herum. Jede Wand ist ein aufgeblasenes Rechteck (Mittellinie plus Dicke), die
/// Wege laufen über einen Sichtbarkeitsgraphen aus Wandecken und den benötigten Punkten
/// (<see cref="VisibilityGraph"/>).
///
/// Ablauf pro Aufruf:
/// 1. Zielpunkt je Bin = Bin-Mitte (Position + halbe Abmessung, wie im Frontend). Mehrere Kandidaten am selben
///    Bin teilen sich einen Knoten (Abstand 0), Kandidaten werden nie als Dictionary-Schlüssel verwendet.
/// 2. Liegt Start, Ende oder ein Pick in einer Wand, wird der Punkt an den Wandrand geschoben (Warnung).
/// 3. Einmal pro Aufruf wird die Distanzmatrix (Start, Picks, Ende) per Dijkstra berechnet. Ist ein Paar nicht
///    wandfrei verbunden (eingeschlossener Bereich), gilt die Luftlinie und die Route bekommt eine Warnung
///    statt einer Exception.
/// 4. Reihenfolge auf dem Pfad mit festem Start (und festem Ende, falls angegeben; das Ende fließt in die
///    Kosten ein): bis 12 unterschiedliche Pick-Punkte exakt (Held-Karp), darüber Nearest-Neighbor als
///    Startlösung mit 2-opt und Or-opt. Alle Tie-Breaker sind fest (Bin-Code, Position, Zeilen-Id),
///    das Ergebnis hängt nicht von der Eingabereihenfolge ab.
/// </summary>
public class WallAwarePickRouteOptimizer : IPickRouteOptimizer
{
    /// <summary>Mindestverbesserung (mm), damit ein Tausch als Verbesserung zählt; verhindert Endlosschleifen durch Rundung.</summary>
    private const double Eps = 1e-9;

    /// <summary>Obergrenze der Verbesserungsdurchläufe; jeder Durchlauf verkürzt den Weg, das Limit ist nur ein Sicherheitsnetz.</summary>
    private const int MaxImproveRounds = 10_000;

    /// <summary>Bis zu dieser Zahl unterschiedlicher Pick-Punkte wird die Reihenfolge exakt bestimmt (2^12 x 12 Zustände), darüber Heuristik.</summary>
    private const int ExactLimit = 12;

    private sealed class PickNode
    {
        public PickNode(Vec2 center, string code)
        {
            Center = center;
            Code = code;
        }

        /// <summary>Bin-Mitte.</summary>
        public Vec2 Center { get; }

        /// <summary>Kleinster Bin-Code der Lagerplätze an dieser Stelle (Tie-Breaker und Klartext für Warnungen).</summary>
        public string Code { get; set; }

        /// <summary>Punkt im Graphen: die Bin-Mitte oder, falls sie in einer Wand liegt, der nächste Wandrand.</summary>
        public Vec2 Anchor { get; set; }

        public List<(int Index, string Code)> Candidates { get; } = new();
    }

    public OptimizedRoute Optimize(
        IReadOnlyCollection<PickCandidate> candidates,
        IReadOnlyDictionary<Guid, StorageLocation> locations,
        Position start,
        IReadOnlyCollection<Wall> walls,
        Position? end = null)
    {
        // Ohne Picks gibt es keinen Weg zu laufen.
        if (candidates.Count == 0)
            return new OptimizedRoute(Array.Empty<PickCandidate>(), 0, new[] { start });

        var cands = candidates as IReadOnlyList<PickCandidate> ?? candidates.ToList();
        var warnings = new List<string>();

        var obstacles = BuildObstacles(walls);
        var pickNodes = BuildPickNodes(cands, locations);

        // Terminals: 0 = Start, 1..m = Pick-Knoten (kanonische Reihenfolge), m+1 = Ende (optional).
        var m = pickNodes.Count;
        var endTerminal = end is null ? -1 : m + 1;
        var terminalCount = m + 1 + (end is null ? 0 : 1);

        var startVec = new Vec2(start.XMm, start.YMm);
        var startAnchor = VisibilityGraph.PushOutOfWalls(startVec, obstacles, pickNodes[0].Center, out var startMoved, out _);
        if (startMoved) warnings.Add("Der Startpunkt liegt in einer Wand und wurde an den Wandrand verschoben.");

        foreach (var node in pickNodes)
        {
            node.Anchor = VisibilityGraph.PushOutOfWalls(node.Center, obstacles, startVec, out var moved, out _);
            if (moved) warnings.Add($"Bin {node.Code} liegt in einer Wand; der Laufweg endet am Wandrand.");
        }

        var endVec = end is null ? default : new Vec2(end.XMm, end.YMm);
        var endAnchor = endVec;
        if (end is not null)
        {
            endAnchor = VisibilityGraph.PushOutOfWalls(endVec, obstacles, startVec, out var endMoved, out _);
            if (endMoved) warnings.Add("Der Endpunkt liegt in einer Wand und wurde an den Wandrand verschoben.");
        }

        var terminals = new Vec2[terminalCount];
        terminals[0] = startAnchor;
        for (var i = 0; i < m; i++) terminals[i + 1] = pickNodes[i].Anchor;
        if (end is not null) terminals[endTerminal] = endAnchor;

        var graph = new VisibilityGraph(obstacles, terminals);
        var (cost, direct, prevs) = ComputeDistances(graph, terminalCount);

        List<int> sequence;
        if (m <= ExactLimit)
        {
            sequence = Exact(cost, m, endTerminal);
        }
        else
        {
            sequence = NearestNeighbour(cost, m);
            if (end is not null) sequence.Add(endTerminal);
            Improve(sequence, cost, closed: end is not null);
        }

        // Ergebnis zusammensetzen: Reihenfolge der Kandidaten, Wegpunkte, Länge.
        var ordered = new List<PickCandidate>(cands.Count);
        foreach (var terminal in sequence)
        {
            if (terminal == 0 || terminal == endTerminal) continue;
            foreach (var (index, _) in pickNodes[terminal - 1].Candidates
                         .OrderBy(c => c.Code, StringComparer.Ordinal)
                         .ThenBy(c => cands[c.Index].OrderLineId)
                         .ThenBy(c => cands[c.Index].ArticleId)
                         .ThenBy(c => cands[c.Index].OrderId)
                         .ThenBy(c => cands[c.Index].Quantity)
                         .ThenBy(c => c.Index))
                ordered.Add(cands[index]);
        }

        if (ordered.Count != cands.Count || ordered.Sum(c => (long)c.Quantity) != cands.Sum(c => (long)c.Quantity))
            throw new InvalidOperationException("Interner Fehler in der Routenberechnung: nicht alle Picks sind in der Route enthalten.");

        var waypoints = new List<Position> { start };
        void AddPoint(Vec2 p)
        {
            var x = (int)Math.Round(p.X);
            var y = (int)Math.Round(p.Y);
            var last = waypoints[^1];
            if (last.XMm == x && last.YMm == y) return;
            waypoints.Add(new Position(x, y, 0));
        }

        string Label(int terminal) =>
            terminal == 0 ? "Start" : terminal == endTerminal ? "Endpunkt" : $"Bin {pickNodes[terminal - 1].Code}";

        double total = Distance(startVec, startAnchor);
        if (startMoved) AddPoint(startAnchor);

        for (var i = 0; i + 1 < sequence.Count; i++)
        {
            var fromTerminal = sequence[i];
            var toTerminal = sequence[i + 1];
            total += cost[fromTerminal][toTerminal];

            if (direct[fromTerminal][toTerminal])
            {
                AddPoint(terminals[toTerminal]);
                var warning = $"Kein wandfreier Weg von {Label(fromTerminal)} nach {Label(toTerminal)} gefunden (z. B. eingeschlossener Bereich); es wird die Luftlinie verwendet.";
                if (!warnings.Contains(warning)) warnings.Add(warning);
                continue;
            }

            foreach (var node in VisibilityGraph.PathNodes(prevs[fromTerminal], fromTerminal, toTerminal).Skip(1))
                AddPoint(graph.Nodes[node]);
        }

        if (end is not null)
        {
            total += Distance(endAnchor, endVec);
            AddPoint(endVec);
        }

        return new OptimizedRoute(ordered, (int)Math.Round(total), waypoints) { Warnings = warnings };
    }

    private static List<WallRect> BuildObstacles(IReadOnlyCollection<Wall> walls)
    {
        var obstacles = new List<WallRect>();
        foreach (var wall in walls)
        {
            for (var i = 0; i + 1 < wall.Points.Count; i++)
            {
                var a = wall.Points[i];
                var b = wall.Points[i + 1];
                var rect = new WallRect(a.XMm, a.YMm, b.XMm, b.YMm, wall.ThicknessMm);
                if (!rect.IsDegenerate) obstacles.Add(rect);
            }
        }
        return obstacles;
    }

    /// <summary>
    /// Ein Knoten je Bin-Mitte (Kandidaten am selben Punkt teilen sich den Knoten), in fester Reihenfolge
    /// (Bin-Code, dann Koordinaten), damit Gleichstände unabhängig von der Eingabereihenfolge entschieden werden.
    /// </summary>
    private static List<PickNode> BuildPickNodes(
        IReadOnlyList<PickCandidate> candidates,
        IReadOnlyDictionary<Guid, StorageLocation> locations)
    {
        var byPosition = new Dictionary<(double, double), PickNode>();
        for (var i = 0; i < candidates.Count; i++)
        {
            if (!locations.TryGetValue(candidates[i].StorageLocationId, out var location))
                throw new InvalidOperationException($"Lagerplatz {candidates[i].StorageLocationId} wurde nicht geladen.");

            var center = new Vec2(location.Position.XMm + location.WidthMm / 2.0, location.Position.YMm + location.DepthMm / 2.0);
            var key = (Math.Round(center.X, 3), Math.Round(center.Y, 3));
            if (!byPosition.TryGetValue(key, out var node))
            {
                node = new PickNode(center, location.Code);
                byPosition[key] = node;
            }
            else if (string.CompareOrdinal(location.Code, node.Code) < 0)
            {
                node.Code = location.Code;
            }
            node.Candidates.Add((i, location.Code));
        }

        return byPosition.Values
            .OrderBy(n => n.Code, StringComparer.Ordinal)
            .ThenBy(n => n.Center.X)
            .ThenBy(n => n.Center.Y)
            .ToList();
    }

    /// <summary>
    /// Distanzcache für diesen Aufruf: ein Dijkstra je Terminal. Nicht verbundene Paare (kein wandfreier Weg)
    /// bekommen die Luftlinie und werden in <c>direct</c> markiert. Die Matrix ist symmetrisch.
    /// </summary>
    private static (double[][] Cost, bool[][] Direct, int[][] Prev) ComputeDistances(VisibilityGraph graph, int terminalCount)
    {
        var cost = new double[terminalCount][];
        var direct = new bool[terminalCount][];
        var prevs = new int[terminalCount][];
        for (var i = 0; i < terminalCount; i++)
        {
            cost[i] = new double[terminalCount];
            direct[i] = new bool[terminalCount];
        }

        for (var s = 0; s < terminalCount; s++)
        {
            var (dist, prev) = graph.ShortestPaths(s);
            prevs[s] = prev;
            for (var t = s + 1; t < terminalCount; t++)
            {
                var d = dist[t];
                var isDirect = double.IsPositiveInfinity(d);
                if (isDirect) d = Distance(graph.Nodes[s], graph.Nodes[t]);
                cost[s][t] = cost[t][s] = d;
                direct[s][t] = direct[t][s] = isDirect;
            }
        }
        return (cost, direct, prevs);
    }

    /// <summary>
    /// Exakte Reihenfolge per Held-Karp (dynamische Programmierung über Teilmengen) für kleine Picklisten:
    /// kürzester Pfad Start → alle Picks → (Ende). Bei Gleichstand gewinnt die zuerst gefundene Lösung in
    /// kanonischer Reihenfolge.
    /// </summary>
    private static List<int> Exact(double[][] cost, int pickCount, int endTerminal)
    {
        var m = pickCount;
        var full = (1 << m) - 1;
        var best = new double[(full + 1) * m];
        var parent = new sbyte[(full + 1) * m];
        Array.Fill(best, double.PositiveInfinity);
        Array.Fill(parent, (sbyte)-1);

        for (var j = 0; j < m; j++) best[(1 << j) * m + j] = cost[0][j + 1];

        for (var mask = 1; mask <= full; mask++)
        {
            for (var j = 0; j < m; j++)
            {
                if ((mask & (1 << j)) == 0) continue;
                var here = best[mask * m + j];
                if (double.IsPositiveInfinity(here)) continue;

                for (var k = 0; k < m; k++)
                {
                    if ((mask & (1 << k)) != 0) continue;
                    var next = mask | (1 << k);
                    var candidate = here + cost[j + 1][k + 1];
                    if (candidate < best[next * m + k] - Eps)
                    {
                        best[next * m + k] = candidate;
                        parent[next * m + k] = (sbyte)j;
                    }
                }
            }
        }

        var last = 0;
        var total = double.PositiveInfinity;
        for (var j = 0; j < m; j++)
        {
            var t = best[full * m + j] + (endTerminal >= 0 ? cost[j + 1][endTerminal] : 0.0);
            if (t < total - Eps) { total = t; last = j; }
        }

        var order = new List<int>(m + 2);
        for (int mask = full, j = last; j >= 0; )
        {
            order.Add(j + 1);
            var p = parent[mask * m + j];
            mask &= ~(1 << j);
            j = p;
        }
        order.Reverse();
        order.Insert(0, 0);
        if (endTerminal >= 0) order.Add(endTerminal);
        return order;
    }

    /// <summary>Startlösung: immer zum nächsten unbesuchten Pick; bei Gleichstand der erste in kanonischer Reihenfolge.</summary>
    private static List<int> NearestNeighbour(double[][] cost, int pickCount)
    {
        var remaining = Enumerable.Range(1, pickCount).ToList();
        var sequence = new List<int>(pickCount + 2) { 0 };
        var current = 0;
        while (remaining.Count > 0)
        {
            var bestAt = 0;
            var best = cost[current][remaining[0]];
            for (var k = 1; k < remaining.Count; k++)
            {
                var d = cost[current][remaining[k]];
                if (d < best - Eps) { best = d; bestAt = k; }
            }
            current = remaining[bestAt];
            sequence.Add(current);
            remaining.RemoveAt(bestAt);
        }
        return sequence;
    }

    /// <summary>
    /// 2-opt und Or-opt auf dem Pfad Start → Picks → (Ende). Position 0 (Start) und, bei <paramref name="closed"/>,
    /// die letzte Position (Ende) bleiben fest; der Endpunkt fließt damit in jede Kostenberechnung ein.
    /// </summary>
    private static void Improve(List<int> sequence, double[][] cost, bool closed)
    {
        var lastMovable = closed ? sequence.Count - 2 : sequence.Count - 1;
        if (lastMovable < 2) return;

        for (var round = 0; round < MaxImproveRounds; round++)
        {
            var improved = TwoOpt(sequence, cost, lastMovable);
            if (OrOpt(sequence, cost, lastMovable, closed)) improved = true;
            if (!improved) return;
        }
    }

    /// <summary>Kehrt Teilstrecken um, solange das den Weg verkürzt (alle Verbesserungen eines Durchlaufs).</summary>
    private static bool TwoOpt(List<int> seq, double[][] cost, int lastMovable)
    {
        var improved = false;
        for (var i = 1; i < lastMovable; i++)
        {
            for (var j = i + 1; j <= lastMovable; j++)
            {
                var a = seq[i - 1];
                var b = seq[i];
                var c = seq[j];
                var before = cost[a][b];
                var after = cost[a][c];
                if (j + 1 < seq.Count)
                {
                    var e = seq[j + 1];
                    before += cost[c][e];
                    after += cost[b][e];
                }

                if (after < before - Eps)
                {
                    seq.Reverse(i, j - i + 1);
                    improved = true;
                }
            }
        }
        return improved;
    }

    /// <summary>
    /// Verschiebt Teilstücke aus 1 bis 3 Picks (auch umgekehrt) an die beste Stelle, wenn das den Weg verkürzt
    /// (je Teilstück die größte Ersparnis; alle Verbesserungen eines Durchlaufs).
    /// </summary>
    private static bool OrOpt(List<int> seq, double[][] cost, int lastMovable, bool closed)
    {
        var lastInsertAfter = closed ? seq.Count - 2 : seq.Count - 1;
        var improved = false;
        for (var len = 1; len <= 3; len++)
        {
            for (var i = 1; i + len - 1 <= lastMovable; i++)
            {
                var j = i + len - 1;
                var prev = seq[i - 1];
                var first = seq[i];
                var last = seq[j];
                var hasNext = j + 1 < seq.Count;
                var removeGain = cost[prev][first];
                if (hasNext)
                {
                    var next = seq[j + 1];
                    removeGain += cost[last][next] - cost[prev][next];
                }

                var bestGain = Eps;
                var bestK = -1;
                var bestReverse = false;
                for (var k = 0; k <= lastInsertAfter; k++)
                {
                    if (k >= i - 1 && k <= j) continue; // vor/in dem Teilstück: keine Verschiebung

                    var a = seq[k];
                    var hasSucc = k + 1 < seq.Count;
                    var gap = hasSucc ? cost[a][seq[k + 1]] : 0.0;
                    var insertForward = cost[a][first] + (hasSucc ? cost[last][seq[k + 1]] : 0.0) - gap;
                    var insertReverse = cost[a][last] + (hasSucc ? cost[first][seq[k + 1]] : 0.0) - gap;

                    var reverse = insertReverse < insertForward - Eps;
                    var gain = removeGain - (reverse ? insertReverse : insertForward);
                    if (gain > bestGain)
                    {
                        bestGain = gain;
                        bestK = k;
                        bestReverse = reverse;
                    }
                }

                if (bestK < 0) continue;

                var segment = seq.GetRange(i, len);
                if (bestReverse) segment.Reverse();
                seq.RemoveRange(i, len);
                seq.InsertRange((bestK < i ? bestK : bestK - len) + 1, segment);
                improved = true;
            }
        }
        return improved;
    }

    private static double Distance(Vec2 a, Vec2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
