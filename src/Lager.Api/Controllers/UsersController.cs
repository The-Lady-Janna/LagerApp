using Lager.Application.Auth;
using Lager.Contracts.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lager.Api.Controllers;

/// <summary>Benutzerverwaltung. Die ganze Klasse ist der Rolle Admin vorbehalten.</summary>
[ApiController]
[Route("api/users")]
[Authorize(Policy = "Admin")]
public class UsersController : ControllerBase
{
    private readonly UserService _service;
    public UsersController(UserService service) => _service = service;

    /// <summary>Listet die Benutzer.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<UserDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<UserDto>> List(CancellationToken ct) => _service.ListAsync(ct);

    /// <summary>Legt einen Benutzer an.</summary>
    /// <remarks>
    /// Antwort 201 mit dem neuen Benutzer. <c>Location</c> zeigt auf die Benutzerliste (<c>GET /api/users</c>), die den Benutzer enthält: es gibt
    /// keinen Abruf eines einzelnen Benutzers. Ein doppelter Benutzername ist 409, ein unbekannter Rollenname 400 (<c>unknown_role</c>).
    /// </remarks>
    [HttpPost]
    [ProducesResponseType<UserDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserDto>> Create([FromBody] CreateUserRequest req, CancellationToken ct)
    {
        var dto = await _service.CreateAsync(req, ct);
        return CreatedAtAction(nameof(List), null, dto);
    }

    /// <summary>Ändert Rollen, E-Mail und Anzeigenamen eines Benutzers.</summary>
    /// <remarks>Regelverstöße (sich selbst oder dem letzten aktiven Administrator die Admin-Rolle entziehen) sind 409 (<c>user_rule_violation</c>).</remarks>
    /// <param name="id">Id des Benutzers.</param>
    [HttpPut("{id:guid}")]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserDto>> Update(Guid id, [FromBody] UpdateUserRequest req, CancellationToken ct)
    {
        var dto = await _service.UpdateAsync(id, req, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>Setzt das Passwort eines Benutzers zurück (Admin).</summary>
    /// <remarks>Bestehende Tokens des Benutzers werden ungültig; auf Wunsch muss er das Passwort beim nächsten Login ändern.</remarks>
    /// <param name="id">Id des Benutzers.</param>
    [HttpPost("{id:guid}/reset-password")]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserDto>> ResetPassword(Guid id, [FromBody] AdminResetPasswordRequest req, CancellationToken ct)
    {
        var dto = await _service.ResetPasswordAsync(id, req, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>Aktiviert einen Benutzer.</summary>
    /// <param name="id">Id des Benutzers.</param>
    [HttpPost("{id:guid}/activate")]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserDto>> Activate(Guid id, CancellationToken ct)
    {
        var dto = await _service.SetActiveAsync(id, true, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>Deaktiviert einen Benutzer (bestehende Tokens werden sofort ungültig).</summary>
    /// <remarks>Regelverstöße (sich selbst oder den letzten aktiven Administrator deaktivieren) sind 409 (<c>user_rule_violation</c>).</remarks>
    /// <param name="id">Id des Benutzers.</param>
    [HttpPost("{id:guid}/deactivate")]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<UserDto>> Deactivate(Guid id, CancellationToken ct)
    {
        var dto = await _service.SetActiveAsync(id, false, ct);
        return dto is null ? NotFound() : Ok(dto);
    }
}
