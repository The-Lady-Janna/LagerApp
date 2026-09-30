using Lager.Application.Shipping;
using Lager.Contracts.Shipping;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lager.Api.Controllers;

/// <summary>
/// Versand. Fachfehler (InvalidOperationException = Regelverstoß wie "Bestellung noch nicht gepackt" oder "Carrier
/// nicht verfügbar" -> 409, ArgumentException = ungültige Eingabe -> 400, KeyNotFoundException = nicht gefunden -> 404)
/// fangen die Actions nicht selbst ab: die zentrale Fehlerabbildung der API antwortet als ProblemDetails mit dem Code
/// aus <c>exception.Data["code"]</c>.
/// </summary>
[ApiController]
[Route("api/shipments")]
[Authorize]
public class ShipmentsController : ControllerBase
{
    private readonly ShipmentService _service;
    public ShipmentsController(ShipmentService service) => _service = service;

    /// <summary>Listet die bekannten Carrier (Versanddienstleister).</summary>
    /// <remarks>Nur die konfigurierten (<c>isConfigured</c>) lassen sich für neue Sendungen wählen.</remarks>
    [HttpGet("carriers")]
    [ProducesResponseType<IReadOnlyList<CarrierDto>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<CarrierDto>> ListCarriers() => Ok(_service.ListCarriers());

    /// <summary>Listet die Sendungen.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ShipmentDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<ShipmentDto>> List(CancellationToken ct) => _service.ListAsync(ct);

    /// <summary>Liefert eine Sendung.</summary>
    /// <param name="id">Id der Sendung.</param>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<ShipmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ShipmentDto>> Get(Guid id, CancellationToken ct)
    {
        var dto = await _service.GetAsync(id, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Legt eine Sendung für eine gepackte Bestellung an.</summary>
    /// <remarks>
    /// Voraussetzungen: der Carrier ist bekannt und konfiguriert, die Bestellung existiert und ist gepackt (Packed), Länge, Breite, Höhe und
    /// Gewicht sind angegeben. Antwort 201 mit <c>Location</c> auf <c>GET /api/shipments/{id}</c>; ein Regelverstoß ist 409.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = "Packer")]
    [ProducesResponseType<ShipmentDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ShipmentDto>> Create([FromBody] CreateShipmentRequest req, CancellationToken ct)
    {
        var dto = await _service.CreateAsync(req, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    /// <summary>Weist einer Sendung die Tracking-Nummer zu (beim Carrier mit Anbindung samt Label).</summary>
    /// <remarks>
    /// Ist der Carrier konfiguriert, erzeugt sein Adapter das Label und die Nummer; meldet er "manuell" oder gibt es keine Anbindung, ist die
    /// Tracking-Nummer der Anfrage Pflicht.
    /// </remarks>
    /// <param name="id">Id der Sendung.</param>
    [HttpPost("{id:guid}/tracking")]
    [Authorize(Policy = "Packer")]
    [ProducesResponseType<ShipmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ShipmentDto>> AssignTracking(Guid id, [FromBody] AssignTrackingRequest req, CancellationToken ct)
    {
        var dto = await _service.AssignTrackingAsync(id, req, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>Meldet die Sendung als versendet.</summary>
    /// <remarks>Sind damit alle Sendungen der Bestellung raus, wechselt die Bestellung von Packed auf Shipped.</remarks>
    /// <param name="id">Id der Sendung.</param>
    [HttpPost("{id:guid}/ship")]
    [Authorize(Policy = "Packer")]
    [ProducesResponseType<ShipmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ShipmentDto>> Ship(Guid id, CancellationToken ct)
    {
        var dto = await _service.MarkShippedAsync(id, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>Meldet die Sendung als zugestellt.</summary>
    /// <param name="id">Id der Sendung.</param>
    [HttpPost("{id:guid}/delivered")]
    [Authorize(Policy = "Packer")]
    [ProducesResponseType<ShipmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ShipmentDto>> Delivered(Guid id, CancellationToken ct)
    {
        var dto = await _service.MarkDeliveredAsync(id, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>Storniert eine offene Sendung (Ready oder Labeled).</summary>
    /// <remarks>Die Bestellung wird dadurch nie zurückgesetzt, sie bleibt Packed. Eine versendete oder gelieferte Sendung ist 409.</remarks>
    /// <param name="id">Id der Sendung.</param>
    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        var ok = await _service.CancelAsync(id, ct);
        return ok ? NoContent() : NotFound();
    }
}
