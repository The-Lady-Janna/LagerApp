using Lager.Domain.Common;

namespace Lager.Domain.Shipping;

public enum ShipmentStatus
{
    Ready = 0,       // Pack ist fertig, wartet auf Versand-Label
    Labeled = 1,     // Tracking-Nr vorhanden, Paket bereit für Übergabe
    Shipped = 2,     // an Carrier übergeben
    Delivered = 3,   // beim Empfänger (manuelle Bestätigung oder Carrier-API-Status)
    Cancelled = 9,
}

/// <summary>
/// Versandstück. Eine PickList kann mehrere Pakete erzeugen — pro Order ein
/// Shipment ist die häufigste Ausprägung. CarrierCode ist frei wählbar (DHL,
/// UPS, GLS, DPD, …) und nicht enum, damit man neue Carrier ohne Migration
/// freischalten kann.
///
/// Statusmaschine (erzwungen in den Methoden, Fehler = InvalidOperationException):
///
///   Status     | SetDimensions | AssignTracking     | MarkShipped | MarkDelivered | Cancel
///   -----------+---------------+--------------------+-------------+---------------+-----------
///   Ready      | ok            | Labeled            | Fehler      | Fehler        | Cancelled
///   Labeled    | ok            | Labeled (Re-Assign)| Shipped     | Fehler        | Cancelled
///   Shipped    | Fehler        | Fehler             | Fehler      | Delivered     | Fehler
///   Delivered  | Fehler        | Fehler             | Fehler      | Fehler        | Fehler
///   Cancelled  | Fehler        | Fehler             | Fehler      | Fehler        | Fehler
///
/// Delivered und Cancelled sind endgültig (keine Wiederbelebung). Eine bereits an den
/// Carrier übergebene (Shipped) Sendung ist nicht mehr stornierbar; das wäre ein
/// eigener Rückruf-/Retoure-Prozess. Ein "Ready mit Tracking" gibt es nicht, weil
/// AssignTracking den Status immer auf Labeled hebt — MarkShipped verlangt daher Labeled.
/// </summary>
public class Shipment : Entity
{
    public string ShipmentNumber { get; private set; } = string.Empty;
    public Guid OrderId { get; private set; }
    public Guid? PickListId { get; private set; }
    public string CarrierCode { get; private set; } = string.Empty;
    public string? TrackingNumber { get; private set; }
    public string? TrackingUrl { get; private set; }
    public int WeightGrams { get; private set; }
    public int LengthMm { get; private set; }
    public int WidthMm { get; private set; }
    public int HeightMm { get; private set; }
    public int CostCents { get; private set; }
    public string? Notes { get; private set; }
    public ShipmentStatus Status { get; private set; } = ShipmentStatus.Ready;
    public DateTime? LabeledAt { get; private set; }
    public DateTime? ShippedAt { get; private set; }
    public DateTime? DeliveredAt { get; private set; }

    private Shipment() { }

    public Shipment(string shipmentNumber, Guid orderId, Guid? pickListId, string carrierCode)
    {
        if (string.IsNullOrWhiteSpace(shipmentNumber)) throw new ArgumentException("ShipmentNumber required", nameof(shipmentNumber));
        if (orderId == Guid.Empty) throw new ArgumentException("OrderId required", nameof(orderId));
        if (string.IsNullOrWhiteSpace(carrierCode)) throw new ArgumentException("CarrierCode required", nameof(carrierCode));
        ShipmentNumber = shipmentNumber.Trim();
        OrderId = orderId;
        PickListId = pickListId;
        CarrierCode = carrierCode.Trim().ToUpperInvariant();
    }

    public void SetDimensions(int lengthMm, int widthMm, int heightMm, int weightGrams)
    {
        if (lengthMm <= 0 || widthMm <= 0 || heightMm <= 0 || weightGrams <= 0)
            throw new ArgumentException("Länge, Breite, Höhe und Gewicht müssen größer als 0 sein");
        EnsureEditable("Maße / Gewicht");
        LengthMm = lengthMm;
        WidthMm = widthMm;
        HeightMm = heightMm;
        WeightGrams = weightGrams;
        Touch();
    }

