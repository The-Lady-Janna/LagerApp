using System.Text;
using Lager.Api.Labels;
using Lager.Application.Abstractions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lager.Api.Controllers;

/// <summary>
/// ZPL-Etiketten als Download. Die Oberfläche (Seite "Etiketten") lädt die .zpl-Datei über den
/// Axios-Client (mit Token) herunter — der User schiebt sie dann an einen Zebra-Drucker oder verwendet
/// Zebra Designer. Bürodrucker und A4-Etikettenbögen bedient dieselbe Seite direkt im Browser (Code 128 als SVG).
///
/// Alle drei Endpunkte liefern dasselbe Format: <c>text/plain; charset=utf-8</c>, Dateiname
/// <c>bin-…zpl</c> / <c>article-…zpl</c> / <c>order-…zpl</c>, optional <c>?copies=n</c> (ZPL <c>^PQ</c>, 1 bis
/// <see cref="ZplLabelRenderer.MaxCopies"/>; sonst 400). Mehrere Etiketten in einer Datei entstehen durch Aneinanderhängen
/// der einzelnen Downloads (jedes Etikett ist ein eigenes ^XA…^XZ).
/// </summary>
[ApiController]
[Route("api/labels")]
[Authorize(Policy = "Picker")]
public class LabelsController : ControllerBase
{
    /// <summary>Einheitlicher Content-Type aller ZPL-Downloads.</summary>
    public const string ZplContentType = "text/plain; charset=utf-8";

    private readonly IWarehouseRepository _warehouse;
    private readonly IArticleRepository _articles;
    private readonly IOrderRepository _orders;

    public LabelsController(IWarehouseRepository warehouse, IArticleRepository articles, IOrderRepository orders)
    {
        _warehouse = warehouse;
        _articles = articles;
        _orders = orders;
    }

    /// <summary>Liefert das ZPL-Etikett eines Lagerplatzes als Download.</summary>
    /// <param name="binId">Id des Lagerplatzes.</param>
    /// <param name="copies">Anzahl der Etiketten (1 bis <c>ZplLabelRenderer.MaxCopies</c>, Standard 1).</param>
    [HttpGet("bin/{binId:guid}.zpl")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK, "text/plain")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> BinLabel(Guid binId, [FromQuery] int copies = 1, CancellationToken ct = default)
    {
        CheckCopies(copies);
        var bin = await _warehouse.GetStorageLocationAsync(binId, ct);
        if (bin is null) return NotFound();
        return Zpl(ZplLabelRenderer.RenderBinLabel(bin.Code, copies: copies), "bin", bin.Code);
    }

    /// <summary>Liefert das ZPL-Etikett eines Artikels als Download.</summary>
    /// <param name="articleId">Id des Artikels.</param>
    /// <param name="copies">Anzahl der Etiketten (1 bis <c>ZplLabelRenderer.MaxCopies</c>, Standard 1).</param>
    [HttpGet("article/{articleId:guid}.zpl")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK, "text/plain")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ArticleLabel(Guid articleId, [FromQuery] int copies = 1, CancellationToken ct = default)
    {
        CheckCopies(copies);
        var a = await _articles.GetAsync(articleId, ct);
        if (a is null) return NotFound();
        return Zpl(ZplLabelRenderer.RenderArticleLabel(a.Sku, a.Name, copies), "article", a.Sku);
    }

    /// <summary>Liefert das ZPL-Etikett einer Bestellung als Download.</summary>
    /// <param name="orderId">Id der Bestellung.</param>
    /// <param name="copies">Anzahl der Etiketten (1 bis <c>ZplLabelRenderer.MaxCopies</c>, Standard 1).</param>
    [HttpGet("order/{orderId:guid}.zpl")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK, "text/plain")]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> OrderLabel(Guid orderId, [FromQuery] int copies = 1, CancellationToken ct = default)
    {
        CheckCopies(copies);
        var o = await _orders.GetAsync(orderId, ct);
        if (o is null) return NotFound();
        return Zpl(ZplLabelRenderer.RenderOrderLabel(o.OrderNumber, o.CustomerReference, copies), "order", o.OrderNumber);
    }

    /// <summary>Ungültige Kopienzahl: ArgumentException, der globale Handler macht daraus 400 (validation_failed).</summary>
    private static void CheckCopies(int copies)
    {
        if (copies < 1 || copies > ZplLabelRenderer.MaxCopies)
            throw new ArgumentException($"copies muss zwischen 1 und {ZplLabelRenderer.MaxCopies} liegen.");
    }

    private FileContentResult Zpl(string zpl, string kind, string code) =>
        File(Encoding.UTF8.GetBytes(zpl), ZplContentType, $"{kind}-{SafeFileName(code)}.zpl");

    /// <summary>
    /// Dateiname aus einem Code: Buchstaben, Ziffern, Punkt, Minus und Unterstrich bleiben, alles andere (Schrägstrich,
    /// Leerzeichen, Doppelpunkt …) wird zu "_" — ein Bin-Code wie "A/01" darf keinen Pfad ergeben.
    /// </summary>
    private static string SafeFileName(string code)
    {
        var sb = new StringBuilder(code.Length);
        foreach (var c in code.Trim())
            sb.Append(char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_');
        return sb.Length == 0 ? "etikett" : sb.ToString();
    }
}
