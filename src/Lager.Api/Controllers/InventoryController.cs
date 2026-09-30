using Lager.Application.Inventory;
using Lager.Contracts.Inventory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lager.Api.Controllers;

/// <summary>
/// Inventur (Zählung). Lesen darf jeder Angemeldete, Starten und Zählen die Rolle Receiver, Abgleichen und Abbrechen der Manager.
/// </summary>
[ApiController]
[Route("api/inventory")]
[Authorize]
public class InventoryController : ControllerBase
{
    private readonly InventoryService _service;

    public InventoryController(InventoryService service) => _service = service;

    /// <summary>Listet die Inventuren.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<InventoryCountDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<InventoryCountDto>> List(CancellationToken ct) => _service.ListAsync(ct);

    /// <summary>Liefert eine Inventur samt Zählzeilen.</summary>
    /// <param name="id">Id der Inventur.</param>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<InventoryCountDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<InventoryCountDto>> Get(Guid id, CancellationToken ct)
    {
        var dto = await _service.GetAsync(id, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Startet eine Inventur: eine Zählzeile je Bestandszeile (auf Wunsch nur eines Lagerplatzes).</summary>
    /// <remarks>Antwort 201 mit <c>Location</c> auf <c>GET /api/inventory/{id}</c>.</remarks>
    [HttpPost("start")]
    [Authorize(Policy = "Receiver")]
    [ProducesResponseType<InventoryCountDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<InventoryCountDto>> Start([FromBody] StartInventoryRequest request, CancellationToken ct)
    {
        var dto = await _service.StartAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    /// <summary>Trägt die gezählte Menge einer Zeile ein.</summary>
    /// <param name="id">Id der Inventur.</param>
    /// <param name="lineId">Id der Zählzeile.</param>
    [HttpPut("{id:guid}/lines/{lineId:guid}")]
    [Authorize(Policy = "Receiver")]
    [ProducesResponseType<InventoryCountDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<InventoryCountDto>> SetCount(Guid id, Guid lineId, [FromBody] SetCountRequest request, CancellationToken ct)
    {
        var dto = await _service.SetCountAsync(id, lineId, request, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Gleicht die Inventur ab und bucht die Differenzen in den Bestand.</summary>
    /// <remarks>Nur eine offene Inventur lässt sich abgleichen, ein zweiter Aufruf bucht nichts und antwortet 409.</remarks>
    /// <param name="id">Id der Inventur.</param>
    [HttpPost("{id:guid}/reconcile")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<InventoryCountDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<InventoryCountDto>> Reconcile(Guid id, CancellationToken ct)
    {
        var dto = await _service.ReconcileAsync(id, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Bricht eine Inventur ab, solange sie nicht abgeglichen ist.</summary>
    /// <param name="id">Id der Inventur.</param>
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
