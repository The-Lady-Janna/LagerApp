using Lager.Domain.Common;

namespace Lager.Domain.Purchasing;

public enum PurchaseOrderStatus
{
    Draft = 0,
    Sent = 1,
    PartiallyReceived = 2,
    Received = 3,
    Cancelled = 9
}

/// <summary>
/// One purchase order against a single supplier. Status workflow:
///   Draft → Sent → (PartiallyReceived* → Received) | Cancelled
/// Receipt is tracked per line — once every line's ReceivedQty equals OrderedQty,
/// MarkReceived flips the header to Received. Until then, status auto-bumps to
/// PartiallyReceived on the first partial.
///
/// Statusmaschine (erzwungen in den Methoden, Fehler = InvalidOperationException):
///
///   Status             | AddLine/RemoveLine | MarkSent() | ReceiveLine()                   | Cancel()
///   -------------------+--------------------+------------+---------------------------------+-----------
///   Draft              | ok                 | Sent       | Fehler                          | Cancelled
///   Sent               | Fehler             | Fehler     | PartiallyReceived | Received    | Cancelled
///   PartiallyReceived  | Fehler             | Fehler     | PartiallyReceived | Received    | Cancelled (Restmenge; gebuchter Bestand bleibt)
///   Received           | Fehler             | Fehler     | Fehler                          | Fehler   (endgültig)
///   Cancelled          | Fehler             | Fehler     | Fehler                          | Fehler   (endgültig)
///
/// Empfangene Menge je Zeile: 1 bis <see cref="MaxQuantity"/> pro Buchung, in Summe höchstens die bestellte Menge.
///
/// Wareneingang: Den Bestand bucht ausschließlich der Wareneingang (InboundService). Der reguläre Weg ist
/// "Wareneingang aus Bestellung anlegen" (POST /api/purchase-orders/{id}/create-inbound) und dessen Buchen: Die
/// Lieferung verweist auf die Bestellzeilen und schreibt beim Buchen die empfangene Menge über
/// <see cref="ReceiveLine"/> fort. <see cref="ReceiveLine"/> selbst bucht keinen Bestand.
/// </summary>
public class PurchaseOrder : Entity
{
    /// <summary>Obergrenze für eine einzelne Wareneingangs-Buchung (Plausibilitätsgrenze gegen Tippfehler).</summary>
    public const int MaxQuantity = 1_000_000;

    public string PoNumber { get; private set; } = string.Empty;
    public Guid SupplierId { get; private set; }
    public PurchaseOrderStatus Status { get; private set; } = PurchaseOrderStatus.Draft;
    public string Currency { get; private set; } = "EUR";
    public string? Notes { get; private set; }
    public DateTime? SentAt { get; private set; }
    public DateTime? ExpectedDate { get; private set; }
    public DateTime? ReceivedAt { get; private set; }

    private readonly List<PurchaseOrderLine> _lines = new();
    public IReadOnlyCollection<PurchaseOrderLine> Lines => _lines.AsReadOnly();

    private PurchaseOrder() { }

    public PurchaseOrder(string poNumber, Guid supplierId, string currency, DateTime? expectedDate = null, string? notes = null)
    {
        if (string.IsNullOrWhiteSpace(poNumber)) throw new ArgumentException("PoNumber required", nameof(poNumber));
        if (supplierId == Guid.Empty) throw new ArgumentException("SupplierId required", nameof(supplierId));
        PoNumber = poNumber.Trim();
        SupplierId = supplierId;
        Currency = string.IsNullOrWhiteSpace(currency) ? "EUR" : currency;
        ExpectedDate = expectedDate;
        Notes = notes;
    }

    public PurchaseOrderLine AddLine(Guid articleId, string articleSku, int orderedQty, int unitPriceCents)
    {
        if (Status != PurchaseOrderStatus.Draft)
            throw new InvalidOperationException("Nur Draft-PO darf Zeilen ergänzen");
        if (orderedQty <= 0) throw new ArgumentException("Menge > 0", nameof(orderedQty));
        var line = new PurchaseOrderLine(articleId, articleSku, orderedQty, unitPriceCents);
        line.AttachTo(Id);
        _lines.Add(line);
        Touch();
        return line;
    }

