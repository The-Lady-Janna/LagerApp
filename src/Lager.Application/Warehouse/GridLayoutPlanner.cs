using Lager.Domain.Warehouse;

namespace Lager.Application.Warehouse;

/// <summary>Eine Reihe gleichartiger Lagerfelder (ein Gang im Sinne der Codes): Ausrichtung und Felder in Leserichtung.</summary>
public sealed record GridRun(AisleOrientation Orientation, IReadOnlyList<GridCell> Cells);

/// <summary>Eine erzeugte Wand: Feldart (Wand oder Hindernis) und Mittellinie in mm; die Dicke ist die Feldgröße.</summary>
public sealed record GridWallSegment(GridCellType Type, Position From, Position To);

/// <summary>Ein zusammenhängender Bereich gleicher Pickpunkt-Art und das Feld, auf dem der Pickpunkt sitzt.</summary>
public sealed record GridPickArea(GridCellType Kind, int Col, int Row, int CellCount);

/// <summary>
/// Reine Rasterlogik ohne Datenbank: Reihen für die Gang-Codes, Wände aus Wand- und Hindernisfeldern, Pickpunkte aus
/// Start-, End- und Verpackungsbereichen.
/// </summary>
public static class GridLayoutPlanner
{
    /// <summary>Feldarten, aus denen ein Pickpunkt entsteht, und sein Typ.</summary>
    public static readonly IReadOnlyDictionary<GridCellType, PickPointType> PickPointKinds = new Dictionary<GridCellType, PickPointType>
    {
        [GridCellType.Start] = PickPointType.Start,
        [GridCellType.End] = PickPointType.End,
        [GridCellType.Packing] = PickPointType.Both,
    };

    /// <summary>
    /// Teilt die Felder einer Lagerfeld-Art in Reihen. Jedes Feld liegt waagerecht, wenn seine waagerechte Folge gleichartiger
    /// Felder mindestens so lang ist wie die senkrechte, sonst senkrecht; zusammenhängende Felder gleicher Ausrichtung in derselben
    /// Zeile (bzw. Spalte) bilden eine Reihe. Reihenfolge: von oben nach unten, dann von links nach rechts; die Felder einer Reihe
    /// von links nach rechts bzw. von oben nach unten.
    /// </summary>
    public static IReadOnlyList<GridRun> DetectRuns(IReadOnlyCollection<GridCell> cells)
    {
        var at = cells.ToDictionary(c => (c.Col, c.Row));
        int RunLength(GridCell c, int dc, int dr)
        {
            var n = 1;
            for (var k = 1; at.ContainsKey((c.Col + dc * k, c.Row + dr * k)); k++) n++;
            for (var k = 1; at.ContainsKey((c.Col - dc * k, c.Row - dr * k)); k++) n++;
            return n;
        }

        var horizontal = cells.Where(c => RunLength(c, 1, 0) >= RunLength(c, 0, 1)).Select(c => (c.Col, c.Row)).ToHashSet();
        var runs = new List<GridRun>();
        var done = new HashSet<(int, int)>();
        foreach (var cell in cells.OrderBy(c => c.Row).ThenBy(c => c.Col))
        {
            if (done.Contains((cell.Col, cell.Row))) continue;
            var isH = horizontal.Contains((cell.Col, cell.Row));
            var (dc, dr) = isH ? (1, 0) : (0, 1);
            var run = new List<GridCell>();
            for (var k = 0; ; k++)
            {
                var key = (cell.Col + dc * k, cell.Row + dr * k);
                if (!at.TryGetValue(key, out var next) || horizontal.Contains(key) != isH || done.Contains(key)) break;
                run.Add(next);
                done.Add(key);
            }
            runs.Add(new GridRun(isH ? AisleOrientation.AlongX : AisleOrientation.AlongY, run));
        }

        return runs
            .OrderBy(r => r.Cells.Min(c => c.Row))
            .ThenBy(r => r.Cells.Min(c => c.Col))
            .ToList();
    }

