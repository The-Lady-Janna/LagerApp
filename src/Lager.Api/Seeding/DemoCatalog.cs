using Lager.Domain.Articles;
using Lager.Domain.Customers;
using Lager.Domain.Warehouse;

namespace Lager.Api.Seeding;

/// <summary>Wie oft ein Artikel bestellt wird; der Zahlenwert ist das Gewicht bei der Zufallsauswahl der Bestellpositionen.</summary>
public enum DemoDemand
{
    /// <summary>Wird in der Historie nie bestellt (Ladenhüter, Altbestand): erscheint in der Dead-Stock-Auswertung.</summary>
    None = 0,
    Slow = 1,
    Medium = 3,
    Fast = 10,
}

/// <summary>Wo der Artikel liegt: Hot-Pick-Platz (mit Reserve-Platz im Reservelager), Standardplatz oder nur im Reservelager.</summary>
public enum DemoPlacement
{
    Standard = 0,
    Hot = 1,
    Reserve = 2,
}

/// <summary>Ein Demo-Artikel. Alle Inhalte sind erfunden (keine echten Marken, Firmen oder Personen).</summary>
public sealed record DemoArticleSpec
{
    public required string Sku { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }

    public int LengthMm { get; init; }
    public int WidthMm { get; init; }
    public int HeightMm { get; init; }
    public int WeightGrams { get; init; }
    public bool Stackable { get; init; } = true;

    /// <summary>Einkaufspreis in Cent.</summary>
    public int PriceCents { get; init; }
    public int MinStock { get; init; }
    public int ReorderPoint { get; init; }
    public int MaxStock { get; init; }

    /// <summary>Index in <see cref="DemoCatalog.Suppliers"/>.</summary>
    public int SupplierIndex { get; init; }

    /// <summary>Gültige GTIN (EAN-13 aus dem Bereich für interne Nummern, gehört keinem Hersteller) oder null.</summary>
    public string? Gtin { get; init; }
    public IReadOnlyList<string> AlternativeSkus { get; init; } = Array.Empty<string>();

    public DemoDemand Demand { get; init; } = DemoDemand.Medium;
    /// <summary>Menge je Bestellposition (untere/obere Grenze, in Schritten von <see cref="QuantityStep"/>).</summary>
    public int QuantityMin { get; init; } = 1;
    public int QuantityMax { get; init; } = 1;
    public int QuantityStep { get; init; } = 1;

    public DemoPlacement Placement { get; init; } = DemoPlacement.Standard;
    /// <summary>Nachschub-Schwelle des Hot-Pick-Platzes (nur <see cref="DemoPlacement.Hot"/>).</summary>
    public int ReplenishmentThreshold { get; init; }

    /// <summary>Restlaufzeit einer neuen Lieferung in Tagen; null = Ware ohne Chargen/MHD.</summary>
    public int? ShelfLifeDays { get; init; }

    /// <summary>Saison-Fenster relativ zu heute (Tage); null = ganzjährig bzw. offen.</summary>
    public int? SeasonFromDays { get; init; }
    public int? SeasonUntilDays { get; init; }

    /// <summary>Bestellvielfaches beim Lieferanten (Bestellmengen werden darauf aufgerundet).</summary>
    public int OrderMultiple { get; init; } = 1;

    /// <summary>Komponenten (SKU und Menge), wenn der Artikel ein Bundle ist; sonst leer.</summary>
    public IReadOnlyList<(string Sku, int Quantity)> Components { get; init; } = Array.Empty<(string, int)>();

    public bool IsBundle => Components.Count > 0;
}

/// <summary>Ein erfundener Lieferant (Domain example.com).</summary>
public sealed record DemoSupplierSpec(string Code, string Name, string Email, string Phone, int LeadTimeDays, int MinOrderValueCents, string Notes);

public sealed record DemoAddressSpec(AddressKind Kind, string Label, string Street, string? Street2, string Zip, string City);

/// <summary>Ein erfundener Kunde (Domain example.com) mit Adressen.</summary>
public sealed record DemoCustomerSpec(
    string Code, string Name, string Email, string Phone, int DiscountPercent, string Notes, IReadOnlyList<DemoAddressSpec> Addresses);

/// <summary>Eine vorab eingelagerte Charge (Altbestand), relativ zu heute: MHD in <see cref="ExpiryInDays"/> Tagen, eingegangen vor <see cref="ReceivedDaysAgo"/> Tagen.</summary>
public sealed record DemoLotSpec(string Sku, string LotNumber, int ExpiryInDays, int Quantity, int ReceivedDaysAgo);

