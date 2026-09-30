using Lager.Domain.Warehouse;

namespace Lager.Tests.WP08;

/// <summary>
/// Reine Unit-Tests (ohne Host) für <see cref="Geometry"/>: Kreuzung, Berührung, kollineare Überlappung.
/// Die Wegsuche verlässt sich darauf, dass Berührungen und Überlappungen nicht als "kein Schnitt" durchrutschen.
/// </summary>
public class GeometryTests
{
    public static TheoryData<string, double[], SegmentRelation> Cases() => new()
    {
        { "Kreuzung im Inneren (X)", new double[] { 0, 0, 10, 10, 0, 10, 10, 0 }, SegmentRelation.Cross },
        { "T-Stoß: Endpunkt liegt auf der anderen Strecke", new double[] { 0, 0, 10, 0, 5, 0, 5, 10 }, SegmentRelation.Touch },
        { "gemeinsamer Endpunkt", new double[] { 0, 0, 10, 0, 10, 0, 10, 10 }, SegmentRelation.Touch },
        { "Endpunkt genau auf der Diagonale", new double[] { 0, 0, 10, 10, 5, 5, 5, 20 }, SegmentRelation.Touch },
        { "parallel und getrennt", new double[] { 0, 0, 10, 0, 0, 5, 10, 5 }, SegmentRelation.Disjoint },
        { "kollinear, teilweise überlappend", new double[] { 0, 0, 10, 0, 5, 0, 15, 0 }, SegmentRelation.Overlap },
        { "kollinear, eine liegt in der anderen", new double[] { 0, 0, 10, 0, 2, 0, 4, 0 }, SegmentRelation.Overlap },
        { "kollinear, nur Endpunkt gemeinsam", new double[] { 0, 0, 10, 0, 10, 0, 20, 0 }, SegmentRelation.Touch },
        { "kollinear, getrennt", new double[] { 0, 0, 10, 0, 11, 0, 20, 0 }, SegmentRelation.Disjoint },
        { "Beinahe-Treffer", new double[] { 0, 0, 10, 0, 5, -1, 5, -0.001 }, SegmentRelation.Disjoint },
        { "Nulllänge auf der Strecke", new double[] { 5, 5, 5, 5, 0, 0, 10, 10 }, SegmentRelation.Touch },
        { "Nulllänge neben der Strecke", new double[] { 5, 6, 5, 6, 0, 0, 10, 10 }, SegmentRelation.Disjoint },
        { "sehr lange Strecken kreuzen sich", new double[] { -1e6, -1e6, 1e6, 1e6, -1e6, 1e6, 1e6, -1e6 }, SegmentRelation.Cross },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Classifies_touching_crossing_and_overlapping_segments(string name, double[] s, SegmentRelation expected)
    {
        var relation = Geometry.Classify(s[0], s[1], s[2], s[3], s[4], s[5], s[6], s[7]);
        Assert.True(expected == relation, $"{name}: erwartet {expected}, war {relation}");

        // SegmentsCross = Kreuzung oder Überlappung (Berührung zählt nicht), SegmentsIntersect = jeder gemeinsame Punkt.
        Assert.Equal(expected is SegmentRelation.Cross or SegmentRelation.Overlap,
            Geometry.SegmentsCross(s[0], s[1], s[2], s[3], s[4], s[5], s[6], s[7]));
        Assert.Equal(expected != SegmentRelation.Disjoint,
            Geometry.SegmentsIntersect(s[0], s[1], s[2], s[3], s[4], s[5], s[6], s[7]));
    }

    [Fact]
    public void Result_does_not_depend_on_segment_order_or_direction()
    {
        var rnd = new Random(20240607);
        for (var i = 0; i < 2000; i++)
        {
            double R() => rnd.Next(0, 6) * 5; // grobes Raster: viele Berührungen, Überlappungen und Nulllängen
            double ax = R(), ay = R(), bx = R(), by = R(), cx = R(), cy = R(), dx = R(), dy = R();

            var expected = Geometry.Classify(ax, ay, bx, by, cx, cy, dx, dy);
            Assert.Equal(expected, Geometry.Classify(bx, by, ax, ay, cx, cy, dx, dy));
            Assert.Equal(expected, Geometry.Classify(ax, ay, bx, by, dx, dy, cx, cy));
            Assert.Equal(expected, Geometry.Classify(cx, cy, dx, dy, ax, ay, bx, by));
        }
    }
}
