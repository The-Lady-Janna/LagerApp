using System.Text;

namespace Lager.Api.Labels;

/// <summary>
/// Ein kodierter Code-128-Barcode: die Symbolwerte (Startzeichen, Daten, Prüfsumme, Stoppzeichen) und die daraus
/// folgende Modulfolge. Erzeugt von <see cref="Code128Encoder.Encode"/>.
/// </summary>
/// <param name="Text">Der kodierte Text (genau das, was ein Scanner wieder ausgibt).</param>
/// <param name="Symbols">Alle Symbolwerte in Druckreihenfolge: Start (104 = B, 105 = C), Daten (inkl. Umschalter 99/100),
/// Prüfsumme, Stopp (106).</param>
/// <param name="Checksum">Die Prüfsumme (Start + Summe Wert × Position) mod 103; steht als vorletztes Symbol in <paramref name="Symbols"/>.</param>
/// <param name="Widths">Breiten der Balken und Lücken in Modulen, abwechselnd, beginnend mit einem Balken (Stopp: 7 Elemente,
/// der letzte Balken ist der Abschlussbalken). Die Ruhezonen sind NICHT enthalten.</param>
public sealed record Code128Barcode(string Text, IReadOnlyList<int> Symbols, int Checksum, IReadOnlyList<int> Widths)
{
    /// <summary>Gesamtbreite in Modulen (ohne Ruhezonen).</summary>
    public int ModuleCount => Widths.Sum();

    /// <summary>Die Modulfolge als Text: "1" = Balken, "0" = Lücke (ohne Ruhezonen), z. B. "11010010000…".</summary>
    public string Modules
    {
        get
        {
            var sb = new StringBuilder(ModuleCount);
            for (var i = 0; i < Widths.Count; i++)
                sb.Append(i % 2 == 0 ? '1' : '0', Widths[i]);
            return sb.ToString();
        }
    }

    /// <summary>Die schwarzen Balken als (Startmodul, Breite in Modulen), zusammengefasst und von links nach rechts.</summary>
    public IEnumerable<(int Start, int Width)> Bars()
    {
        var position = 0;
        for (var i = 0; i < Widths.Count; i++)
        {
            if (i % 2 == 0) yield return (position, Widths[i]);
            position += Widths[i];
        }
    }
}

/// <summary>
/// Code-128-Encoder (Code Set B und C). Set B deckt ASCII 32..126 ab (Ziffern, Buchstaben, Satzzeichen), Set C kodiert
/// zwei Ziffern je Symbol. Set A (Steuerzeichen), FNC-Zeichen und Zeichen außerhalb von ASCII 32..126 werden bewusst
/// nicht unterstützt: Lagerplatz-Codes, SKUs und Bestellnummern kommen damit aus, und ein Zeichen, das nicht
/// kodierbar ist, wird als Fehler gemeldet statt still verändert (der Scan muss exakt den Code liefern).
///
/// Die Wahl zwischen B und C ist eine kleine dynamische Programmierung mit dem Ziel der kürzesten Symbolfolge.
/// Bei Gleichstand gilt: Start in B vor Start in C, im Set bleiben vor Umschalten. Dadurch ist die Folge
/// eindeutig, und der Frontend-Encoder (frontend/lager-ui/src/features/labels/code128.ts) liefert für jede Eingabe
/// dieselben Symbole. Beispiele: "ORD-DEMO-01" bleibt ganz in B (zwei Ziffern am Ende sparen nichts),
/// "ART-123456" wechselt vor den sechs Ziffern nach C, "1234567890" startet in C, eine ungerade Ziffernfolge
/// ("12345") kodiert die erste Ziffer in B und wechselt dann nach C.
/// </summary>
public static class Code128Encoder
{
    public const int StartB = 104;
    public const int StartC = 105;
    public const int CodeB = 100;
    public const int CodeC = 99;
    public const int Stop = 106;

    /// <summary>Mindestbreite der Ruhezone links und rechts in Modulen (Vorgabe der Norm: 10 X).</summary>
    public const int QuietZoneModules = 10;

    /// <summary>
    /// Balken-/Lückenbreiten je Symbolwert (0..105: sechs Elemente, 106 = Stopp mit sieben Elementen), Balken zuerst.
    /// Standardtabelle nach ISO/IEC 15417.
    /// </summary>
    private static readonly string[] Patterns =
    {
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312", "132212", "221213",
        "221312", "231212", "112232", "122132", "122231", "113222", "123122", "123221", "223211", "221132",
        "221231", "213212", "223112", "312131", "311222", "321122", "321221", "312212", "322112", "322211",
        "212123", "212321", "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121", "313121", "211331",
        "231131", "213113", "213311", "213131", "311123", "311321", "331121", "312113", "312311", "332111",
        "314111", "221411", "431111", "111224", "111422", "121124", "121421", "141122", "141221", "112214",
        "112412", "122114", "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112", "421211", "212141",
        "214121", "412121", "111143", "111341", "131141", "114113", "114311", "411113", "411311", "113141",
        "114131", "311141", "411131", "211412", "211214", "211232", "2331112",
    };

