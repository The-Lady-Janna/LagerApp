using Lager.Domain.Stock;
using Lager.Domain.Warehouse;

namespace Lager.Application.PickLists;

/// <summary>
/// Eine Pick-Anforderung: Menge eines (physischen) Artikels für eine Bestellzeile. Bundle-Zeilen sind
/// vorher in ihre Komponenten aufgelöst - ein Bundle hat keinen eigenen Bestand.
/// </summary>
public record PickRequest(Guid OrderId, Guid OrderLineId, Guid ArticleId, int Quantity);

/// <summary>
/// Reine, framework- und datenbankfreie Bestandsallokation für eine Pickliste. Der Allocator führt einen
/// laufenden Restbestand je <see cref="StockItem"/> über ALLE Anforderungen (mehrere Bestellungen, doppelte
/// Artikelzeilen, Bundle-Komponenten): dieselbe Einheit wird nie zweimal vergeben, und eine Fehlmenge
/// bricht mit <see cref="InvalidOperationException"/> ab, statt zu überzuteilen.
///
/// Auswahlreihenfolge je Anforderung:
///  1. nur nicht abgelaufene Chargen (MHD &lt; heute UTC gilt als nicht verfügbar),
///  2. FEFO - frühestes MHD zuerst, Ware ohne MHD zuletzt,
///  3. HotPick- vor Standard- vor Reserve-Lagerplätzen (<see cref="BinType"/>),
///  4. kleinerer (Rest-)Bestand zuerst - angebrochene Plätze werden leergeräumt,
///  5. deterministisch: Lagerplatz-Code, dann Zeilen-Id.
/// </summary>
public sealed class StockAllocator
{
    private readonly IReadOnlyDictionary<Guid, StorageLocation> _locations;
    private readonly Dictionary<Guid, List<StockItem>> _byArticle = new();
    private readonly Dictionary<Guid, int> _remaining = new();

    /// <param name="stock">Bestandszeilen; abgelaufene und leere Zeilen werden ausgeblendet.</param>
    /// <param name="locations">Lagerplätze der Bestandszeilen (für den <see cref="BinType"/> und den Code).</param>
    /// <param name="todayUtc">Stichtag für den Ablauf (nur das Datum zählt); Standard: heute UTC.</param>
    public StockAllocator(IEnumerable<StockItem> stock, IReadOnlyDictionary<Guid, StorageLocation> locations, DateTime? todayUtc = null)
    {
        _locations = locations;
        var today = (todayUtc ?? DateTime.UtcNow).Date;
        foreach (var s in stock)
        {
            if (s.Quantity <= 0 || s.IsExpired(today)) continue;
            if (!_byArticle.TryGetValue(s.ArticleId, out var rows))
                _byArticle[s.ArticleId] = rows = new List<StockItem>();
            rows.Add(s);
            _remaining[s.Id] = s.Quantity;
        }
    }

    /// <summary>Noch nicht vergebener, verfügbarer (nicht abgelaufener) Bestand des Artikels.</summary>
    public int Available(Guid articleId)
    {
        if (!_byArticle.TryGetValue(articleId, out var rows)) return 0;
        long sum = 0;
        foreach (var r in rows) sum += _remaining[r.Id];
        return (int)Math.Min(sum, int.MaxValue);
    }

    /// <summary>
    /// Vergibt Bestand für alle Anforderungen und schreibt den Restbestand fort. Alles oder nichts: reicht der
    /// Bestand für eine Anforderung nicht, wirft die Methode <see cref="InvalidOperationException"/> (mit SKU
    /// und Fehlmenge aller betroffenen Artikel) und lässt den Restbestand unverändert.
    /// Ergebnis: je (Bestellung, Zeile, Artikel, Lagerplatz) ein Kandidat; mehrere Chargen desselben
    /// Lagerplatzes werden zusammengefasst (die Charge wird erst beim Buchen FEFO gewählt).
    /// </summary>
    /// <param name="requests">Anforderungen in der Reihenfolge, in der sie Bestand bekommen sollen.</param>
    /// <param name="skuByArticle">Optional für die Fehlermeldung; ohne Eintrag erscheint die Artikel-Id.</param>
    public IReadOnlyList<PickCandidate> Allocate(IEnumerable<PickRequest> requests, IReadOnlyDictionary<Guid, string>? skuByArticle = null)
    {
        var (candidates, shortages, taken) = Run(requests);
        if (shortages.Count > 0)
        {
            var text = string.Join(", ", shortages.Select(kv =>
                $"{(skuByArticle is not null && skuByArticle.TryGetValue(kv.Key, out var sku) ? sku : kv.Key.ToString())} (Fehlmenge {kv.Value})"));
            throw new InvalidOperationException($"Nicht genug Bestand für: {text}");
        }

        foreach (var (stockId, qty) in taken)
            _remaining[stockId] -= qty;
        return candidates;
    }

