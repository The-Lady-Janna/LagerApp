using Lager.Application.Returns;
using Lager.Contracts.Returns;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lager.Api.Controllers;

/// <summary>
/// Retouren. Fachfehler (InvalidOperationException = Regelverstoß/Konflikt, ArgumentException = ungültige Eingabe,
/// KeyNotFoundException = nicht gefunden) fangen die Actions nicht selbst ab: die zentrale Fehlerabbildung der API
/// macht daraus 409/400/404. Eine Wiederholung (Doppelklick) ist durch die Status-Guards der Retoure sicher.
/// </summary>
[ApiController]
[Route("api/returns")]
[Authorize]
public class ReturnsController : ControllerBase
{
    private readonly ReturnService _service;
    public ReturnsController(ReturnService service) => _service = service;

    /// <summary>Listet die Retouren.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ReturnShipmentDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<ReturnShipmentDto>> List(CancellationToken ct) => _service.ListAsync(ct);

    /// <summary>Liefert eine Retoure samt Zeilen.</summary>
    /// <param name="id">Id der Retoure.</param>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<ReturnShipmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReturnShipmentDto>> Get(Guid id, CancellationToken ct)
    {
        var dto = await _service.GetAsync(id, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Legt eine Retoure im Entwurf an.</summary>
    /// <remarks>
    /// Antwort 201 mit <c>Location</c> auf <c>GET /api/returns/{id}</c>. Mit Bestellbezug wird gegen die Bestellung geprüft (nur Artikel und
    /// Mengen der Lieferung); eine unbekannte Bestellung, ein unbekannter Artikel oder Lagerplatz ist 404, ein Regelverstoß 409.
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = "Receiver")]
    [ProducesResponseType<ReturnShipmentDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ReturnShipmentDto>> Create([FromBody] CreateReturnShipmentRequest req, CancellationToken ct)
    {
        var dto = await _service.CreateAsync(req, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    /// <summary>Fügt einer Retoure im Entwurf eine Zeile hinzu.</summary>
    /// <remarks>
    /// Antwort 201 mit der aktualisierten Retoure. <c>Location</c> zeigt auf die Retoure (<c>GET /api/returns/{id}</c>): Zeilen haben keine
    /// eigene Adresse, sie stehen in <c>lines</c> der Retoure.
    /// </remarks>
    /// <param name="id">Id der Retoure.</param>
    [HttpPost("{id:guid}/lines")]
    [Authorize(Policy = "Receiver")]
    [ProducesResponseType<ReturnShipmentDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ReturnShipmentDto>> AddLine(Guid id, [FromBody] AddReturnLineRequest req, CancellationToken ct)
    {
        var dto = await _service.AddLineAsync(id, req, ct);
        return dto is null ? NotFound() : CreatedAtAction(nameof(Get), new { id }, dto);
    }

    /// <summary>Trägt das Ergebnis der Qualitätsprüfung (QC) einer Zeile ein.</summary>
    /// <remarks>Ergebnis nur als benannter Wert: Pending, Sellable, BGrade, Defect oder Destroy (Groß-/Kleinschreibung egal).</remarks>
    /// <param name="id">Id der Retoure.</param>
    /// <param name="lineId">Id der Zeile.</param>
    [HttpPut("{id:guid}/lines/{lineId:guid}/qc")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<ReturnShipmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ReturnShipmentDto>> SetQc(Guid id, Guid lineId, [FromBody] SetQcRequest req, CancellationToken ct)
    {
        var dto = await _service.SetQcAsync(id, lineId, req, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>Schließt die Retoure ab und bucht je Zeile nach dem QC-Ergebnis.</summary>
    /// <remarks>
    /// Sellable geht zurück in den Bestand, BGrade wird gesperrt, Defect und Destroy sind Ausschuss. Nur ein Entwurf ohne offene (Pending)
    /// Zeilen lässt sich abschließen; ein zweiter Aufruf bucht nichts (409).
    /// </remarks>
    /// <param name="id">Id der Retoure.</param>
    [HttpPost("{id:guid}/process")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<ReturnShipmentDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ReturnShipmentDto>> Process(Guid id, CancellationToken ct)
    {
        var dto = await _service.ProcessAsync(id, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>Storniert eine Retoure, solange sie nicht abgeschlossen ist.</summary>
    /// <param name="id">Id der Retoure.</param>
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
