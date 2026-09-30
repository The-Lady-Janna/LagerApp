using Lager.Application.Customers;
using Lager.Contracts.Customers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lager.Api.Controllers;

/// <summary>
/// Kunden. Fachfehler (InvalidOperationException = Regelverstoß wie doppelter Kunden-Code -> 409, ArgumentException =
/// ungültige Eingabe -> 400) fangen die Actions nicht selbst ab: die zentrale Fehlerabbildung der API antwortet als
/// ProblemDetails mit dem Code aus <c>exception.Data["code"]</c>.
/// </summary>
[ApiController]
[Route("api/customers")]
[Authorize]
public class CustomersController : ControllerBase
{
    private readonly CustomerService _service;
    public CustomersController(CustomerService service) => _service = service;

    /// <summary>Listet die Kunden.</summary>
    /// <param name="includeInactive">Auch deaktivierte Kunden (Standard: nur aktive).</param>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<CustomerDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<CustomerDto>> List([FromQuery] bool includeInactive = false, CancellationToken ct = default) =>
        _service.ListAsync(includeInactive, ct);

    /// <summary>Liefert einen Kunden samt Adressen.</summary>
    /// <param name="id">Id des Kunden.</param>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<CustomerDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CustomerDto>> Get(Guid id, CancellationToken ct)
    {
        var dto = await _service.GetAsync(id, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Legt einen Kunden an.</summary>
    /// <remarks>Antwort 201 mit <c>Location</c> auf <c>GET /api/customers/{id}</c>. Ein doppelter Kunden-Code ist 409.</remarks>
    [HttpPost]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<CustomerDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CustomerDto>> Create([FromBody] CreateCustomerRequest req, CancellationToken ct)
    {
        var dto = await _service.CreateAsync(req, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    /// <summary>Ändert die Stammdaten eines Kunden.</summary>
    /// <param name="id">Id des Kunden.</param>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<CustomerDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CustomerDto>> Update(Guid id, [FromBody] UpdateCustomerRequest req, CancellationToken ct)
    {
        var dto = await _service.UpdateAsync(id, req, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Fügt dem Kunden eine Adresse hinzu.</summary>
    /// <remarks>
    /// Antwort 201 mit dem aktualisierten Kunden. <c>Location</c> zeigt auf den Kunden (<c>GET /api/customers/{id}</c>): Adressen haben
    /// keine eigene Adresse, sie stehen in <c>addresses</c> des Kunden.
    /// </remarks>
    /// <param name="id">Id des Kunden.</param>
    [HttpPost("{id:guid}/addresses")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<CustomerDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CustomerDto>> AddAddress(Guid id, [FromBody] AddAddressRequest req, CancellationToken ct)
    {
        var dto = await _service.AddAddressAsync(id, req, ct);
        return dto is null ? NotFound() : CreatedAtAction(nameof(Get), new { id }, dto);
    }

    /// <summary>Entfernt eine Adresse des Kunden.</summary>
    /// <remarks>Antwort 200 mit dem aktualisierten Kunden (nicht 204: die Oberfläche übernimmt den neuen Stand direkt).</remarks>
    /// <param name="id">Id des Kunden.</param>
    /// <param name="addressId">Id der Adresse.</param>
    [HttpDelete("{id:guid}/addresses/{addressId:guid}")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<CustomerDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CustomerDto>> RemoveAddress(Guid id, Guid addressId, CancellationToken ct)
    {
        var dto = await _service.RemoveAddressAsync(id, addressId, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>Aktiviert einen Kunden.</summary>
    /// <param name="id">Id des Kunden.</param>
    [HttpPost("{id:guid}/activate")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<CustomerDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CustomerDto>> Activate(Guid id, CancellationToken ct)
    {
        var dto = await _service.SetActiveAsync(id, true, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>Deaktiviert einen Kunden (für neue Bestellungen gesperrt, bestehende bleiben).</summary>
    /// <param name="id">Id des Kunden.</param>
    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<CustomerDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<CustomerDto>> Deactivate(Guid id, CancellationToken ct)
    {
        var dto = await _service.SetActiveAsync(id, false, ct);
        return dto is null ? NotFound() : Ok(dto);
    }
}
