using Lager.Application.PickLists;
using Lager.Contracts.PickLists;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lager.Api.Controllers;

/// <summary>Pickwagen-Konfigurationen (Ebenen, Maße und Traglast eines Wagens für das Kommissionieren mit Wagen). Lesen darf jeder Angemeldete, Pflegen der Manager.</summary>
[ApiController]
[Route("api/cart-configs")]
[Authorize]
public class PickCartConfigsController : ControllerBase
{
    private readonly PickCartConfigService _service;

    public PickCartConfigsController(PickCartConfigService service) => _service = service;

    /// <summary>Listet die Pickwagen-Konfigurationen.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<PickCartConfigDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<PickCartConfigDto>> List(CancellationToken ct) => _service.ListAsync(ct);

    /// <summary>Liefert eine Pickwagen-Konfiguration.</summary>
    /// <param name="id">Id der Konfiguration.</param>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<PickCartConfigDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PickCartConfigDto>> Get(Guid id, CancellationToken ct)
    {
        var dto = await _service.GetAsync(id, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Legt eine Pickwagen-Konfiguration an.</summary>
    /// <remarks>Antwort 201 mit <c>Location</c> auf <c>GET /api/cart-configs/{id}</c>.</remarks>
    [HttpPost]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<PickCartConfigDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PickCartConfigDto>> Create([FromBody] CreatePickCartConfigRequest request, CancellationToken ct)
    {
        var dto = await _service.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    /// <summary>Ändert eine Pickwagen-Konfiguration.</summary>
    /// <param name="id">Id der Konfiguration.</param>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<PickCartConfigDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PickCartConfigDto>> Update(Guid id, [FromBody] UpdatePickCartConfigRequest request, CancellationToken ct)
    {
        var dto = await _service.UpdateAsync(id, request, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Löscht eine Pickwagen-Konfiguration.</summary>
    /// <param name="id">Id der Konfiguration.</param>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var ok = await _service.DeleteAsync(id, ct);
        return ok ? NoContent() : NotFound();
    }
}
