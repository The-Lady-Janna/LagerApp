namespace Lager.Application.PickLists;

/// <summary>Punkt in der Hallenebene (Millimeter).</summary>
public readonly record struct Vec2(double X, double Y);

/// <summary>
/// Ein Wandsegment als konvexes Viereck (gedrehtes Rechteck): Mittellinie A-B, aufgeblasen um die halbe
/// Wanddicke. Segmente der Länge 0 sind kein Hindernis (<see cref="IsDegenerate"/>).
/// </summary>
public sealed class WallRect
{
    /// <summary>
    /// Tiefe in mm, ab der ein Punkt bzw. ein Wegstück als "im Inneren der Wand" gilt. Wege entlang des
    /// Wandrands oder durch eine Ecke (Tiefe ≈ 0, nur Rundungsrauschen) bleiben damit erlaubt.
    /// </summary>
    public const double DepthTolerance = 1e-3;

    private readonly double[] _vx = new double[4];
    private readonly double[] _vy = new double[4];
    private readonly double[] _nx = new double[4]; // nach innen zeigende Einheitsnormale je Kante
    private readonly double[] _ny = new double[4];

    public Vec2[] Corners { get; }
    public bool IsDegenerate { get; }
    public double MinX { get; }
    public double MaxX { get; }
    public double MinY { get; }
    public double MaxY { get; }

    public WallRect(double ax, double ay, double bx, double by, double thicknessMm)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9)
        {
            IsDegenerate = true;
            Corners = new[] { new Vec2(ax, ay), new Vec2(ax, ay), new Vec2(ax, ay), new Vec2(ax, ay) };
            MinX = MaxX = ax;
            MinY = MaxY = ay;
            return;
        }

        var nx = -dy / len;
        var ny = dx / len;
        var h = Math.Max(1, thicknessMm) / 2.0;
        Corners = new[]
        {
            new Vec2(ax + nx * h, ay + ny * h),
            new Vec2(bx + nx * h, by + ny * h),
            new Vec2(bx - nx * h, by - ny * h),
            new Vec2(ax - nx * h, ay - ny * h),
        };

        MinX = Corners.Min(c => c.X);
        MaxX = Corners.Max(c => c.X);
        MinY = Corners.Min(c => c.Y);
        MaxY = Corners.Max(c => c.Y);

        var cx = (ax + bx) / 2;
        var cy = (ay + by) / 2;
        for (var i = 0; i < 4; i++)
        {
            var p = Corners[i];
            var q = Corners[(i + 1) % 4];
            var ex = q.X - p.X;
            var ey = q.Y - p.Y;
            var el = Math.Sqrt(ex * ex + ey * ey);
            var mx = -ey / el;
            var my = ex / el;
            if (mx * (cx - p.X) + my * (cy - p.Y) < 0) { mx = -mx; my = -my; }
            _vx[i] = p.X; _vy[i] = p.Y; _nx[i] = mx; _ny[i] = my;
        }
    }

    /// <summary>
    /// Tiefe des Punkts im Viereck in mm: Abstand zur nächsten Kante, positiv im Inneren, negativ außerhalb
    /// (bezogen auf die am schlechtesten passende Kante). Werte &gt; <see cref="DepthTolerance"/> = im Inneren.
    /// </summary>
    public double Depth(Vec2 p)
    {
        if (IsDegenerate) return double.NegativeInfinity;
        var depth = double.PositiveInfinity;
        for (var i = 0; i < 4; i++)
        {
            var d = _nx[i] * (p.X - _vx[i]) + _ny[i] * (p.Y - _vy[i]);
            if (d < depth) depth = d;
        }
        return depth;
    }

    /// <summary>
    /// True, wenn die Strecke a-b durch das INNERE des Rechtecks läuft. Ein Weg entlang des Rands oder eine bloße
    /// Berührung einer Ecke bleibt frei, eine Diagonale durch zwei gegenüberliegende Ecken (Pfeiler) wird
    /// blockiert, auch wenn sie den Rand nur in Endpunkten schneidet.
    /// Umsetzung: Clipping der Strecke an den vier Halbebenen (Cyrus-Beck); blockiert ist sie, wenn ein
    /// Reststück übrig bleibt, dessen Mitte mehr als <see cref="DepthTolerance"/> im Inneren liegt.
    /// </summary>
    public bool Blocks(Vec2 a, Vec2 b)
    {
        if (IsDegenerate) return false;

        // Bounding-Box-Vorfilter: die Strecke muss die (um die Toleranz verkleinerte) Box überhaupt erreichen.
        if (Math.Max(a.X, b.X) <= MinX + DepthTolerance || Math.Min(a.X, b.X) >= MaxX - DepthTolerance) return false;
        if (Math.Max(a.Y, b.Y) <= MinY + DepthTolerance || Math.Min(a.Y, b.Y) >= MaxY - DepthTolerance) return false;

        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-9) return Depth(a) > DepthTolerance;

        double t0 = 0, t1 = 1;
        for (var i = 0; i < 4; i++)
        {
            var num = _nx[i] * (a.X - _vx[i]) + _ny[i] * (a.Y - _vy[i]); // Abstand von a zur Kante, > 0 = innen
            var den = _nx[i] * dx + _ny[i] * dy;                          // Änderung dieses Abstands über t = 0..1
            if (Math.Abs(den) <= 1e-12 * len)
            {
                // Parallel zur Kante: liegt die Strecke auf oder außerhalb der Kante, gibt es kein Inneres.
                if (num <= DepthTolerance) return false;
                continue;
            }

            var t = -num / den;
            if (den > 0) { if (t > t0) t0 = t; }
            else if (t < t1) t1 = t;
            if (t0 >= t1) return false;
        }

        var tm = (t0 + t1) / 2;
        return Depth(new Vec2(a.X + dx * tm, a.Y + dy * tm)) > DepthTolerance;
    }

    /// <summary>
    /// Nächster Randpunkt zu einem Punkt im Inneren. Bei (fast) gleicher Tiefe zu mehreren Kanten (Punkt
    /// genau auf der Wandmitte) gewinnt der Randpunkt, der <paramref name="hint"/> am nächsten liegt, damit die
    /// Route nicht unnötig um die Wand herum muss.
    /// </summary>
    public Vec2 ProjectToBoundary(Vec2 p, Vec2 hint)
    {
        if (IsDegenerate) return p;
        var best = p;
        var bestDepth = double.PositiveInfinity;
        var bestHint = double.PositiveInfinity;
        for (var i = 0; i < 4; i++)
        {
            var d = _nx[i] * (p.X - _vx[i]) + _ny[i] * (p.Y - _vy[i]);
            var q = new Vec2(p.X - _nx[i] * d, p.Y - _ny[i] * d);
            var hx = q.X - hint.X;
            var hy = q.Y - hint.Y;
            var hd = hx * hx + hy * hy;
            if (d < bestDepth - 1e-6 || (Math.Abs(d - bestDepth) <= 1e-6 && hd < bestHint))
            {
                best = q;
                bestDepth = Math.Min(d, bestDepth);
                bestHint = hd;
            }
        }
        return best;
    }
}

