using Lager.Api.Labels;

namespace Lager.Tests.WP26;

/// <summary>ZPL-Etiketten: Code 128 (^BC), UTF-8, Kopienzahl (^PQ) und Schutz vor Steuerzeichen in Nutzerdaten.</summary>
public class ZplLabelRendererTests
{
    [Fact]
    public void Bin_article_and_order_labels_carry_a_Code128_barcode_of_their_code()
    {
        var bin = ZplLabelRenderer.RenderBinLabel("A-01-01");
        var article = ZplLabelRenderer.RenderArticleLabel("ART-1", "Schraube M8");
        var order = ZplLabelRenderer.RenderOrderLabel("ORD-DEMO-01", "K-4711");

        Assert.Contains("^BCN,80,Y,N,N^FDA-01-01^FS", bin);
        Assert.Contains("^BCN,80,Y,N,N^FDART-1^FS", article);
        Assert.Contains("Schraube M8", article);
        Assert.Contains("^BCN,70,Y,N,N^FDORD-DEMO-01^FS", order);
        Assert.Contains("Kunde: K-4711", order);
        foreach (var zpl in new[] { bin, article, order })
        {
            Assert.StartsWith("^XA", zpl);
            Assert.EndsWith("^XZ" + Environment.NewLine, zpl);
            Assert.Contains("^CI28", zpl);                       // UTF-8: Umlaute im Namen kommen richtig auf das Etikett
            Assert.DoesNotContain("^PQ", zpl);                   // eine Kopie ist der Normalfall
        }
    }

    [Fact]
    public void Copies_become_a_print_quantity_before_the_label_end()
    {
        var zpl = ZplLabelRenderer.RenderBinLabel("A-01-01", copies: 12);

        Assert.Contains("^PQ12", zpl);
        Assert.True(zpl.IndexOf("^PQ12", StringComparison.Ordinal) < zpl.IndexOf("^XZ", StringComparison.Ordinal));
        Assert.Contains("^PQ3", ZplLabelRenderer.RenderArticleLabel("S", "N", 3));
        Assert.Contains("^PQ500", ZplLabelRenderer.RenderOrderLabel("O", null, ZplLabelRenderer.MaxCopies));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(ZplLabelRenderer.MaxCopies + 1)]
    public void Rejects_a_copy_count_outside_the_range(int copies)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ZplLabelRenderer.RenderBinLabel("A-01-01", copies: copies));
    }

    [Fact]
    public void User_data_cannot_inject_ZPL_commands_or_break_the_field()
    {
        var zpl = ZplLabelRenderer.RenderBinLabel("A^XZ~JA\r\n^FO0,0", "Zeile 1\nZeile 2");

        // ^ und ~ fallen weg, Zeilenumbrüche werden zu Leerzeichen: genau ein ^XA und ein ^XZ, jedes Feld auf einer Zeile
        Assert.Equal(1, Count(zpl, "^XA"));
        Assert.Equal(1, Count(zpl, "^XZ"));
        Assert.Contains("^FDAXZJA  FO0,0^FS", zpl);
        Assert.Contains("^FDZeile 1 Zeile 2^FS", zpl);
    }

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }
}
