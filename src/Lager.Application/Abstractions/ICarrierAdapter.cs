namespace Lager.Application.Abstractions;

/// <summary>
/// Carrier-spezifischer Adapter. Implementierungen pro Carrier (DHL/UPS/…)
/// kapseln die jeweilige API. Heute gibt es nur den `ManualCarrierAdapter`
/// (Tracking-Nr wird per Hand eingegeben); die Adapter für DHL und UPS sind nicht angebunden
/// (<see cref="IsConfigured"/> = false) und lassen sich nicht wählen. Ein konfigurierter Adapter bekommt beim
/// Zuweisen des Trackings die Chance, das Label zu erzeugen (<see cref="CreateLabelAsync"/>).
/// </summary>
public interface ICarrierAdapter
{
    /// <summary>Carrier-Code, z. B. "MANUAL", "DHL", "UPS", "GLS".</summary>
    string CarrierCode { get; }

    /// <summary>Anzeige-Name fürs UI (ohne Zusätze wie "Stub" oder "TODO").</summary>
    string DisplayName { get; }

    /// <summary>
    /// Ob der Carrier einsatzbereit ist (Zugangsdaten vorhanden, API angebunden). Nur solche Carrier lassen sich für
    /// neue Sendungen wählen; das UI zeigt die übrigen als "nicht verfügbar". Standard: verfügbar.
    /// </summary>
    bool IsConfigured => true;

    /// <summary>Erklärung für Nutzer, warum der Carrier nicht verfügbar ist (nur relevant bei <see cref="IsConfigured"/> = false).</summary>
    string? UnavailableReason => null;

    /// <summary>
    /// Erzeugt ein Versandlabel und liefert Tracking-Nr + optional URL +
    /// Versandkosten zurück. Für ManualCarrier liefert das nichts und
    /// signalisiert dem Service, die Nummer manuell anzunehmen.
    /// </summary>
    Task<CarrierLabelResult> CreateLabelAsync(CreateLabelRequest req, CancellationToken ct = default);
}

public record CreateLabelRequest(
    Guid ShipmentId,
    string ShipmentNumber,
    int WeightGrams,
    int LengthMm, int WidthMm, int HeightMm,
    string RecipientName,
    string RecipientStreet,
    string RecipientZip,
    string RecipientCity,
    string RecipientCountry,
    string? RecipientStreet2 = null);

public record CarrierLabelResult(
    bool IsManual,
    string? TrackingNumber,
    string? TrackingUrl,
    int CostCents);

public interface ICarrierRegistry
{
    IReadOnlyList<ICarrierAdapter> All { get; }
    ICarrierAdapter? Get(string code);
}