    public void RemoveLine(Guid lineId)
    {
        if (Status != PurchaseOrderStatus.Draft)
            throw new InvalidOperationException("Nur Draft-PO darf Zeilen entfernen");
        var line = _lines.FirstOrDefault(l => l.Id == lineId)
            ?? throw new InvalidOperationException("Zeile nicht gefunden");
        _lines.Remove(line);
        Touch();
    }

    public void MarkSent(DateTime? expectedDate = null)
    {
        if (Status != PurchaseOrderStatus.Draft)
            throw new InvalidOperationException("Nur Draft-PO kann versendet werden");
        if (_lines.Count == 0)
            throw new InvalidOperationException("Leere PO kann nicht versendet werden");
        Status = PurchaseOrderStatus.Sent;
        SentAt = DateTime.UtcNow;
        if (expectedDate is not null) ExpectedDate = expectedDate;
        Touch();
    }

    /// <summary>
    /// Schreibt die empfangene Menge einer Zeile fort; der Status springt automatisch auf PartiallyReceived oder
    /// Received je nach Erfüllungsstand. Bucht KEINEN Bestand: Das macht der Wareneingang (Inbound), der diese
    /// Methode beim Buchen aufruft. Direkt aufgerufen ist sie eine reine Statuskorrektur.
    /// </summary>
    public void ReceiveLine(Guid lineId, int receivedQty)
    {
        if (Status != PurchaseOrderStatus.Sent && Status != PurchaseOrderStatus.PartiallyReceived)
            throw new InvalidOperationException("PO ist nicht in Empfangsstatus");
        var line = _lines.FirstOrDefault(l => l.Id == lineId)
            ?? throw new InvalidOperationException("Zeile nicht gefunden");
        line.AddReceived(receivedQty);

        var allFull = _lines.All(l => l.ReceivedQty >= l.OrderedQty);
        Status = allFull ? PurchaseOrderStatus.Received : PurchaseOrderStatus.PartiallyReceived;
        if (allFull) ReceivedAt = DateTime.UtcNow;
        Touch();
    }

    public void Cancel()
    {
        if (Status == PurchaseOrderStatus.Received)
            throw new InvalidOperationException("Empfangene PO kann nicht storniert werden");
        if (Status == PurchaseOrderStatus.Cancelled)
            throw new InvalidOperationException("PO ist bereits storniert");
        Status = PurchaseOrderStatus.Cancelled;
        Touch();
    }

    public long TotalValueCents() => _lines.Sum(l => (long)l.OrderedQty * l.UnitPriceCents);

    /// <summary>Ob die Bestellung noch Ware erwartet (Sent oder PartiallyReceived).</summary>
    public bool IsOpen => Status is PurchaseOrderStatus.Sent or PurchaseOrderStatus.PartiallyReceived;
}

public class PurchaseOrderLine : Entity
{
    public Guid PurchaseOrderId { get; private set; }
    public Guid ArticleId { get; private set; }
    public string ArticleSku { get; private set; } = string.Empty;
    public int OrderedQty { get; private set; }
    public int ReceivedQty { get; private set; }
    public int UnitPriceCents { get; private set; }

    private PurchaseOrderLine() { }

    public PurchaseOrderLine(Guid articleId, string articleSku, int orderedQty, int unitPriceCents)
    {
        if (articleId == Guid.Empty) throw new ArgumentException("ArticleId", nameof(articleId));
        if (orderedQty <= 0) throw new ArgumentOutOfRangeException(nameof(orderedQty), orderedQty, "Menge muss > 0 sein");
        ArticleId = articleId;
        ArticleSku = articleSku;
        OrderedQty = orderedQty;
        UnitPriceCents = Math.Max(0, unitPriceCents);
    }

    internal void AttachTo(Guid poId) => PurchaseOrderId = poId;

    /// <summary>Noch offene (nicht empfangene) Menge der Zeile, nie negativ.</summary>
    public int OpenQuantity() => Math.Max(0, OrderedQty - ReceivedQty);

    public void AddReceived(int qty)
    {
        if (qty <= 0 || qty > PurchaseOrder.MaxQuantity)
            throw new ArgumentOutOfRangeException(nameof(qty), qty, $"Menge muss > 0 und <= {PurchaseOrder.MaxQuantity} sein");
        if (ReceivedQty + qty > OrderedQty)
            throw new InvalidOperationException("Empfangene Menge überschreitet bestellte Menge");
        ReceivedQty += qty;
        Touch();
    }
}
