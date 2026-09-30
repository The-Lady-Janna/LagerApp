using Lager.Domain.Common;

namespace Lager.Domain.Returns;

public enum ReturnStatus
{
    Draft = 0,
    Processed = 1,
    Cancelled = 9
}

public enum QcResult
{
    Pending = 0,
    Sellable = 1,
    BGrade = 2,
    Defect = 3,
    Destroy = 4
}

/// <summary>
/// Kunden-Retoure. Nach QC pro Zeile entscheidet sich, was mit der Ware passiert (gebucht beim Abschluss,
/// siehe ReturnService.ProcessAsync):
///  - Sellable (A-Ware): zurück in den verkaufsfähigen Bestand des TargetBinId (Movement Return).
///  - BGrade (B-Ware): Sperrbuchung (Movement ReturnB) - die Ware erhöht den Verkaufsbestand nicht.
///  - Defect / Destroy: Ausschussbuchung (Movement ReturnScrap) - ebenfalls kein Zuwachs im Verkaufsbestand.
/// Die Charge der Zeile bleibt in allen Fällen erhalten (das MHD ist dem Bestand der Charge bekannt: eine schon
/// geführte Charge behält ihr MHD). Die QC-Entscheidung selbst steht dauerhaft an der Zeile.
///
/// Statusmaschine (erzwungen in den Methoden):
///
///   Status     | AddLine / SetLineQc | MarkProcessed() | Cancel()
///   -----------+---------------------+-----------------+-----------
///   Draft      | ok                  | Processed       | Cancelled
///   Processed  | Fehler              | Fehler          | Fehler   (endgültig: Bestand ist gebucht)
///   Cancelled  | Fehler              | Fehler          | Fehler   (endgültig)
///
/// Fehler = InvalidOperationException. Zeilenmengen: 1 bis <see cref="MaxQuantity"/>.
/// </summary>
public class ReturnShipment : Entity
{
    /// <summary>Obergrenze für Retouren-Mengen je Zeile (Plausibilitätsgrenze gegen Tippfehler).</summary>
    public const int MaxQuantity = 1_000_000;

    public string RmaNumber { get; private set; } = string.Empty;
    public Guid? OrderId { get; private set; }
    public string? CustomerReference { get; private set; }
    public string? Notes { get; private set; }
    public ReturnStatus Status { get; private set; } = ReturnStatus.Draft;
    public DateTime? ProcessedAt { get; private set; }

    private readonly List<ReturnLine> _lines = new();
    public IReadOnlyCollection<ReturnLine> Lines => _lines.AsReadOnly();

    private ReturnShipment() { }

    public ReturnShipment(string rmaNumber, Guid? orderId, string? customerReference, string? notes)
    {
        if (string.IsNullOrWhiteSpace(rmaNumber)) throw new ArgumentException("RmaNumber required", nameof(rmaNumber));
        RmaNumber = rmaNumber.Trim();
        OrderId = orderId;
        CustomerReference = customerReference;
        Notes = notes;
    }

    public ReturnLine AddLine(Guid articleId, string articleSku, int quantity, string? lotNumber = null)
    {
        if (Status != ReturnStatus.Draft) throw new InvalidOperationException("Nur Draft-Return darf erweitert werden");
        if (quantity <= 0 || quantity > MaxQuantity)
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, $"Menge muss > 0 und <= {MaxQuantity} sein");
        var l = new ReturnLine(articleId, articleSku, quantity, lotNumber);
        l.AttachTo(Id);
        _lines.Add(l);
        Touch();
        return l;
    }

    public void SetLineQc(Guid lineId, QcResult result, Guid? targetBinId, string? notes)
    {
        if (Status != ReturnStatus.Draft) throw new InvalidOperationException("Nur Draft-Return darf editiert werden");
        var line = _lines.FirstOrDefault(l => l.Id == lineId) ?? throw new InvalidOperationException("Zeile nicht gefunden");
        line.SetQc(result, targetBinId, notes);
        Touch();
    }

    /// <summary>
    /// Schließt den Vorgang ab. Caller (Service) ist dafür verantwortlich die
    /// Sellable-Zeilen tatsächlich wieder in den Bestand zu buchen — die Domain
    /// stellt nur sicher dass ohne Pending-Zeilen abgeschlossen wird.
    /// </summary>
    public void MarkProcessed()
    {
        if (Status != ReturnStatus.Draft) throw new InvalidOperationException("Bereits abgeschlossen");
        if (_lines.Count == 0) throw new InvalidOperationException("Leere Retoure");
        if (_lines.Any(l => l.QcResult == QcResult.Pending))
            throw new InvalidOperationException("Es gibt noch Pending-Zeilen — bitte QC abschließen");
        Status = ReturnStatus.Processed;
        ProcessedAt = DateTime.UtcNow;
        Touch();
    }

    public void Cancel()
    {
        if (Status == ReturnStatus.Processed) throw new InvalidOperationException("Abgeschlossene Retoure nicht stornierbar");
        if (Status == ReturnStatus.Cancelled) throw new InvalidOperationException("Retoure ist bereits storniert");
        Status = ReturnStatus.Cancelled;
        Touch();
    }
}

public class ReturnLine : Entity
{
    public Guid ReturnShipmentId { get; private set; }
    public Guid ArticleId { get; private set; }
    public string ArticleSku { get; private set; } = string.Empty;
    public int Quantity { get; private set; }
    /// <summary>Charge der zurückgekommenen Ware (normalisiert: leer = null).</summary>
    public string? LotNumber { get; private set; }
    public QcResult QcResult { get; private set; } = QcResult.Pending;
    public Guid? TargetBinId { get; private set; }
    public string? QcNotes { get; private set; }

    private ReturnLine() { }

    public ReturnLine(Guid articleId, string articleSku, int quantity, string? lotNumber)
    {
        if (quantity <= 0 || quantity > ReturnShipment.MaxQuantity)
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, $"Menge muss > 0 und <= {ReturnShipment.MaxQuantity} sein");
        var lot = Lager.Domain.Stock.StockItem.NormalizeLot(lotNumber);
        if (lot is { Length: > 64 })
            throw new ArgumentException("Chargennummer darf höchstens 64 Zeichen haben", nameof(lotNumber));
        ArticleId = articleId;
        ArticleSku = articleSku;
        Quantity = quantity;
        LotNumber = lot;
    }

    internal void AttachTo(Guid returnId) => ReturnShipmentId = returnId;

    internal void SetQc(QcResult result, Guid? targetBinId, string? notes)
    {
        if (!Enum.IsDefined(result))
            throw new ArgumentException($"Unbekanntes QC-Ergebnis: {(int)result}", nameof(result));
        if (result == QcResult.Sellable && targetBinId is null)
            throw new ArgumentException("Sellable braucht einen Ziel-Bin", nameof(targetBinId));
        QcResult = result;
        TargetBinId = targetBinId;
        QcNotes = notes;
        Touch();
    }
}