/// <summary>
/// Sichtbarkeitsgraph für die Wegsuche um Wände: Knoten = die Terminals (Start, Picks, Ende) plus die Ecken der
/// Wandrechtecke. Eine Kante existiert, wenn die Strecke zwischen zwei Knoten durch kein Wandinneres läuft
/// (<see cref="WallRect.Blocks"/>). Ecken, die selbst in einer anderen Wand liegen (Knickstellen von
/// Wandzügen), sind nie Knoten. Kürzeste Wege per Dijkstra mit Prioritätswarteschlange über Adjazenzlisten.
/// </summary>
public sealed class VisibilityGraph
{
    private readonly WallRect[] _obstacles;
    private readonly int[][] _to;
    private readonly double[][] _weight;

    /// <summary>Alle Knoten: zuerst die Terminals (in der übergebenen Reihenfolge), dann die Wandecken.</summary>
    public IReadOnlyList<Vec2> Nodes { get; }

    public int TerminalCount { get; }

    public VisibilityGraph(IReadOnlyList<WallRect> obstacles, IReadOnlyList<Vec2> terminals)
    {
        _obstacles = obstacles.Where(o => !o.IsDegenerate).ToArray();
        TerminalCount = terminals.Count;

        var nodes = new List<Vec2>(terminals);
        var seenCorners = new HashSet<(double, double)>();
        foreach (var rect in _obstacles)
        {
            foreach (var corner in rect.Corners)
            {
                if (!seenCorners.Add((Math.Round(corner.X, 3), Math.Round(corner.Y, 3)))) continue;
                if (IsInsideAnotherObstacle(rect, corner)) continue;
                nodes.Add(corner);
            }
        }
        Nodes = nodes;

        var n = nodes.Count;
        var to = new List<int>[n];
        var weight = new List<double>[n];
        for (var i = 0; i < n; i++) { to[i] = new List<int>(); weight[i] = new List<double>(); }

        var grid = new RectGrid(_obstacles);
        for (var i = 0; i < n; i++)
        {
            for (var j = i + 1; j < n; j++)
            {
                if (grid.IsBlocked(nodes[i], nodes[j])) continue;
                var w = Distance(nodes[i], nodes[j]);
                to[i].Add(j); weight[i].Add(w);
                to[j].Add(i); weight[j].Add(w);
            }
        }

        _to = to.Select(l => l.ToArray()).ToArray();
        _weight = weight.Select(l => l.ToArray()).ToArray();
    }

