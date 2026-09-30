using Lager.Domain.Common;

namespace Lager.Domain.Inbound;

public enum InboundShipmentStatus
{
    Draft = 0,
    Received = 1,
    Cancelled = 9,
}

/// <summary>
/// Wareneingang (Lieferschein). Statusmaschine (erzwungen in den Methoden):
///
///   Status     | AddLine / RemoveLine | MarkReceived() | Cancel()
///   -----------+----------------------+----------------+-----------
///   Draft      | ok                   | Received       | Cancelled
///   Received   | Fehler               | Fehler         | Fehler   (endgültig: Bestand ist gebucht)
///   Cancelled  | Fehler               | Fehler         | Fehler   (endgültig)
///
/// Fehler = InvalidOperationException. Zeilenmengen: 1 bis <see cref="MaxQuantity"/>.
///
/// Optionaler Bestellbezug: Eine Lieferung kann zu einer Bestellung (PurchaseOrderId) gehören; ihre Zeilen können
/// auf Bestellzeilen verweisen (PurchaseOrderLineId) und einen Einkaufspreis tragen (UnitCostCents). Beim Buchen
/// (InboundService.ReceiveAsync) schreibt der Wareneingang die empfangene Menge in die Bestellzeile fort.
/// Gespeichert werden diese Angaben nicht in den Tabellen der Lieferung selbst, sondern in den kleinen Detailtabellen
/// InboundShipmentLinks und InboundLineLinks (siehe <see cref="InboundShipmentLink"/>, <see cref="InboundLineLink"/>);
/// die Properties der Domain-Klassen lesen sie von dort.
///
/// Chargen: Innerhalb der Lieferung darf dieselbe Charge (Artikel, Lagerplatz, Charge) nur mit demselben MHD
/// vorkommen (Fehlercode lot_expiry_mismatch); Zeilen mit gleicher Charge und gleichem MHD werden beim Buchen zu
/// einer Bestandszeile zusammengeführt, verschiedene Chargen bleiben getrennte Bestandszeilen.
/// </summary>
public class InboundShipment : Entity
{
    /// <summary>Obergrenze für Wareneingangs-Mengen je Zeile (Plausibilitätsgrenze gegen Tippfehler).</summary>
    public const int MaxQuantity = 1_000_000;

    public string ShipmentNumber { get; private set; } = string.Empty;
    public string? SupplierReference { get; private set; }
    public string? Notes { get; private set; }
    /// <summary>Detailzeile mit dem Bestellbezug; null bei einem freien Wareneingang ohne Bestellung.</summary>
    public InboundShipmentLink? PurchaseOrderLink { get; private set; }
    /// <summary>Bestellung, zu der die Lieferung gehört (null = freier Wareneingang ohne Bestellbezug).</summary>
    public Guid? PurchaseOrderId => PurchaseOrderLink?.PurchaseOrderId;
    public InboundShipmentStatus Status { get; private set; } = InboundShipmentStatus.Draft;
    public DateTime? ReceivedAt { get; private set; }

    private readonly List<InboundLine> _lines = new();
    public IReadOnlyList<InboundLine> Lines => _lines;

    private InboundShipment() { }

    public InboundShipment(string shipmentNumber, string? supplierReference, string? notes, Guid? purchaseOrderId = null)
    {
        if (string.IsNullOrWhiteSpace(shipmentNumber)) throw new ArgumentException("Lieferschein-Nummer erforderlich", nameof(shipmentNumber));
        if (purchaseOrderId == Guid.Empty) throw new ArgumentException("Bestellung ungültig", nameof(purchaseOrderId));
        ShipmentNumber = shipmentNumber.Trim();
        SupplierReference = supplierReference;
        Notes = notes;
        if (purchaseOrderId is Guid orderId)
            PurchaseOrderLink = new InboundShipmentLink(Id, orderId);
    }

