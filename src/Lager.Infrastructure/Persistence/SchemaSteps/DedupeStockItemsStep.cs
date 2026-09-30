using Microsoft.Extensions.Logging;

namespace Lager.Infrastructure.Persistence.SchemaSteps;

/// <summary>
/// Bestandszeilen sind pro (Artikel, Lagerplatz, Charge) eindeutig. Bis hierher erzwang das nichts: zwei parallele
/// Wareneingänge in einen leeren Lagerplatz konnten je eine eigene StockItem-Zeile anlegen. Dieser Schritt führt
/// solche Legacy-Duplikate zusammen und legt danach den Unique-Index
/// <c>IX_StockItems_ArticleId_StorageLocationId_LotNumber</c> an (das EF-Modell in StockItemConfiguration
/// deklariert ihn für neue Datenbanken).
///
///  - Zusammenführen: Die Mengen werden summiert, die Summe je Artikel bleibt also erhalten. Übrig bleibt die Zeile
///    mit dem frühesten Ablaufdatum (FEFO-konservativ), sonst die mit der kleinsten Id. Der Movement-Ledger
///    (StockMovements) wird nicht angefasst, er bucht Deltas und bleibt gültig. Eine leere Charge zählt wie keine.
///  - Nur rohes SQL mit Parametern (der Schritt läuft auf Datenbanken, denen spätere Spalten noch fehlen) und keine
///    Audit-Einträge; jedes Zusammenführen steht im Log.
///  - NULL-Chargen erfasst ein Unique-Index in SQLite und MySQL nicht (NULLs gelten als verschieden). Der Index
///    schützt also nur Zeilen mit Charge; für Zeilen ohne Charge muss die Find-or-Create-Logik der Bestandsbuchung
///    leere Chargen auf null normalisieren und vorhandene Zeilen wiederverwenden.
///  - Nicht kritisch: Scheitert der Index an unerwarteten Daten, läuft die App weiter (ohne Index), der Fehler steht
///    im Log und der Schritt wird beim nächsten Start erneut versucht.
/// </summary>
public sealed class DedupeStockItemsStep : ISchemaUpgradeStep
{
    public const string IndexName = "IX_StockItems_ArticleId_StorageLocationId_LotNumber";

    public int Order => 20;
    public string Name => "0020_DedupeStockItems";
    public bool IsCritical => false;

    private sealed record Row(string Id, string ArticleId, string LocationId, string? Lot, long Quantity, object? Expiry);

    public async Task ApplyAsync(LagerDbContext db, ILogger logger)
    {
        if (!await SchemaSql.TableExistsAsync(db, "StockItems")) return;

        var isMySql = SchemaSql.IsMySql(db);
        await SchemaSql.InTransactionAsync(db, () => MergeDuplicatesAsync(db, logger, isMySql));
        await EnsureUniqueIndexAsync(db, logger);
    }

