using Lager.Domain.Common;

namespace Lager.Domain.Inventory;

public enum InventoryStatus
{
    Open = 0,
    Reconciled = 1,
    Cancelled = 9,
}

/// <summary>
/// Inventur (Zählbeleg). Statusmaschine (erzwungen in den Methoden):
///
///   Status      | AddSnapshotLine / SetCount | Reconcile() | Cancel()
///   ------------+----------------------------+-------------+-----------
///   Open        | ok                         | Reconciled  | Cancelled
///   Reconciled  | Fehler                     | Fehler      | Fehler   (endgültig: Differenzen sind gebucht)
///   Cancelled   | Fehler                     | Fehler      | Fehler   (endgültig)
///
/// Fehler = InvalidOperationException. Gezählte Mengen: 0 (leer gezählt) bis <see cref="MaxQuantity"/>.
///
/// Eine Zeile ist eine Bestandszeile zum Startzeitpunkt: Lagerplatz, Artikel und Charge/MHD (mehrere Chargen im
/// selben Lagerplatz sind mehrere Zeilen). Der Abgleich bucht auf den GEZÄHLTEN Wert gegen den aktuellen Bestand
/// (siehe InventoryService.ReconcileAsync); <see cref="InventoryLine.ExpectedQty"/> ist nur der Snapshot beim Start.
/// </summary>
public class InventoryCount : Entity
{
    /// <summary>Obergrenze für gezählte Mengen (Plausibilitätsgrenze gegen Tippfehler).</summary>
    public const int MaxQuantity = 1_000_000;

    public string Name { get; private set; } = string.Empty;
    public InventoryStatus Status { get; private set; } = InventoryStatus.Open;
    public DateTime? ReconciledAt { get; private set; }

    private readonly List<InventoryLine> _lines = new();
    public IReadOnlyList<InventoryLine> Lines => _lines;

    private InventoryCount() { }

    public InventoryCount(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name erforderlich", nameof(name));
        Name = name.Trim();
    }

    public InventoryLine AddSnapshotLine(Guid binId, string binCode, Guid articleId, string articleSku, int expectedQty,
        string? lotNumber = null, DateTime? expiryDate = null)
    {
        if (Status != InventoryStatus.Open)
            throw new InvalidOperationException("Inventur ist nicht mehr offen");
        var line = new InventoryLine(Id, binId, binCode, articleId, articleSku, expectedQty, lotNumber, expiryDate);
        _lines.Add(line);
        return line;
    }

    public void SetCount(Guid lineId, int countedQty, string? reason)
    {
        if (Status != InventoryStatus.Open)
            throw new InvalidOperationException("Inventur ist nicht mehr offen");
        var line = _lines.FirstOrDefault(l => l.Id == lineId)
            ?? throw new InvalidOperationException("Zähl-Position nicht gefunden");
        line.SetCounted(countedQty, reason);
        Touch();
    }

    public void Reconcile()
    {
        if (Status != InventoryStatus.Open)
            throw new InvalidOperationException("Inventur ist nicht mehr offen");
        if (_lines.Any(l => l.CountedQty is null))
            throw new InvalidOperationException("Es sind noch nicht alle Positionen gezählt");
        Status = InventoryStatus.Reconciled;
        ReconciledAt = DateTime.UtcNow;
        Touch();
    }

    /// <summary>
    /// Vermerkt beim Abgleich einen Hinweis an der Zeile (z. B. "Bestand seit Zählbeginn verändert"); er steht hinter der
    /// Bemerkung des Zählers im Grund der Zeile. Nur an einer abgeglichenen Inventur (im selben Vorgang wie <see cref="Reconcile"/>).
    /// </summary>
    public void AnnotateLine(Guid lineId, string note)
    {
        if (Status != InventoryStatus.Reconciled)
            throw new InvalidOperationException("Hinweise werden nur beim Abgleich an der Zeile vermerkt");
        var line = _lines.FirstOrDefault(l => l.Id == lineId)
            ?? throw new InvalidOperationException("Zähl-Position nicht gefunden");
        line.AppendNote(note);
    }

