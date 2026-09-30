using Lager.Domain.Common;

namespace Lager.Domain.Stock;

public enum StockMovementReason
{
    Inbound = 1,           // Wareneingang
    Pick = 2,              // Pickliste pack/abschluss
    Return = 3,            // Retoure (Sellable zurück)
    Inventory = 4,         // Inventur-Reconcile
    ReplenishmentOut = 5,  // Quellabbuchung Replenishment
    ReplenishmentIn = 6,   // Zielzubuchung Replenishment
    Adjust = 7,            // Manuelle Stock-Korrektur
    BinMove = 8,           // Verschieben zwischen Bins (zukünftig)
    ReturnB = 9,           // Retoure B-Ware: Sperrbuchung (Zugang und sofortige Sperrung, netto 0), kein Verkaufsbestand
    ReturnScrap = 10,      // Retoure Defekt/Vernichtung: Ausschussbuchung (Zugang und Ausschuss, netto 0), kein Verkaufsbestand
}

/// <summary>
/// Append-only Bewegungs-Ledger. Für jede Stock-Mutation (Quantity-Änderung
/// auf einem StockItem) wird genau ein Eintrag geschrieben. Geliefert wird:
///   - signed QuantityDelta (positiv = Zugang, negativ = Abgang)
///   - UnitCostCents als Snapshot des Einkaufspreises zum Buchungszeitpunkt
///   - Reason + Referenz auf den auslösenden Vorgang (PO/Pick/Return/...)
///   - Charge und MHD der betroffenen Bestandszeile (StockItem) - einheitlich aus der Zeile, nie aus der Anfrage
/// Invariante: Die Summe der QuantityDelta je Artikel entspricht der Summe der StockItem-Mengen (ausgenommen
/// Altbestand ohne Ledger-Historie). Sperr-/Ausschussbuchungen (ReturnB/ReturnScrap) erfüllen sie mit einem Paar
/// aus Zugang und Abgang: netto 0, also kein Zuwachs im Verkaufsbestand.
/// Damit lassen sich später aufbauen:
///   - FIFO-Cost-Layers für echte Bestandsbewertung
///   - Echte Quantity-Deltas im Stock-Trend-Chart (statt Audit-Marker)
///   - Historische Charge-Picks (LotNumber bleibt am Movement, auch wenn der
///     ursprüngliche StockItem längst verbraucht ist)
/// </summary>
public class StockMovement : Entity
{
    public DateTime At { get; private set; } = DateTime.UtcNow;
    public Guid ArticleId { get; private set; }
    public Guid BinId { get; private set; }
    /// <summary>Signed delta. +5 = 5 Stück rein, -3 = 3 Stück raus.</summary>
    public int QuantityDelta { get; private set; }
    /// <summary>Snapshot des Stückpreises zum Buchungszeitpunkt (für FIFO-Cost-Walk).</summary>
    public int UnitCostCents { get; private set; }
    public StockMovementReason Reason { get; private set; }
    public string? ReferenceType { get; private set; }
    public Guid? ReferenceId { get; private set; }
    public string? LotNumber { get; private set; }
    public DateTime? ExpiryDate { get; private set; }

    private StockMovement() { }

    public StockMovement(
        Guid articleId, Guid binId, int quantityDelta, int unitCostCents,
        StockMovementReason reason, string? referenceType = null, Guid? referenceId = null,
        string? lotNumber = null, DateTime? expiryDate = null)
    {
        if (articleId == Guid.Empty) throw new ArgumentException("ArticleId required", nameof(articleId));
        if (binId == Guid.Empty) throw new ArgumentException("BinId required", nameof(binId));
        if (quantityDelta == 0) throw new ArgumentException("QuantityDelta darf nicht 0 sein", nameof(quantityDelta));
        if (unitCostCents < 0) throw new ArgumentOutOfRangeException(nameof(unitCostCents));

        ArticleId = articleId;
        BinId = binId;
        QuantityDelta = quantityDelta;
        UnitCostCents = unitCostCents;
        Reason = reason;
        ReferenceType = referenceType;
        ReferenceId = referenceId;
        LotNumber = lotNumber;
        ExpiryDate = expiryDate;
    }
}
