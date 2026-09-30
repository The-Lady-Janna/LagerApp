using Lager.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lager.Api.Controllers;

/// <summary>Zustand der angeschlossenen Waage.</summary>
/// <param name="DeviceName">Name des Geräts (bei fehlender Waage der Platzhalter des Fallbacks).</param>
/// <param name="IsConnected">Ob eine Waage verbunden ist; sonst gibt die Oberfläche das Gewicht von Hand ein.</param>
public sealed record ScaleStatusDto(string DeviceName, bool IsConnected);

/// <summary>
/// Endpoints für angeschlossene Hardware (Waage, Scanner-Status, …).
/// Heute nur Waage — Scanner läuft frontend-seitig über die BarcodeDetector-API.
/// </summary>
[ApiController]
[Route("api/hardware")]
[Authorize(Policy = "Packer")]
public class HardwareController : ControllerBase
{
    private readonly IScaleReader _scale;
    public HardwareController(IScaleReader scale) => _scale = scale;

    /// <summary>Liefert den Zustand der Waage.</summary>
    [HttpGet("scale/status")]
    [ProducesResponseType<ScaleStatusDto>(StatusCodes.Status200OK)]
    public ActionResult<ScaleStatusDto> ScaleStatus() => Ok(new ScaleStatusDto(_scale.DeviceName, _scale.IsConnected));

    /// <summary>Liest das aktuelle Gewicht der Waage.</summary>
    /// <remarks>200 mit Gewicht (Gramm), Stabilitätsflag und Zeitpunkt; 204, wenn gerade keine Messung vorliegt (keine Waage verbunden).</remarks>
    [HttpGet("scale/read")]
    [ProducesResponseType<ScaleReading>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult<ScaleReading>> ScaleRead(CancellationToken ct)
    {
        var r = await _scale.ReadAsync(ct);
        if (r is null) return NoContent();
        return Ok(r);
    }
}