    /// <summary>True, wenn a-b durch keine Wand läuft (Berührung des Rands oder einer Ecke ist erlaubt).</summary>
    public bool IsVisible(Vec2 a, Vec2 b)
    {
        foreach (var rect in _obstacles)
            if (rect.Blocks(a, b)) return false;
        return true;
    }

    /// <summary>
    /// Gleichmäßiges Raster über die Wandrechtecke (Bounding-Box-Vorfilter): eine Strecke muss nur gegen die
    /// Rechtecke der Zellen geprüft werden, die sie durchläuft (Zellendurchlauf nach Amanatides/Woo), nicht
    /// gegen alle. Jedes Rechteck steht in allen Zellen, die seine (leicht vergrößerte) Bounding-Box berühren.
    /// Nicht threadsicher (Stempel-Puffer), gedacht für den Aufbau des Graphen.
    /// </summary>
    private sealed class RectGrid
    {
        private const double Pad = 1.0; // mm Sicherheitsrand gegen Rundung am Zellrand

        private readonly WallRect[] _rects;
        private readonly int[] _stamp;
        private int _stampId;
        private readonly double _minX, _minY, _maxX, _maxY, _cell;
        private readonly int _nx, _ny;
        private readonly List<int>[] _cells;

        public RectGrid(WallRect[] rects)
        {
            _rects = rects;
            _stamp = new int[rects.Length];
            if (rects.Length == 0) { _cells = Array.Empty<List<int>>(); return; }

            _minX = rects.Min(r => r.MinX) - Pad;
            _minY = rects.Min(r => r.MinY) - Pad;
            _maxX = rects.Max(r => r.MaxX) + Pad;
            _maxY = rects.Max(r => r.MaxY) + Pad;

            var perAxis = Math.Clamp((int)Math.Ceiling(Math.Sqrt(rects.Length)) * 2, 1, 128);
            _cell = Math.Max(Math.Max(_maxX - _minX, _maxY - _minY) / perAxis, 1.0);
            _nx = (int)Math.Ceiling((_maxX - _minX) / _cell);
            _ny = (int)Math.Ceiling((_maxY - _minY) / _cell);
            _cells = new List<int>[_nx * _ny];

            for (var i = 0; i < rects.Length; i++)
            {
                var r = rects[i];
                var x0 = CellIndex(r.MinX - Pad, _minX, _nx);
                var x1 = CellIndex(r.MaxX + Pad, _minX, _nx);
                var y0 = CellIndex(r.MinY - Pad, _minY, _ny);
                var y1 = CellIndex(r.MaxY + Pad, _minY, _ny);
                for (var cy = y0; cy <= y1; cy++)
                for (var cx = x0; cx <= x1; cx++)
                    (_cells[cy * _nx + cx] ??= new List<int>()).Add(i);
            }
        }

        private int CellIndex(double v, double origin, int count) =>
            Math.Clamp((int)Math.Floor((v - origin) / _cell), 0, count - 1);

        /// <summary>True, wenn a-b durch das Innere eines Rechtecks läuft.</summary>
        public bool IsBlocked(Vec2 a, Vec2 b)
        {
            if (_rects.Length == 0) return false;

            // Strecke auf die Rasterfläche beschneiden (Liang-Barsky); außerhalb gibt es keine Wand.
            var dx = b.X - a.X;
            var dy = b.Y - a.Y;
            double t0 = 0, t1 = 1;
            if (!Clip(-dx, a.X - _minX, ref t0, ref t1) || !Clip(dx, _maxX - a.X, ref t0, ref t1)
                || !Clip(-dy, a.Y - _minY, ref t0, ref t1) || !Clip(dy, _maxY - a.Y, ref t0, ref t1))
                return false;

            var cx = CellIndex(a.X + dx * t0, _minX, _nx);
            var cy = CellIndex(a.Y + dy * t0, _minY, _ny);
            var stepX = dx > 0 ? 1 : dx < 0 ? -1 : 0;
            var stepY = dy > 0 ? 1 : dy < 0 ? -1 : 0;
            var tDeltaX = stepX == 0 ? double.PositiveInfinity : _cell / Math.Abs(dx);
            var tDeltaY = stepY == 0 ? double.PositiveInfinity : _cell / Math.Abs(dy);
            var tMaxX = stepX == 0 ? double.PositiveInfinity : (_minX + (stepX > 0 ? cx + 1 : cx) * _cell - a.X) / dx;
            var tMaxY = stepY == 0 ? double.PositiveInfinity : (_minY + (stepY > 0 ? cy + 1 : cy) * _cell - a.Y) / dy;

            var stamp = ++_stampId;
            while (true)
            {
                var cell = _cells[cy * _nx + cx];
                if (cell is not null)
                {
                    foreach (var index in cell)
                    {
                        if (_stamp[index] == stamp) continue;
                        _stamp[index] = stamp;
                        if (_rects[index].Blocks(a, b)) return true;
                    }
                }

                if (tMaxX < tMaxY)
                {
                    if (tMaxX > t1) return false;
                    cx += stepX;
                    tMaxX += tDeltaX;
                    if (cx < 0 || cx >= _nx) return false;
                }
                else
                {
                    if (tMaxY > t1) return false;
                    cy += stepY;
                    tMaxY += tDeltaY;
                    if (cy < 0 || cy >= _ny) return false;
                }
            }
        }

