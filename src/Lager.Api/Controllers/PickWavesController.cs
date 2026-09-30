using Lager.Application.PickLists;
using Lager.Contracts.PickLists;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lager.Api.Controllers;

/// <summary>
/// Kommissionierwellen. Fachfehler (InvalidOperationException = ungültiger Zustand, ArgumentException = ungültige
/// Eingabe, KeyNotFoundException = nicht gefunden) fangen die Actions nicht selbst ab: die zentrale Fehlerabbildung
/// der API macht daraus 409/400/404.
/// </summary>
[ApiController]
[Route("api/pick-waves")]
[Authorize]
public class PickWavesController : ControllerBase
{
    private readonly PickWaveService _service;

    public PickWavesController(PickWaveService service) => _service = service;

    /// <summary>Listet die Wellen.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<PickWaveDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<PickWaveDto>> List(CancellationToken ct) => _service.ListAsync(ct);

    /// <summary>Liefert eine Welle samt Bestellungen.</summary>
    /// <param name="id">Id der Welle.</param>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<PickWaveDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PickWaveDto>> Get(Guid id, CancellationToken ct)
    {
        var dto = await _service.GetAsync(id, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Legt eine offene Welle an.</summary>
    /// <remarks>Antwort 201 mit <c>Location</c> auf <c>GET /api/pick-waves/{id}</c>.</remarks>
    [HttpPost]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<PickWaveDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PickWaveDto>> Create([FromBody] CreatePickWaveRequest req, CancellationToken ct)
    {
        var dto = await _service.CreateAsync(req, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    /// <summary>Nimmt Bestellungen in eine offene Welle auf.</summary>
    /// <remarks>Antwort 200 mit der aktualisierten Welle: die Bestellungen sind schon vorhanden, sie werden der Welle nur zugeordnet.</remarks>
    /// <param name="id">Id der Welle.</param>
    [HttpPost("{id:guid}/orders")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<PickWaveDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PickWaveDto>> AddOrders(Guid id, [FromBody] AddOrdersToWaveRequest req, CancellationToken ct)
    {
        var dto = await _service.AddOrdersAsync(id, req, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Nimmt eine Bestellung aus einer offenen Welle heraus.</summary>
    /// <remarks>Antwort 200 mit der aktualisierten Welle.</remarks>
    /// <param name="id">Id der Welle.</param>
    /// <param name="orderId">Id der Bestellung.</param>
    [HttpDelete("{id:guid}/orders/{orderId:guid}")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<PickWaveDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PickWaveDto>> RemoveOrder(Guid id, Guid orderId, CancellationToken ct)
    {
        var dto = await _service.RemoveOrderAsync(id, orderId, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Gibt die Welle frei und erzeugt die Pickliste aus ihren Bestellungen.</summary>
    /// <remarks>Nur eine offene Welle lässt sich freigeben, ein zweiter Aufruf erzeugt keine zweite Liste (409).</remarks>
    /// <param name="id">Id der Welle.</param>
    [HttpPost("{id:guid}/release")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<PickWaveDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PickWaveDto>> Release(Guid id, [FromBody] ReleaseWaveRequest req, CancellationToken ct)
    {
        var dto = await _service.ReleaseAsync(id, req, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Bricht eine Welle ab.</summary>
    /// <remarks>
    /// Aus Open jederzeit, aus Released nur, solange keine ihrer Picklisten begonnen wurde (dann werden diese Listen storniert und die
    /// Bestellungen wieder freigegeben). Abgeschlossene oder bereits abgebrochene Wellen antworten 409.
    /// </remarks>
    /// <param name="id">Id der Welle.</param>
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
