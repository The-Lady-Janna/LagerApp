using Lager.Api.Documents;
using Lager.Application.PickLists;
using Lager.Contracts.PickLists;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lager.Api.Controllers;

/// <summary>
/// Picklisten. Fachfehler (InvalidOperationException = ungültiger Zustand oder Fehlmenge, ArgumentException =
/// ungültige Eingabe, KeyNotFoundException = nicht gefunden) fangen die Actions nicht selbst ab: die zentrale
/// Fehlerabbildung der API macht daraus 409/400/404.
/// </summary>
[ApiController]
[Route("api/picklists")]
[Authorize]
public class PickListsController : ControllerBase
{
    private readonly PickListService _service;

    public PickListsController(PickListService service) => _service = service;

    /// <summary>Erzeugt eine Pickliste mit optimierter Route für die genannten Bestellungen.</summary>
    /// <remarks>
    /// Alle Bestellungen müssen den Status New haben; der Bestand wird über alle Bestellungen gemeinsam zugeteilt, eine Fehlmenge
    /// bricht ab (409). Die Bestellungen gehen auf Picking. Antwort 201 mit <c>Location</c> auf <c>GET /api/picklists/{id}</c>.
    /// </remarks>
    [HttpPost("generate")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<PickListDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PickListDto>> Generate([FromBody] GeneratePickListRequest request, CancellationToken ct)
    {
        var dto = await _service.GenerateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    /// <summary>Listet die Picklisten.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<PickListDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<PickListDto>> List(CancellationToken ct) => _service.ListAsync(ct);

    /// <summary>Liefert eine Pickliste samt Positionen und Route.</summary>
    /// <param name="id">Id der Pickliste.</param>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<PickListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PickListDto>> Get(Guid id, CancellationToken ct)
    {
        var dto = await _service.GetAsync(id, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Berechnet die Route einer offenen Pickliste neu.</summary>
    /// <remarks>Nur Pending/InProgress; die Positionen bleiben inhaltlich gleich. Bei Picked, Completed oder Cancelled 409.</remarks>
    /// <param name="id">Id der Pickliste.</param>
    [HttpPost("{id:guid}/recalculate")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<PickListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PickListDto>> Recalculate(Guid id, [FromBody] RecalculatePickListRequest request, CancellationToken ct)
    {
        var dto = await _service.RecalculateAsync(id, request, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Löscht die Picklisten ohne Buchungswirkung.</summary>
    /// <remarks>
    /// Löscht die Picklisten ohne Buchungswirkung (Pending, InProgress, Picked). Verpackte (Completed) und
    /// stornierte Listen bleiben; ihre Anzahl steht in <c>skippedCompleted</c>.
    /// </remarks>
    [HttpDelete]
    [Authorize(Policy = "Admin")]
    [ProducesResponseType<ResetPickListsResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ResetPickListsResult>> Reset(CancellationToken ct) =>
        await _service.ResetAllAsync(ct);

    /// <summary>Erzeugt eine Wagen-Pickliste: so viele offene Bestellungen, wie der gewählte Pickwagen fasst.</summary>
    /// <remarks>Antwort 201 mit <c>Location</c> auf <c>GET /api/picklists/{id}</c>; eine Fehlmenge oder unbekannte Konfiguration ist 409.</remarks>
    [HttpPost("generate-cart")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<PickListDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PickListDto>> GenerateCart([FromBody] GenerateCartPickListRequest request, CancellationToken ct)
    {
        var dto = await _service.GenerateCartAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    /// <summary>Bestätigt die gepackten Mengen und bucht den Bestand ab.</summary>
    /// <remarks>
    /// Genau einmal je Pickliste: eine bereits verpackte oder stornierte Liste antwortet 409 und bucht nichts. Nicht genannte
    /// Positionen gelten als 0 gepackt; eine Fehlmenge im Bestand ist 409.
    /// </remarks>
    /// <param name="id">Id der Pickliste.</param>
    [HttpPost("{id:guid}/pack")]
    [Authorize(Policy = "Packer")]
    [ProducesResponseType<PickListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PickListDto>> Pack(Guid id, [FromBody] PackPickListRequest request, CancellationToken ct)
    {
        var dto = await _service.PackAsync(id, request, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Meldet das Kommissionieren als abgeschlossen (Pickliste und Bestellungen auf Picked).</summary>
    /// <param name="id">Id der Pickliste.</param>
    [HttpPost("{id:guid}/mark-picked")]
    [Authorize(Policy = "Picker")]
    [ProducesResponseType<PickListDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PickListDto>> MarkPicked(Guid id, CancellationToken ct)
    {
        var dto = await _service.MarkPickingCompleteAsync(id, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Liefert den Lieferschein der Pickliste als PDF.</summary>
    /// <remarks>
    /// Generates a multi-page PDF shipping label — one page per order in the
    /// PickList. Browser typically previews this inline; the frontend forces
    /// a download via a content-disposition trick if needed.
    /// </remarks>
    /// <param name="id">Id der Pickliste.</param>
    [HttpGet("{id:guid}/shipping-label.pdf")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK, "application/pdf")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ShippingLabel(Guid id, CancellationToken ct)
    {
        var dto = await _service.GetAsync(id, ct);
        if (dto is null) return NotFound();
        var pdf = ShippingLabelRenderer.Render(dto);
        return File(pdf, "application/pdf", $"lieferschein-{dto.PickListNumber}.pdf");
    }
}
