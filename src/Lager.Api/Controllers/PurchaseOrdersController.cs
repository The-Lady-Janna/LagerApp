using Lager.Application.Inbound;
using Lager.Application.Purchasing;
using Lager.Contracts.Inbound;
using Lager.Contracts.Purchasing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lager.Api.Controllers;

/// <summary>
/// Bestellungen (Einkauf). Fachfehler (InvalidOperationException = Regelverstoß/Konflikt, ArgumentException =
/// ungültige Eingabe, KeyNotFoundException = nicht gefunden) fangen die Actions nicht selbst ab: die zentrale
/// Fehlerabbildung der API macht daraus 409/400/404.
/// </summary>
[ApiController]
[Route("api/purchase-orders")]
[Authorize]
public class PurchaseOrdersController : ControllerBase
{
    private readonly PurchaseOrderService _service;
    public PurchaseOrdersController(PurchaseOrderService service) => _service = service;

    /// <summary>Listet die Einkaufsbestellungen.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<PurchaseOrderDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<PurchaseOrderDto>> List(CancellationToken ct) => _service.ListAsync(ct);

    /// <summary>Liefert Bestellvorschläge: Artikel unter dem Meldebestand, gruppiert nach Lieferant.</summary>
    /// <remarks>
    /// Berücksichtigt auch bereits bestellte, noch nicht gelieferte Mengen (Status Sent/PartiallyReceived), damit derselbe Bedarf
    /// nach dem Versenden nicht erneut erscheint. Artikel ohne Lieferant stehen in einer eigenen Gruppe.
    /// </remarks>
    [HttpGet("suggestions")]
    [ProducesResponseType<IReadOnlyList<PurchaseSuggestionDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<PurchaseSuggestionDto>> Suggestions(CancellationToken ct) => _service.SuggestionsAsync(ct);

    /// <summary>Liefert eine Einkaufsbestellung samt Zeilen.</summary>
    /// <param name="id">Id der Bestellung.</param>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<PurchaseOrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<PurchaseOrderDto>> Get(Guid id, CancellationToken ct)
    {
        var dto = await _service.GetAsync(id, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Legt eine Einkaufsbestellung im Entwurf an.</summary>
    /// <remarks>
    /// Antwort 201 mit <c>Location</c> auf <c>GET /api/purchase-orders/{id}</c>. Ein unbekannter Lieferant oder Artikel ist 404, ein
    /// deaktivierter Lieferant 409 (<c>supplier_inactive</c>).
    /// </remarks>
    [HttpPost]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<PurchaseOrderDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PurchaseOrderDto>> Create([FromBody] CreatePurchaseOrderRequest req, CancellationToken ct)
    {
        var dto = await _service.CreateAsync(req, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    /// <summary>Fügt einer Einkaufsbestellung eine Zeile hinzu.</summary>
    /// <remarks>
    /// Antwort 201 mit der aktualisierten Bestellung. <c>Location</c> zeigt auf die Bestellung (<c>GET /api/purchase-orders/{id}</c>):
    /// Zeilen haben keine eigene Adresse, sie stehen in <c>lines</c> der Bestellung.
    /// </remarks>
    /// <param name="id">Id der Bestellung.</param>
    [HttpPost("{id:guid}/lines")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<PurchaseOrderDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PurchaseOrderDto>> AddLine(Guid id, [FromBody] AddPurchaseOrderLineRequest req, CancellationToken ct)
    {
        var dto = await _service.AddLineAsync(id, req, ct);
        return dto is null ? NotFound() : CreatedAtAction(nameof(Get), new { id }, dto);
    }

    /// <summary>Entfernt eine Zeile aus einer Einkaufsbestellung.</summary>
    /// <remarks>Antwort 200 mit der aktualisierten Bestellung.</remarks>
    /// <param name="id">Id der Bestellung.</param>
    /// <param name="lineId">Id der Zeile.</param>
    [HttpDelete("{id:guid}/lines/{lineId:guid}")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<PurchaseOrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PurchaseOrderDto>> RemoveLine(Guid id, Guid lineId, CancellationToken ct)
    {
        var dto = await _service.RemoveLineAsync(id, lineId, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>Versendet eine Einkaufsbestellung an den Lieferanten (Status Sent).</summary>
    /// <param name="id">Id der Bestellung.</param>
    [HttpPost("{id:guid}/send")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<PurchaseOrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PurchaseOrderDto>> Send(Guid id, CancellationToken ct)
    {
        var dto = await _service.SendAsync(id, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>Statuskorrektur an der Bestellzeile - bucht KEINEN Bestand (den bucht der Wareneingang aus der Bestellung).</summary>
    /// <param name="id">Id der Bestellung.</param>
    /// <param name="lineId">Id der Zeile.</param>
    [HttpPost("{id:guid}/lines/{lineId:guid}/receive")]
    [Authorize(Policy = "Receiver")]
    [ProducesResponseType<PurchaseOrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<PurchaseOrderDto>> ReceiveLine(Guid id, Guid lineId, [FromBody] ReceivePurchaseOrderLineRequest req, CancellationToken ct)
    {
        var dto = await _service.ReceiveLineAsync(id, lineId, req, ct);
        return dto is null ? NotFound() : Ok(dto);
    }

    /// <summary>Storniert eine Einkaufsbestellung, solange sie nicht vollständig empfangen ist.</summary>
    /// <param name="id">Id der Bestellung.</param>
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

/// <summary>
/// Die Brücke Bestellung → Wareneingang: <c>POST /api/purchase-orders/{id}/create-inbound</c> legt aus den offenen
/// Mengen der Bestellung einen Wareneingang (Entwurf) an; sein Buchen (POST /api/inbound/{id}/receive) schreibt die
/// empfangenen Mengen in die Bestellzeilen fort und schließt die Bestellung. Als eigener Controller (gleiche Route),
/// weil die Aktion den Wareneingangs-Dienst benutzt. Berechtigung wie der Wareneingang selbst: Receiver.
/// </summary>
[ApiController]
[Route("api/purchase-orders")]
[Authorize]
public class PurchaseOrderInboundController : ControllerBase
{
    private readonly InboundService _inbound;
    public PurchaseOrderInboundController(InboundService inbound) => _inbound = inbound;

    /// <summary>Legt aus den offenen Mengen einer Einkaufsbestellung einen Wareneingang im Entwurf an.</summary>
    /// <remarks>
    /// Antwort 201 mit <c>Location</c> auf <c>GET /api/inbound/{id}</c>. Pro Bestellung gibt es höchstens einen offenen Wareneingang, ein
    /// zweiter Aufruf (Doppelklick) ist 409. Eine unbekannte Bestellung oder ein unbekannter Lagerplatz ist 404.
    /// </remarks>
    /// <param name="id">Id der Einkaufsbestellung.</param>
    [HttpPost("{id:guid}/create-inbound")]
    [Authorize(Policy = "Receiver")]
    [ProducesResponseType<InboundShipmentDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<InboundShipmentDto>> CreateInbound(Guid id, [FromBody] CreateInboundFromPurchaseOrderRequest req, CancellationToken ct)
    {
        var dto = await _inbound.CreateFromPurchaseOrderAsync(id, req, ct);
        return dto is null
            ? NotFound()
            : CreatedAtAction(nameof(InboundController.Get), "Inbound", new { id = dto.Id }, dto);
    }
}
