using Lager.Api.Errors;
using Lager.Application.Articles;
using Lager.Contracts.Articles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lager.Api.Controllers;

/// <summary>Artikelstamm. Lesen darf jeder Angemeldete, Anlegen und Ändern nur die Rolle Manager.</summary>
[ApiController]
[Route("api/articles")]
[Authorize]
public class ArticlesController : ControllerBase
{
    private readonly ArticleService _service;

    public ArticlesController(ArticleService service) => _service = service;

    /// <summary>Listet die Artikel, auf Wunsch gefiltert nach einem Suchtext.</summary>
    /// <remarks>
    /// Mit <c>search</c> nur die Artikel, in deren Name, SKU, Alternativ-SKU oder GTIN der Suchtext vorkommt
    /// (Teilstring, Groß-/Kleinschreibung egal). Weiter ohne Paging.
    /// </remarks>
    /// <param name="search">Suchtext (optional).</param>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<ArticleDto>>(StatusCodes.Status200OK)]
    public Task<IReadOnlyList<ArticleDto>> List([FromQuery] string? search, CancellationToken ct) => _service.ListAsync(search, ct);

    /// <summary>Liefert einen Artikel.</summary>
    /// <param name="id">Id des Artikels.</param>
    [HttpGet("{id:guid}")]
    [ProducesResponseType<ArticleDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ArticleDto>> Get(Guid id, CancellationToken ct)
    {
        var dto = await _service.GetAsync(id, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Legt einen Artikel an.</summary>
    /// <remarks>Antwort 201 mit <c>Location</c> auf <c>GET /api/articles/{id}</c>. Eine doppelte SKU ist 409 (<c>duplicate</c>).</remarks>
    [HttpPost]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<ArticleDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ArticleDto>> Create([FromBody] CreateArticleRequest request, CancellationToken ct)
    {
        var dto = await _service.CreateAsync(request, ct);
        return CreatedAtAction(nameof(Get), new { id = dto.Id }, dto);
    }

    /// <summary>Ändert die Stammdaten eines Artikels (die SKU bleibt).</summary>
    /// <param name="id">Id des Artikels.</param>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<ArticleDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ArticleDto>> Update(Guid id, [FromBody] UpdateArticleRequest request, CancellationToken ct)
    {
        var dto = await _service.UpdateAsync(id, request, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Löst einen gescannten Code auf einen Artikel auf (Kamera, Handscanner).</summary>
    /// <remarks>
    /// Lesen darf jeder Angemeldete wie bei der Artikelliste.
    /// GTIN/EAN (auch als UPC-A oder GTIN-14 geschrieben), SKU (Groß-/Kleinschreibung egal) oder, wenn beides nichts findet,
    /// Alternativ-SKU. 200 = der Artikel, 404 = unbekannter Code, 409 (Code <c>ambiguous_code</c>, Feld <c>candidates</c> = Liste
    /// der Artikel) = der Code passt auf mehrere Artikel; der Aufrufer lässt wählen.
    /// Die Route nimmt den Rest des Pfads als Code (<c>**</c>): SKUs mit Schrägstrich ("REG/A-01") sind gültige Codes.
    /// </remarks>
    /// <param name="code">GTIN, SKU oder Alternativ-SKU.</param>
    [HttpGet("by-code/{**code}")]
    [ProducesResponseType<ArticleDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<ArticleDto>> ByCode(string code, CancellationToken ct)
    {
        // Ein kodierter Schrägstrich (%2F) bleibt im Pfad stehen (weder Kestrel noch der TestServer dekodieren ihn): hier auflösen.
        code = code.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase);
        var matches = await _service.ResolveByCodeAsync(code, ct);
        if (matches.Count == 1) return matches[0];

        var shown = code.Trim();
        if (matches.Count == 0)
            return Failure(StatusCodes.Status404NotFound, ProblemCodes.NotFound, $"Kein Artikel zum Code '{shown}' gefunden.");

        return Failure(StatusCodes.Status409Conflict, "ambiguous_code",
            $"Der Code '{shown}' passt auf {matches.Count} Artikel: {string.Join(", ", matches.Select(m => m.Sku))}. Bitte den Artikel wählen.",
            candidates: matches);
    }

    /// <summary>Fehlerantwort im einheitlichen Format (RFC 7807 mit Code und Korrelations-ID), optional mit den Kandidaten.</summary>
    private ObjectResult Failure(int status, string code, string detail, IReadOnlyList<ArticleDto>? candidates = null)
    {
        var problem = Problems.Create(HttpContext, status, code, detail);
        if (candidates is not null) problem.Extensions["candidates"] = candidates;
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { Problems.ContentType } };
    }
}
