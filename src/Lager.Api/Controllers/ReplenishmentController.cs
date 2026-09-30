using Lager.Application.Stock;
using Lager.Contracts.Stock;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lager.Api.Controllers;

/// <summary>
/// Nachschub: Aufgaben, die Hot-Pick-Lagerplätze mit Ware aus Reserve-Lagerplätzen auffüllen. Lesen darf jeder Angemeldete,
/// Scannen und Abbrechen der Manager, Ausführen die Rolle Picker.
/// </summary>
[ApiController]
[Route("api/replenishment")]
[Authorize]
public class ReplenishmentController : ControllerBase
{
    private readonly ReplenishmentService _service;
    public ReplenishmentController(ReplenishmentService service) => _service = service;

    /// <summary>Listet alle Nachschub-Aufgaben.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ReplenishmentTaskDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<ReplenishmentTaskDto>> List(CancellationToken ct) => _service.ListAsync(ct);

    /// <summary>Listet die offenen Nachschub-Aufgaben.</summary>
    [HttpGet("open")]
    [ProducesResponseType<IReadOnlyList<ReplenishmentTaskDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<ReplenishmentTaskDto>> ListOpen(CancellationToken ct) => _service.ListOpenAsync(ct);

    /// <summary>Prüft die Hot-Pick-Lagerplätze und legt für jeden Artikel unter der Schwelle eine Nachschub-Aufgabe an.</summary>
    /// <remarks>
    /// Quelle ist der Reserve-Lagerplatz mit der FEFO-ersten Ware; bestehende offene Aufgaben werden übersprungen. Die Antwort
    /// nennt die neu angelegten Aufgaben (eine leere Liste, wenn nichts nachzufüllen ist).
    /// </remarks>
    [HttpPost("scan")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<IReadOnlyList<ReplenishmentTaskDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<IReadOnlyList<ReplenishmentTaskDto>> Scan([FromBody] ScanReplenishmentRequest req, CancellationToken ct) =>
        _service.ScanAsync(req, ct);

    /// <summary>Meldet den Nachschub als ausgeführt und bucht die Umlagerung.</summary>
    /// <remarks>Nur eine offene Aufgabe lässt sich abschließen, ein zweiter Aufruf bucht nichts (409). Reicht der Bestand der Quelle nicht: 409 (<c>insufficient_stock</c>).</remarks>
    /// <param name="id">Id der Aufgabe.</param>
    [HttpPost("{id:guid}/complete")]
    [Authorize(Policy = "Picker")]
    [ProducesResponseType<ReplenishmentTaskDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ReplenishmentTaskDto>> Complete(Guid id, [FromBody] CompleteReplenishmentRequest req, CancellationToken ct)
    {
        var dto = await _service.CompleteAsync(id, req, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Bricht eine Nachschub-Aufgabe ab, solange sie nicht ausgeführt ist.</summary>
    /// <param name="id">Id der Aufgabe.</param>
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
