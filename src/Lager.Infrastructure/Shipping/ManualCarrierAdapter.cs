using Lager.Application.Abstractions;

namespace Lager.Infrastructure.Shipping;

/// <summary>
/// Default-Carrier ohne API-Anbindung. Tracking-Nummer wird manuell vom User
/// eingegeben (z. B. nachdem der Carrier-Webportal das Label erzeugt hat).
/// Liefert IsManual=true zurück — der Service nimmt dann die Eingabe vom Frontend.
/// </summary>
public class ManualCarrierAdapter : ICarrierAdapter
{
    public string CarrierCode => "MANUAL";
    public string DisplayName => "Manuell (Tracking-Nr per Hand)";

    public Task<CarrierLabelResult> CreateLabelAsync(CreateLabelRequest req, CancellationToken ct = default)
        => Task.FromResult(new CarrierLabelResult(IsManual: true, TrackingNumber: null, TrackingUrl: null, CostCents: 0));
}

/// <summary>
/// Platzhalter für DHL: die Carrier-API ist nicht angebunden (echte Implementierung braucht Account + REST-Calls
/// gegen api-eu.dhl.com). Der Adapter steht in der Registry, damit der Carrier-Code bekannt ist, ist aber nicht
/// konfiguriert und lässt sich nicht wählen; ein Label erzeugt er nicht.
/// </summary>
public class DhlStubAdapter : ICarrierAdapter
{
    public string CarrierCode => "DHL";
    public string DisplayName => "DHL";
    public bool IsConfigured => false;
    public string? UnavailableReason => "Die DHL-Anbindung ist nicht eingerichtet.";

    public Task<CarrierLabelResult> CreateLabelAsync(CreateLabelRequest req, CancellationToken ct = default)
        => throw CarrierNotAvailable.For(this);
}

/// <summary>Platzhalter für UPS, siehe <see cref="DhlStubAdapter"/>.</summary>
public class UpsStubAdapter : ICarrierAdapter
{
    public string CarrierCode => "UPS";
    public string DisplayName => "UPS";
    public bool IsConfigured => false;
    public string? UnavailableReason => "Die UPS-Anbindung ist nicht eingerichtet.";

    public Task<CarrierLabelResult> CreateLabelAsync(CreateLabelRequest req, CancellationToken ct = default)
        => throw CarrierNotAvailable.For(this);
}

/// <summary>Fehler für einen nicht konfigurierten Carrier (Regelverstoß, Code <c>carrier_not_available</c>).</summary>
internal static class CarrierNotAvailable
{
    public static InvalidOperationException For(ICarrierAdapter adapter)
    {
        var ex = new InvalidOperationException(
            $"Carrier {adapter.DisplayName} ist nicht verfügbar: {adapter.UnavailableReason ?? "nicht konfiguriert"}");
        ex.Data["code"] = "carrier_not_available";
        return ex;
    }
}

public class CarrierRegistry : ICarrierRegistry
{
    public IReadOnlyList<ICarrierAdapter> All { get; }
    public CarrierRegistry(IEnumerable<ICarrierAdapter> adapters) => All = adapters.ToList();
    public ICarrierAdapter? Get(string code) =>
        All.FirstOrDefault(a => a.CarrierCode.Equals(code, StringComparison.OrdinalIgnoreCase));
}
