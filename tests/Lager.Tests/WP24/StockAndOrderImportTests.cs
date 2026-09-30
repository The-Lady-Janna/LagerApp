using System.Net;
using System.Net.Http.Json;
using Lager.Contracts.Orders;
using Lager.Domain.Stock;
using Lager.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static Lager.Tests.WP24.Wp24Support;

namespace Lager.Tests.WP24;

/// <summary>
/// Bestandsimport (<c>POST /api/import/stock</c>): gebucht ausschließlich über StockBooking (Ledger, ReferenceType "CsvImport"), die
/// Invariante "Summe der Buchungen = Bestand" bleibt erhalten, der Trockenlauf ändert nichts. Bestellimport: neue Bestellungen über
/// den OrderService, vorhandene Bestellnummern werden als Fehler gemeldet.
/// </summary>
public class StockAndOrderImportTests : IClassFixture<ImportExportFixture>
{
    private readonly ImportExportFixture _fx;

    public StockAndOrderImportTests(ImportExportFixture fixture) => _fx = fixture;

    [Fact]
    public async Task A_dry_run_with_an_unknown_SKU_lists_that_row_and_changes_nothing()
    {
        var article = await _fx.W.AddArticleAsync(WorldBuilder.Unique("STK"));
        var bin = _fx.Warehouse.PickA;
        var auditBefore = await AuditCountAsync(_fx.W);
        var csv = Csv("Sku;Location;Quantity",
            $"{article.Sku};{bin.Code};10",
            $"GIBT-ES-NICHT-{article.Sku};{bin.Code};5");

        var result = await ImportAsync(_fx.Admin, "stock", csv, dryRun: true);

        Assert.True(result.DryRun);
        Assert.False(result.Applied);
        Assert.Equal((2, 1, 1), (result.Rows, result.Created, result.ErrorCount));
        Assert.Equal("1 neu, 0 aktualisiert, 1 Fehler", result.Summary);
        var error = Assert.Single(result.Errors);
        Assert.Equal((3, "unknown_sku"), (error.Line, error.Code));                               // Zeile 3 der Datei (Kopfzeile = 1)
        Assert.Contains("GIBT-ES-NICHT", error.Message);

        // nichts gebucht: kein Bestand, keine Bewegung, kein Audit
        Assert.Empty(await _fx.W.StockRowsAsync(article.Id));
        Assert.Empty(await _fx.W.MovementsAsync(article.Id));
        Assert.Equal(auditBefore, await AuditCountAsync(_fx.W));
    }

    [Fact]
    public async Task The_import_books_the_difference_through_the_ledger_and_keeps_ledger_and_stock_consistent()
    {
        var article = await _fx.W.AddArticleAsync(WorldBuilder.Unique("LED"), priceCents: 250);
        var other = await _fx.W.AddArticleAsync(WorldBuilder.Unique("LED"));
        var (binA, binB) = (_fx.Warehouse.PickA, _fx.Warehouse.PickB);
        var expiry = WorldBuilder.InDays(90);
        var csv = Csv("Sku;Location;Quantity;LotNumber;ExpiryDate",
            $"{article.Sku};{binA.Code};10;L-1;{expiry:yyyy-MM-dd}",
            $"{article.Sku};{binA.Code};4;L-2;{expiry.AddDays(30):dd.MM.yyyy}",
            $"{article.Sku};{binB.Code};7;;",
            $"{other.Sku};{binB.Code};3;;");

        // 1. anlegen
        var first = await ImportAsync(_fx.Admin, "stock", csv, dryRun: false);
        Assert.True(first.Applied);
        Assert.Equal((4, 0, 0), (first.Created, first.Updated, first.ErrorCount));
        Assert.Equal(21, await _fx.W.QuantityAsync(article.Id));
        Assert.Equal(10, (await _fx.W.StockRowsAsync(article.Id)).Single(r => r.LotNumber == "L-1").Quantity);
        Assert.Equal(expiry, (await _fx.W.StockRowsAsync(article.Id)).Single(r => r.LotNumber == "L-1").ExpiryDate);

        var movements = await _fx.W.MovementsAsync(article.Id);
        Assert.Equal(3, movements.Count);                                                      // je Zeile ein Zugang im Ledger
        Assert.All(movements, m =>
        {
            Assert.Equal(StockMovementReason.Adjust, m.Reason);
            Assert.Equal("CsvImport", m.ReferenceType);
            Assert.Equal(first.ImportId, m.ReferenceId);                                       // gemeinsame Kennung des Imports
            Assert.Equal(250, m.UnitCostCents);                                                // Kosten-Snapshot des Artikels
        });
        Assert.Empty(await _fx.W.LedgerViolationsAsync(article.Id));

        // 2. dieselbe Datei: nichts zu tun, keine neue Bewegung
        var again = await ImportAsync(_fx.Admin, "stock", csv, dryRun: false);
        Assert.False(again.Applied);
        Assert.Equal((0, 0, 4), (again.Created, again.Updated, again.Unchanged));
        Assert.Equal(3, (await _fx.W.MovementsAsync(article.Id)).Count);

        // 3. Sollbestand ändern: die DIFFERENZ wird gebucht (10 -> 4 = -6, 7 -> 9 = +2), Zeilen der Datenbank ohne Zeile in der Datei bleiben
        var lower = Csv("Sku;Location;Quantity;LotNumber;ExpiryDate",
            $"{article.Sku};{binA.Code};4;L-1;{expiry:yyyy-MM-dd}",
            $"{article.Sku};{binB.Code};9;;");
        var third = await ImportAsync(_fx.Admin, "stock", lower, dryRun: false);
        Assert.Equal((0, 2, 0), (third.Created, third.Updated, third.ErrorCount));
        Assert.Equal(4 + 4 + 9, await _fx.W.QuantityAsync(article.Id));                          // L-2 (4) blieb unberührt
        Assert.Equal(new[] { 10, 4, 7, -6, 2 }.Order().ToArray(), (await _fx.W.MovementsAsync(article.Id)).Select(m => m.QuantityDelta).Order().ToArray());
        Assert.Empty(await _fx.W.LedgerViolationsAsync(article.Id));
        Assert.Empty(await _fx.W.LedgerViolationsAsync(other.Id));
    }