    private async Task MergeDuplicatesAsync(LagerDbContext db, ILogger logger, bool isMySql)
    {
        var rows = await SchemaSql.QueryAsync(db,
            "SELECT Id, ArticleId, StorageLocationId, LotNumber, Quantity, ExpiryDate FROM StockItems",
            r => new Row(
                SchemaSql.Str(r, 0)!,
                SchemaSql.Str(r, 1)!,
                SchemaSql.Str(r, 2)!,
                SchemaSql.Str(r, 3),
                r.IsDBNull(4) ? 0L : Convert.ToInt64(r.GetValue(4)),
                r.IsDBNull(5) ? null : r.GetValue(5)));

        // Guid-Text kann je nach Schreiber groß oder klein stehen: fürs Gruppieren vereinheitlichen.
        var duplicateGroups = rows
            .GroupBy(r => (Article: r.ArticleId.ToUpperInvariant(), Location: r.LocationId.ToUpperInvariant(), Lot: LotKey(r.Lot, isMySql)))
            .Where(g => g.Count() > 1)
            .ToList();
        if (duplicateGroups.Count == 0)
        {
            logger.LogInformation("{Step}: keine doppelten Bestandszeilen.", Name);
            return;
        }

        var mergedRows = 0;
        foreach (var group in duplicateGroups)
        {
            var ordered = group
                .OrderBy(r => r.Expiry is null ? 1 : 0)
                .ThenBy(r => r.Expiry, ExpiryComparer.Instance)
                .ThenBy(r => r.Id, StringComparer.Ordinal)
                .ToList();
            var survivor = ordered[0];
            var total = ordered.Sum(r => r.Quantity);
            var lot = string.IsNullOrWhiteSpace(survivor.Lot) ? null : survivor.Lot;

            await SchemaSql.ExecuteAsync(db,
                "UPDATE StockItems SET Quantity = @q, LotNumber = @lot, UpdatedAt = @now, ConcurrencyToken = @token WHERE Id = @id",
                ("@q", total), ("@lot", lot), ("@now", DateTime.UtcNow), ("@token", NewToken(isMySql)), ("@id", survivor.Id));

            foreach (var duplicate in ordered.Skip(1))
                await SchemaSql.ExecuteAsync(db, "DELETE FROM StockItems WHERE Id = @id", ("@id", duplicate.Id));

            mergedRows += ordered.Count - 1;
            logger.LogWarning(
                "{Step}: Bestandszeilen zusammengeführt - Artikel {ArticleId}, Lagerplatz {LocationId}, Charge {Lot}: {Count} Zeilen -> 1, Menge {Quantity}.",
                Name, survivor.ArticleId, survivor.LocationId, lot ?? "(keine)", ordered.Count, total);
        }

        logger.LogWarning("{Step}: {Groups} Gruppen mit doppelten Bestandszeilen bereinigt, {Rows} Zeilen entfernt (Mengen summiert).",
            Name, duplicateGroups.Count, mergedRows);
    }

    private static async Task EnsureUniqueIndexAsync(LagerDbContext db, ILogger logger)
    {
        var unique = await SchemaSql.GetIndexUniqueAsync(db, "StockItems", IndexName);
        if (unique == true) return; // frisch aus dem Modell angelegt oder schon umgestellt

        if (unique == false)
            await SchemaSql.DropIndexAsync(db, "StockItems", IndexName);
        try
        {
            await SchemaSql.EnsureIndexAsync(db, "StockItems", IndexName, unique: true, "ArticleId", "StorageLocationId", "LotNumber");
            logger.LogInformation("Unique-Index {Index} auf StockItems angelegt.", IndexName);
        }
        catch
        {
            // Den bisherigen (nicht eindeutigen) Index wiederherstellen, dann den Fehler weiterreichen.
            if (unique == false)
                await SchemaSql.EnsureIndexAsync(db, "StockItems", IndexName, unique: false, "ArticleId", "StorageLocationId", "LotNumber");
            throw;
        }
    }

    /// <summary>
    /// Gleichheitsschlüssel der Charge: leer = keine Charge. SQLite vergleicht Text binär (Groß-/Kleinschreibung zählt),
    /// MySQL (Standard-Collation *_ci) nicht und ignoriert nachgestellte Leerzeichen - der Unique-Index dort ebenfalls.
    /// </summary>
    private static string? LotKey(string? lot, bool isMySql)
    {
        if (string.IsNullOrWhiteSpace(lot)) return null;
        return isMySql ? lot.TrimEnd().ToUpperInvariant() : lot;
    }

    private static string NewToken(bool isMySql)
    {
        // Wie der Provider Guids schreibt: SQLite (Microsoft.Data.Sqlite) als GROSS geschriebenen Text.
        var token = Guid.NewGuid().ToString();
        return isMySql ? token : token.ToUpperInvariant();
    }

    /// <summary>Ablaufdaten: SQLite liefert Text (ISO, sortierbar), MySQL DateTime; nie gemischt.</summary>
    private sealed class ExpiryComparer : IComparer<object?>
    {
        public static readonly ExpiryComparer Instance = new();

        public int Compare(object? x, object? y)
        {
            if (x is null && y is null) return 0;
            if (x is null) return 1;
            if (y is null) return -1;
            return x is IComparable c && x.GetType() == y.GetType()
                ? c.CompareTo(y)
                : string.CompareOrdinal(Convert.ToString(x), Convert.ToString(y));
        }
    }
}
