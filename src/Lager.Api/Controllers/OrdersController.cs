using Lager.Application.Orders;
using Lager.Contracts.Orders;
using Lager.Domain.Orders;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lager.Api.Controllers;

/// <summary>
/// Bestellungen. Fachfehler (InvalidOperationException = Regelverstoß/Konflikt -> 409, ArgumentException = ungültige
/// Eingabe -> 400, KeyNotFoundException = nicht gefunden -> 404) fangen die Actions nicht selbst ab: die zentrale
/// Fehlerabbildung der API antwortet als ProblemDetails mit dem Code aus <c>exception.Data["code"]</c> (snake_case).
/// </summary>
[ApiController]
[Route("api/orders")]
[Authorize]
public class OrdersController : ControllerBase
{
    /// <summary>Header der externen Bestell-API: dieselbe Kennung liefert dieselbe Bestellung statt eines Duplikats.</summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    private readonly OrderService _service;

    public OrdersController(OrderService service) => _service = service;

    /// <summary>Listet die Bestellungen.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<OrderDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<OrderDto>> List(CancellationToken ct) => _service.ListAsync(ct);

    /// <summary>Liefert eine Bestellung samt Positionen.</summary>
    /// <param name="id">Id der Bestellung.</param>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<OrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<OrderDto>> Get(Guid id, CancellationToken ct)
    {
        var dto = await _service.GetAsync(id, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Legt eine Bestellung manuell an (Quelle <c>Manual</c>, z. B. aus der Oberfläche).</summary>
    /// <remarks>
    /// Antwort 201 mit <c>Location</c> auf <c>GET /api/orders/{id}</c>. Eine doppelte Bestellnummer ist 409 (Code <c>duplicate_order_number</c>),
    /// ein unbekannter Artikel oder Kunde eine ungültige Eingabe (400).
    /// </remarks>
    [HttpPost("manual")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<OrderDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<OrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<ActionResult<OrderDto>> CreateManual([FromBody] CreateOrderRequest request, CancellationToken ct) =>
        CreateAsync(request, OrderSource.Manual, ct);

    /// <summary>Legt eine Bestellung über die externe Bestell-API an (Quelle <c>Api</c>), wiederholbar per Idempotency-Key.</summary>
    /// <remarks>
    /// Externe Bestellungs-API. Tagt die Source als `Api` für Reporting/Filter.
    /// Auth läuft über das normale JWT und erfordert die Policy `Manager` (Rolle Manager oder Admin).
    /// Für Maschinen-zu-Maschinen ist ein eigener Service-Account-User mit Rolle Manager die saubere Lösung.
    /// Wiederholbar: <c>ExternalReference</c> im Body (oder der Header <c>Idempotency-Key</c>) macht das Anlegen
    /// idempotent - dieselbe Kennung liefert die bereits angelegte Bestellung (200) statt eines Duplikats (201 nur
    /// beim ersten Mal). Eine doppelte Bestellnummer ohne passende Kennung ist ein Konflikt (409,
    /// Code <c>duplicate_order_number</c>). Zeilen nennen den Artikel per <c>articleId</c> oder <c>sku</c>.
    /// Ein zu langer oder dem Body widersprechender Idempotency-Key ist eine ungültige Eingabe (400, Codes
    /// <c>invalid_idempotency_key</c> bzw. <c>idempotency_key_mismatch</c>).
    /// Antwort 201 mit <c>Location</c> auf <c>GET /api/orders/{id}</c>; bei einer Wiederholung 200 mit dem Header <c>Idempotent-Replayed: true</c>.
    /// </remarks>
    /// <param name="idempotencyKey">Kennung der Bestellung (Header <c>Idempotency-Key</c>, höchstens 128 Zeichen); ersetzt <c>externalReference</c> im Body.</param>
    [HttpPost]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<OrderDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<OrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrderDto>> CreateFromApi(
        [FromBody] CreateOrderRequest request, [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey, CancellationToken ct)
    {
        var key = idempotencyKey?.Trim();
        if (!string.IsNullOrEmpty(key))
        {
            if (key.Length > Order.MaxExternalReferenceLength)
                throw InvalidInput("invalid_idempotency_key",
                    $"Der Idempotency-Key darf höchstens {Order.MaxExternalReferenceLength} Zeichen lang sein");
            var inBody = request.ExternalReference?.Trim();
            if (!string.IsNullOrEmpty(inBody) && inBody != key)
                throw InvalidInput("idempotency_key_mismatch",
                    "Der Idempotency-Key und die externalReference der Bestellung müssen übereinstimmen");
            request = request with { ExternalReference = key };
        }

        return await CreateAsync(request, OrderSource.Api, ct);
    }

    private async Task<ActionResult<OrderDto>> CreateAsync(CreateOrderRequest request, OrderSource source, CancellationToken ct)
    {
        var result = await _service.CreateOrGetAsync(request, source, ct);
        if (result.Created)
            return CreatedAtAction(nameof(Get), new { id = result.Order.Id }, result.Order);

        Response.Headers["Idempotent-Replayed"] = "true";
        return Ok(result.Order);
    }

    /// <summary>Ungültige Eingabe mit maschinenlesbarem Code: die zentrale Fehlerabbildung macht daraus 400.</summary>
    private static ArgumentException InvalidInput(string code, string message) =>
        new(message) { Data = { ["code"] = code } };
}

/// <summary>
/// Storno einer Bestellung: <c>POST /api/orders/{id}/cancel</c> (Rolle Manager). Erlaubt aus New, Picking und Picked
/// (kein Bestandseffekt, Positionen verlassen die Picklisten); aus Packed/Shipped 409 mit Code
/// <c>order_not_cancellable</c>. Eigene Klasse mit demselben Routenpräfix wie <see cref="OrdersController"/>.
/// </summary>
[ApiController]
[Route("api/orders")]
[Authorize]
public class OrderCancellationController : ControllerBase
{
    private readonly OrderService _service;

    public OrderCancellationController(OrderService service) => _service = service;

    /// <summary>Storniert eine Bestellung (aus New, Picking oder Picked).</summary>
    /// <remarks>Antwort 200 mit der stornierten Bestellung; aus Packed oder Shipped 409 (<c>order_not_cancellable</c>).</remarks>
    /// <param name="id">Id der Bestellung.</param>
    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<OrderDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<OrderDto>> Cancel(Guid id, CancellationToken ct)
    {
        var dto = await _service.CancelAsync(id, ct);
        return dto is null ? NotFound() : Ok(dto);
    }
}
