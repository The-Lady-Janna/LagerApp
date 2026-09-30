using System.Text.RegularExpressions;
using Lager.Api.Seeding;
using Lager.Domain.Articles;
using Lager.Domain.Customers;

namespace Lager.Tests.WP27;

/// <summary>
/// Der feste Inhalt des Demo-Datensatzes (Katalog): vollständig, in sich stimmig und neutral - keine echten Firmen, Marken,
/// Personen oder Adressen, keine festen Passwörter. Reine Prüfung der Konstanten, ohne Datenbank.
/// </summary>
public class DemoCatalogTests
{
    private static readonly string SeedingDirectory = Path.Combine(RepoRoot.Path, "src", "Lager.Api", "Seeding");

    private static IEnumerable<(string Name, string Text)> SeedingSources() =>
        Directory.GetFiles(SeedingDirectory, "*.cs").Select(f => (Path.GetFileName(f), File.ReadAllText(f)));

    // ---- Umfang --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Der_Katalog_hat_mindestens_40_Artikel_3_Lieferanten_und_5_Kunden_mit_Adressen()
    {
        Assert.True(DemoCatalog.Articles.Count >= 40, $"Nur {DemoCatalog.Articles.Count} Artikel");
        Assert.Equal(3, DemoCatalog.Suppliers.Count);
        Assert.True(DemoCatalog.Customers.Count >= 5);
        Assert.All(DemoCatalog.Customers, c =>
        {
            Assert.NotEmpty(c.Addresses);
            Assert.Contains(c.Addresses, a => a.Kind != AddressKind.Billing);   // jeder Kunde hat eine Lieferadresse
        });
    }

