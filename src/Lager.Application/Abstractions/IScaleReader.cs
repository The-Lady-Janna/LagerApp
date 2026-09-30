namespace Lager.Application.Abstractions;

/// <summary>
/// Liest den aktuellen Gewichtswert einer angeschlossenen Waage. Echte
/// Implementations sind hardware-spezifisch (Serial-Port, USB-HID, RS232,
/// vendor-specific protocols). Default ist ein NullScaleReader, der den
/// User zur manuellen Eingabe zwingt.
/// </summary>
public interface IScaleReader
{
    string DeviceName { get; }
    bool IsConnected { get; }
    Task<ScaleReading?> ReadAsync(CancellationToken ct = default);
}

public record ScaleReading(int WeightGrams, bool IsStable, DateTime At);

/// <summary>
/// Fallback wenn keine Waage konfiguriert ist. Returns IsConnected=false und
/// liefert keine Reads. Frontend zeigt ein Texteingabe-Feld als Plan B.
/// </summary>
public class NullScaleReader : IScaleReader
{
    public string DeviceName => "Keine Waage konfiguriert";
    public bool IsConnected => false;
    public Task<ScaleReading?> ReadAsync(CancellationToken ct = default) => Task.FromResult<ScaleReading?>(null);
}
