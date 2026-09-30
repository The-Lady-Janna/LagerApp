namespace Lager.Contracts.Shipping;

/// <summary>
/// Sendung. Die Empfängerfelder sind die Lieferadresse der Bestellung (Kundenname und verknüpfte Adresse) zum Zeitpunkt
/// der Abfrage; ohne Kunde/Adresse an der Bestellung sind sie leer.
/// </summary>
public record ShipmentDto(
    Guid Id,
    string ShipmentNumber,
    Guid OrderId,
    string? OrderNumber,
    Guid? PickListId,
    string CarrierCode,
    string? TrackingNumber,
    string? TrackingUrl,
    int WeightGrams,
    int LengthMm,
    int WidthMm,
    int HeightMm,
    int CostCents,
    string? Notes,
    string Status,
    DateTime CreatedAt,
    DateTime? LabeledAt,
    DateTime? ShippedAt,
    DateTime? DeliveredAt,
    string? RecipientName = null,
    string? RecipientStreet = null,
    string? RecipientStreet2 = null,
    string? RecipientZip = null,
    string? RecipientCity = null,
    string? RecipientCountry = null);

/// <summary>
/// Neue Sendung für eine gepackte Bestellung. Länge, Breite, Höhe (mm) und Gewicht (g) sind Pflicht und größer als 0.
/// Empfänger ist die Lieferadresse der Bestellung (siehe <see cref="ShipmentDto"/>).
/// </summary>
public record CreateShipmentRequest(
    Guid OrderId,
    Guid? PickListId,
    string CarrierCode,
    int LengthMm,
    int WidthMm,
    int HeightMm,
    int WeightGrams,
    string? Notes = null);

/// <summary>
/// Tracking zuweisen. Bei einem Carrier ohne API (manuell) ist <paramref name="TrackingNumber"/> Pflicht; bei einem
/// konfigurierten Carrier erzeugt dessen Adapter das Label, dann wird die eigene Nummer nicht gebraucht.
/// <paramref name="TrackingUrl"/> nur als absolute http(s)-Adresse.
/// </summary>
public record AssignTrackingRequest(
    string? TrackingNumber = null,
    string? TrackingUrl = null,
    int CostCents = 0);

/// <summary>
/// Ein Carrier. Nur konfigurierte (<paramref name="IsConfigured"/>) lassen sich für neue Sendungen wählen;
/// <paramref name="Note"/> erklärt bei den übrigen, warum nicht.
/// </summary>
public record CarrierDto(string Code, string DisplayName, bool IsConfigured = true, string? Note = null);
