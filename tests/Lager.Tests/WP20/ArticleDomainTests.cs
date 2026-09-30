using Lager.Domain.Articles;

namespace Lager.Tests.WP20;

/// <summary>
/// Die Regeln am Artikel selbst: Saison-Fenster (der letzte Gültigkeitstag gilt noch), GTIN setzen/entfernen, Bundle-Komponenten
/// (keine Duplikate, bei Fehler bleibt der bisherige Stand), Suche über alle Kennungen.
/// </summary>
public class ArticleDomainTests
{
    private static Article NewArticle(string sku = "ART-1", string name = "Schraube M8") =>
        new(sku, name, new Dimensions(10, 10, 10), 100, StackingInfo.NotStackable);

    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) =>
        new(year, month, day, hour, minute, second, DateTimeKind.Utc);

    // ---- Saison-Fenster (Übernahme WP13-Review) --------------------------

    [Fact]
    public void The_last_valid_day_is_still_valid_until_the_end_of_that_day()
    {
        var article = NewArticle();
        article.SetSeasonWindow(null, Utc(2026, 9, 30));   // ein Datum: Mitternacht

        Assert.True(article.IsCurrentlyActive(Utc(2026, 9, 29, 12)));
        Assert.True(article.IsCurrentlyActive(Utc(2026, 9, 30)));
        Assert.True(article.IsCurrentlyActive(Utc(2026, 9, 30, 12)));
        Assert.True(article.IsCurrentlyActive(Utc(2026, 9, 30, 23, 59, 59)));
        Assert.False(article.IsCurrentlyActive(Utc(2026, 10, 1)));
        Assert.False(article.IsCurrentlyActive(Utc(2026, 10, 1, 8)));
    }

    [Fact]
    public void The_start_of_the_season_counts_from_the_first_day()
    {
        var article = NewArticle();
        article.SetSeasonWindow(Utc(2026, 3, 1), null);

        Assert.False(article.IsCurrentlyActive(Utc(2026, 2, 28, 23, 59, 59)));
        Assert.True(article.IsCurrentlyActive(Utc(2026, 3, 1)));
        Assert.True(article.IsCurrentlyActive(Utc(2030, 1, 1)));
    }

    [Fact]
    public void A_one_day_window_is_valid_and_an_end_before_the_start_is_rejected()
    {
        var article = NewArticle();

        article.SetSeasonWindow(Utc(2026, 5, 5, 14), Utc(2026, 5, 5));    // dieselbe Kalendertag, Uhrzeit egal
        Assert.True(article.IsCurrentlyActive(Utc(2026, 5, 5, 20)));
        Assert.False(article.IsCurrentlyActive(Utc(2026, 5, 6)));

        var ex = Assert.Throws<ArgumentException>(() => article.SetSeasonWindow(Utc(2026, 5, 5), Utc(2026, 5, 4)));
        Assert.Contains("ValidUntil", ex.Message);
    }

    [Fact]
    public void Without_a_window_the_article_is_always_active()
    {
        Assert.True(NewArticle().IsCurrentlyActive(Utc(1999, 1, 1)));
        Assert.True(NewArticle().IsCurrentlyActive());
    }

    // ---- GTIN ------------------------------------------------------------

    [Fact]
    public void A_valid_gtin_is_stored_without_whitespace_and_an_empty_value_removes_it()
    {
        var article = NewArticle();

        article.SetGtin(" 4 006381 333931 ");
        Assert.Equal("4006381333931", article.Gtin);

        article.SetGtin("   ");
        Assert.Null(article.Gtin);
    }

    [Fact]
    public void An_invalid_gtin_is_rejected_and_the_old_value_stays()
    {
        var article = NewArticle();
        article.SetGtin("4006381333931");

        var ex = Assert.Throws<ArgumentException>(() => article.SetGtin("4006381333932"));

        Assert.Contains("Prüfziffer", ex.Message);
        Assert.Equal("4006381333931", article.Gtin);
    }

    // ---- Bundle ----------------------------------------------------------

    [Fact]
    public void Bundle_components_reject_self_reference_duplicates_and_zero_quantity_and_keep_the_old_list_on_error()
    {
        var bundle = NewArticle("KIT");
        var a = NewArticle("A");
        var b = NewArticle("B");
        bundle.ReplaceBundleComponents(new[] { (a.Id, 2) });

        Assert.Throws<InvalidOperationException>(() => bundle.ReplaceBundleComponents(new[] { (bundle.Id, 1) }));
        Assert.Throws<ArgumentException>(() => bundle.ReplaceBundleComponents(new[] { (b.Id, 1), (b.Id, 2) }));
        Assert.Throws<ArgumentException>(() => bundle.ReplaceBundleComponents(new[] { (b.Id, 0) }));

        var kept = Assert.Single(bundle.BundleComponents);
        Assert.Equal((a.Id, 2), (kept.ComponentArticleId, kept.Quantity));
    }

    // ---- Suche ------------------------------------------------------------

    [Fact]
    public void Search_finds_name_sku_alternative_sku_and_gtin_case_insensitively()
    {
        var article = NewArticle("SKU-ÄPFEL-1", "Äpfel Bio");
        article.SetAlternatives(new[] { "ALT-BIRNE-7", "Lieferant-42" });
        article.SetGtin("4006381333931");

        Assert.True(article.MatchesSearch("äpfel"));            // Name, Umlaut, Schreibweise egal
        Assert.True(article.MatchesSearch("sku-äpfel"));        // SKU
        Assert.True(article.MatchesSearch("birne"));            // Alternativ-SKU (Teilstring)
        Assert.True(article.MatchesSearch("lieferant-42"));
        Assert.True(article.MatchesSearch("4006381333931"));    // GTIN komplett
        Assert.True(article.MatchesSearch("4006 3813"));        // GTIN teilweise, mit Leerraum getippt
        Assert.True(article.MatchesSearch("  "));               // leer trifft alles
        Assert.False(article.MatchesSearch("birnen"));
        Assert.False(article.MatchesSearch("9999"));
    }

    [Fact]
    public void Code_matching_ignores_case_and_surrounding_whitespace_and_knows_equivalent_gtin_forms()
    {
        var article = NewArticle("Abc-1");
        article.SetAlternatives(new[] { "Alt-9" });
        article.SetGtin("036000291452");

        Assert.True(article.HasSku("  aBC-1 "));
        Assert.False(article.HasSku("abc"));
        Assert.True(article.HasAlternativeSku(" alt-9"));
        Assert.False(article.HasAlternativeSku("alt"));
        Assert.True(article.HasGtin("0036000291452"));   // UPC-A als EAN-13 gescannt
        Assert.False(article.HasGtin("4006381333931"));
    }
}
