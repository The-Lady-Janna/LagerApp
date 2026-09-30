using Lager.Api.Labels;
using Lager.Contracts.PickLists;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Lager.Api.Documents;

/// <summary>
/// Renders a multi-page shipping label PDF — one page per order in the
/// PickList. Each page lists the order header, the picked items, and a
/// real Code 128 barcode of the OrderNumber, drawn as vector rectangles
/// (see <see cref="Code128Encoder"/>; no barcode font needed) with the
/// number in plain text below. An order number that Code 128 (Set B/C) cannot
/// represent (e.g. an umlaut) falls back to a plain text box, so the PDF
/// never fails because of it.
/// </summary>
public static class ShippingLabelRenderer
{
    static ShippingLabelRenderer()
    {
        // QuestPDF-Lizenz: Hier wird die Community-Lizenz (MIT) gewählt. Sie ist kostenlos für
        // Open-Source-Projekte, Non-Profit/Bildung und Unternehmen mit weniger als 1 Mio. USD
        // Jahresumsatz. Wer diese Bedingungen nicht erfüllt (z. B. größere Firmen, die Lager
        // forken, selbst kompilieren und betreiben), braucht eine kommerzielle QuestPDF-Lizenz
        // (Professional/Enterprise) und muss die Zuweisung hier entsprechend ändern.
        // Maßgeblich ist der Wortlaut auf https://www.questpdf.com/license/ (bzw. die
        // PackageLicense.md der verwendeten Version); das ist keine Rechtsberatung.
        // Beim Aktualisieren des Pakets nur innerhalb dieser Lizenzlinie bleiben.
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public static byte[] Render(PickListDto pickList)
    {
        var orders = pickList.Items
            .GroupBy(i => (i.OrderId, i.OrderNumber))
            .Select(g => (g.Key.OrderId, g.Key.OrderNumber, Items: g.OrderBy(i => i.SequenceNumber).ToList()))
            .ToList();

        var doc = Document.Create(container =>
        {
            foreach (var (_, orderNumber, items) in orders)
            {
                container.Page(page =>
                {
                    page.Margin(40);
                    page.Size(PageSizes.A4);
                    page.DefaultTextStyle(x => x.FontSize(11));

                    page.Header().Column(col =>
                    {
                        col.Item().Text("Lieferschein").FontSize(22).Bold();
                        col.Item().Text($"Pickliste: {pickList.PickListNumber}").FontSize(9).FontColor(Colors.Grey.Darken1);
                        col.Item().Text($"Bestellung: {orderNumber}").FontSize(14).Bold();
                    });

                    page.Content().PaddingVertical(15).Column(col =>
                    {
                        col.Spacing(10);

                        col.Item().Text($"Erstellt: {pickList.CreatedAt:dd.MM.yyyy HH:mm}")
                            .FontSize(9).FontColor(Colors.Grey.Darken2);

                        col.Item().Table(t =>
                        {
                            t.ColumnsDefinition(c =>
                            {
                                c.ConstantColumn(35);  // #
                                c.RelativeColumn(2);   // SKU
                                c.RelativeColumn(4);   // Name
                                c.RelativeColumn(2);   // Bin
                                c.ConstantColumn(50);  // Qty
                            });

                            t.Header(h =>
                            {
                                static IContainer HeaderCell(IContainer c) =>
                                    c.DefaultTextStyle(x => x.SemiBold()).PaddingVertical(5).BorderBottom(1).BorderColor(Colors.Grey.Medium);
                                h.Cell().Element(HeaderCell).Text("#");
                                h.Cell().Element(HeaderCell).Text("SKU");
                                h.Cell().Element(HeaderCell).Text("Artikel");
                                h.Cell().Element(HeaderCell).Text("Bin");
                                h.Cell().Element(HeaderCell).AlignRight().Text("Menge");
                            });

                            var seq = 1;
                            foreach (var item in items)
                            {
                                static IContainer DataCell(IContainer c) =>
                                    c.PaddingVertical(4).BorderBottom(1).BorderColor(Colors.Grey.Lighten2);

                                t.Cell().Element(DataCell).Text(seq.ToString());
                                t.Cell().Element(DataCell).Text(item.ArticleSku);
                                t.Cell().Element(DataCell).Text(item.ArticleName);
                                t.Cell().Element(DataCell).Text(item.StorageLocationCode);
                                t.Cell().Element(DataCell).AlignRight().Text((item.ConfirmedQuantity ?? item.Quantity).ToString());
                                seq++;
                            }
                        });

                        col.Item().PaddingTop(25).Element(c => OrderBarcode(c, orderNumber));
                    });

                    page.Footer().AlignCenter().Text(t =>
                    {
                        t.DefaultTextStyle(x => x.FontSize(8).FontColor(Colors.Grey.Darken1));
                        t.Span("Seite ");
                        t.CurrentPageNumber();
                        t.Span(" / ");
                        t.TotalPages();
                    });
                });
            }
        });

        return doc.GeneratePdf();
    }

    /// <summary>Höhe der Balken in Punkt (etwa 18 mm).</summary>
    private const float BarcodeHeight = 50;

    /// <summary>
    /// Breite eines Moduls (des schmalsten Balkens) in Punkt (etwa 0,53 mm). Der Code wird nur schmaler gesetzt, wenn er
    /// sonst nicht auf die Seite passt (sehr lange Bestellnummern).
    /// </summary>
    private const float ModuleWidth = 1.5f;

    /// <summary>
    /// Der Barcode der Bestellnummer: Code 128 als Vektor-Rechtecke (ein gefülltes QuestPDF-Rechteck je Balken, keine
    /// Schrift, kein Bild) mit der Nummer im Klartext darunter. Ohne kodierbare Nummer (leer, Zeichen außerhalb von
    /// ASCII 32..126) bleibt der Text-Kasten.
    /// </summary>
    private static void OrderBarcode(IContainer container, string orderNumber)
    {
        if (!Code128Encoder.CanEncode(orderNumber))
        {
            container.AlignCenter().Border(1).BorderColor(Colors.Grey.Darken2).Padding(12)
                .Text(orderNumber).FontFamily(Fonts.CourierNew).FontSize(20).Bold();
            return;
        }

        var barcode = Code128Encoder.Encode(orderNumber);
        container.Column(col =>
        {
            col.Item().Element(c => BarcodeBars(c, barcode));
            col.Item().PaddingTop(4).AlignCenter().Text(orderNumber).FontFamily(Fonts.CourierNew).FontSize(14).Bold();
        });
    }

    /// <summary>
    /// Die Balken als Reihe relativ breiter Felder (Breite = Module): schwarze Felder sind Balken, leere Lücken.
    /// Links und rechts steht die Ruhezone (10 Module). Die Reihe ist höchstens so breit wie <see cref="ModuleWidth"/> je Modul
    /// und wird mittig gesetzt; ist weniger Platz, skalieren alle Felder gleich mit.
    /// </summary>
    private static void BarcodeBars(IContainer container, Code128Barcode barcode)
    {
        var totalModules = barcode.ModuleCount + 2 * Code128Encoder.QuietZoneModules;
        container.AlignCenter().MaxWidth(totalModules * ModuleWidth).Row(row =>
        {
            row.RelativeItem(Code128Encoder.QuietZoneModules);
            for (var i = 0; i < barcode.Widths.Count; i++)
            {
                var field = row.RelativeItem(barcode.Widths[i]);
                if (i % 2 == 0) field.Height(BarcodeHeight).Background(Colors.Black);
            }
            row.RelativeItem(Code128Encoder.QuietZoneModules);
        });
    }
}
