using Lager.Application.Reports;
using Lager.Contracts.Reports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Lager.Api.Controllers;

/// <summary>Auswertungen (Dashboard, Kennzahlen, Rückverfolgung). Lesen darf jeder Angemeldete, nur die Bestandsbewertung ist dem Manager vorbehalten.</summary>
[ApiController]
[Route("api/reports")]
[Authorize]
public class ReportsController : ControllerBase
{
    private readonly ReportService _service;

    public ReportsController(ReportService service) => _service = service;

    /// <summary>Liefert alle Kennzahlen des Dashboards in einer Antwort.</summary>
    /// <remarks>
    /// Single rollup endpoint for the dashboard. Caller picks the window in days
    /// (default 7, clamped 1–365). All KPIs are computed in one call so the
    /// frontend doesn't have to chain 5 requests.
    /// </remarks>
    /// <param name="range">Zeitraum in Tagen (Standard 7; erlaubt 1 bis 3660, intern höchstens 365).</param>
    [HttpGet("dashboard")]
    [ProducesResponseType<ReportDashboardDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<ReportDashboardDto> Dashboard([FromQuery] int range = 7, CancellationToken ct = default) =>
        _service.DashboardAsync(range, ct);

    /// <summary>Liefert die Pick-Häufigkeit je Lagerplatz (Heatmap).</summary>
    /// <remarks>Gepickte Positionen je Bin (nur Picklisten Picked/Completed mit Ist-Menge &gt; 0).</remarks>
    /// <param name="range">Zeitraum in Tagen (Standard 30).</param>
    [HttpGet("bin-heatmap")]
    [ProducesResponseType<IReadOnlyList<BinHeatPointDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<IReadOnlyList<BinHeatPointDto>> BinHeatmap([FromQuery] int range = 30, CancellationToken ct = default) =>
        _service.BinHeatmapAsync(range, ct);

    /// <summary>Listet Artikel mit Bestand, aber ohne Bewegung (Ladenhüter).</summary>
    /// <remarks>
    /// Artikel mit Bestand ohne Ledger-Bewegung seit <c>days</c> Tagen. Interne Umlagerungen (Nachschub) zählen
    /// nicht als Bewegung; LocationCount = Lagerplätze mit Bestand.
    /// </remarks>
    /// <param name="days">Tage ohne Bewegung (Standard 90).</param>
    [HttpGet("dead-stock")]
    [ProducesResponseType<IReadOnlyList<DeadStockArticleDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<IReadOnlyList<DeadStockArticleDto>> DeadStock([FromQuery] int days = 90, CancellationToken ct = default) =>
        _service.DeadStockAsync(days, ct);

    /// <summary>Liefert die ABC-Analyse der Artikel nach Pickmenge.</summary>
    /// <remarks>
    /// Klasse nach kumuliertem Anteil vor dem Artikel: A &lt; 80 %,
    /// B &lt; 95 %, sonst C; ein Einzelartikel ist A. Nur tatsächlich gepickte Picklisten (Picked/Completed), Basis ist die Ist-Pickmenge.
    /// </remarks>
    /// <param name="range">Zeitraum in Tagen (Standard 30).</param>
    [HttpGet("abc-analysis")]
    [ProducesResponseType<IReadOnlyList<AbcArticleDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<IReadOnlyList<AbcArticleDto>> AbcAnalysis([FromQuery] int range = 30, CancellationToken ct = default) =>
        _service.AbcAnalysisAsync(range, ct);

    /// <summary>Liefert den aktuellen Betriebsstand (Bestellungen und Picklisten je Status, offene Inventuren und Nachschub-Aufgaben, Bestandswarnungen) für die Live-Ansicht.</summary>
    [HttpGet("live-status")]
    [ProducesResponseType<LiveStatusDto>(StatusCodes.Status200OK)]
    public Task<LiveStatusDto> LiveStatus(CancellationToken ct = default) =>
        _service.LiveStatusAsync(ct);

    /// <summary>Liefert den Bestandsverlauf eines Artikels.</summary>
    /// <param name="articleId">Id des Artikels.</param>
    /// <param name="days">Zeitraum in Tagen (Standard 90).</param>
    [HttpGet("stock-trend/{articleId:guid}")]
    [ProducesResponseType<StockTrendDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StockTrendDto>> StockTrend(Guid articleId, [FromQuery] int days = 90, CancellationToken ct = default)
    {
        var dto = await _service.StockTrendAsync(articleId, days, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Liefert die MHD-Warnliste: abgelaufene und bald ablaufende Bestände.</summary>
    /// <remarks>
    /// Bestandszeilen (Menge &gt; 0), deren MHD abgelaufen ist oder innerhalb von <c>days</c> Tagen abläuft
    /// (Standard 30, erlaubt 1–3660 wie jeder <c>days</c>-Parameter der API, sonst 400). Je Zeile Artikel, Charge, Lagerplatz,
    /// Menge, MHD, Tage bis zum Ablauf (UTC-Kalendertage, negativ = abgelaufen) und Status (Expired / Critical bis
    /// 7 Tage / Soon). Sortiert nach MHD. Lesen darf jeder angemeldete Nutzer.
    /// </remarks>
    /// <param name="days">Vorlauf in Tagen (Standard 30).</param>
    [HttpGet("expiring")]
    [ProducesResponseType<IReadOnlyList<ExpiringStockDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IReadOnlyList<ExpiringStockDto>> Expiring([FromQuery] int days = 30, CancellationToken ct = default) =>
        await _service.ExpiringStockAsync(days, ct);

    /// <summary>Verfolgt eine Charge zurück: Wareneingänge, Bestand, Bewegungen und mögliche betroffene Bestellungen.</summary>
    /// <remarks>
    /// Alle Wareneingänge dieser LotNumber, aktueller
    /// Bestand pro Bin, alle historischen Bewegungen (Picks/Inventur/Retouren)
    /// aus dem StockMovement-Ledger — auch wenn der ursprüngliche StockItem
    /// längst verbraucht ist — und die daraus ableitbaren, möglicherweise
    /// betroffenen Bestellungen. Eine unbekannte Charge ist 404.
    /// </remarks>
    /// <param name="lotNumber">Chargennummer.</param>
    [HttpGet("charge/{lotNumber}")]
    [ProducesResponseType<ChargeTraceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ChargeTraceDto>> ChargeTrace(string lotNumber, CancellationToken ct)
    {
        var dto = await _service.ChargeTraceAsync(lotNumber, ct);
        return dto is null ? NotFound() : dto;
    }

    /// <summary>Liefert den aktuellen Lagerwert: je Artikel und insgesamt (nur Manager).</summary>
    /// <remarks>
    /// FIFO über die Zugangsbuchungen des StockMovement-Ledgers
    /// mit dem Kosten-Snapshot je Buchung (Stammpreis zum Buchungszeitpunkt, kein Rechnungs-/PO-Preis).
    /// Umlagerungen/Nachschub sind bewertungsneutral, Retouren und Inventurzugänge kommen zum Stammpreis
    /// der Buchung. Menge ohne Ledger-Historie wird zum aktuellen Stammpreis bewertet und als
    /// FallbackQuantity/FallbackValueCents ausgewiesen. Währung: immer EUR.
    /// </remarks>
    [HttpGet("stock-valuation")]
    [Authorize(Policy = "Manager")]
    [ProducesResponseType<StockValuationDto>(StatusCodes.Status200OK)]
    public Task<StockValuationDto> StockValuation(CancellationToken ct) => _service.StockValuationAsync(ct);

    /// <summary>Liefert die Picker-Performance: je Picker Picklisten, Positionen, Wegstrecke und Dauer.</summary>
    /// <remarks>
    /// Pro Picker im Zeitraum die Anzahl Picklisten,
    /// Items, Ø Wegstrecke, Ø Dauer. Picker wird aus PickList.AssignedTo
    /// gezogen (gesetzt beim MarkPickingComplete), Fallback aus Audit-Trail.
    /// </remarks>
    /// <param name="range">Zeitraum in Tagen (Standard 7).</param>
    [HttpGet("picker-performance")]
    [ProducesResponseType<PickerPerformanceDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    public Task<PickerPerformanceDto> PickerPerformance([FromQuery] int range = 7, CancellationToken ct = default) =>
        _service.PickerPerformanceAsync(range, ct);
}
