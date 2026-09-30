using Lager.Application.Stock;
using Lager.Contracts.Stock;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lager.Api.Controllers;

/// <summary>Lagerplatz-Empfehlungen (Slotting und Einlagerung). Nur Vorschläge, es wird nichts gebucht; lesen darf jeder Angemeldete.</summary>
[ApiController]
[Route("api/slotting")]
[Authorize]
public class SlottingController : ControllerBase
{
    private readonly SlottingService _slotting;
    private readonly PutawayService _putaway;

    public SlottingController(SlottingService slotting, PutawayService putaway)
    {
        _slotting = slotting;
        _putaway = putaway;
    }

    /// <summary>Liefert Slotting-Empfehlungen (Tausch oder Verschiebung in einen leeren Bin).</summary>
    /// <remarks>
    /// Netto-Ersparnis über beide Artikel; Basis sind nur tatsächlich gepickte Positionen. <c>top</c> = Anzahl Empfehlungen (1–100).
    /// </remarks>
    /// <param name="rangeDays">Zeitraum der ausgewerteten Picks in Tagen (Standard 30).</param>
    /// <param name="top">Anzahl der Empfehlungen (Standard 10).</param>
    [HttpGet("suggestions")]
    [ProducesResponseType<IReadOnlyList<SlottingSuggestionDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<IReadOnlyList<SlottingSuggestionDto>> Suggestions([FromQuery] int rangeDays = 30, [FromQuery] int top = 10, CancellationToken ct = default) =>
        _slotting.SuggestAsync(rangeDays, top, ct);

    /// <summary>Liefert Einlagerungs-Vorschläge für einen Artikel.</summary>
    /// <remarks>
    /// Nur Bins, in die die Menge nach Maß, freiem Volumen und Restgewicht passt.
    /// <c>quantity</c> &lt;= 0 ergibt eine leere Liste; <c>top</c> wird auf 1–50 begrenzt.
    /// </remarks>
    /// <param name="articleId">Id des Artikels.</param>
    /// <param name="quantity">Einzulagernde Menge.</param>
    /// <param name="top">Anzahl der Vorschläge (Standard 5).</param>
    [HttpGet("putaway")]
    [ProducesResponseType<IReadOnlyList<PutawaySuggestionDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<IReadOnlyList<PutawaySuggestionDto>> Putaway([FromQuery] Guid articleId, [FromQuery] int quantity, [FromQuery] int top = 5, CancellationToken ct = default) =>
        _putaway.SuggestAsync(articleId, quantity, top, ct);
}