/// <summary>Ein Demo-Benutzer (Rolle steckt im Namen; das Passwort wird beim Seeding zufällig erzeugt).</summary>
public sealed record DemoUserSpec(string Username, string DisplayName, string Role);

/// <summary>
/// Der feste Inhalt des Demo-Datensatzes: Artikel, Lieferanten, Kunden, Chargen und Benutzer. Alles ist erfunden und neutral
/// (keine echten Firmen, Marken, Personen oder Adressen; Mail-Domains nur <c>example.com</c>). Der Zufall der Historie steckt im
/// <see cref="DemoHistoryGenerator"/> mit festem Startwert - der Katalog selbst ist reine Konstante.
/// </summary>
public static class DemoCatalog
{
    public static IReadOnlyList<DemoSupplierSpec> Suppliers { get; } = new[]
    {
        new DemoSupplierSpec("LIEF-01", "Muster Befestigungstechnik GmbH", "vertrieb@muster-befestigung.example.com", "+49 30 0000 1101", 5, 15_000,
            "Fiktiver Demo-Lieferant: Schrauben, Muttern, Elektro."),
        new DemoSupplierSpec("LIEF-02", "Beispiel Werkzeug & Bedarf KG", "einkauf@beispiel-werkzeug.example.com", "+49 30 0000 1102", 7, 25_000,
            "Fiktiver Demo-Lieferant: Werkzeug, Farben, Saisonware."),
        new DemoSupplierSpec("LIEF-03", "Demo Verpackung & Büro AG", "bestellung@demo-verpackung.example.com", "+49 30 0000 1103", 4, 10_000,
            "Fiktiver Demo-Lieferant: Verpackung und Bürobedarf."),
    };

    public static IReadOnlyList<DemoCustomerSpec> Customers { get; } = new[]
    {
        new DemoCustomerSpec("KD-1001", "Beispiel Bau GmbH", "bestellung@beispiel-bau.example.com", "+49 30 0000 2201", 5,
            "Fiktiver Demo-Kunde: Baustellenbelieferung, feste Lieferadresse.",
            new[]
            {
                new DemoAddressSpec(AddressKind.Shipping, "Baustelle Nord", "Musterweg 12", null, "00101", "Beispielstadt"),
                new DemoAddressSpec(AddressKind.Billing, "Verwaltung", "Beispielstraße 1", "2. Etage", "00101", "Beispielstadt"),
            }),
        new DemoCustomerSpec("KD-1002", "Testwerkstatt Nord", "info@testwerkstatt-nord.example.com", "+49 30 0000 2202", 0,
            "Fiktiver Demo-Kunde: kleine Werkstatt, bestellt häufig kleine Mengen.",
            new[] { new DemoAddressSpec(AddressKind.Both, "Werkstatt", "Demoallee 17", null, "00202", "Musterhausen") }),
        new DemoCustomerSpec("KD-1003", "Demo Handwerk Süd OHG", "office@demo-handwerk.example.com", "+49 30 0000 2203", 10,
            "Fiktiver Demo-Kunde: Rahmenvertrag mit 10 % Rabatt.",
            new[]
            {
                new DemoAddressSpec(AddressKind.Shipping, "Lager", "Probestraße 5", "Halle B", "00303", "Testdorf"),
                new DemoAddressSpec(AddressKind.Both, "Büro", "Probestraße 3", null, "00303", "Testdorf"),
            }),
        new DemoCustomerSpec("KD-1004", "Probe Hausverwaltung GmbH", "service@probe-hausverwaltung.example.com", "+49 30 0000 2204", 0,
            "Fiktiver Demo-Kunde: Objektbelieferung, mehrere Liegenschaften.",
            new[] { new DemoAddressSpec(AddressKind.Both, "Hausmeisterei", "Demoring 8", null, "00404", "Demoburg") }),
        new DemoCustomerSpec("KD-1005", "Schreinerei Beispielholz", "kontakt@beispielholz.example.com", "+49 30 0000 2205", 5,
            "Fiktiver Demo-Kunde: Schreinerei, Großabnehmer für Befestigungsmaterial.",
            new[] { new DemoAddressSpec(AddressKind.Both, "Werkhof", "Musterallee 21", null, "00505", "Probeheim") }),
    };