    /// <summary>
    /// Setzt die Tracking-Nummer (vom Carrier oder manuell) und schiebt den
    /// Status auf Labeled. Setzt auch die URL falls der Carrier eine liefert. Die URL muss eine absolute
    /// http(s)-Adresse sein (höchstens <see cref="MaxTrackingUrlLength"/> Zeichen): Sie wird als Link angezeigt,
    /// andere Schemata (z. B. javascript:) haben dort nichts verloren.
    /// </summary>
    public void AssignTracking(string trackingNumber, string? trackingUrl, int costCents)
    {
        if (string.IsNullOrWhiteSpace(trackingNumber))
            throw new ArgumentException("TrackingNumber required", nameof(trackingNumber));
        var url = string.IsNullOrWhiteSpace(trackingUrl) ? null : trackingUrl.Trim();
        if (url is not null && !IsHttpUrl(url))
            throw new ArgumentException("Die Tracking-URL muss eine absolute http(s)-Adresse sein", nameof(trackingUrl));
        EnsureEditable("TrackingNr");
        TrackingNumber = trackingNumber.Trim();
        TrackingUrl = url;
        CostCents = Math.Max(0, costCents);
        Status = ShipmentStatus.Labeled;
        LabeledAt = DateTime.UtcNow;
        Touch();
    }

    /// <summary>
    /// Wirft <see cref="InvalidOperationException"/>, wenn sich das Tracking nicht mehr zuweisen lässt (Sendung storniert
    /// oder schon an den Carrier übergeben). Der Aufrufer prüft das, BEVOR er einen Carrier ein Label erzeugen lässt.
    /// </summary>
    public void EnsureTrackingAssignable() => EnsureEditable("TrackingNr");

    /// <summary>Maximale Länge der Tracking-URL (auch die Spaltenbreite).</summary>
    public const int MaxTrackingUrlLength = 500;

    /// <summary>Absolute http- oder https-Adresse mit höchstens <see cref="MaxTrackingUrlLength"/> Zeichen.</summary>
    public static bool IsHttpUrl(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= MaxTrackingUrlLength
        && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    public void MarkShipped()
    {
        if (Status == ShipmentStatus.Cancelled)
            throw new InvalidOperationException("Sendung ist storniert — Versand nicht möglich");
        if (Status != ShipmentStatus.Labeled)
            throw new InvalidOperationException("Erst Tracking zuweisen, dann versenden");
        Status = ShipmentStatus.Shipped;
        ShippedAt = DateTime.UtcNow;
        Touch();
    }

    public void MarkDelivered()
    {
        if (Status == ShipmentStatus.Cancelled)
            throw new InvalidOperationException("Sendung ist storniert — Zustellung nicht möglich");
        if (Status != ShipmentStatus.Shipped)
            throw new InvalidOperationException("Versand muss erst raus sein");
        Status = ShipmentStatus.Delivered;
        DeliveredAt = DateTime.UtcNow;
        Touch();
    }

    public void Cancel()
    {
        switch (Status)
        {
            case ShipmentStatus.Shipped:
                throw new InvalidOperationException("Versendetes Paket kann nicht storniert werden — Rückruf/Retoure nutzen");
            case ShipmentStatus.Delivered:
                throw new InvalidOperationException("Geliefertes Paket kann nicht storniert werden");
            case ShipmentStatus.Cancelled:
                throw new InvalidOperationException("Sendung ist bereits storniert");
        }
        Status = ShipmentStatus.Cancelled;
        Touch();
    }

    public void SetNotes(string? notes) { Notes = notes; Touch(); }

    /// <summary>
    /// Wächter für Änderungen an Tracking/Maßen: nur solange die Sendung offen
    /// (Ready/Labeled) ist. Stornierte Sendungen sind endgültig und werden nicht
    /// per Tracking-Zuweisung wiederbelebt.
    /// </summary>
    private void EnsureEditable(string was)
    {
        switch (Status)
        {
            case ShipmentStatus.Cancelled:
                throw new InvalidOperationException($"Sendung ist storniert — {was} nicht änderbar");
            case ShipmentStatus.Shipped:
            case ShipmentStatus.Delivered:
                throw new InvalidOperationException($"Versand ist bereits raus — {was} nicht mehr änderbar");
        }
    }
}
