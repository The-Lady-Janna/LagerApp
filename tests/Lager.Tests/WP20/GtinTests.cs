using Lager.Domain.Articles;

namespace Lager.Tests.WP20;

/// <summary>
/// Die GTIN-Regeln ohne Host: Prüfziffer nach GS1 (Modulo 10) für GTIN-8/12/13/14, Normalisierung, gleichwertige Schreibweisen.
/// Die Prüfwerte sind echte, von Hand nachgerechnete Codes - nicht mit derselben Funktion erzeugt, die sie prüft.
/// </summary>
public class GtinTests
{
    [Theory]
    [InlineData("4006381333931")]   // EAN-13
    [InlineData("73513537")]        // EAN-8
    [InlineData("036000291452")]    // UPC-A (GTIN-12)
    [InlineData("10012345678902")]  // GTIN-14
    public void Valid_codes_of_all_four_lengths_pass(string code)
    {
        Assert.True(Gtin.IsValid(code));
        Assert.Null(Gtin.GetError(code));
    }

    [Theory]
    [InlineData("4006381333932", "Prüfziffer")]      // richtige Länge, falsche Prüfziffer (erwartet 1)
    [InlineData("73513538", "Prüfziffer")]           // EAN-8, falsche Prüfziffer
    [InlineData("036000291453", "Prüfziffer")]       // UPC-A, falsche Prüfziffer
    [InlineData("10012345678903", "Prüfziffer")]     // GTIN-14, falsche Prüfziffer
    [InlineData("40063813339A1", "Ziffern")]         // Buchstabe
    [InlineData("4006-3813-33931", "Ziffern")]       // Bindestriche werden nicht erraten
    [InlineData("４００６３８１３３３９３１", "Ziffern")] // Ziffern aus anderen Zeichensätzen (Vollbreite) sind keine
    [InlineData("400638133393", "Prüfziffer")]       // 12 Stellen (UPC-A-Länge), Prüfziffer falsch (erwartet 0)
    [InlineData("123456789", "Stellen")]             // 9 Stellen
    [InlineData("12345678901", "Stellen")]           // 11 Stellen
    [InlineData("123456789012345", "Stellen")]       // 15 Stellen
    [InlineData("0000000000000", "Nullen")]          // rechnerisch gültig, aber keine GTIN
    public void Invalid_codes_are_rejected_with_a_reason(string code, string reasonContains)
    {
        Assert.False(Gtin.IsValid(code));
        var error = Gtin.GetError(code);
        Assert.NotNull(error);
        Assert.Contains(reasonContains, error);
    }

    [Fact]
    public void The_error_names_the_expected_check_digit()
    {
        Assert.Contains("erwartet 1", Gtin.GetError("4006381333932"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void No_value_is_no_error_but_also_not_a_valid_gtin(string? value)
    {
        Assert.Null(Gtin.Normalize(value));
        Assert.Null(Gtin.GetError(value));
        Assert.False(Gtin.IsValid(value));
    }

    [Fact]
    public void Normalize_removes_all_whitespace_but_changes_nothing_else()
    {
        Assert.Equal("4006381333931", Gtin.Normalize(" 4 006381 333931\t"));
        Assert.Equal("4006-3813", Gtin.Normalize("4006-3813"));
        Assert.True(Gtin.IsValid("4 006381 333931"));
    }

    [Fact]
    public void Check_digit_is_computed_with_weights_3_and_1_from_the_right()
    {
        Assert.Equal(1, Gtin.ComputeCheckDigit("400638133393"));
        Assert.Equal(7, Gtin.ComputeCheckDigit("7351353"));
        Assert.Equal(2, Gtin.ComputeCheckDigit("03600029145"));
        Assert.Equal(2, Gtin.ComputeCheckDigit("1001234567890"));
        Assert.Throws<ArgumentException>(() => Gtin.ComputeCheckDigit("40063813339x"));
    }

    [Fact]
    public void A_upc_a_is_the_same_gtin_as_its_zero_padded_ean13_and_gtin14()
    {
        var forms = Gtin.EquivalentForms("036000291452");

        Assert.Equal("036000291452", forms[0]);
        Assert.Contains("0036000291452", forms);
        Assert.Contains("00036000291452", forms);
        Assert.DoesNotContain(forms, f => f.Length == 8);
        Assert.True(Gtin.AreEquivalent("0036000291452", "036000291452"));
        Assert.True(Gtin.AreEquivalent("036000291452", "00036000291452"));
        Assert.False(Gtin.AreEquivalent("4006381333931", "036000291452"));
    }

    [Fact]
    public void An_ean8_has_the_padded_forms_but_a_full_ean13_has_no_shorter_form()
    {
        Assert.Equal(new[] { "73513537", "000073513537", "0000073513537", "00000073513537" }, Gtin.EquivalentForms("73513537"));
        Assert.Equal(new[] { "4006381333931", "04006381333931" }, Gtin.EquivalentForms("4006381333931"));
        Assert.Empty(Gtin.EquivalentForms("4006381333932"));   // ungültig
    }
}