    /// <summary>
    /// Chargen des Altbestands: mehrere laufen in den nächsten 30 Tagen ab, eine ist abgelaufen. Sie gehören zu Artikeln mit
    /// geringer oder ohne Nachfrage, damit sie am Ende der Historie noch Bestand haben (FEFO würde sie sonst aufbrauchen).
    /// </summary>
    public static IReadOnlyList<DemoLotSpec> OpeningLots { get; } = new[]
    {
        new DemoLotSpec("FRB-4002", "MK-2411-A", ExpiryInDays: 9, Quantity: 30, ReceivedDaysAgo: 120),
        new DemoLotSpec("FRB-4002", "MK-2503-B", ExpiryInDays: 27, Quantity: 40, ReceivedDaysAgo: 90),
        new DemoLotSpec("FRB-4003", "SD-2410-A", ExpiryInDays: 14, Quantity: 24, ReceivedDaysAgo: 100),
        new DemoLotSpec("ELK-5003", "BT-2408-C", ExpiryInDays: 21, Quantity: 36, ReceivedDaysAgo: 110),
        new DemoLotSpec("FRB-4008", "HL-2311-A", ExpiryInDays: -12, Quantity: 8, ReceivedDaysAgo: 140),
        new DemoLotSpec("FRB-4008", "HL-2402-B", ExpiryInDays: 5, Quantity: 4, ReceivedDaysAgo: 135),
    };

    /// <summary>
    /// Demo-Benutzer: je Rolle einer, dazu zwei weitere Picker, damit die Picker-Performance mehrere Zeilen zeigt. Die Namen sind
    /// zugleich die Picker in der Historie. Passwörter gibt es hier nicht: sie entstehen beim Seeding zufällig.
    /// </summary>
    public static IReadOnlyList<DemoUserSpec> Users { get; } = new[]
    {
        new DemoUserSpec("picker", "Demo Picker", "Picker"),
        new DemoUserSpec("picker2", "Demo Picker 2", "Picker"),
        new DemoUserSpec("picker3", "Demo Picker 3", "Picker"),
        new DemoUserSpec("packer", "Demo Packer", "Packer"),
        new DemoUserSpec("receiver", "Demo Wareneingang", "Receiver"),
        new DemoUserSpec("viewer", "Demo Betrachter", "Viewer"),
        new DemoUserSpec("manager", "Demo Lagerleitung", "Manager"),
    };

    /// <summary>Die Picker der Historie (Benutzernamen der Demo-Picker).</summary>
    public static IReadOnlyList<string> Pickers { get; } = new[] { "picker", "picker2", "picker3" };

    public static IReadOnlyList<DemoArticleSpec> Articles { get; } = BuildArticles();

    // ------------------------------------------------------------------------------------------------------------------
    // Artikel
    // ------------------------------------------------------------------------------------------------------------------

