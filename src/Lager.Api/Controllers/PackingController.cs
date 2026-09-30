using Lager.Application.Packing;
using Lager.Contracts.Packing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lager.Api.Controllers;

/// <summary>Verpackungsvorschläge für Bestellungen.</summary>
[ApiController]
[Route("api/packing")]
[Authorize]
public class PackingController : ControllerBase
{
    private readonly PackingService _service;

    public PackingController(PackingService service) => _service = service;

    /// <summary>Berechnet einen Verpackungsvorschlag für eine Bestellung.</summary>
    /// <remarks>
    /// Berechnet nur einen Verpackungsvorschlag und ändert keine Daten (POST wegen des Routenparameters/Rechenaufwands).
    /// Die Bestell-Detailseite zeigt den Plan jedem Nutzer, deshalb genügt hier die Anmeldung (Klassen-[Authorize]):
    /// eine Rollen-Policy würde Viewer/Picker/Receiver dort einen 403-Fehler zeigen, ohne dass etwas geschützt wird.
    /// Unbekannte Bestellung: 404.
    /// </remarks>
    /// <param name="orderId">Id der Bestellung.</param>
    [HttpPost("{orderId:guid}/plan")]
    [ProducesResponseType<PackingPlanDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PackingPlanDto>> Plan(Guid orderId, CancellationToken ct)
    {
        var dto = await _service.PlanForOrderAsync(orderId, ct);
        return dto is null ? NotFound() : dto;
    }
}
