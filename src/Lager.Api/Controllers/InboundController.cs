using Lager.Application.Inbound;
using Lager.Contracts.Inbound;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lager.Api.Controllers;

/// <summary>
/// Wareneingang (Lieferungen). Lesen darf jeder Angemeldete, Anlegen, Zeilen pflegen und Buchen die Rolle Receiver, Stornieren der Manager.
/// Der Wareneingang aus einer Einkaufsbestellung steht unter <c>POST /api/purchase-orders/{id}/create-inbound</c>.
/// </summary>
[ApiController]
[Route("api/inbound")]
[Authorize]
public class InboundController : ControllerBase
{
    private readonly InboundService _service;

    public InboundController(InboundService service) => _service = service;

    /// <summary>Listet die Wareneingänge.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<InboundShipmentDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<InboundShipmentDto>> List(CancellationToken ct) => _service.ListAsync(ct);

    /// <summary>Liefert einen Wareneingang samt Zeilen.</summary>
    /// <param name="id">Id des Wareneingangs.</param>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<InboundShipmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InboundShipmentDto>> Get(Guid id, CancellationToken ct)
    {
        var dto = await _service.GetAsync(id, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Legt eine Lieferung im Entwurf an.</summary>
    /// <remarks>
    /// Enthält der Request <c>lines</c>, entstehen Kopf und Zeilen atomar (jede Zeile wird
    /// wie bei <c>POST {id}/lines</c> geprüft; scheitert eine, wird nichts angelegt) - die Zeilen gehen nicht mehr still verloren.
    /// Antwort 201 mit <c>Location</c> auf <c>GET /api/inbound/{id}</c>; ein unbekannter Artikel oder Lagerplatz ist 404, ein
    /// Regelverstoß (z. B. dieselbe Charge mit verschiedenem MHD) 409.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = "Receiver")]
    [ProducesResponseType<InboundShipmentDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<InboundShipmentDto>> Create([FromBody] CreateInboundShipmentRequest request, CancellationToken ct)
    {
        var dto = await _service.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    /// <summary>Fügt einer Lieferung im Entwurf eine Zeile hinzu.</summary>
    /// <remarks>Antwort 200 mit der aktualisierten Lieferung (die Zeile hat keine eigene Adresse).</remarks>
    /// <param name="id">Id des Wareneingangs.</param>
    [HttpPost("{id:guid}/lines")]
    [Authorize(Policy = "Receiver")]
    [ProducesResponseType<InboundShipmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<InboundShipmentDto>> AddLine(Guid id, [FromBody] AddInboundLineRequest request, CancellationToken ct)
    {
        var dto = await _service.AddLineAsync(id, request, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Entfernt eine Zeile aus einer Lieferung im Entwurf.</summary>
    /// <remarks>Antwort 200 mit der aktualisierten Lieferung.</remarks>
    /// <param name="id">Id des Wareneingangs.</param>
    /// <param name="lineId">Id der Zeile.</param>
    [HttpDelete("{id:guid}/lines/{lineId:guid}")]
    [Authorize(Policy = "Receiver")]
    [ProducesResponseType<InboundShipmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<InboundShipmentDto>> RemoveLine(Guid id, Guid lineId, CancellationToken ct)
    {
        var dto = await _service.RemoveLineAsync(id, lineId, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Bucht die Lieferung in den Bestand.</summary>
    /// <remarks>
    /// Jede Zeile wird chargengenau gebucht; nur eine Lieferung im Entwurf lässt sich buchen, ein zweiter Aufruf (Doppelklick, Retry)
    /// bucht nichts und antwortet 409.
    /// </remarks>
    /// <param name="id">Id des Wareneingangs.</param>
    [HttpPost("{id:guid}/receive")]
    [Authorize(Policy = "Receiver")]
    [ProducesResponseType<InboundShipmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<InboundShipmentDto>> Receive(Guid id, CancellationToken ct)
    {
        var dto = await _service.ReceiveAsync(id, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Storniert eine Lieferung, solange sie nicht gebucht ist.</summary>
    /// <remarks>Eine bereits empfangene oder schon stornierte Lieferung ist 409.</remarks>
    /// <param name="id">Id des Wareneingangs.</param>
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
