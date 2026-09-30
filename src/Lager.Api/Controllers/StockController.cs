using Lager.Application.Stock;
using Lager.Contracts.Stock;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lager.Api.Controllers;

/// <summary>Bestand je Artikel, Lagerplatz und Charge. Lesen darf jeder Angemeldete, Korrigieren nur die Rolle Manager.</summary>
[ApiController]
[Route("api/stock")]
[Authorize]
public class StockController : ControllerBase
{
    private readonly StockService _service;

    public StockController(StockService service) => _service = service;

    /// <summary>Listet alle Bestandszeilen.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<StockItemDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<StockItemDto>> List(CancellationToken ct) => _service.ListAsync(ct);

    /// <summary>Liefert den Bestand je Artikel (Summe über alle Lagerplätze).</summary>
    [HttpGet("summary")]
    [ProducesResponseType<IReadOnlyList<StockSummaryDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<StockSummaryDto>> Summary(CancellationToken ct) => _service.SummaryAsync(ct);

    /// <summary>Listet die Bestandszeilen eines Artikels.</summary>
    /// <param name="articleId">Id des Artikels.</param>
    [HttpGet("article/{articleId:guid}")]
    [ProducesResponseType<IReadOnlyList<StockItemDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<StockItemDto>> ForArticle(Guid articleId, CancellationToken ct) =>
        _service.ListForArticleAsync(articleId, ct);

    /// <summary>Korrigiert den Bestand manuell (Zugang oder Abgang).</summary>
    /// <remarks>
    /// Läuft über den einheitlichen Buchungsweg chargengenau: ein Zugang bucht auf die Bestandszeile der genannten Charge (samt MHD), ein
    /// Abgang braucht genug Bestand (409 <c>insufficient_stock</c>). Dieselbe Charge mit anderem MHD ist 409 (<c>lot_expiry_mismatch</c>),
    /// ein unbekannter Artikel oder Lagerplatz 409 (<c>invalid_reference</c>). Die Antwort ist die betroffene Bestandszeile.
    /// </remarks>
    [HttpPost("adjust")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<StockItemDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public Task<StockItemDto> Adjust([FromBody] AdjustStockRequest request, CancellationToken ct) =>
        _service.AdjustAsync(request, ct);

    /// <summary>Listet die Bestandswarnungen: Artikel unter Mindest- oder Meldebestand.</summary>
    [HttpGet("alerts")]
    [ProducesResponseType<IReadOnlyList<StockAlertDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<StockAlertDto>> Alerts(CancellationToken ct) => _service.AlertsAsync(ct);
}