    private static IReadOnlyList<DemoArticleSpec> BuildArticles()
    {
        var list = new List<DemoArticleSpec>();
        var gtinCounter = 0;

        // Kurzform für die Tabelle unten; GTINs vergibt ein Zähler im Bereich 200... (interne Nummern, gehören keinem Hersteller).
        DemoArticleSpec Add(
            string sku, string name, int l, int w, int h, int weightG, int priceCents, int min, int reorder, int max,
            int supplier, DemoDemand demand, int qMin, int qMax, DemoPlacement placement,
            string? desc = null, int hotThreshold = 0, bool stackable = true, int? shelfLife = null,
            int? seasonFrom = null, int? seasonUntil = null, bool gtin = false, string[]? alt = null, int step = 1, int multiple = 1)
        {
            string? gtinValue = null;
            if (gtin)
            {
                var baseDigits = "2000000" + (++gtinCounter).ToString("D5");
                gtinValue = baseDigits + Gtin.ComputeCheckDigit(baseDigits);
            }

            var spec = new DemoArticleSpec
            {
                Sku = sku, Name = name, Description = desc,
                LengthMm = l, WidthMm = w, HeightMm = h, WeightGrams = weightG, Stackable = stackable,
                PriceCents = priceCents, MinStock = min, ReorderPoint = reorder, MaxStock = max,
                SupplierIndex = supplier, Gtin = gtinValue, AlternativeSkus = alt ?? Array.Empty<string>(),
                Demand = demand, QuantityMin = qMin, QuantityMax = qMax, QuantityStep = step,
                Placement = placement, ReplenishmentThreshold = hotThreshold,
                ShelfLifeDays = shelfLife, SeasonFromDays = seasonFrom, SeasonUntilDays = seasonUntil, OrderMultiple = multiple,
            };
            list.Add(spec);
            return spec;
        }

        const DemoDemand Fast = DemoDemand.Fast, Med = DemoDemand.Medium, Slow = DemoDemand.Slow, None = DemoDemand.None;
        const DemoPlacement Hot = DemoPlacement.Hot, Std = DemoPlacement.Standard, Res = DemoPlacement.Reserve;

        // ---- Befestigung (Lieferant 0) ----
        Add("BEF-1001", "Sechskantschraube M6x20 verzinkt, 100er Pack", 120, 80, 50, 420, 490, 40, 90, 300, 0, Fast, 2, 12, Hot,
            hotThreshold: 40, gtin: true, multiple: 10);
        Add("BEF-1002", "Sechskantmutter M6 verzinkt, 100er Pack", 110, 80, 45, 300, 390, 40, 90, 300, 0, Fast, 2, 12, Hot,
            hotThreshold: 40, gtin: true, multiple: 10);
        Add("BEF-1003", "Unterlegscheibe M6 verzinkt, 200er Pack", 110, 80, 40, 260, 320, 40, 90, 300, 0, Fast, 2, 15, Hot,
            hotThreshold: 40, multiple: 10);
        Add("BEF-1004", "Holzschraube 4x40 Innensechsrund, 200er Pack", 130, 90, 55, 640, 650, 30, 70, 240, 0, Fast, 1, 8, Hot,
            hotThreshold: 30, gtin: true, multiple: 10);
        Add("BEF-1005", "Spanplattenschraube 5x50 Innensechsrund, 100er Pack", 130, 90, 55, 520, 590, 15, 30, 100, 0, Med, 1, 6, Std,
            alt: new[] { "BEF-1004" }, multiple: 5);
        Add("BEF-1006", "Dübel 8 mm Nylon, 50er Pack", 120, 80, 40, 220, 450, 15, 30, 100, 0, Med, 1, 6, Std, multiple: 5);
        Add("BEF-1007", "Kabelbinder 300 mm schwarz, 100er Pack", 300, 40, 30, 190, 590, 12, 25, 80, 0, Med, 1, 5, Std, gtin: true, multiple: 5);
        Add("BEF-1008", "Schlossschraube M8x60 verzinkt, 25er Pack", 100, 70, 50, 780, 780, 8, 16, 50, 0, Med, 1, 4, Std, multiple: 5);
        Add("BEF-1009", "Gewindestange M8, 500 mm", 500, 12, 12, 200, 350, 6, 12, 40, 0, Slow, 1, 4, Std, multiple: 5);
        Add("BEF-1010", "Schraubensortiment 600-teilig, Sortimentskasten", 300, 200, 60, 1100, 1990, 3, 5, 15, 0, None, 1, 1, Std,
            desc: "Ladenhüter aus dem Altbestand.");

        // ---- Werkzeug (Lieferant 1) ----
        Add("WZG-2001", "Schraubendreher-Set 8-teilig", 280, 100, 35, 450, 1490, 6, 12, 40, 1, Med, 1, 3, Std, gtin: true, multiple: 2);
        Add("WZG-2002", "Bohrer-Set HSS 13-teilig", 180, 120, 25, 350, 1190, 6, 12, 40, 1, Med, 1, 3, Std, gtin: true, multiple: 2);
        Add("WZG-2003", "Handsäge Universal 450 mm", 550, 130, 25, 480, 1290, 3, 6, 20, 1, Slow, 1, 2, Std);
        Add("WZG-2004", "Cuttermesser 18 mm mit Ersatzklingen", 170, 45, 20, 90, 390, 30, 60, 200, 1, Fast, 1, 10, Hot,
            hotThreshold: 30, multiple: 10);
        Add("WZG-2005", "Zollstock 2 m Buche", 250, 30, 25, 200, 490, 8, 16, 50, 1, Med, 1, 4, Std, multiple: 4);
        Add("WZG-2006", "Wasserwaage 60 cm", 600, 50, 30, 700, 1690, 3, 6, 20, 1, Slow, 1, 2, Std);
        Add("WZG-2007", "Akku-Bohrschrauber 18 V im Karton", 280, 90, 240, 1800, 7900, 3, 6, 15, 1, Slow, 1, 2, Std, stackable: false);
        Add("WZG-2008", "Akku-Pack 18 V 4 Ah", 150, 80, 90, 750, 4900, 3, 6, 15, 1, Slow, 1, 2, Std);
        Add("WZG-2009", "Schutzbrille klar", 170, 150, 60, 60, 590, 10, 20, 60, 1, Med, 1, 5, Std, multiple: 5);
        Add("WZG-2010", "Arbeitshandschuhe Gr. M (Paar)", 150, 100, 20, 90, 290, 15, 30, 100, 1, Med, 2, 10, Std, multiple: 10);
        Add("WZG-2011", "Arbeitshandschuhe Gr. L (Paar)", 150, 100, 20, 95, 290, 15, 30, 100, 1, Med, 2, 10, Std, multiple: 10);
        Add("WZG-2012", "Werkstattlampe LED (Auslaufmodell)", 420, 180, 90, 1200, 2490, 2, 4, 10, 1, None, 1, 1, Std,
            desc: "Auslaufmodell, keine Nachfrage mehr.");

        // ---- Farben und Chemie (Lieferant 1), mit MHD ----
        Add("FRB-4001", "Wandfarbe weiß 5 l Eimer", 230, 230, 220, 6500, 2490, 4, 8, 24, 1, Slow, 1, 3, Res,
            stackable: false, shelfLife: 730, multiple: 4);
        Add("FRB-4002", "Montagekleber 310 ml Kartusche", 60, 60, 220, 400, 490, 8, 15, 50, 1, Slow, 1, 4, Std, shelfLife: 540, multiple: 6);
        Add("FRB-4003", "Silikon-Dichtstoff transparent 310 ml", 60, 60, 220, 380, 450, 8, 15, 50, 1, Slow, 1, 4, Std, shelfLife: 540, multiple: 6);
        Add("FRB-4004", "Universalreiniger 1 l Flasche", 90, 70, 260, 1100, 390, 6, 12, 40, 1, Med, 1, 4, Std,
            stackable: false, shelfLife: 900, multiple: 6);
        Add("FRB-4005", "Schleifpapier-Sortiment 10 Blatt", 280, 230, 5, 120, 590, 10, 20, 60, 1, Med, 1, 5, Std, multiple: 10);
        Add("FRB-4006", "Abdeckvlies 25 m² Rolle", 300, 120, 120, 900, 990, 3, 6, 20, 1, Slow, 1, 3, Res);
        Add("FRB-4007", "Malerkrepp 50 mm x 50 m", 60, 60, 50, 90, 250, 12, 24, 80, 1, Med, 2, 8, Std, multiple: 12);
        Add("FRB-4008", "Holzlasur nussbaum 2,5 l (Auslauf)", 190, 190, 170, 2900, 2190, 2, 4, 8, 1, None, 1, 1, Res,
            stackable: false, shelfLife: 900, desc: "Auslaufartikel, teils abgelaufene Chargen.");

        // ---- Saisonware (Lieferant 1): Fenster relativ zu heute ----
        Add("SAI-7001", "Sonnenschirm 3 m", 1400, 150, 150, 6500, 4990, 2, 4, 10, 1, Slow, 1, 2, Res,
            seasonFrom: -150, seasonUntil: 40, desc: "Sommerartikel, noch bestellbar.");
        Add("SAI-7002", "Streusalz 25 kg Sack", 600, 400, 120, 25000, 990, 5, 10, 30, 1, Slow, 1, 3, Res,
            seasonFrom: 35, seasonUntil: 200, desc: "Winterartikel, vorab eingelagert (noch nicht bestellbar).");
        Add("SAI-7003", "Gartenschlauch 20 m", 300, 300, 150, 1800, 2490, 2, 4, 10, 1, Slow, 1, 2, Std,
            seasonFrom: -160, seasonUntil: -25, desc: "Sommerartikel, Saison beendet.");
        Add("SAI-7004", "Schneeschieber Aluminium", 700, 350, 60, 1500, 1990, 3, 6, 15, 1, Slow, 1, 2, Res,
            seasonFrom: 35, seasonUntil: 200, desc: "Winterartikel, vorab eingelagert (noch nicht bestellbar).");

        // ---- Verpackung und Büro (Lieferant 2) ----
        Add("VRP-3001", "Versandkarton klein 300x200x150 mm (flach)", 300, 200, 10, 180, 60, 60, 120, 400, 2, Med, 5, 30, Std,
            alt: new[] { "VRP-3002" }, step: 5, multiple: 25);
        Add("VRP-3002", "Versandkarton mittel 400x300x200 mm (flach)", 400, 300, 12, 230, 85, 30, 60, 200, 2, Med, 5, 30, Res,
            alt: new[] { "VRP-3001", "VRP-3003" }, step: 5, multiple: 25);
        Add("VRP-3003", "Versandkarton groß 600x400x300 mm (flach)", 600, 400, 15, 420, 140, 20, 40, 120, 2, Slow, 5, 20, Res, step: 5, multiple: 20);
        Add("VRP-3004", "Paketklebeband 50 mm x 66 m transparent", 60, 60, 50, 250, 220, 40, 80, 260, 2, Fast, 2, 12, Hot,
            hotThreshold: 40, multiple: 12);
        Add("VRP-3005", "Luftpolsterfolie 50 m Rolle", 500, 150, 150, 900, 1490, 4, 8, 24, 2, Med, 1, 3, Res);
        Add("VRP-3006", "Füllpapier 5 kg Ballen", 500, 350, 250, 5000, 890, 3, 6, 20, 2, Slow, 1, 3, Res);
        Add("VRP-3007", "Thermoetiketten 100x150 mm, 500er Rolle", 110, 110, 160, 700, 990, 6, 12, 40, 2, Med, 1, 4, Std, gtin: true, multiple: 4);
        Add("VRP-3008", "Stretchfolie 500 mm Rolle", 500, 100, 100, 2300, 790, 3, 6, 18, 2, Slow, 1, 3, Res);
        Add("BRO-6001", "Druckerpapier A4 500 Blatt", 300, 215, 50, 2500, 390, 40, 80, 260, 2, Fast, 1, 10, Hot,
            hotThreshold: 40, gtin: true, multiple: 5);
        Add("BRO-6002", "Kugelschreiber blau, 50er Box", 180, 100, 60, 400, 1290, 4, 8, 24, 2, Slow, 1, 2, Std);
        Add("BRO-6003", "Ordner A4 breit (Altbestand)", 320, 285, 80, 350, 190, 5, 10, 30, 2, None, 1, 1, Res,
            desc: "Ladenhüter aus dem Altbestand.");
        Add("BRO-6004", "Haftnotizen 76x76 mm, 12er Pack", 80, 80, 50, 260, 890, 6, 12, 40, 2, Med, 1, 4, Std, multiple: 6);

        // ---- Elektro (Lieferant 0) ----
        Add("ELK-5001", "Verlängerungskabel 5 m", 200, 200, 60, 450, 1290, 5, 10, 30, 0, Med, 1, 3, Std);
        Add("ELK-5002", "Mehrfachsteckdose 5-fach mit Schalter", 350, 60, 45, 380, 1590, 3, 6, 20, 0, Slow, 1, 2, Std);
        Add("ELK-5003", "Batterien AA, 24er Pack", 200, 150, 50, 620, 1090, 8, 16, 50, 0, Slow, 1, 4, Std, shelfLife: 1800, multiple: 4);
        Add("ELK-5004", "LED-Lampe E27 10 W warmweiß", 65, 65, 110, 60, 390, 30, 60, 200, 0, Fast, 2, 12, Hot,
            hotThreshold: 30, gtin: true, multiple: 10);

        // ---- Bundles (ohne eigenen Bestand) ----
        list.Add(new DemoArticleSpec
        {
            Sku = "SET-8001", Name = "Werkstatt-Starterset (Bundle)",
            Description = "Bundle: Schraubendreher-Set, Zollstock, Schutzbrille und Kabelbinder.",
            LengthMm = 300, WidthMm = 200, HeightMm = 120, WeightGrams = 900, PriceCents = 3160,
            SupplierIndex = 1, Demand = DemoDemand.Slow, QuantityMin = 1, QuantityMax = 2,
            Components = new[] { ("WZG-2001", 1), ("WZG-2005", 1), ("WZG-2009", 1), ("BEF-1007", 1) },
        });
        list.Add(new DemoArticleSpec
        {
            Sku = "SET-8002", Name = "Versandstation klein (Bundle)",
            Description = "Bundle: 20 kleine Versandkartons, Paketklebeband und Stretchfolie.",
            LengthMm = 400, WidthMm = 300, HeightMm = 250, WeightGrams = 6000, PriceCents = 3020,
            SupplierIndex = 2, Demand = DemoDemand.Slow, QuantityMin = 1, QuantityMax = 1,
            Components = new[] { ("VRP-3001", 20), ("VRP-3004", 2), ("VRP-3008", 1) },
        });

        return list;
    }
}

