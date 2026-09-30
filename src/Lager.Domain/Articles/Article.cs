using Lager.Domain.Common;
// Die Eigenschaft Article.Gtin verdeckt innerhalb der Klasse den Typ Gtin (string statt Klasse): der Alias hält die Regeln erreichbar.
using GtinRules = Lager.Domain.Articles.Gtin;

namespace Lager.Domain.Articles;

public class Article : Entity
{
    public string Sku { get; private set; } = string.Empty;
    /// <summary>
    /// GTIN/EAN der Ware (nur Ziffern, 8/12/13/14 Stellen, gültige Prüfziffer; siehe <see cref="GtinRules"/>) oder null.
    /// Eindeutig über alle Artikel; wird beim Scannen der Verpackung aufgelöst.
    /// </summary>
    public string? Gtin { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public Dimensions Dimensions { get; private set; } = Dimensions.Zero;
    public int WeightGrams { get; private set; }
    public StackingInfo Stacking { get; private set; } = StackingInfo.NotStackable;

    /// <summary>Absolute lower bound — alarms when total stock drops below.</summary>
    public int MinStock { get; private set; }
    /// <summary>Reorder trigger — typically &gt; MinStock; the alarm dashboard surfaces this.</summary>
    public int ReorderPoint { get; private set; }
    /// <summary>Upper bound — useful for "do not order beyond this" suggestions (informational only).</summary>
    public int MaxStock { get; private set; }

    /// <summary>Bevorzugter Lieferant für Nachbestellungen. Steuert das Gruppieren in Bestellvorschlägen.</summary>
    public Guid? PrimarySupplierId { get; private set; }
    /// <summary>Einkaufspreis in Cent. Basis für Bestandsbewertung und PO-Wertsumme.</summary>
    public int PurchasePriceCents { get; private set; }

    /// <summary>
    /// Wenn nicht leer, ist dieser Artikel ein Bundle/Kit. Eine Bestell-Line
    /// "1× Bundle" wird beim Pick-Generieren in N Sub-Komponenten aufgelöst.
    /// </summary>
    private readonly List<BundleComponent> _bundleComponents = new();
    public IReadOnlyCollection<BundleComponent> BundleComponents => _bundleComponents.AsReadOnly();

    public bool IsBundle => _bundleComponents.Count > 0;

    /// <summary>
    /// Komma-separierte Liste von alternativen SKUs (substituierbar bei Out-of-Stock).
    /// CSV-persistiert um zusätzlichen Join zu sparen.
    /// </summary>
    public string? AlternativeSkusCsv { get; private set; }

    public IReadOnlyList<string> AlternativeSkus =>
        string.IsNullOrWhiteSpace(AlternativeSkusCsv)
            ? Array.Empty<string>()
            : AlternativeSkusCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Saison-Fenster, ab dem der Artikel bestellbar ist (null = immer).</summary>
    public DateTime? ValidFrom { get; private set; }
    /// <summary>
    /// Saison-Fenster-Ende (null = unbegrenzt). Ein Datum: der angegebene Tag ist der LETZTE Gültigkeitstag und gilt
    /// noch vollständig (Vergleich auf den Kalendertag, UTC, inklusive).
    /// </summary>
    public DateTime? ValidUntil { get; private set; }

    /// <summary>
    /// Ist der Artikel zum Zeitpunkt <paramref name="at"/> (Standard: jetzt, UTC) bestellbar? Der Beginn gilt ab dem
    /// angegebenen Zeitpunkt (bei einem Datum: 00:00 UTC). Das Ende ist ein Kalendertag und gilt inklusive:
    /// <c>ValidUntil = 30.09.</c> heißt, der Artikel ist am 30.09. bis 23:59 UTC noch bestellbar, erst ab dem 01.10. nicht mehr.
    /// </summary>
    public bool IsCurrentlyActive(DateTime? at = null)
    {
        var t = at ?? DateTime.UtcNow;
        if (t.Kind == DateTimeKind.Local) t = t.ToUniversalTime();
        if (ValidFrom is DateTime from && t < from) return false;
        if (ValidUntil is DateTime until && t.Date > until.Date) return false;
        return true;
    }

    /// <summary>
    /// Ist <paramref name="term"/> in Name, SKU, einer Alternativ-SKU oder der GTIN enthalten (Teilstring, Groß-/Kleinschreibung
    /// egal)? Leerer Suchtext trifft alles. Die GTIN wird ohne Leerraum verglichen ("4006 3813" findet "4006381333931").
    /// </summary>
    public bool MatchesSearch(string? term)
    {
        var text = term?.Trim();
        if (string.IsNullOrEmpty(text)) return true;

        if (Name.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
        if (Sku.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
        if (AlternativeSkus.Any(s => s.Contains(text, StringComparison.OrdinalIgnoreCase))) return true;

        var digits = GtinRules.Normalize(text);
        return Gtin is not null && digits is not null && Gtin.Contains(digits, StringComparison.Ordinal);
    }

    /// <summary>Ist <paramref name="sku"/> (Groß-/Kleinschreibung und Leerraum am Rand egal) die SKU dieses Artikels?</summary>
    public bool HasSku(string? sku) =>
        !string.IsNullOrWhiteSpace(sku) && string.Equals(Sku, sku.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Führt dieser Artikel <paramref name="sku"/> (Groß-/Kleinschreibung und Leerraum am Rand egal) als Alternativ-SKU?</summary>
    public bool HasAlternativeSku(string? sku) =>
        !string.IsNullOrWhiteSpace(sku) && AlternativeSkus.Any(s => string.Equals(s, sku.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Trägt dieser Artikel die GTIN (auch in gleichwertiger Schreibweise, siehe <see cref="GtinRules.EquivalentForms"/>)?</summary>
    public bool HasGtin(string? gtin) => Gtin is not null && GtinRules.AreEquivalent(Gtin, gtin);

    private Article() { }

    public Article(string sku, string name, Dimensions dimensions, int weightGrams, StackingInfo stacking, string? description = null)
    {
        if (string.IsNullOrWhiteSpace(sku)) throw new ArgumentException("SKU is required", nameof(sku));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required", nameof(name));
        if (weightGrams < 0) throw new ArgumentOutOfRangeException(nameof(weightGrams));

        Sku = sku.Trim();
        Name = name.Trim();
        Description = description;
        Dimensions = dimensions;
        WeightGrams = weightGrams;
        Stacking = stacking;
    }

    public void Update(string name, string? description, Dimensions dimensions, int weightGrams, StackingInfo stacking)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required", nameof(name));
        if (weightGrams < 0) throw new ArgumentOutOfRangeException(nameof(weightGrams));

        Name = name.Trim();
        Description = description;
        Dimensions = dimensions;
        WeightGrams = weightGrams;
        Stacking = stacking;
        Touch();
    }

    public void SetPurchasing(Guid? primarySupplierId, int purchasePriceCents)
    {
        if (purchasePriceCents < 0) throw new ArgumentOutOfRangeException(nameof(purchasePriceCents));
        PrimarySupplierId = primarySupplierId;
        PurchasePriceCents = purchasePriceCents;
        Touch();
    }

    public void SetSeasonWindow(DateTime? validFrom, DateTime? validUntil)
    {
        // Das Ende ist ein Kalendertag (siehe IsCurrentlyActive): am selben Tag wie der Beginn ist ein gültiges Ein-Tages-Fenster.
        if (validFrom is not null && validUntil is not null && validUntil.Value.Date < validFrom.Value.Date)
            throw new ArgumentException("ValidUntil muss am oder nach ValidFrom liegen");
        ValidFrom = validFrom;
        ValidUntil = validUntil;
        Touch();
    }

    /// <summary>
    /// Setzt die GTIN (<see cref="GtinRules.Normalize"/>: Leerraum entfernt). Leer oder nur Leerraum entfernt die GTIN.
    /// Eine ungültige GTIN (Nicht-Ziffern, falsche Länge, falsche Prüfziffer) wirft <see cref="ArgumentException"/> mit deutscher
    /// Meldung. Die Eindeutigkeit über alle Artikel prüft der Service (die Domain kennt die anderen Artikel nicht).
    /// </summary>
    public void SetGtin(string? gtin)
    {
        var error = GtinRules.GetError(gtin);
        if (error is not null) throw new ArgumentException(error, nameof(gtin));
        Gtin = GtinRules.Normalize(gtin);
        Touch();
    }

    public void SetAlternatives(IEnumerable<string> alternativeSkus)
    {
        var sanitized = alternativeSkus
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        AlternativeSkusCsv = sanitized.Count == 0 ? null : string.Join(",", sanitized);
        Touch();
    }

    public void ReplaceBundleComponents(IEnumerable<(Guid ComponentArticleId, int Quantity)> components)
    {
        // Erst prüfen, dann ersetzen: bei einem Fehler bleiben die bisherigen Komponenten unverändert.
        var incoming = components.ToList();
        var seen = new HashSet<Guid>();
        foreach (var (cid, qty) in incoming)
        {
            if (cid == Id) throw new InvalidOperationException("Bundle darf sich nicht selbst enthalten");
            if (qty <= 0) throw new ArgumentException("Bundle-Komponente braucht Menge > 0");
            if (!seen.Add(cid)) throw new ArgumentException("Bundle-Komponente ist doppelt aufgeführt: eine Komponente höchstens einmal, mit der Gesamtmenge");
        }

        _bundleComponents.Clear();
        foreach (var (cid, qty) in incoming)
        {
            var bc = new BundleComponent(cid, qty);
            bc.AttachTo(Id);
            _bundleComponents.Add(bc);
        }
        Touch();
    }

    public void SetStockThresholds(int minStock, int reorderPoint, int maxStock)
    {
        if (minStock < 0) throw new ArgumentOutOfRangeException(nameof(minStock));
        if (reorderPoint < 0) throw new ArgumentOutOfRangeException(nameof(reorderPoint));
        if (maxStock < 0) throw new ArgumentOutOfRangeException(nameof(maxStock));
        if (maxStock > 0 && maxStock < reorderPoint)
            throw new ArgumentException("MaxStock must be ≥ ReorderPoint when set", nameof(maxStock));

        MinStock = minStock;
        ReorderPoint = reorderPoint;
        MaxStock = maxStock;
        Touch();
    }
}