    private const int SetB = 0;
    private const int SetC = 1;

    /// <summary>Lässt sich der Text kodieren? (nicht leer, nur ASCII 32..126)</summary>
    public static bool CanEncode(string? text) => !string.IsNullOrEmpty(text) && text.All(IsSetBCharacter);

    /// <summary>
    /// Kodiert den Text. Wirft <see cref="ArgumentException"/> bei leerem Text oder einem Zeichen außerhalb von ASCII 32..126
    /// (der Aufrufer prüft vorher mit <see cref="CanEncode"/>, wenn er ohne Barcode weiterarbeiten will).
    /// </summary>
    public static Code128Barcode Encode(string text)
    {
        ArgumentException.ThrowIfNullOrEmpty(text);
        foreach (var c in text)
        {
            if (!IsSetBCharacter(c))
                throw new ArgumentException($"Das Zeichen '{c}' (U+{(int)c:X4}) ist in Code 128 (Set B/C) nicht darstellbar; erlaubt sind die ASCII-Zeichen 32 bis 126.", nameof(text));
        }

        var n = text.Length;
        // cost[i, set]: kleinste Zahl weiterer Symbole (ohne Start, Prüfsumme, Stopp), um text[i..] zu kodieren, wenn gerade
        // Set B bzw. C aktiv ist. Rückwärts gefüllt; B(i) und C(i) hängen gegenseitig voneinander ab, die geschlossene Form
        // unten löst das ohne Iteration: ein Wechsel in ein Set, das sofort wieder verlassen wird, lohnt nie.
        var cost = new int[n + 1, 2];
        for (var i = n - 1; i >= 0; i--)
        {
            if (PairAt(text, i))
            {
                var pair = 1 + cost[i + 2, SetC];                               // Ziffernpaar in C kodieren
                cost[i, SetB] = 1 + Math.Min(cost[i + 1, SetB], pair);          // in B: Zeichen kodieren ODER nach C wechseln (1) + Paar
                cost[i, SetC] = Math.Min(pair, 1 + cost[i, SetB]);              // in C: Paar kodieren ODER nach B wechseln (1)
            }
            else
            {
                cost[i, SetB] = 1 + cost[i + 1, SetB];
                cost[i, SetC] = 1 + cost[i, SetB];                              // in C ohne Paar bleibt nur der Wechsel nach B
            }
        }

        var set = cost[0, SetB] <= cost[0, SetC] ? SetB : SetC;
        var symbols = new List<int>(n + 4) { set == SetB ? StartB : StartC };
        var index = 0;
        while (index < n)
        {
            if (set == SetB)
            {
                // Bei Gleichstand im Set bleiben (Zeichen kodieren), sonst nach C wechseln.
                if (1 + cost[index + 1, SetB] <= 1 + cost[index, SetC])
                {
                    symbols.Add(text[index] - ' ');
                    index++;
                }
                else
                {
                    symbols.Add(CodeC);
                    set = SetC;
                }
            }
            else
            {
                var pair = PairAt(text, index) ? 1 + cost[index + 2, SetC] : int.MaxValue;
                if (pair <= 1 + cost[index, SetB])
                {
                    symbols.Add((text[index] - '0') * 10 + (text[index + 1] - '0'));
                    index += 2;
                }
                else
                {
                    symbols.Add(CodeB);
                    set = SetB;
                }
            }
        }

        // Prüfsumme: Startwert + Summe (Wert × Position ab 1) mod 103 (Start und Stopp selbst zählen nicht als Position).
        var sum = symbols[0];
        for (var i = 1; i < symbols.Count; i++) sum += symbols[i] * i;
        var checksum = sum % 103;
        symbols.Add(checksum);
        symbols.Add(Stop);

        var widths = new List<int>(symbols.Count * 6 + 1);
        foreach (var symbol in symbols)
            foreach (var digit in Patterns[symbol])
                widths.Add(digit - '0');

        return new Code128Barcode(text, symbols, checksum, widths);
    }

    private static bool IsSetBCharacter(char c) => c is >= ' ' and <= '~';

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    /// <summary>Stehen an dieser Stelle zwei Ziffern (ein Paar für Set C)?</summary>
    private static bool PairAt(string text, int index) =>
        index + 1 < text.Length && IsDigit(text[index]) && IsDigit(text[index + 1]);
}
