namespace Lager.Application.ImportExport;

/// <summary>
/// Die Spalten der Dateien - eine Quelle für Export und Import, damit jede exportierte Datei wieder importierbar ist.
/// Reine Anzeigespalten des Exports (<c>ArticleName</c>, <c>Status</c> ...) kennt der Import und ignoriert sie ohne Warnung.
/// </summary>
public static class CsvColumns
{
    /// <summary>Artikel: ohne Bundle-Komponenten (sie bleiben beim Import unverändert, siehe docs/features/csv-import-export.md).</summary>
    public static readonly IReadOnlyList<string> Articles = new[]
    {
        "Sku", "Name", "Description", "Gtin", "LengthMm", "WidthMm", "HeightMm", "WeightGrams",
        "IsStackable", "StackingAxis", "StackingIncrementMm", "MaxStackCount",
        "MinStock", "ReorderPoint", "MaxStock", "PurchasePrice", "SupplierCode", "AlternativeSkus", "ValidFrom", "ValidUntil",
    };

    /// <summary>Bestand: eine Zeile je Artikel, Lagerplatz und Charge.</summary>
    public static readonly IReadOnlyList<string> Stock = new[] { "Sku", "ArticleName", "Location", "Quantity", "LotNumber", "ExpiryDate" };

    /// <summary>Bestellungen: eine Zeile je Position, die Kopfdaten wiederholen sich.</summary>
    public static readonly IReadOnlyList<string> Orders = new[]
    {
        "OrderNumber", "Status", "Source", "CreatedAt", "CustomerReference", "Priority", "DueDate", "ExternalReference", "Sku", "Quantity",
    };

    public static readonly IReadOnlyList<string> Movements = new[]
    {
        "At", "Sku", "Location", "QuantityDelta", "Reason", "ReferenceType", "ReferenceId", "LotNumber", "ExpiryDate", "UnitCost",
    };

    public static readonly IReadOnlyList<string> Audit = new[] { "At", "User", "EntityType", "EntityId", "Operation", "Changes" };

    /// <summary>Weitere Namen für die Lagerplatz-Spalte des Bestandsimports.</summary>
    public static readonly IReadOnlyList<string> LocationAliases = new[] { "Location", "LocationCode", "Bin" };
}