        private static bool Clip(double p, double q, ref double t0, ref double t1)
        {
            if (p == 0) return q >= 0;
            var r = q / p;
            if (p < 0)
            {
                if (r > t1) return false;
                if (r > t0) t0 = r;
            }
            else
            {
                if (r < t0) return false;
                if (r < t1) t1 = r;
            }
            return true;
        }
    }

    /// <summary>
    /// Kürzeste Distanzen von dem Terminal <paramref name="source"/> zu allen Terminals (∞ = nicht erreichbar) und
    /// der Vorgänger je Knoten (-1 = keiner). Deterministisch: bei gleicher Länge gewinnt der zuerst gefundene Weg.
    /// Terminals sind Endpunkte, nie Zwischenstationen (ein kürzester Weg knickt nur an Wandecken ab), und die
    /// Suche endet, sobald alle Terminals feststehen. Endgültig sind deshalb nur die Distanzen der Terminals und
    /// die Vorgängerketten der Terminals; die Werte der übrigen Knoten können vorläufig sein.
    /// </summary>
    public (double[] Dist, int[] Prev) ShortestPaths(int source)
    {
        var n = Nodes.Count;
        var dist = new double[n];
        var prev = new int[n];
        Array.Fill(dist, double.PositiveInfinity);
        Array.Fill(prev, -1);
        dist[source] = 0;

        var settledTerminals = 0;
        var queue = new PriorityQueue<int, double>();
        queue.Enqueue(source, 0);
        while (queue.TryDequeue(out var u, out var d))
        {
            if (d > dist[u]) continue; // veralteter Eintrag

            if (u < TerminalCount)
            {
                if (++settledTerminals == TerminalCount) break;
                if (u != source) continue; // ein anderes Terminal ist Ziel, keine Zwischenstation
            }

            var to = _to[u];
            var weight = _weight[u];
            for (var k = 0; k < to.Length; k++)
            {
                var v = to[k];
                var alt = d + weight[k];
                if (alt < dist[v])
                {
                    dist[v] = alt;
                    prev[v] = u;
                    queue.Enqueue(v, alt);
                }
            }
        }
        return (dist, prev);
    }

    /// <summary>Knotenfolge source → target (beide eingeschlossen) anhand der Vorgänger; leer, wenn unerreichbar.</summary>
    public static IReadOnlyList<int> PathNodes(int[] prev, int source, int target)
    {
        var reversed = new List<int> { target };
        var cur = target;
        while (cur != source)
        {
            cur = prev[cur];
            if (cur < 0) return Array.Empty<int>();
            reversed.Add(cur);
        }
        reversed.Reverse();
        return reversed;
    }

    /// <summary>
    /// Schiebt einen Punkt, der im Inneren einer Wand liegt, an den nächsten Wandrand (bei mehreren
    /// überlappenden Wänden wiederholt). <paramref name="hint"/> entscheidet bei gleichem Abstand zu zwei Seiten.
    /// <paramref name="resolved"/> ist false, wenn der Punkt nach allen Versuchen noch in einer Wand steckt.
    /// </summary>
    public static Vec2 PushOutOfWalls(Vec2 p, IReadOnlyList<WallRect> obstacles, Vec2 hint, out bool moved, out bool resolved)
    {
        moved = false;
        for (var attempt = 0; attempt <= obstacles.Count; attempt++)
        {
            WallRect? containing = null;
            foreach (var rect in obstacles)
            {
                if (rect.IsDegenerate || rect.Depth(p) <= WallRect.DepthTolerance) continue;
                containing = rect;
                break;
            }

            if (containing is null)
            {
                resolved = true;
                return p;
            }

            p = containing.ProjectToBoundary(p, hint);
            moved = true;
        }

        resolved = false;
        return p;
    }

    private bool IsInsideAnotherObstacle(WallRect own, Vec2 p)
    {
        foreach (var rect in _obstacles)
        {
            if (ReferenceEquals(rect, own)) continue;
            if (rect.Depth(p) > WallRect.DepthTolerance) return true;
        }
        return false;
    }

    private static double Distance(Vec2 a, Vec2 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