/// <summary>
/// Aufbau des Demo-Lagers in Millimetern: zwei Zonen, fünf Gänge, je Gang vier Regale mit je drei Lagerplätzen, eine Trennwand
/// zwischen den Zonen und eine Mittelwand in der Kommissionierzone, jeweils mit einer Tür. Die Regale stehen nie in einer Wand,
/// damit die Wegberechnung ohne Warnungen auskommt.
/// </summary>
public static class DemoLayout
{
    public const string WarehouseCode = "WH01";
    public const string WarehouseName = "Hauptlager (Demo)";

    public static IReadOnlyList<(string Code, string Name, int OriginYMm)> Zones { get; } = new[]
    {
        ("Z-A", "Kommissionierzone", 0),
        ("Z-B", "Reservelager", 8500),
    };

    /// <summary>(Zone, Gang, Y-Lage der Gangmitte).</summary>
    public static IReadOnlyList<(string Zone, string Aisle, int YMm)> Aisles { get; } = new[]
    {
        ("Z-A", "A1", 1000),
        ("Z-A", "A2", 3500),
        ("Z-A", "A3", 6000),
        ("Z-B", "B1", 9500),
        ("Z-B", "B2", 12000),
    };

    /// <summary>X-Lage der vier Regale je Gang: zwei westlich, zwei östlich der Mittelwand (x = 6500).</summary>
    public static IReadOnlyList<int> ShelfXMm { get; } = new[] { 500, 3000, 8000, 10500 };