    /// <param name="purchaseOrderLineId">Bestellzeile, die diese Zeile erfüllt (nur bei einer Lieferung mit Bestellbezug).</param>
    /// <param name="unitCostCents">Einkaufspreis je Stück (Cent) für die Bewertung; null = aktueller Artikelpreis beim Buchen.</param>
    public InboundLine AddLine(Guid articleId, Guid targetBinId, int quantity, string? lotNumber, DateTime? expiryDate,
        Guid? purchaseOrderLineId = null, int? unitCostCents = null)
    {
        if (Status != InboundShipmentStatus.Draft)
            throw new InvalidOperationException("Lieferung ist nicht mehr im Bearbeitungs-Status (Draft)");
        if (quantity <= 0 || quantity > MaxQuantity)
            throw new ArgumentOutOfRangeException(nameof(quantity), quantity, $"Menge muss > 0 und <= {MaxQuantity} sein");
        if (purchaseOrderLineId is not null && PurchaseOrderId is null)
            throw new InvalidOperationException("Die Lieferung gehört zu keiner Bestellung - eine Bestellzeile kann nicht zugeordnet werden");

        var line = new InboundLine(Id, articleId, targetBinId, quantity, lotNumber, expiryDate, purchaseOrderLineId, unitCostCents);
        EnsureLotConsistent(_lines.Append(line));
        _lines.Add(line);
        Touch();
        return line;
    }

    /// <summary>
    /// Dieselbe Charge im selben Lagerplatz kann nur ein MHD haben (Bestandszeilen sind pro Artikel, Lagerplatz und
    /// Charge eindeutig): zwei Zeilen mit gleicher Charge und verschiedenem MHD wären nicht buchbar.
    /// </summary>
    private static void EnsureLotConsistent(IEnumerable<InboundLine> lines)
    {
        var clash = lines
            .Where(l => l.LotNumber is not null)
            .GroupBy(l => (l.ArticleId, l.TargetBinId, l.LotNumber))
            .FirstOrDefault(g => g.Select(l => l.ExpiryDate?.Date).Distinct().Count() > 1);
        if (clash is null) return;

        var ex = new InvalidOperationException(
            $"Charge {clash.Key.LotNumber} kommt im selben Lagerplatz mit verschiedenem MHD vor - eine Charge hat genau ein MHD");
        ex.Data["code"] = "lot_expiry_mismatch";
        throw ex;
    }

    public void RemoveLine(Guid lineId)
    {
        if (Status != InboundShipmentStatus.Draft)
            throw new InvalidOperationException("Lieferung ist nicht mehr im Bearbeitungs-Status (Draft)");
        var line = _lines.FirstOrDefault(l => l.Id == lineId)
            ?? throw new InvalidOperationException("Position nicht gefunden");
        _lines.Remove(line);
        Touch();
    }

    /// <summary>
    /// Marks the shipment received. Caller (service) is responsible for actually
    /// booking the stock changes — the entity just records the status transition.
    /// </summary>
    public void MarkReceived()
    {
        if (Status != InboundShipmentStatus.Draft)
            throw new InvalidOperationException("Lieferung wurde bereits abgeschlossen oder storniert");
        if (_lines.Count == 0)
            throw new InvalidOperationException("Lieferung hat keine Positionen");
        EnsureLotConsistent(_lines);
        Status = InboundShipmentStatus.Received;
        ReceivedAt = DateTime.UtcNow;
        Touch();
    }

    public void Cancel()
    {
        if (Status == InboundShipmentStatus.Received)
            throw new InvalidOperationException("Bereits empfangene Lieferung kann nicht storniert werden");
        if (Status == InboundShipmentStatus.Cancelled)
            throw new InvalidOperationException("Lieferung ist bereits storniert");
        Status = InboundShipmentStatus.Cancelled;
        Touch();
    }
}

