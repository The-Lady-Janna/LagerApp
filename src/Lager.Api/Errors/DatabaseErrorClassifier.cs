using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;

namespace Lager.Api.Errors;

/// <summary>Art einer Datenbank-Constraint-Verletzung.</summary>
public enum DatabaseErrorKind
{
    /// <summary>Etwas anderes (NOT NULL, CHECK, Verbindungsfehler ...): bleibt ein 500.</summary>
    Other,
    UniqueViolation,
    ForeignKeyViolation,
}

/// <summary>Ergebnis von <see cref="DatabaseErrorClassifier.Classify"/>; <paramref name="Columns"/> nennt bei Unique die betroffenen Spalten.</summary>
public sealed record DatabaseError(DatabaseErrorKind Kind, IReadOnlyList<string> Columns);

/// <summary>
/// Erkennt Unique- und Fremdschlüssel-Verletzungen in einer <see cref="DbUpdateException"/>, ohne sich an einen
/// Datenbank-Provider zu binden: SQLite ("UNIQUE constraint failed: Articles.Sku", "FOREIGN KEY constraint failed")
/// und MySQL ("Duplicate entry '…' for key 'Articles.IX_Articles_Sku'", "a foreign key constraint fails")
/// werden über den Text der inneren <see cref="DbException"/> unterschieden. Der Rohtext geht nie an den Client;
/// die Antwort nennt nur die Spaltennamen (siehe <see cref="DescribeColumns"/>).
/// </summary>
public static class DatabaseErrorClassifier
{
    private static readonly Regex MySqlKey = new(@"for key '(?:[^']*\.)?(?<key>[^']+)'", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex EfIndexName = new(@"^IX_[^_]+_(?<cols>.+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Sprechende Namen für die Spalten der Unique-Indizes (alles andere wird unverändert genannt).
    private static readonly Dictionary<string, string> ColumnLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sku"] = "SKU",
        ["OrderNumber"] = "Bestellnummer",
        ["Username"] = "Benutzername",
        ["PickListNumber"] = "Picklisten-Nummer",
        ["WaveNumber"] = "Wellen-Nummer",
        ["PoNumber"] = "Einkaufsbestellnummer",
        ["RmaNumber"] = "Retourennummer",
        ["ShipmentNumber"] = "Sendungsnummer",
        ["ArticleId"] = "Artikel",
        ["StorageLocationId"] = "Lagerplatz",
        ["LotNumber"] = "Charge",
        ["Code"] = "Code",
        ["Name"] = "Name",
    };

    public static DatabaseError Classify(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is not DbException) continue;
            var message = e.Message;

            if (message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
            {
                var colon = message.IndexOf(':', message.IndexOf("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase));
                var list = colon < 0 ? "" : message[(colon + 1)..].Trim().TrimEnd('.', '\'', ' ');
                // Bei Indizes mit eigener Sortierung (ohne Groß-/Kleinschreibung) nennt SQLite den Index statt der Spalten:
                // "UNIQUE constraint failed: index 'IX_Articles_Sku'".
                if (list.StartsWith("index ", StringComparison.OrdinalIgnoreCase))
                    return new DatabaseError(DatabaseErrorKind.UniqueViolation, ColumnsOfIndex(list["index ".Length..].Trim('\'', ' ')));
                var columns = list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(c => c[(c.LastIndexOf('.') + 1)..])
                    .ToList();
                return new DatabaseError(DatabaseErrorKind.UniqueViolation, columns);
            }

            if (message.Contains("Duplicate entry", StringComparison.OrdinalIgnoreCase))
            {
                var key = MySqlKey.Match(message) is { Success: true } m ? m.Groups["key"].Value : "";
                return new DatabaseError(DatabaseErrorKind.UniqueViolation, ColumnsOfIndex(key));
            }

            if (message.Contains("FOREIGN KEY constraint failed", StringComparison.OrdinalIgnoreCase)
                || message.Contains("foreign key constraint fails", StringComparison.OrdinalIgnoreCase))
                return new DatabaseError(DatabaseErrorKind.ForeignKeyViolation, Array.Empty<string>());
        }

        return new DatabaseError(DatabaseErrorKind.Other, Array.Empty<string>());
    }

    /// <summary>Spalten aus einem EF-Indexnamen: "IX_StockItems_ArticleId_StorageLocationId" -> ArticleId, StorageLocationId.</summary>
    private static IReadOnlyList<string> ColumnsOfIndex(string indexName) =>
        EfIndexName.Match(indexName) is { Success: true } m
            ? m.Groups["cols"].Value.Split('_', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();

    /// <summary>"SKU", "Artikel, Lagerplatz, Charge" ... - leer, wenn keine Spalte bekannt ist.</summary>
    public static string DescribeColumns(IEnumerable<string> columns) =>
        string.Join(", ", columns.Select(c => ColumnLabels.TryGetValue(c, out var label) ? label : c));
}
