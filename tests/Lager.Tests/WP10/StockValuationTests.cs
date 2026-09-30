using Lager.Application.Reports;
using Lager.Domain.Stock;

namespace Lager.Tests.WP10;

/// <summary>
/// FIFO-Bestandsbewertung: Kosten aus den Zugangsbuchungen des Ledgers, Umlagerungen (Nachschub Out+In)
/// bewertungsneutral, Stammpreis nur als gekennzeichneter Fallback ohne Ledger-Historie.
/// </summary>
public class StockValuationTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Art = Guid.NewGuid();

    [Fact]
    public void Replenishment_transfer_does_not_change_the_valuation_even_after_a_price_change()
    {
        // Wareneingang 10 @ 1,00 EUR; der Stammpreis steigt später auf 1,20 EUR; der Nachschub von 10 Stück bucht
        // Out und In mit dem AKTUELLEN Stammpreis (so schreibt es ReplenishmentService).
        var ledger = new[]
        {
            Wp10.Move(Art, T0, +10, 100, StockMovementReason.Inbound),
            Wp10.Move(Art, T0.AddDays(5), -10, 120, StockMovementReason.ReplenishmentOut, Guid.NewGuid()),
            Wp10.Move(Art, T0.AddDays(5), +10, 120, StockMovementReason.ReplenishmentIn, Guid.NewGuid()),
        };

        var (value, fallback) = StockValuationCalculator.ValueArticle(10, 120, ledger);

        Assert.Equal(1000, value); // vorher 1200: die Umlagerung wurde zum neuen Preis neu bewertet
        Assert.Equal(0, fallback);
    }

    [Fact]
    public void Partial_replenishment_keeps_the_original_layers()
    {
        var task = Guid.NewGuid();
        var ledger = new[]
        {
            Wp10.Move(Art, T0, +10, 100, StockMovementReason.Inbound),
            Wp10.Move(Art, T0.AddDays(1), -5, 150, StockMovementReason.ReplenishmentOut, task),
            Wp10.Move(Art, T0.AddDays(1), +5, 150, StockMovementReason.ReplenishmentIn, task),
        };

        Assert.Equal(1000, StockValuationCalculator.ValueArticle(10, 150, ledger).ValueCents);
    }

    [Fact]
    public void Fifo_consumes_oldest_layers_first_and_values_the_rest_with_their_own_snapshot()
    {
        var ledger = new[]
        {
            Wp10.Move(Art, T0, +10, 100, StockMovementReason.Inbound),
            Wp10.Move(Art, T0.AddDays(1), +10, 200, StockMovementReason.Inbound),
            Wp10.Move(Art, T0.AddDays(2), -12, 200, StockMovementReason.Pick),
        };

        // 10 @100 + 10 @200 - 12 (10 alte + 2 neue) = 8 @200
        var (value, fallback) = StockValuationCalculator.ValueArticle(8, 999, ledger);

        Assert.Equal(1600, value);
        Assert.Equal(0, fallback);
    }

    [Fact]
    public void Stock_without_ledger_history_is_valued_at_the_master_price_and_flagged()
    {
        var (value, fallback) = StockValuationCalculator.ValueArticle(10, 250, Array.Empty<LedgerMovementSnapshot>());

        Assert.Equal(2500, value);
        Assert.Equal(10, fallback);
    }

    [Fact]
    public void Pre_ledger_stock_is_the_oldest_and_is_consumed_before_ledger_receipts()
    {
        // Ist-Bestand 8; das Ledger kennt nur +5 @200 und -4: davor lagen 7 Stück ohne Historie (Stammpreis 100).
        var ledger = new[]
        {
            Wp10.Move(Art, T0, +5, 200, StockMovementReason.Inbound),
            Wp10.Move(Art, T0.AddDays(1), -4, 200, StockMovementReason.Pick),
        };

        var (value, fallback) = StockValuationCalculator.ValueArticle(8, 100, ledger);

        // Der Pick verbraucht zuerst den Altbestand: 3 @100 (Fallback) + 5 @200
        Assert.Equal(3 * 100 + 5 * 200, value);
        Assert.Equal(3, fallback);
    }

    [Fact]
    public void Layers_are_capped_at_the_real_stock_when_the_ledger_claims_more()
    {
        var ledger = new[] { Wp10.Move(Art, T0, +10, 100, StockMovementReason.Inbound) };

        Assert.Equal(500, StockValuationCalculator.ValueArticle(5, 999, ledger).ValueCents);
    }

    [Fact]
    public void Returns_and_inventory_receipts_form_their_own_layer_with_the_booking_snapshot()
    {
        var ledger = new[]
        {
            Wp10.Move(Art, T0, +10, 100, StockMovementReason.Inbound),
            Wp10.Move(Art, T0.AddDays(1), -4, 100, StockMovementReason.Pick),
            Wp10.Move(Art, T0.AddDays(2), +2, 130, StockMovementReason.Return),
        };

        var (value, fallback) = StockValuationCalculator.ValueArticle(8, 130, ledger);

        Assert.Equal(6 * 100 + 2 * 130, value);
        Assert.Equal(0, fallback);
    }

    [Fact]
    public async Task Service_totals_include_fallback_value_currency_and_method()
    {
        var withHistory = Guid.NewGuid();
        var withoutHistory = Guid.NewGuid();
        var gateway = new FakeGateway();
        gateway.ValuationArticles.Add(new ValuationArticleSnapshot(withHistory, "SKU-A", "A", 120, 10));
        gateway.ValuationArticles.Add(new ValuationArticleSnapshot(withoutHistory, "SKU-B", "B", 50, 4));
        gateway.Ledger.Add(Wp10.Move(withHistory, T0, +10, 100, StockMovementReason.Inbound));
        gateway.Ledger.Add(Wp10.Move(withHistory, T0.AddDays(3), -10, 120, StockMovementReason.ReplenishmentOut));
        gateway.Ledger.Add(Wp10.Move(withHistory, T0.AddDays(3), +10, 120, StockMovementReason.ReplenishmentIn));

        var valuation = await new ReportService(gateway).StockValuationAsync();

        Assert.Equal(1000 + 4 * 50, valuation.TotalValueCents);
        Assert.Equal(4 * 50, valuation.FallbackValueCents);
        Assert.Equal("EUR", valuation.Currency);
        Assert.Contains("FIFO", valuation.Basis);
        Assert.Equal(new[] { "SKU-A", "SKU-B" }, valuation.Lines.Select(l => l.Sku).ToArray()); // absteigend nach Wert
        Assert.Equal(4, valuation.Lines.Single(l => l.Sku == "SKU-B").FallbackQuantity);
        Assert.Equal(0, valuation.Lines.Single(l => l.Sku == "SKU-A").FallbackQuantity);
    }
}