    /// <summary>
    /// Trockenlauf: prüft, ob der Bestand für alle Anforderungen reicht, und liefert die Kandidaten, die
    /// <see cref="Allocate"/> jetzt vergeben würde - ohne den Restbestand zu verändern.
    /// </summary>
    public bool TryPreview(IEnumerable<PickRequest> requests, out IReadOnlyList<PickCandidate> candidates)
    {
        var (result, shortages, _) = Run(requests);
        candidates = result;
        return shortages.Count == 0;
    }

    /// <summary>
    /// Reihenfolge für das Abbuchen aus einem Lagerplatz: nicht abgelaufene Zeilen zuerst, darin FEFO
    /// (frühestes MHD, ohne MHD zuletzt); abgelaufene Ware nur als letzter Ausweg. Stabil und deterministisch.
    /// </summary>
    public static IReadOnlyList<StockItem> OrderForBooking(IEnumerable<StockItem> rows, DateTime? todayUtc = null)
    {
        var today = (todayUtc ?? DateTime.UtcNow).Date;
        return rows
            .OrderBy(s => s.IsExpired(today) ? 1 : 0)
            .ThenBy(s => s.ExpiryDate ?? DateTime.MaxValue)
            .ThenBy(s => s.CreatedAt)
            .ThenBy(s => s.Id)
            .ToList();
    }

    private (List<PickCandidate> Candidates, Dictionary<Guid, int> Shortages, Dictionary<Guid, int> Taken) Run(IEnumerable<PickRequest> requests)
    {
        var taken = new Dictionary<Guid, int>();          // StockItem.Id -> in diesem Lauf vergebene Menge
        var shortages = new Dictionary<Guid, int>();      // Artikel-Id -> Fehlmenge
        var candidates = new List<PickCandidate>();
        var position = new Dictionary<(Guid Order, Guid Line, Guid Article, Guid Location), int>();

        foreach (var request in requests)
        {
            if (request.Quantity <= 0)
                throw new ArgumentOutOfRangeException(nameof(requests), $"Menge muss größer 0 sein (Artikel {request.ArticleId})");

            var missing = request.Quantity;
            if (_byArticle.TryGetValue(request.ArticleId, out var rows))
            {
                foreach (var row in Rank(rows, taken))
                {
                    if (missing == 0) break;
                    var free = _remaining[row.Id] - taken.GetValueOrDefault(row.Id);
                    if (free <= 0) continue;

                    var take = Math.Min(missing, free);
                    taken[row.Id] = taken.GetValueOrDefault(row.Id) + take;
                    missing -= take;

                    var key = (request.OrderId, request.OrderLineId, request.ArticleId, row.StorageLocationId);
                    if (position.TryGetValue(key, out var index))
                    {
                        candidates[index] = candidates[index] with { Quantity = candidates[index].Quantity + take };
                    }
                    else
                    {
                        position[key] = candidates.Count;
                        candidates.Add(new PickCandidate(request.OrderId, request.OrderLineId, request.ArticleId, row.StorageLocationId, take));
                    }
                }
            }

            if (missing > 0)
                shortages[request.ArticleId] = shortages.GetValueOrDefault(request.ArticleId) + missing;
        }

        return (candidates, shortages, taken);
    }

    /// <summary>Sortiert die Zeilen eines Artikels nach der oben beschriebenen Auswahlreihenfolge.</summary>
    private IEnumerable<StockItem> Rank(List<StockItem> rows, Dictionary<Guid, int> taken) =>
        rows
            .OrderBy(s => s.ExpiryDate ?? DateTime.MaxValue)
            .ThenBy(s => BinRank(s.StorageLocationId))
            .ThenBy(s => _remaining[s.Id] - taken.GetValueOrDefault(s.Id))
            .ThenBy(s => _locations.TryGetValue(s.StorageLocationId, out var loc) ? loc.Code : string.Empty, StringComparer.Ordinal)
            .ThenBy(s => s.Id);

    private int BinRank(Guid locationId)
    {
        if (!_locations.TryGetValue(locationId, out var loc)) return 1;
        return loc.BinType switch
        {
            BinType.HotPick => 0,
            BinType.Reserve => 2,
            _ => 1,
        };
    }
}