    /// <summary>
    /// Wände aus Wand- und Hindernisfeldern: zuerst waagerechte Folgen ab zwei Feldern, die übrigen Felder als senkrechte Folgen.
    /// Jede Folge wird eine gerade Wand entlang der Feldmitte mit der Feldgröße als Dicke; das Rechteck deckt die Felder genau ab.
    /// </summary>
    public static IReadOnlyList<GridWallSegment> PlanWalls(IReadOnlyCollection<GridCell> cells, int cellSizeMm)
    {
        var segments = new List<GridWallSegment>();
        foreach (var type in new[] { GridCellType.Wall, GridCellType.Obstacle })
        {
            var set = cells.Where(c => c.Type == type).Select(c => (c.Col, c.Row)).ToHashSet();
            var used = new HashSet<(int Col, int Row)>();

            foreach (var row in set.Select(p => p.Row).Distinct().OrderBy(r => r))
            {
                var cols = set.Where(p => p.Row == row).Select(p => p.Col).OrderBy(c => c).ToList();
                foreach (var (first, last) in Ranges(cols))
                {
                    if (last == first) continue;
                    var y = row * cellSizeMm + cellSizeMm / 2;
                    segments.Add(new GridWallSegment(type, new Position(first * cellSizeMm, y, 0), new Position((last + 1) * cellSizeMm, y, 0)));
                    for (var c = first; c <= last; c++) used.Add((c, row));
                }
            }

            var rest = set.Except(used).ToList();
            foreach (var col in rest.Select(p => p.Col).Distinct().OrderBy(c => c))
            {
                var rows = rest.Where(p => p.Col == col).Select(p => p.Row).OrderBy(r => r).ToList();
                foreach (var (first, last) in Ranges(rows))
                {
                    var x = col * cellSizeMm + cellSizeMm / 2;
                    segments.Add(new GridWallSegment(type, new Position(x, first * cellSizeMm, 0), new Position(x, (last + 1) * cellSizeMm, 0)));
                }
            }
        }
        return segments;
    }

    /// <summary>
    /// Pickpunkt-Bereiche: zusammenhängende Felder (waagerecht/senkrecht benachbart) gleicher Art ergeben einen Pickpunkt auf dem
    /// Feld, das dem Schwerpunkt des Bereichs am nächsten liegt. Reihenfolge je Art: von oben nach unten, dann von links nach rechts.
    /// </summary>
    public static IReadOnlyList<GridPickArea> PlanPickAreas(IReadOnlyCollection<GridCell> cells)
    {
        var areas = new List<GridPickArea>();
        foreach (var kind in PickPointKinds.Keys)
        {
            var set = cells.Where(c => c.Type == kind).Select(c => (c.Col, c.Row)).ToHashSet();
            var seen = new HashSet<(int Col, int Row)>();
            foreach (var startCell in set.OrderBy(p => p.Row).ThenBy(p => p.Col))
            {
                if (!seen.Add(startCell)) continue;
                var area = new List<(int Col, int Row)>();
                var queue = new Queue<(int Col, int Row)>();
                queue.Enqueue(startCell);
                while (queue.Count > 0)
                {
                    var p = queue.Dequeue();
                    area.Add(p);
                    foreach (var n in new[] { (p.Col + 1, p.Row), (p.Col - 1, p.Row), (p.Col, p.Row + 1), (p.Col, p.Row - 1) })
                        if (set.Contains(n) && seen.Add(n)) queue.Enqueue(n);
                }

                var cx = area.Average(p => p.Col);
                var cy = area.Average(p => p.Row);
                var anchor = area
                    .OrderBy(p => (p.Col - cx) * (p.Col - cx) + (p.Row - cy) * (p.Row - cy))
                    .ThenBy(p => p.Row).ThenBy(p => p.Col)
                    .First();
                areas.Add(new GridPickArea(kind, anchor.Col, anchor.Row, area.Count));
            }
        }
        return areas;
    }

    private static IEnumerable<(int First, int Last)> Ranges(IReadOnlyList<int> sorted)
    {
        for (var i = 0; i < sorted.Count;)
        {
            var j = i;
            while (j + 1 < sorted.Count && sorted[j + 1] == sorted[j] + 1) j++;
            yield return (sorted[i], sorted[j]);
            i = j + 1;
        }
    }
}