public class InboundLine : Entity
{
    public Guid InboundShipmentId { get; private set; }
    public Guid ArticleId { get; private set; }
    public Guid TargetBinId { get; private set; }
    public int Quantity { get; private set; }
    /// <summary>Charge (normalisiert: leer = null).</summary>
    public string? LotNumber { get; private set; }
    public DateTime? ExpiryDate { get; private set; }
    /// <summary>Detailzeile mit Bestellzeile und/oder Einkaufspreis; null, wenn die Zeile beides nicht hat.</summary>
    public InboundLineLink? Link { get; private set; }
    /// <summary>Bestellzeile, die diese Zeile erfüllt (null = ohne Bestellbezug).</summary>
    public Guid? PurchaseOrderLineId => Link?.PurchaseOrderLineId;
    /// <summary>Einkaufspreis je Stück in Cent (Kosten-Snapshot für die Bewertung); null = Artikelpreis beim Buchen.</summary>
    public int? UnitCostCents => Link?.UnitCostCents;

    /// <summary>Spaltenbreite der Chargennummer (wie beim Bestand).</summary>
    public const int MaxLotLength = 64;

    private InboundLine() { }

    internal InboundLine(Guid shipmentId, Guid articleId, Guid targetBinId, int quantity, string? lotNumber, DateTime? expiryDate,
        Guid? purchaseOrderLineId = null, int? unitCostCents = null)
    {
        var lot = Lager.Domain.Stock.StockItem.NormalizeLot(lotNumber);
        if (lot is { Length: > MaxLotLength })
            throw new ArgumentException($"Chargennummer darf höchstens {MaxLotLength} Zeichen haben", nameof(lotNumber));
        if (unitCostCents is < 0)
            throw new ArgumentOutOfRangeException(nameof(unitCostCents), unitCostCents, "Einkaufspreis darf nicht negativ sein");

        InboundShipmentId = shipmentId;
        ArticleId = articleId;
        TargetBinId = targetBinId;
        Quantity = quantity;
        LotNumber = lot;
        ExpiryDate = expiryDate;
        if (purchaseOrderLineId is not null || unitCostCents is not null)
            Link = new InboundLineLink(Id, purchaseOrderLineId, unitCostCents);
    }
}

/// <summary>
/// Bestellbezug einer Lieferung: eine Zeile je Lieferung, die zu einer Bestellung gehört (Tabelle InboundShipmentLinks,
/// Schlüssel = Id der Lieferung). Getrennt von der Lieferung gespeichert, weil deren Tabelle mit festem Legacy-DDL
/// im SchemaUpgrader angelegt wird, das dem Modell entsprechen muss; neue Felder kommen als eigene Tabelle über
/// PurchaseOrderInboundLinkStep. Wird nur beim Anlegen der Lieferung erzeugt und nie geändert.
/// </summary>
public class InboundShipmentLink
{
    public Guid InboundShipmentId { get; private set; }
    public Guid PurchaseOrderId { get; private set; }

    private InboundShipmentLink() { }

    internal InboundShipmentLink(Guid shipmentId, Guid purchaseOrderId)
    {
        InboundShipmentId = shipmentId;
        PurchaseOrderId = purchaseOrderId;
    }
}

/// <summary>
/// Bestellzeile und Einkaufspreis einer Wareneingangszeile: eine Zeile (Tabelle InboundLineLinks, Schlüssel = Id der
/// Wareneingangszeile) nur für Zeilen, die eines von beiden haben. Siehe <see cref="InboundShipmentLink"/> zur Begründung.
/// </summary>
public class InboundLineLink
{
    public Guid InboundLineId { get; private set; }
    public Guid? PurchaseOrderLineId { get; private set; }
    public int? UnitCostCents { get; private set; }

    private InboundLineLink() { }

    internal InboundLineLink(Guid lineId, Guid? purchaseOrderLineId, int? unitCostCents)
    {
        InboundLineId = lineId;
        PurchaseOrderLineId = purchaseOrderLineId;
        UnitCostCents = unitCostCents;
    }
}