    public const int ShelfWidthMm = 2000;
    public const int ShelfDepthMm = 600;
    public const int ShelfHeightMm = 2000;
    public const int BinsPerShelf = 3;
    public const int BinPitchMm = 650;
    public const int BinWidthMm = 600;
    public const int BinDepthMm = 600;
    public const int BinHeightMm = 500;
    public const int BinMaxWeightGrams = 50_000;

    /// <summary>Wände (Polygonzüge in mm, Dicke, Beschriftung). Türen: West (y 3000-4500), Trennwand (x 5500-7000), Mittelwand (y 3000-4500).</summary>
    public static IReadOnlyList<(int[][] Points, int ThicknessMm, string Label)> Walls { get; } = new[]
    {
        (new[] { new[] { -500, -500 }, new[] { 13_500, -500 }, new[] { 13_500, 14_000 }, new[] { -500, 14_000 } }, 150, "Außenmauer Süd/Ost/Nord"),
        (new[] { new[] { -500, -500 }, new[] { -500, 3_000 } }, 150, "Außenmauer West (unten)"),
        (new[] { new[] { -500, 4_500 }, new[] { -500, 14_000 } }, 150, "Außenmauer West (oben)"),
        (new[] { new[] { -500, 8_000 }, new[] { 5_500, 8_000 } }, 150, "Trennwand Reservelager (links)"),
        (new[] { new[] { 7_000, 8_000 }, new[] { 13_500, 8_000 } }, 150, "Trennwand Reservelager (rechts)"),
        (new[] { new[] { 6_500, -500 }, new[] { 6_500, 3_000 } }, 120, "Mittelwand (unten)"),
        (new[] { new[] { 6_500, 4_500 }, new[] { 6_500, 8_000 } }, 120, "Mittelwand (oben)"),
    };

    /// <summary>Start (Wareneingang, an der Westtür), Ende (Versand) und ein Pickpunkt für beides (Verpackung).</summary>
    public static IReadOnlyList<(string Label, int XMm, int YMm, PickPointType Type)> PickPoints { get; } = new[]
    {
        ("Wareneingang", 200, 3_750, PickPointType.Start),
        ("Versand", 13_000, 3_750, PickPointType.End),
        ("Verpackung", 12_500, 7_500, PickPointType.Both),
    };

    public static IReadOnlyList<(string Name, int Levels, int WidthMm, int DepthMm, int HeightMm, int MaxWeightGrams)> Carts { get; } = new[]
    {
        ("Kommissionierwagen 4 Ebenen", 4, 600, 400, 250, 60_000),
        ("Handwagen klein 2 Ebenen", 2, 500, 350, 300, 30_000),
    };
}