    [Fact]
    public async Task Bad_stock_rows_are_reported_and_block_the_import_unless_skipErrors_is_set()
    {
        var article = await _fx.W.AddArticleAsync(WorldBuilder.Unique("BAD"));
        var bin = _fx.Warehouse.PickA;
        var bundleParent = await _fx.W.AddArticleAsync(WorldBuilder.Unique("BUN"));
        await _fx.W.DbAsync(async db =>
        {
            var parent = await db.Articles.FirstAsync(a => a.Id == bundleParent.Id);
            parent.ReplaceBundleComponents(new[] { (article.Id, 2) });
            await db.SaveChangesAsync();
        });
        var csv = Csv("Sku;Location;Quantity;LotNumber;ExpiryDate",
            $"{article.Sku};{bin.Code};5;;",                              // 2: gültig
            $"{article.Sku};{bin.Code};-3;;",                             // 3: negative Menge
            $"{article.Sku};KEIN-PLATZ;3;;",                              // 4: unbekannter Lagerplatz
            $"{article.Sku};{bin.Code};;;",                               // 5: Menge fehlt
            $"{article.Sku};{bin.Code};2,5;;",                            // 6: keine ganze Zahl
            $"{article.Sku};{bin.Code};5;;",                              // 7: dieselbe Zeile wie 2
            $"{article.Sku};{bin.Code};5;L-X;31.02.2030",                 // 8: ungültiges Datum
            $"{bundleParent.Sku};{bin.Code};5;;",                         // 9: Bundles haben keinen eigenen Bestand
            $";{bin.Code};5;;");                                          // 10: SKU fehlt
        var dry = await ImportAsync(_fx.Admin, "stock", csv, dryRun: true);

        Assert.Equal(new Dictionary<int, string>
        {
            [3] = "negative_quantity", [4] = "unknown_location", [5] = "quantity_missing", [6] = "invalid_number", [7] = "duplicate_row",
            [8] = "invalid_date", [9] = "article_is_bundle", [10] = "sku_missing",
        }, dry.Errors.ToDictionary(e => e.Line, e => e.Code));
        Assert.Equal((1, 8), (dry.Created, dry.ErrorCount));

        var blocked = await ImportAsync(_fx.Admin, "stock", csv, dryRun: false);
        Assert.False(blocked.Applied);
        Assert.Empty(await _fx.W.StockRowsAsync(article.Id));                                    // auch die gültige Zeile 2 wurde nicht gebucht

        var skipped = await ImportAsync(_fx.Admin, "stock", csv, dryRun: false, skipErrors: true);
        Assert.True(skipped.Applied);
        Assert.Equal(5, await _fx.W.QuantityAsync(article.Id));
        Assert.Empty(await _fx.W.LedgerViolationsAsync(article.Id));
    }

    [Fact]
    public async Task One_lot_has_one_expiry_date_a_conflicting_row_is_an_error()
    {
        var article = await _fx.W.AddArticleAsync(WorldBuilder.Unique("LOT"));
        var bin = _fx.Warehouse.PickA;
        var csv = Csv("Sku;Location;Quantity;LotNumber;ExpiryDate",
            $"{article.Sku};{bin.Code};5;L-1;2030-01-31",
            $"{article.Sku};{bin.Code};5;L-1;2030-02-28");

        var result = await ImportAsync(_fx.Admin, "stock", csv, dryRun: true);

        var error = Assert.Single(result.Errors);
        Assert.Equal((3, "lot_expiry_mismatch"), (error.Line, error.Code));
        Assert.Contains("eine Charge hat genau ein MHD", error.Message);
    }

