using System.Text;

namespace Lager.Api.Labels;

/// <summary>
/// Generiert Zebra ZPL II (Zebra Programming Language) für Etikettendruck.
/// Standardgröße: 50 × 30 mm bei 8 dpmm (~400 × 240 dots), passt auf typische
/// Inventur-Rollen. Druck erfolgt über das Browser-File-Download — der User
/// schickt die .zpl-Datei dann zum Drucker (USB-Druckerwarteschlange oder
/// "Zebra Designer"-Tool). Echtes Network-Print via TCP-Stream zum Drucker
/// kommt mit "Print-Service"-Iteration.
///
/// Der Barcode ist ein Code 128 (^BC); den Bogen für Bürodrucker (A4-Etikettenbögen) erzeugt
/// die Oberfläche selbst im Browser (Seite "Etiketten").
/// </summary>
public static class ZplLabelRenderer
{
    /// <summary>Obergrenze für die Kopienzahl (^PQ) je Etikett.</summary>
    public const int MaxCopies = 500;

    /// <summary>
    /// Bin-Label: Code128-Barcode oben, BinCode lesbar darunter.
    /// </summary>
    public static string RenderBinLabel(string binCode, string? extraText = null, int copies = 1)
    {
        var sb = Begin();
        sb.AppendLine("^CF0,30");                              // Default-Font
        // Code128 Barcode
        sb.AppendLine("^FO40,30^BY2");
        sb.AppendLine($"^BCN,80,Y,N,N^FD{Escape(binCode)}^FS");
        // Human-readable label
        sb.AppendLine($"^FO40,160^A0N,30,30^FD{Escape(binCode)}^FS");
        if (!string.IsNullOrWhiteSpace(extraText))
            sb.AppendLine($"^FO40,200^A0N,18,18^FD{Escape(extraText)}^FS");
        return End(sb, copies);
    }

    /// <summary>
    /// Article-Label: SKU als Code128 + Name als Text.
    /// </summary>
    public static string RenderArticleLabel(string sku, string name, int copies = 1)
    {
        var sb = Begin();
        sb.AppendLine("^FO40,20^BY2");
        sb.AppendLine($"^BCN,80,Y,N,N^FD{Escape(sku)}^FS");
        sb.AppendLine($"^FO40,150^A0N,28,28^FD{Escape(sku)}^FS");
        // Name kann länger sein — manual line break bei ~30 Zeichen.
        sb.AppendLine($"^FO40,185^A0N,22,22^FB320,2,0,L,0^FD{Escape(name)}^FS");
        return End(sb, copies);
    }

    /// <summary>
    /// Order-Label für die Versand-Box (kein vollwertiges Versandetikett —
    /// dafür gibt es separate Carrier-Labels).
    /// </summary>
    public static string RenderOrderLabel(string orderNumber, string? customerRef = null, int copies = 1)
    {
        var sb = Begin();
        sb.AppendLine("^FO40,20^BY2");
        sb.AppendLine($"^BCN,70,Y,N,N^FD{Escape(orderNumber)}^FS");
        sb.AppendLine($"^FO40,135^A0N,26,26^FDOrder: {Escape(orderNumber)}^FS");
        if (!string.IsNullOrWhiteSpace(customerRef))
            sb.AppendLine($"^FO40,175^A0N,22,22^FDKunde: {Escape(customerRef)}^FS");
        return End(sb, copies);
    }

    /// <summary>Label-Start mit Größe und UTF-8-Zeichensatz (^CI28), damit Umlaute in Namen richtig gedruckt werden.</summary>
    private static StringBuilder Begin()
    {
        var sb = new StringBuilder();
        sb.AppendLine("^XA");                                  // Label-Start
        sb.AppendLine("^CI28");                                // Zeichensatz UTF-8 (die Datei wird als UTF-8 geliefert)
        sb.AppendLine("^PW400");                               // Print-Width 400 dots
        sb.AppendLine("^LL240");                               // Label-Length 240 dots
        return sb;
    }

    /// <summary>Kopienzahl (^PQ, nur bei mehr als einer) und Label-Ende.</summary>
    private static string End(StringBuilder sb, int copies)
    {
        if (copies < 1 || copies > MaxCopies)
            throw new ArgumentOutOfRangeException(nameof(copies), copies, $"Die Kopienzahl muss zwischen 1 und {MaxCopies} liegen.");
        if (copies > 1) sb.AppendLine($"^PQ{copies}");         // Print-Quantity
        sb.AppendLine("^XZ");                                  // Label-End
        return sb.ToString();
    }

    /// <summary>
    /// ZPL-Inhalt nutzt `^` und `~` als Steuerzeichen. Wir entfernen sie
    /// präventiv aus User-Daten — sonst kann ein Bin-Code mit Sonderzeichen
    /// das Label zerschießen. Zeilenumbrüche würden das Feld ebenfalls zerreißen und werden zu Leerzeichen.
    /// </summary>
    private static string Escape(string s) =>
        s.Replace("^", "").Replace("~", "").Replace("\r", " ").Replace("\n", " ").Trim();
}