    public void Cancel()
    {
        // Nur offene Inventuren: nach Reconcile sind die Bestandsdifferenzen bereits gebucht.
        if (Status == InventoryStatus.Reconciled)
            throw new InvalidOperationException("Abgeglichene Inventur kann nicht storniert werden");
        if (Status == InventoryStatus.Cancelled)
            throw new InvalidOperationException("Inventur ist bereits storniert");
        Status = InventoryStatus.Cancelled;
        Touch();
    }
}

public class InventoryLine : Entity
{
    public Guid InventoryCountId { get; private set; }
    public Guid BinId { get; private set; }
    public string BinCode { get; private set; } = string.Empty;
    public Guid ArticleId { get; private set; }
    public string ArticleSku { get; private set; } = string.Empty;
    /// <summary>Detailzeile mit Charge und MHD; null bei Ware ohne beides (Tabelle InventoryLineLots).</summary>
    public InventoryLineLot? Lot { get; private set; }
    /// <summary>Charge der Bestandszeile (normalisiert: leer = null); zusammen mit <see cref="ExpiryDate"/> Teil des Zeilenschlüssels.</summary>
    public string? LotNumber => Lot?.LotNumber;
    public DateTime? ExpiryDate => Lot?.ExpiryDate;
    /// <summary>Bestand der Zeile beim Start der Inventur (Snapshot).</summary>
    public int ExpectedQty { get; private set; }
    public int? CountedQty { get; private set; }
    public string? Reason { get; private set; }

    /// <summary>Gezählt minus Snapshot. Nur zur Anzeige: gebucht wird gegen den aktuellen Bestand, nicht mit dieser Differenz.</summary>
    public int Diff => (CountedQty ?? ExpectedQty) - ExpectedQty;

    private InventoryLine() { }

    internal InventoryLine(Guid countId, Guid binId, string binCode, Guid articleId, string articleSku, int expectedQty,
        string? lotNumber = null, DateTime? expiryDate = null)
    {
        InventoryCountId = countId;
        BinId = binId;
        BinCode = binCode;
        ArticleId = articleId;
        ArticleSku = articleSku;
        ExpectedQty = expectedQty;
        var lot = Lager.Domain.Stock.StockItem.NormalizeLot(lotNumber);
        if (lot is not null || expiryDate is not null)
            Lot = new InventoryLineLot(Id, lot, expiryDate);
    }

    /// <summary>Spaltenbreite des Grundes.</summary>
    public const int MaxReasonLength = 500;

    internal void AppendNote(string note)
    {
        if (string.IsNullOrWhiteSpace(note)) return;
        var text = string.IsNullOrWhiteSpace(Reason) ? note.Trim() : $"{Reason.Trim()} | {note.Trim()}";
        Reason = text.Length <= MaxReasonLength ? text : text[..MaxReasonLength];
        Touch();
    }

    internal void SetCounted(int countedQty, string? reason)
    {
        if (countedQty < 0 || countedQty > InventoryCount.MaxQuantity)
            throw new ArgumentOutOfRangeException(nameof(countedQty), countedQty, $"Gezählte Menge muss >= 0 und <= {InventoryCount.MaxQuantity} sein");
        CountedQty = countedQty;
        Reason = reason;
        Touch();
    }
}

/// <summary>
/// Charge und MHD einer Zählzeile: eine Zeile (Tabelle InventoryLineLots, Schlüssel = Id der Zählzeile) nur für Zeilen
/// von Ware mit Charge oder MHD. Getrennt von der Zählzeile gespeichert, weil deren Tabelle mit festem Legacy-DDL im
/// SchemaUpgrader angelegt wird, das dem Modell entsprechen muss (neue Felder kommen als eigene Tabelle über
/// PurchaseOrderInboundLinkStep). Wird nur beim Anlegen der Zeile erzeugt und nie geändert.
/// </summary>
public class InventoryLineLot
{
    public Guid InventoryLineId { get; private set; }
    public string? LotNumber { get; private set; }
    public DateTime? ExpiryDate { get; private set; }

    private InventoryLineLot() { }

    internal InventoryLineLot(Guid lineId, string? lotNumber, DateTime? expiryDate)
    {
        InventoryLineId = lineId;
        LotNumber = lotNumber;
        ExpiryDate = expiryDate;
    }
}