    [Fact]
    public async Task Orders_are_created_through_the_order_service_and_existing_numbers_are_reported_as_duplicates()
    {
        var a = await _fx.W.AddArticleAsync(WorldBuilder.Unique("ORD"));
        var b = await _fx.W.AddArticleAsync(WorldBuilder.Unique("ORD"));
        var prefix = WorldBuilder.Unique("IMP");
        var existing = $"{prefix}-EXIST";
        Assert.Equal(HttpStatusCode.Created, (await _fx.Admin.PostAsJsonAsync("/api/orders/manual",
            new CreateOrderRequest(existing, null, new[] { new CreateOrderLineRequest(a.Id, 1) }))).StatusCode);
        var withRef = $"{prefix}-REF";
        Assert.Equal(HttpStatusCode.Created, (await _fx.Admin.PostAsJsonAsync("/api/orders/manual",
            new CreateOrderRequest(withRef, null, new[] { new CreateOrderLineRequest(a.Id, 1) }, ExternalReference: "SHOP-7"))).StatusCode);

        var csv = Csv("OrderNumber;Sku;Quantity;CustomerReference;Priority;DueDate;ExternalReference",
            $"{prefix}-1;{a.Sku};3;Kunde A;2;24.12.2026;SHOP-1",
            $"{prefix}-1;{b.Sku};2;;;;",                                   // 3: zweite Position derselben Bestellung
            $"{prefix}-2;{b.Sku};1;;;;",                                   // 4: eine einzeilige Bestellung
            $"{existing};{a.Sku};1;;;;",                                   // 5: Bestellnummer gibt es schon
            $"{withRef};{a.Sku};1;;;;SHOP-7",                              // 6: dieselbe externe Referenz: eine Wiederholung, unverändert
            $"{prefix}-3;GIBT-ES-NICHT;1;;;;",                             // 7: unbekannte SKU
            $"{prefix}-4;{a.Sku};0;;;;",                                   // 8: Menge 0
            $"{prefix}-5;{a.Sku};1;;7;;");                                 // 9: Priorität außerhalb 0..3
        var dry = await ImportAsync(_fx.Admin, "orders", csv, dryRun: true);

        Assert.Equal((2, 0, 1, 4), (dry.Created, dry.Updated, dry.Unchanged, dry.ErrorCount));
        Assert.Equal("2 neu, 0 aktualisiert, 4 Fehler", dry.Summary);
        Assert.Equal(new Dictionary<int, string>
        {
            [5] = "duplicate_order_number", [7] = "unknown_sku", [8] = "invalid_quantity", [9] = "invalid_priority",
        }, dry.Errors.ToDictionary(e => e.Line, e => e.Code));
        Assert.DoesNotContain(await OrdersAsync(), o => o.OrderNumber.StartsWith($"{prefix}-1"));   // Trockenlauf: nichts angelegt

        var blocked = await ImportAsync(_fx.Admin, "orders", csv, dryRun: false);
        Assert.False(blocked.Applied);
        Assert.DoesNotContain(await OrdersAsync(), o => o.OrderNumber.StartsWith($"{prefix}-1"));

        var applied = await ImportAsync(_fx.Admin, "orders", csv, dryRun: false, skipErrors: true);
        Assert.True(applied.Applied);
        Assert.Equal((2, 4), (applied.Created, applied.ErrorCount));
        var orders = (await OrdersAsync()).Where(o => o.OrderNumber.StartsWith(prefix)).ToDictionary(o => o.OrderNumber);
        var one = orders[$"{prefix}-1"];
        Assert.Equal(("New", "Kunde A", 2, "SHOP-1"), (one.Status, one.CustomerReference, one.Priority, one.ExternalReference));
        Assert.Equal(new DateTime(2026, 12, 24), one.DueDate!.Value.Date);
        Assert.Equal(new[] { (a.Sku, 3), (b.Sku, 2) }.OrderBy(x => x.Item1, StringComparer.Ordinal).ToArray(),
            one.Lines.Select(l => (l.ArticleSku, l.Quantity)).OrderBy(x => x.ArticleSku, StringComparer.Ordinal).ToArray());
        Assert.Single(orders[$"{prefix}-2"].Lines);
        Assert.False(orders.ContainsKey($"{prefix}-3"));                                          // fehlerhafte Zeilen sind nicht entstanden
    }

    private async Task<List<OrderDto>> OrdersAsync() => (await _fx.Admin.GetFromJsonAsync<List<OrderDto>>("/api/orders"))!;
}
