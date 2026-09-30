namespace Lager.Domain.Warehouse;

/// <summary>Lage zweier Strecken zueinander (siehe <see cref="Geometry.Classify"/>).</summary>
public enum SegmentRelation
{
    /// <summary>Kein gemeinsamer Punkt.</summary>
    Disjoint = 0,

    /// <summary>
    /// Genau ein gemeinsamer Punkt, der aber keine echte Kreuzung ist: gemeinsamer Endpunkt, T-Berührung
    /// (Endpunkt der einen Strecke liegt auf der anderen), Endpunkt auf einer Strecke, kollineare Berührung.
    /// </summary>
    Touch = 1,

    /// <summary>Die Strecken kreuzen sich in einem inneren Punkt beider Strecken.</summary>
    Cross = 2,

    /// <summary>Die Strecken liegen auf einer Geraden und überlappen sich auf einer Länge &gt; 0.</summary>
    Overlap = 3,
}

public static class Geometry
{
    /// <summary>Längentoleranz in mm: Abstände darunter gelten als "gleicher Punkt" bzw. "auf der Geraden".</summary>
    private const double LenEps = 1e-6;

    /// <summary>Relative Parallel-Toleranz (Sinus des Winkels), unabhängig von der Einheit der Koordinaten.</summary>
    private const double ParallelEps = 1e-12;

    /// <summary>
    /// True, wenn sich die Strecken A-B und C-D kreuzen oder auf einer Länge &gt; 0 überlappen
    /// (<see cref="SegmentRelation.Cross"/> bzw. <see cref="SegmentRelation.Overlap"/>). Eine bloße Berührung
    /// (gemeinsamer Endpunkt, T-Stoß, Endpunkt auf der anderen Strecke, kollinear nur am Ende) zählt NICHT;
    /// dafür gibt es <see cref="SegmentsIntersect"/>. Parallele, disjunkte Strecken und Strecken der Länge 0
    /// kreuzen sich nie.
    /// </summary>
    public static bool SegmentsCross(
        double ax, double ay, double bx, double by,
        double cx, double cy, double dx, double dy) =>
        Classify(ax, ay, bx, by, cx, cy, dx, dy) is SegmentRelation.Cross or SegmentRelation.Overlap;

    /// <summary>True, wenn die Strecken mindestens einen Punkt gemeinsam haben (auch bei bloßer Berührung).</summary>
    public static bool SegmentsIntersect(
        double ax, double ay, double bx, double by,
        double cx, double cy, double dx, double dy) =>
        Classify(ax, ay, bx, by, cx, cy, dx, dy) != SegmentRelation.Disjoint;

    /// <summary>
    /// Ordnet zwei Strecken ein: kein gemeinsamer Punkt, Berührung, Kreuzung oder kollineare Überlappung.
    /// Die Toleranzen sind längenbezogen (mm) bzw. dimensionslos, nicht an das Quadrat der Koordinaten gebunden.
    /// </summary>
    public static SegmentRelation Classify(
        double ax, double ay, double bx, double by,
        double cx, double cy, double dx, double dy)
    {
        var rx = bx - ax; var ry = by - ay;
        var sx = dx - cx; var sy = dy - cy;
        var lr = Math.Sqrt(rx * rx + ry * ry);
        var ls = Math.Sqrt(sx * sx + sy * sy);

        // Nulllängen-Strecken sind Punkte: sie berühren höchstens.
        if (lr <= LenEps && ls <= LenEps)
            return Distance(ax, ay, cx, cy) <= LenEps ? SegmentRelation.Touch : SegmentRelation.Disjoint;
        if (lr <= LenEps)
            return DistancePointSegment(ax, ay, cx, cy, dx, dy) <= LenEps ? SegmentRelation.Touch : SegmentRelation.Disjoint;
        if (ls <= LenEps)
            return DistancePointSegment(cx, cy, ax, ay, bx, by) <= LenEps ? SegmentRelation.Touch : SegmentRelation.Disjoint;

        var qx = cx - ax; var qy = cy - ay;
        var rxs = rx * sy - ry * sx;

        if (Math.Abs(rxs) <= ParallelEps * lr * ls)
        {
            // Parallel: nur wenn C auf der Geraden AB liegt, sind die Strecken kollinear.
            var offLine = Math.Abs(qx * ry - qy * rx) / lr;
            if (offLine > LenEps) return SegmentRelation.Disjoint;

            var tc = (qx * rx + qy * ry) / (lr * lr);
            var td = ((dx - ax) * rx + (dy - ay) * ry) / (lr * lr);
            var overlap = (Math.Min(1.0, Math.Max(tc, td)) - Math.Max(0.0, Math.Min(tc, td))) * lr;
            if (overlap > LenEps) return SegmentRelation.Overlap;
            return overlap >= -LenEps ? SegmentRelation.Touch : SegmentRelation.Disjoint;
        }

        var t = (qx * sy - qy * sx) / rxs;
        var u = (qx * ry - qy * rx) / rxs;
        var tEps = LenEps / lr;
        var uEps = LenEps / ls;

        if (t < -tEps || t > 1 + tEps || u < -uEps || u > 1 + uEps) return SegmentRelation.Disjoint;
        if (t > tEps && t < 1 - tEps && u > uEps && u < 1 - uEps) return SegmentRelation.Cross;
        return SegmentRelation.Touch;
    }

    private static double Distance(double ax, double ay, double bx, double by)
    {
        var dx = ax - bx; var dy = ay - by;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double DistancePointSegment(double px, double py, double ax, double ay, double bx, double by)
    {
        var vx = bx - ax; var vy = by - ay;
        var len2 = vx * vx + vy * vy;
        var t = len2 <= 0 ? 0 : Math.Clamp(((px - ax) * vx + (py - ay) * vy) / len2, 0.0, 1.0);
        return Distance(px, py, ax + t * vx, ay + t * vy);
    }
}
