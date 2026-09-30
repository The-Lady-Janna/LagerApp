namespace Lager.Domain.Articles;

/// <summary>
/// GTIN/EAN einer Ware (Strichcode auf der Verpackung): nur Ziffern, Länge 8 (GTIN-8/EAN-8), 12 (UPC-A), 13 (EAN-13)
/// oder 14 (GTIN-14), letzte Stelle = Prüfziffer nach GS1 (Modulo 10, Gewichte 3 und 1 von rechts).
///
/// Reine Regeln ohne Framework: <see cref="Normalize"/> (Leerraum entfernen), <see cref="IsValid"/>/<see cref="GetError"/>
/// (Prüfung samt deutscher Meldung für Validator und Domain) und <see cref="EquivalentForms"/> (dieselbe GTIN in den
/// anderen Längen: Scanner liefern einen UPC-A oft als EAN-13 mit führender Null).
/// Das Frontend spiegelt die Logik in <c>src/pages/articleEditor/gtin.ts</c>; beide Seiten müssen dieselben Ergebnisse liefern.
/// </summary>
public static class Gtin
{
    /// <summary>Erlaubte Stellenzahlen einschließlich Prüfziffer.</summary>
    public static readonly IReadOnlyList<int> AllowedLengths = new[] { 8, 12, 13, 14 };

    /// <summary>Längste erlaubte GTIN (Spaltenbreite in der Datenbank).</summary>
    public const int MaxLength = 14;

    /// <summary>
    /// Entfernt allen Leerraum (auch innerhalb: GTINs sind auf Verpackungen oft gruppiert gedruckt, "4 006381 333931").
    /// Leer oder nur Leerraum ergibt <c>null</c> (= keine GTIN). Sonst wird nichts verändert oder erraten:
    /// Bindestriche oder Buchstaben bleiben stehen und fallen bei der Prüfung durch.
    /// </summary>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var compact = new string(value.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return compact.Length == 0 ? null : compact;
    }

    /// <summary>
    /// Prüft eine GTIN. Kein Wert (leer/Leerraum) ist kein Fehler (= keine GTIN gesetzt), ergibt also <c>null</c>.
    /// Sonst die deutsche Fehlermeldung oder <c>null</c>, wenn der Wert gültig ist.
    /// </summary>
    public static string? GetError(string? value)
    {
        var gtin = Normalize(value);
        if (gtin is null) return null;

        if (!gtin.All(IsAsciiDigit))
            return "GTIN darf nur Ziffern enthalten.";
        if (!AllowedLengths.Contains(gtin.Length))
            return $"GTIN muss 8, 12, 13 oder 14 Stellen haben (angegeben: {gtin.Length}).";
        if (gtin.All(c => c == '0'))
            return "GTIN darf nicht nur aus Nullen bestehen.";

        var expected = ComputeCheckDigit(gtin[..^1]);
        if (gtin[^1] - '0' != expected)
            return $"Prüfziffer der GTIN stimmt nicht (erwartet {expected}).";
        return null;
    }

    /// <summary>Ist der Wert nach dem Normalisieren eine gültige GTIN? Leer/nur Leerraum ist keine GTIN (false).</summary>
    public static bool IsValid(string? value) => Normalize(value) is not null && GetError(value) is null;

    /// <summary>
    /// Die Prüfziffer zu den Stellen ohne Prüfziffer (7, 11, 12 oder 13 Ziffern): von rechts abwechselnd mit 3 und 1
    /// gewichten, die Summe auf das nächste Vielfache von 10 auffüllen.
    /// </summary>
    public static int ComputeCheckDigit(string digitsWithoutCheckDigit)
    {
        if (string.IsNullOrEmpty(digitsWithoutCheckDigit) || !digitsWithoutCheckDigit.All(IsAsciiDigit))
            throw new ArgumentException("Nur Ziffern erlaubt.", nameof(digitsWithoutCheckDigit));

        var sum = 0;
        var weight = 3;
        for (var i = digitsWithoutCheckDigit.Length - 1; i >= 0; i--)
        {
            sum += (digitsWithoutCheckDigit[i] - '0') * weight;
            weight = weight == 3 ? 1 : 3;
        }
        return (10 - sum % 10) % 10;
    }

    /// <summary>
    /// Alle Schreibweisen derselben GTIN: mit führenden Nullen auf 8/12/13/14 Stellen aufgefüllt bzw. auf den Kern
    /// gekürzt (UPC-A "036000291452" = EAN-13 "0036000291452" = GTIN-14 "00036000291452"). Führende Nullen ändern die
    /// Prüfziffer nicht, jede Form ist also selbst gültig. Zuerst kommt der Wert selbst; ein ungültiger Wert ergibt eine leere Liste.
    /// </summary>
    public static IReadOnlyList<string> EquivalentForms(string? value)
    {
        var gtin = Normalize(value);
        if (gtin is null || GetError(gtin) is not null) return Array.Empty<string>();

        var core = gtin.TrimStart('0');
        var forms = new List<string> { gtin };
        foreach (var length in AllowedLengths)
        {
            if (length < core.Length) continue;
            var form = core.PadLeft(length, '0');
            if (!forms.Contains(form)) forms.Add(form);
        }
        return forms;
    }

    /// <summary>Sind beide Werte dieselbe (gültige) GTIN, ggf. in unterschiedlicher Länge?</summary>
    public static bool AreEquivalent(string? a, string? b)
    {
        var forms = EquivalentForms(a);
        var other = Normalize(b);
        return other is not null && forms.Contains(other);
    }

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';
}
