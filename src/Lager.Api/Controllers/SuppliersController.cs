using Lager.Application.Suppliers;
using Lager.Contracts.Suppliers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lager.Api.Controllers;

/// <summary>Lieferanten. Lesen darf jeder Angemeldete, Anlegen und Ändern nur die Rolle Manager.</summary>
[ApiController]
[Route("api/suppliers")]
[Authorize]
public class SuppliersController : ControllerBase
{
    private readonly SupplierService _service;
    public SuppliersController(SupplierService service) => _service = service;

    /// <summary>Listet die Lieferanten.</summary>
    /// <param name="includeInactive">Auch deaktivierte Lieferanten (Standard: nur aktive).</param>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<SupplierDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<SupplierDto>> List([FromQuery] bool includeInactive = false, CancellationToken ct = default) =>
        _service.ListAsync(includeInactive, ct);

    /// <summary>Liefert einen Lieferanten.</summary>
    /// <param name="id">Id des Lieferanten.</param>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<SupplierDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierDto>> Get(Guid id, CancellationToken ct)
    {
        var dto = await _service.GetAsync(id, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Legt einen Lieferanten an.</summary>
    /// <remarks>Antwort 201 mit <c>Location</c> auf <c>GET /api/suppliers/{id}</c>. Ein doppelter Lieferanten-Code ist 409.</remarks>
    [HttpPost]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<SupplierDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SupplierDto>> Create([FromBody] CreateSupplierRequest req, CancellationToken ct)
    {
        var dto = await _service.CreateAsync(req, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    /// <summary>Ändert die Stammdaten eines Lieferanten.</summary>
    /// <param name="id">Id des Lieferanten.</param>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<SupplierDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SupplierDto>> Update(Guid id, [FromBody] UpdateSupplierRequest req, CancellationToken ct)
    {
        var dto = await _service.UpdateAsync(id, req, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Aktiviert einen Lieferanten.</summary>
    /// <param name="id">Id des Lieferanten.</param>
    [HttpPost("{id:guid}/activate")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<SupplierDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SupplierDto>> Activate(Guid id, CancellationToken ct)
    {
        var dto = await _service.SetActiveAsync(id, true, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Deaktiviert einen Lieferanten (für neue Einkaufsbestellungen gesperrt, bestehende bleiben).</summary>
    /// <param name="id">Id des Lieferanten.</param>
    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<SupplierDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SupplierDto>> Deactivate(Guid id, CancellationToken ct)
    {
        var dto = await _service.SetActiveAsync(id, false, ct);
        return dto is null ? NotFound() : dto;
    }
}
