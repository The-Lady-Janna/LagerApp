using System.Text;
using Lager.Api.Labels;

namespace Lager.Tests.WP26;

/// <summary>
/// Der Code-128-Encoder (Set B/C, Prüfsumme mod 103, Modulfolge). Die Testvektoren stehen unverändert auch im
/// Frontend-Test (frontend/lager-ui/src/tests/WP26/code128.test.ts): beide Encoder müssen für dieselben Eingaben
/// dieselben Symbolfolgen, Prüfsummen und Modulfolgen liefern. Die Werte stammen aus einer unabhängigen
/// Referenzimplementierung (nicht aus einem der beiden Encoder).
/// </summary>
public class Code128EncoderTests
{
    /// <summary>Text, Symbolfolge (Start bis Stopp), Prüfsumme, Modulfolge ohne Ruhezonen.</summary>
    public static IEnumerable<object[]> Vectors()
    {
        // Set B durchgehend: Bestellnummer (zwei Ziffern am Ende sparen durch einen Wechsel nichts) und Lagerplatz-Code
        yield return new object[] { "ORD-DEMO-01", new[] { 104, 47, 50, 36, 13, 36, 37, 45, 47, 13, 16, 17, 11, 106 }, 11, "110100100001000111011011000101110101100010001001101110010110001000100011010001011101100010001110110100110111001001110110010011100110110001001001100011101011" };
        yield return new object[] { "A-01-01", new[] { 104, 33, 13, 16, 17, 13, 16, 17, 44, 106 }, 44, "1101001000010100011000100110111001001110110010011100110100110111001001110110010011100110100011011101100011101011" };
        yield return new object[] { "Hello, World!", new[] { 104, 40, 69, 76, 76, 79, 12, 0, 55, 79, 82, 76, 68, 1, 76, 106 }, 76, "1101001000011000101000101100100001100101000011001010000100011110101011001110011011001100111010001101000111101010010011110110010100001000010011011001101100110010100001100011101011" };
        // Set C: Ziffernfolge, Start in C
        yield return new object[] { "1234567890", new[] { 105, 12, 34, 56, 78, 90, 85, 106 }, 85, "110100111001011001110010001011000111000101101100001010011011110110100111100101100011101011" };
        yield return new object[] { "12", new[] { 105, 12, 14, 106 }, 14, "1101001110010110011100100110011101100011101011" };
        // Wechsel B -> C (sechs Ziffern am Ende), B -> C -> B, ungerade Ziffernzahl (erste Ziffer in B, Rest in C)
        yield return new object[] { "ART-123456", new[] { 104, 33, 50, 52, 13, 99, 12, 34, 56, 50, 106 }, 50, "110100100001010001100011000101110110111000101001101110010111011110101100111001000101100011100010110110001011101100011101011" };
        yield return new object[] { "A123456B", new[] { 104, 33, 99, 12, 34, 56, 100, 34, 80, 106 }, 80, "1101001000010100011000101110111101011001110010001011000111000101101011110111010001011000101001111001100011101011" };
        yield return new object[] { "12345", new[] { 104, 17, 99, 23, 45, 53, 106 }, 53, "1101001000010011100110101110111101110110111010111011000110111011101100011101011" };
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Encodes_the_known_vectors_exactly(string text, int[] symbols, int checksum, string modules)
    {
        var barcode = Code128Encoder.Encode(text);

        Assert.Equal(text, barcode.Text);
        Assert.Equal(symbols, barcode.Symbols);
        Assert.Equal(checksum, barcode.Checksum);
        Assert.Equal(checksum, barcode.Symbols[^2]);
        Assert.Equal(modules, barcode.Modules);
        Assert.Equal(modules.Length, barcode.ModuleCount);
    }

    [Fact]
    public void ORD_DEMO_01_is_start_B_data_checksum_stop()
    {
        var barcode = Code128Encoder.Encode("ORD-DEMO-01");

        // 14 Symbole: Start B, elf Zeichen, Prüfsumme, Stopp -> 13 x 11 Module + Stopp mit 13 Modulen
        Assert.Equal(Code128Encoder.StartB, barcode.Symbols[0]);
        Assert.Equal(Code128Encoder.Stop, barcode.Symbols[^1]);
        Assert.Equal(14, barcode.Symbols.Count);
        Assert.Equal(13 * 11 + 13, barcode.ModuleCount);
        // Prüfsumme von Hand: (104 + 47*1 + 50*2 + 36*3 + 13*4 + 36*5 + 37*6 + 45*7 + 47*8 + 13*9 + 16*10 + 17*11) = 1968; 1968 mod 103 = 11
        Assert.Equal(1968 % 103, barcode.Checksum);
        Assert.Equal(11, barcode.Checksum);
    }

    [Fact]
    public void Uses_the_standard_start_stop_and_known_symbol_patterns()
    {
        // Muster aus der Norm (ISO/IEC 15417), unabhängig von der Tabelle im Encoder aufgeschrieben.
        Assert.StartsWith("11010010000", Code128Encoder.Encode("A").Modules);                    // Start B
        Assert.StartsWith("11010011100", Code128Encoder.Encode("12").Modules);                   // Start C
        Assert.EndsWith("1100011101011", Code128Encoder.Encode("A").Modules);                    // Stopp (13 Module, endet mit Abschlussbalken)
        Assert.Equal("11011001100", Code128Encoder.Encode(" ").Modules[11..22]);                 // Wert 0 = Leerzeichen
        Assert.Equal("10100011000", Code128Encoder.Encode("A").Modules[11..22]);                 // Wert 33 = "A"
        Assert.Equal("10011101100", Code128Encoder.Encode("0").Modules[11..22]);                 // Wert 16 = "0"
    }

    [Fact]
    public void Every_symbol_pattern_is_well_formed_and_distinct()
    {
        // Alle 95 Zeichen in Set B, die Ziffernpaare 95..99 in Set C sowie Start- und Umschaltzeichen: jedes Symbol hat 11 Module,
        // drei Balken (gerade Zahl schwarzer Module) und kommt nur einmal vor - ein Tippfehler in der Tabelle fiele hier auf.
        var patterns = new Dictionary<string, string>();
        for (var c = ' '; c <= '~'; c++)
            patterns[$"B{c - ' '}"] = Code128Encoder.Encode(c.ToString()).Modules[11..22];              // Werte 0..94
        for (var pair = 95; pair <= 99; pair++)
            patterns[$"C{pair}"] = Code128Encoder.Encode(pair.ToString()).Modules[11..22];              // Werte 95..99 (nur in C als Paar)
        patterns["CodeB"] = Code128Encoder.Encode("A123456B").Modules[66..77];                          // Wert 100
        patterns["StartB"] = Code128Encoder.Encode("A").Modules[..11];                                  // Wert 104
        patterns["StartC"] = Code128Encoder.Encode("12").Modules[..11];                                 // Wert 105

        foreach (var (name, bits) in patterns)
        {
            Assert.True(bits.Length == 11, $"{name}: {bits}");
            Assert.True(bits.Count(b => b == '1') % 2 == 0, $"{name}: ungerade Zahl schwarzer Module");
            Assert.True(RunLengths(bits).Count(r => r.Bar) == 3, $"{name}: nicht drei Balken");
        }
        Assert.Equal(patterns.Count, patterns.Values.Distinct().Count());
        Assert.Equal(95 + 5 + 3, patterns.Count);

        // Ziffernpaare 00..94 sind dieselben Symbole wie die B-Zeichen mit gleichem Wert, "Code C" (99) dasselbe wie das Paar 99.
        for (var pair = 0; pair <= 94; pair++)
            Assert.Equal(patterns[$"B{pair}"], Code128Encoder.Encode(pair.ToString("D2")).Modules[11..22]);
        Assert.Equal(patterns["C99"], Code128Encoder.Encode("A123456B").Modules[22..33]);
    }

    [Fact]
    public void Bars_are_the_black_runs_of_the_module_sequence()
    {
        var barcode = Code128Encoder.Encode("ORD-DEMO-01");
        var fromModules = RunLengths(barcode.Modules).Where(r => r.Bar).Select(r => (r.Start, r.Length)).ToList();

        Assert.Equal(fromModules, barcode.Bars().ToList());
        Assert.Equal(43, barcode.Bars().Count());                                 // 13 Symbole x 3 Balken + 4 Balken im Stopp
        Assert.Equal(barcode.ModuleCount, barcode.Widths.Sum());
    }

    [Fact]
    public void Chooses_the_shortest_sequence_and_prefers_B_on_a_tie()
    {
        // "12": Start C spart ein Symbol gegenüber Start B
        Assert.Equal(new[] { 105, 12 }, Code128Encoder.Encode("12").Symbols.Take(2));
        // "A1234B": ein Wechsel nach C und zurück kostet zwei Symbole und spart zwei - Gleichstand, also in B bleiben
        Assert.DoesNotContain(Code128Encoder.CodeC, Code128Encoder.Encode("A1234B").Symbols.Take(7));   // ohne Prüfsumme und Stopp
        // "A123456B": sechs Ziffern rechtfertigen den Wechsel
        Assert.Contains(Code128Encoder.CodeC, Code128Encoder.Encode("A123456B").Symbols);
        // Ziffernfolge mit gerader Länge am Anfang startet in C, ungerade beginnt mit der ersten Ziffer in B
        Assert.Equal(Code128Encoder.StartC, Code128Encoder.Encode("123456").Symbols[0]);
        Assert.Equal(Code128Encoder.StartB, Code128Encoder.Encode("1234567").Symbols[0]);
    }

    [Fact]
    public void Symbols_decode_back_to_the_text_for_many_strings()
    {
        // Zufällige Texte (deterministisch), stark ziffernlastig, damit alle Wechsel vorkommen: die Symbole müssen nach den
        // Regeln von Set B/C wieder genau den Text ergeben, die Prüfsumme stimmen und nie länger sein als reines Set B.
        var random = new Random(26);
        const string alphabet = "0123456789012345678901234567890123456789ABCxyz-/. ~";
        for (var round = 0; round < 500; round++)
        {
            var text = new string(Enumerable.Range(0, random.Next(1, 25)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            var barcode = Code128Encoder.Encode(text);

            Assert.Equal(text, DecodeSymbols(barcode.Symbols));
            Assert.True(barcode.Symbols.Count <= text.Length + 3, $"'{text}' ist länger als in reinem Set B");
            Assert.Equal(11 * (barcode.Symbols.Count - 1) + 13, barcode.ModuleCount);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("Größe")]
    [InlineData("Tab\there")]
    [InlineData("Zeile\nzwei")]
    [InlineData("\u007f")]
    public void Rejects_text_that_Code128_B_and_C_cannot_represent(string text)
    {
        Assert.False(Code128Encoder.CanEncode(text));
        Assert.Throws<ArgumentException>(() => Code128Encoder.Encode(text));
    }

    [Fact]
    public void Reports_the_offending_character()
    {
        var error = Assert.Throws<ArgumentException>(() => Code128Encoder.Encode("BIN-Ü"));

        Assert.Contains("U+00DC", error.Message);
        Assert.False(Code128Encoder.CanEncode(null));
        Assert.True(Code128Encoder.CanEncode(" ~"));
    }

    // ---- Hilfen ---------------------------------------------------------------------------------------------------

    private static List<(bool Bar, int Start, int Length)> RunLengths(string bits)
    {
        var runs = new List<(bool, int, int)>();
        var start = 0;
        for (var i = 1; i <= bits.Length; i++)
        {
            if (i < bits.Length && bits[i] == bits[start]) continue;
            runs.Add((bits[start] == '1', start, i - start));
            start = i;
        }
        return runs;
    }

    /// <summary>Liest eine Symbolfolge nach den Regeln von Code 128 (Start, Umschalter, Prüfsumme, Stopp) zurück in Text.</summary>
    private static string DecodeSymbols(IReadOnlyList<int> symbols)
    {
        Assert.Equal(Code128Encoder.Stop, symbols[^1]);
        var checksum = symbols[0];
        for (var i = 1; i < symbols.Count - 2; i++) checksum += symbols[i] * i;
        Assert.Equal(checksum % 103, symbols[^2]);

        var set = symbols[0] switch
        {
            Code128Encoder.StartB => 'B',
            Code128Encoder.StartC => 'C',
            var other => throw new InvalidOperationException($"Startzeichen {other}"),
        };
        var text = new StringBuilder();
        foreach (var value in symbols.Skip(1).Take(symbols.Count - 3))
        {
            if (set == 'B' && value == Code128Encoder.CodeC) set = 'C';
            else if (set == 'C' && value == Code128Encoder.CodeB) set = 'B';
            else if (set == 'C') text.Append(value.ToString("D2"));
            else text.Append((char)(value + ' '));
        }
        return text.ToString();
    }
}