    [Fact]
    public void Artikel_haben_eindeutige_SKUs_Preise_Schwellwerte_und_Abmessungen()
    {
        Assert.Equal(DemoCatalog.Articles.Count, DemoCatalog.Articles.Select(a => a.Sku).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        foreach (var a in DemoCatalog.Articles.Where(a => !a.IsBundle))
        {
            Assert.True(a.PriceCents > 0, $"{a.Sku}: Preis fehlt");
            Assert.True(a.MinStock > 0 && a.MinStock <= a.ReorderPoint && a.ReorderPoint <= a.MaxStock,
                $"{a.Sku}: Min/Reorder/Max müssen 0 < Min <= Reorder <= Max erfüllen ({a.MinStock}/{a.ReorderPoint}/{a.MaxStock})");
            Assert.True(a.LengthMm > 0 && a.WidthMm > 0 && a.HeightMm > 0 && a.WeightGrams > 0, $"{a.Sku}: Abmessungen/Gewicht fehlen");
            Assert.InRange(a.SupplierIndex, 0, DemoCatalog.Suppliers.Count - 1);
        }
    }

    [Fact]
    public void Es_gibt_ein_Bundle_Saisonware_Alt_SKUs_und_Artikel_mit_gueltiger_GTIN()
    {
        var bySku = DemoCatalog.Articles.ToDictionary(a => a.Sku, StringComparer.OrdinalIgnoreCase);

        var bundles = DemoCatalog.Articles.Where(a => a.IsBundle).ToList();
        Assert.NotEmpty(bundles);
        foreach (var bundle in bundles)
            Assert.All(bundle.Components, c =>
            {
                Assert.True(bySku.TryGetValue(c.Sku, out var component), $"Komponente {c.Sku} von {bundle.Sku} fehlt im Katalog");
                Assert.False(component!.IsBundle, "Bundles in Bundles sind hier nicht vorgesehen");
                Assert.True(c.Quantity > 0);
            });

        Assert.Contains(DemoCatalog.Articles, a => a.SeasonFromDays is not null || a.SeasonUntilDays is not null);
        Assert.Contains(DemoCatalog.Articles, a => a.AlternativeSkus.Count > 0);
        foreach (var a in DemoCatalog.Articles.Where(a => a.AlternativeSkus.Count > 0))
            Assert.All(a.AlternativeSkus, alt =>
            {
                Assert.True(bySku.ContainsKey(alt), $"Alt-SKU {alt} von {a.Sku} ist kein Artikel des Katalogs");
                Assert.NotEqual(a.Sku, alt, StringComparer.OrdinalIgnoreCase);
            });

        var gtins = DemoCatalog.Articles.Where(a => a.Gtin is not null).Select(a => a.Gtin!).ToList();
        Assert.True(gtins.Count >= 5, "Einige Artikel sollen eine GTIN tragen");
        Assert.All(gtins, g => Assert.True(Gtin.IsValid(g), $"GTIN {g} ist ungültig"));
        Assert.Equal(gtins.Count, gtins.Distinct().Count());
        // Interne Nummern (Präfix 20-29): gehören keinem Hersteller, also keine echte Produktnummer.
        Assert.All(gtins, g => Assert.Matches("^2[0-9]", g));
    }

    [Fact]
    public void Es_gibt_Hot_Pick_und_Reserve_Artikel_Ladenhueter_und_die_Nachschub_Schwellen_sind_gesetzt()
    {
        var hot = DemoCatalog.Articles.Where(a => a.Placement == DemoPlacement.Hot).ToList();
        Assert.True(hot.Count >= 4);
        Assert.All(hot, a => Assert.True(a.ReplenishmentThreshold > 0, $"{a.Sku}: Nachschub-Schwelle fehlt"));
        Assert.Contains(DemoCatalog.Articles, a => a.Placement == DemoPlacement.Reserve);
        Assert.Contains(DemoCatalog.Articles, a => a.Demand == DemoDemand.None && !a.IsBundle);   // Dead-Stock
    }

    [Fact]
    public void Mindestens_drei_Chargen_laufen_in_30_Tagen_ab_und_eine_ist_abgelaufen()
    {
        var expiring = DemoCatalog.OpeningLots.Count(l => l.ExpiryInDays is >= 0 and <= 30 && l.Quantity > 0);
        Assert.True(expiring >= 3, $"Nur {expiring} Chargen laufen in den nächsten 30 Tagen ab");
        Assert.Contains(DemoCatalog.OpeningLots, l => l.ExpiryInDays < 0 && l.Quantity > 0);

        var bySku = DemoCatalog.Articles.ToDictionary(a => a.Sku, StringComparer.OrdinalIgnoreCase);
        Assert.All(DemoCatalog.OpeningLots, l =>
        {
            Assert.True(bySku.TryGetValue(l.Sku, out var article), $"Charge {l.LotNumber}: Artikel {l.Sku} fehlt");
            Assert.NotNull(article!.ShelfLifeDays);
            Assert.True(l.ReceivedDaysAgo > 60, "Chargen des Altbestands sind älter als das Beobachtungsfenster");
        });
        Assert.Equal(DemoCatalog.OpeningLots.Count, DemoCatalog.OpeningLots.Select(l => (l.Sku, l.LotNumber)).Distinct().Count());
    }

    [Fact]
    public void Es_gibt_Demo_Benutzer_je_Rolle_und_mehrere_Picker_ohne_feste_Passwoerter()
    {
        var roles = DemoCatalog.Users.Select(u => u.Role).ToHashSet();
        foreach (var role in new[] { "Picker", "Packer", "Receiver", "Viewer", "Manager" })
            Assert.Contains(role, roles);
        Assert.DoesNotContain("Admin", roles);   // Admin bleibt der Bootstrap-Admin

        foreach (var name in new[] { "picker", "packer", "receiver", "viewer", "manager" })
            Assert.Contains(DemoCatalog.Users, u => u.Username == name);

        Assert.Equal(DemoCatalog.Users.Select(u => u.Username).Distinct().Count(), DemoCatalog.Users.Count);
        Assert.True(DemoCatalog.Pickers.Count >= 2);
        Assert.All(DemoCatalog.Pickers, p => Assert.Contains(DemoCatalog.Users, u => u.Username == p && u.Role == "Picker"));
        // Die Spezifikation trägt gar kein Passwortfeld.
        Assert.DoesNotContain(typeof(DemoUserSpec).GetProperties(), p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    // ---- Neutralität ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Lieferanten_und_Kunden_haben_nur_example_com_Adressen()
    {
        var mails = DemoCatalog.Suppliers.Select(s => s.Email).Concat(DemoCatalog.Customers.Select(c => c.Email)).ToList();
        Assert.All(mails, m => Assert.EndsWith(".example.com", m));
    }

    [Fact]
    public void Der_Seeding_Quelltext_enthaelt_keine_festen_Passwoerter()
    {
        foreach (var (name, text) in SeedingSources())
            Assert.False(Regex.IsMatch(text, "ChangeMe|password123|passwort123", RegexOptions.IgnoreCase),
                $"{name} enthält ein festes Passwort");
    }

    [Fact]
    public void Der_Seeding_Quelltext_nennt_nur_example_com_Mailadressen_und_keine_echten_Marken_oder_Firmen()
    {
        // Eine Auswahl bekannter Firmen und Marken (Werkzeug, Baumarkt, Versand, Handel): der Katalog darf keine nennen.
        var real = new Regex(
            @"\b(Bosch|Makita|Hilti|W(ü|ue)rth|Stanley|DeWalt|Festool|Metabo|Hornbach|Bauhaus|Ikea|Amazon|Ebay|" +
            @"DHL|FedEx|Siemens|Henkel|Tesa|Uhu|Pattex|Loctite|Sikaflex|Wolfcraft|Knipex|Torx|Post-it|Leitz|Pelikan|" +
            @"Google|Microsoft|Samsung|Osram|Duracell|Varta)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var mail = new Regex(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}");

        foreach (var (name, text) in SeedingSources())
        {
            // Kommentare und Erläuterungen dürfen Marken erwähnen (z. B. "DHL" in einem Hinweis) - die Daten nicht. Geprüft wird
            // deshalb nur der Katalog, der die Inhalte trägt; alle Mailadressen aller Dateien müssen example.com sein.
            foreach (Match m in mail.Matches(text))
                Assert.EndsWith("example.com", m.Value, StringComparison.OrdinalIgnoreCase);
            if (name == "DemoCatalog.cs")
                Assert.False(real.IsMatch(text), $"{name} nennt eine echte Firma oder Marke: {real.Match(text).Value}");
        }
    }

    [Fact]
    public void Kein_Katalogtext_nennt_eine_echte_Marke()
    {
        var real = new Regex(
            @"\b(Bosch|Makita|Hilti|W(ü|ue)rth|Stanley|DeWalt|Festool|Metabo|Hornbach|Bauhaus|Ikea|Amazon|DHL|Torx|Post-it|Duracell|Varta|Osram)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        var texts = DemoCatalog.Articles.SelectMany(a => new[] { a.Name, a.Description ?? "" })
            .Concat(DemoCatalog.Suppliers.SelectMany(s => new[] { s.Name, s.Notes }))
            .Concat(DemoCatalog.Customers.SelectMany(c => new[] { c.Name, c.Notes }.Concat(c.Addresses.SelectMany(a => new[] { a.Street, a.City, a.Label }))));
        Assert.All(texts, t => Assert.False(real.IsMatch(t), $"'{t}' nennt eine echte Marke"));
    }
}
