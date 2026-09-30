using System.Net.Http.Json;
using Lager.Application.Reports;
using Lager.Contracts.Reports;
using Lager.Contracts.Returns;
using Lager.Domain.Stock;
using Microsoft.EntityFrameworkCore;

namespace Lager.Tests.WP32;

/// <summary>
/// Sperr- und Ausschussbuchungen der Retoure (ReturnB, ReturnScrap) stehen im Ledger als Paar aus Zugang und Abgang
/// derselben Menge (netto 0) und sind für die FIFO-Bewertung layer-neutral wie eine Umlagerung. Als normaler Zu-/Abgang
/// gewertet, entstünde ein neuer Layer zum aktuellen Preis, während der Abgang den ältesten Layer verbraucht: bei
/// geändertem Einkaufspreis verschöbe sich der Lagerwert um Menge mal Preisdifferenz.
/// </summary>
public class ReturnValuationTests : IClassFixture<Wp32Fixture>
{
    private static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Art = Guid.NewGuid();

    private readonly Wp32Fixture _fx;

    public ReturnValuationTests(Wp32Fixture fixture) => _fx = fixture;

    private static LedgerMovementSnapshot Move(DateTime at, int delta, int unitCostCents, StockMovementReason reason) =>
        new(Art, at, delta, unitCostCents, reason, Guid.NewGuid());

    [Theory]
    [InlineData(StockMovementReason.ReturnB)]
    [InlineData(StockMovementReason.ReturnScrap)]
    public void A_blocked_or_scrapped_return_does_not_change_the_valuation_after_a_price_change(StockMovementReason reason)
    {
        // Wareneingang 10 @ 5,00 EUR; der Preis steigt auf 7,00 EUR; 2 Stück kommen als B-Ware/Defekt zurück und
        // werden (Zugang + Abgang, netto 0) mit dem AKTUELLEN Preis gebucht. Der Bestand bleibt 10 @ 5,00 EUR.
        var ledger = new[]
        {
            Move(T0, +10, 500, StockMovementReason.Inbound),
            Move(T0.AddDays(5), +2, 700, reason),
            Move(T0.AddDays(5), -2, 700, reason),
        };

        var (value, fallback) = StockValuationCalculator.ValueArticle(10, 700, ledger);

        Assert.Equal(5000, value); // vorher 5400: 8 @ 5,00 + 2 @ 7,00 - die Sperrbuchung wurde zum neuen Preis neu bewertet
        Assert.Equal(0, fallback);
    }

    [Theory]
    [InlineData(StockMovementReason.ReturnB)]
    [InlineData(StockMovementReason.ReturnScrap)]
    public void The_fifo_order_of_the_real_layers_is_not_disturbed_by_a_blocked_return_in_between(StockMovementReason reason)
    {
        // 5 @ 5,00, später 5 @ 7,00; dazwischen die Sperrbuchung von 3 Stück zu 9,00 EUR; dann werden 6 Stück gepickt:
        // FIFO verbraucht 5 @ 5,00 und 1 @ 7,00 - übrig bleiben 4 @ 7,00 (die Sperrbuchung ist nie Bestand gewesen).
        var ledger = new[]
        {
            Move(T0, +5, 500, StockMovementReason.Inbound),
            Move(T0.AddDays(1), +5, 700, StockMovementReason.Inbound),
            Move(T0.AddDays(2), +3, 900, reason),
            Move(T0.AddDays(2), -3, 900, reason),
            Move(T0.AddDays(3), -6, 700, StockMovementReason.Pick),
        };

        Assert.Equal(2800, StockValuationCalculator.ValueArticle(4, 900, ledger).ValueCents);
    }

    [Fact]
    public void A_sellable_return_is_still_a_real_receipt_valued_at_its_booking_price()
    {
        // Gegenprobe: A-Ware (Reason Return) erhöht den Bestand wirklich und behält ihren Kosten-Snapshot.
        var ledger = new[]
        {
            Move(T0, +10, 500, StockMovementReason.Inbound),
            Move(T0.AddDays(5), +2, 700, StockMovementReason.Return),
        };

        Assert.Equal(10 * 500 + 2 * 700, StockValuationCalculator.ValueArticle(12, 700, ledger).ValueCents);
    }

    [Fact]
    public async Task The_stock_valuation_report_ignores_a_processed_b_grade_return_despite_a_price_change()
    {
        var stock = _fx.Stock;
        var bin = await stock.AddBinAsync(await stock.AddSiteAsync());
        var article = await stock.AddArticleAsync(priceCents: 500);
        await stock.ReceiveAsync((article, bin, 10, null, null));

        // Einkaufspreis steigt; danach kommt B-Ware zurück (Ledger: Zugang und Abgang zum neuen Preis)
        await stock.DbAsync(async db =>
        {
            var entity = await db.Articles.SingleAsync(a => a.Id == article);
            entity.SetPurchasing(null, 700);
            await db.SaveChangesAsync();
        });
        var ret = await stock.ReturnsAsync(s => s.CreateAsync(
            new CreateReturnShipmentRequest(null, null, null, new[] { new CreateReturnLineRequest(article, 2) })));
        await stock.ReturnsAsync(s => s.SetQcAsync(ret.Id, ret.Lines.Single().Id, new SetQcRequest("BGrade", null, null)));
        await stock.ReturnsAsync(s => s.ProcessAsync(ret.Id));

        // Voraussetzung: das Ledger enthält das Paar zum neuen Preis
        var movements = await stock.MovementsAsync(article);
        Assert.Equal(new[] { (10, 500, StockMovementReason.Inbound), (2, 700, StockMovementReason.ReturnB), (-2, 700, StockMovementReason.ReturnB) },
            movements.Select(m => (m.QuantityDelta, m.UnitCostCents, m.Reason)).ToArray());

        var valuation = (await _fx.Admin.GetFromJsonAsync<StockValuationDto>("/api/reports/stock-valuation"))!;
        var line = valuation.Lines.Single(l => l.ArticleId == article);

        Assert.Equal(10, line.Quantity);
        Assert.Equal(5000, line.TotalValueCents); // vorher 5400
        Assert.Equal(0, line.FallbackQuantity);
        Assert.Contains("Sperr-/Ausschussbuchungen", valuation.Basis);
    }
}
