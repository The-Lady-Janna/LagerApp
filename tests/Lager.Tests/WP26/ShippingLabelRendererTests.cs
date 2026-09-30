using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Lager.Api.Documents;
using Lager.Api.Labels;
using Lager.Contracts.PickLists;
using Lager.Contracts.Warehouse;

namespace Lager.Tests.WP26;

/// <summary>
/// Das Lieferschein-PDF: gültiges PDF mit einer Seite je Bestellung, und der Barcode der Bestellnummer ist ein echter
/// Code 128 aus Vektor-Rechtecken (kein Text-Kasten, kein Bild). Der Test liest die Rechtecke aus dem Seiteninhalt des PDFs
/// zurück und vergleicht sie mit der Modulfolge des Encoders.
/// </summary>
public class ShippingLabelRendererTests
{
    [Fact]
    public void Renders_a_valid_PDF_with_one_page_per_order()
    {
        var pdf = ShippingLabelRenderer.Render(PickList(("ORD-DEMO-01", "ART-1"), ("ORD-DEMO-02", "ART-2"), ("ORD-DEMO-03", "ART-3")));

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
        Assert.Contains("%%EOF", Encoding.ASCII.GetString(pdf, pdf.Length - 32, 32));
        Assert.Equal(3, PdfPages(pdf));
        Assert.Equal(1, PdfPages(ShippingLabelRenderer.Render(PickList(("ORD-DEMO-01", "ART-1")))));
    }

    [Fact]
    public void The_order_number_barcode_is_Code128_drawn_as_vector_rectangles()
    {
        var pdf = ShippingLabelRenderer.Render(PickList(("ORD-DEMO-01", "ART-1")));

        var bars = BarcodeRectangles(pdf);
        var expected = Code128Encoder.Encode("ORD-DEMO-01");

        // Genau ein Rechteck je Balken (13 Symbole x 3 + 4 im Stopp).
        Assert.Equal(43, bars.Count);
        Assert.Equal(expected.Bars().Count(), bars.Count);

        // Modulbreite = schmalster Balken; Lage und Breite jedes Balkens entsprechen der Modulfolge des Encoders.
        var module = bars.Min(b => b.Width);
        Assert.True(module > 0.4, "Modul kleiner als 0,4 pt: nicht mehr sicher lesbar");
        var first = bars[0].Left;
        var actual = bars.Select(b => ((int)Math.Round((b.Left - first) / module), (int)Math.Round(b.Width / module))).ToList();
        Assert.Equal(expected.Bars().ToList(), actual);

        // Die Ruhezone links und rechts ist frei (10 Module) - hier prüfbar an der Mittigkeit des Codes auf der Seite.
        var right = bars[^1].Left + bars[^1].Width;
        var pageCenter = 595.0 / 2;
        Assert.InRange((first + right) / 2, pageCenter - 1, pageCenter + 1);
    }

    [Fact]
    public void Every_page_gets_the_barcode_of_its_own_order()
    {
        var pdf = ShippingLabelRenderer.Render(PickList(("ORD-1", "ART-1"), ("A-01-01", "ART-2")));

        // Zwei Seiten, je ein Barcode: Balkenzahl = Summe der Balken beider Codes
        var expectedBars = Code128Encoder.Encode("ORD-1").Bars().Count() + Code128Encoder.Encode("A-01-01").Bars().Count();
        Assert.Equal(expectedBars, BarcodeRectangles(pdf).Count);
    }

    [Fact]
    public void An_order_number_Code128_cannot_represent_falls_back_to_a_text_box_instead_of_failing()
    {
        var pdf = ShippingLabelRenderer.Render(PickList(("ÄRGER-01", "ART-1")));

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(pdf, 0, 5));
        Assert.Equal(1, PdfPages(pdf));
        Assert.Empty(BarcodeRectangles(pdf));
    }

    [Fact]
    public void A_very_long_order_number_is_scaled_to_fit_the_page()
    {
        var number = "BESTELLUNG-" + new string('7', 60);
        var pdf = ShippingLabelRenderer.Render(PickList((number, "ART-1")));

        var bars = BarcodeRectangles(pdf);
        Assert.Equal(Code128Encoder.Encode(number).Bars().Count(), bars.Count);
        Assert.InRange(bars[^1].Left + bars[^1].Width, 0, 595 - 40);         // nicht über den rechten Seitenrand (Rand 40 pt)
        Assert.InRange(bars[0].Left, 40, 595);
    }

    // ---- Hilfen ---------------------------------------------------------------------------------------------------

    private static PickListDto PickList(params (string OrderNumber, string Sku)[] orders)
    {
        var items = orders.Select((o, i) => new PickItemDto(
            Guid.NewGuid(), i + 1, Guid.NewGuid(), o.OrderNumber, Guid.NewGuid(), o.Sku, "Artikel " + o.Sku,
            Guid.NewGuid(), "A-01-0" + (i + 1), 5, false, null, null)).ToList();
        return new PickListDto(Guid.NewGuid(), "PL-0001", "Pending", null, 0, new DateTime(2026, 1, 2, 3, 4, 0, DateTimeKind.Utc),
            items, new List<PositionDto>());
    }

    private static int PdfPages(byte[] pdf) =>
        Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Type\s*/Page\b").Count;

    private sealed record Rect(double Left, double Width, double Height);

    /// <summary>
    /// Die Balken-Rechtecke aus dem Seiteninhalt: jedes Rechteck der Höhe des Barcodes (50 pt), das an einer festen Stelle
    /// gefüllt wird. Der Inhalt liegt Flate-komprimiert in den Streams; QuestPDF/Skia schreibt jedes Rechteck als
    /// <c>q … 4 0 0 4 X Y cm … 0 0 W H re f Q</c> (X in Viertelpunkt, ab der Seitenskalierung 0,25).
    /// </summary>
    private static List<Rect> BarcodeRectangles(byte[] pdf)
    {
        const double barcodeHeight = 50;
        var rects = new List<Rect>();
        foreach (var content in PageContents(pdf))
        {
            var cm = new Regex(@"4 0 0 4 (?<x>-?[\d.]+) (?<y>-?[\d.]+) cm\s+[^Q]*?\s0 0 (?<w>[\d.]+) (?<h>[\d.]+) re\s+f", RegexOptions.Singleline);
            foreach (Match m in cm.Matches(content))
            {
                var height = double.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
                if (Math.Abs(height - barcodeHeight) > 0.01) continue;
                rects.Add(new Rect(
                    double.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture) / 4,
                    double.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture),
                    height));
            }
        }
        return rects;
    }

    private static IEnumerable<string> PageContents(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        foreach (Match m in Regex.Matches(text, @"(?<!end)stream\r?\n(?<data>.*?)\r?\nendstream", RegexOptions.Singleline))
        {
            var raw = Encoding.Latin1.GetBytes(m.Groups["data"].Value);
            string? inflated;
            try
            {
                using var zlib = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
                using var reader = new StreamReader(zlib, Encoding.Latin1);
                inflated = reader.ReadToEnd();
            }
            catch (InvalidDataException)
            {
                continue;    // kein Flate-Stream (Schrift, Bild …)
            }
            if (inflated.Contains(" re", StringComparison.Ordinal)) yield return inflated;
        }
    }
}
