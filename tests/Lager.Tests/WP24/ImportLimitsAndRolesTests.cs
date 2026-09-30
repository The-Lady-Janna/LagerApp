using System.Net;
using System.Text;
using Lager.Application.ImportExport;
using Lager.Tests.Infrastructure;
using static Lager.Tests.WP24.Wp24Support;

namespace Lager.Tests.WP24;

/// <summary>
/// Rollen (nur Manager und Admin) und Grenzen der Import-Endpunkte: 5 MB, 20000 Zeilen, Fehlerliste auf 500 gekürzt, leere und
/// kaputte Dateien, ungültige Parameter - jeweils mit klarer Meldung und ohne dass etwas geschrieben wird.
/// </summary>
public class ImportLimitsAndRolesTests : IClassFixture<ImportExportFixture>
{
    private readonly ImportExportFixture _fx;

    public ImportLimitsAndRolesTests(ImportExportFixture fixture) => _fx = fixture;

    [Theory]
    [InlineData("Viewer", "articles")]
    [InlineData("Viewer", "stock")]
    [InlineData("Viewer", "orders")]
    [InlineData("Picker", "articles")]
    [InlineData("Packer", "stock")]
    [InlineData("Receiver", "orders")]
    public async Task Importing_with_a_token_below_manager_is_403_and_writes_nothing(string role, string kind)
    {
        var client = await _fx.W.ClientAsync(role);
        var sku = WorldBuilder.Unique("DENY");
        var csv = kind switch
        {
            "articles" => Csv("Sku;Name;LengthMm;WidthMm;HeightMm", $"{sku};Nein;10;10;10"),
            "stock" => Csv("Sku;Location;Quantity", $"{sku};X;1"),
            _ => Csv("OrderNumber;Sku;Quantity", $"{sku};{sku};1"),
        };

        foreach (var dryRun in new[] { true, false })
        {
            using var content = Upload(csv);
            var response = await PostImportAsync(client, kind, content, dryRun);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        Assert.Equal(0, await ArticleCountAsync(_fx.W, sku));
    }

    [Fact]
    public async Task An_anonymous_upload_is_401_and_a_manager_may_import()
    {
        using var anonymous = Upload(Csv("Sku", "X"));
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostImportAsync(_fx.W.Anonymous(), "articles", anonymous, true)).StatusCode);

        var manager = await _fx.W.ClientAsync("Manager");
        var sku = WorldBuilder.Unique("MGR");
        var result = await ImportAsync(manager, "articles", Csv("Sku;Name;LengthMm;WidthMm;HeightMm", $"{sku};Manager;10;10;10"), dryRun: false);
        Assert.True(result.Applied);
    }

    [Fact]
    public async Task Without_the_dryRun_parameter_the_import_only_checks()
    {
        var sku = WorldBuilder.Unique("DEF");
        using var content = Upload(Csv("Sku;Name;LengthMm;WidthMm;HeightMm", $"{sku};Standard;10;10;10"));

        var response = await PostImportAsync(_fx.Admin, "articles", content, dryRun: null);
        var result = await ReadResultAsync(response);

        Assert.True(result.DryRun);
        Assert.False(result.Applied);
        Assert.Equal(0, await ArticleCountAsync(_fx.W, sku));
    }

    [Fact]
    public async Task A_file_over_5_MB_is_rejected_with_a_clear_message_and_a_file_of_exactly_5_MB_is_still_read()
    {
        var tooBig = Utf8.GetBytes("Sku\r\n" + new string('x', ImportLimits.MaxFileBytes));          // 5 MB + 5 Bytes
        using (var content = Upload(tooBig))
        {
            var response = await PostImportAsync(_fx.Admin, "articles", content, dryRun: true);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("größer als 5 MB", await ProblemDetailAsync(response));
        }

        var exactly = Utf8.GetBytes("Sku\r\n" + new string('x', ImportLimits.MaxFileBytes - 5));      // genau 5 MB: zulässig (die SKU ist dann zu lang)
        Assert.Equal(ImportLimits.MaxFileBytes, exactly.Length);
        using var exact = Upload(exactly);
        var ok = await ReadResultAsync(await PostImportAsync(_fx.Admin, "articles", exact, dryRun: true));
        Assert.Equal("sku_too_long", Assert.Single(ok.Errors).Code);
    }

    [Fact]
    public async Task The_service_itself_refuses_a_file_over_the_limit_without_reading_all_of_it()
    {
        await using var stream = new MemoryStream(new byte[ImportLimits.MaxFileBytes + 1]);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => ImportService.ReadLimitedAsync(stream));

        Assert.Equal("import_file_too_large", ex.Data["code"]);
        Assert.True(stream.Position <= ImportLimits.MaxFileBytes + 81_920);                            // höchstens ein Puffer über der Grenze gelesen
    }

    [Fact]
    public async Task Exactly_20000_rows_are_accepted_and_20001_are_a_clear_error_for_the_whole_file()
    {
        string Build(int rows, string name) => "Sku;Name;LengthMm;WidthMm;HeightMm\r\n" +
            string.Concat(Enumerable.Range(1, rows).Select(i => $"LIM-{i};{name};10;10;10\r\n"));

        var ok = await ImportAsync(_fx.Admin, "articles", Build(ImportLimits.MaxRows, "Artikel"), dryRun: true);
        Assert.Equal((20_000, 20_000, 0), (ok.Rows, ok.Created, ok.ErrorCount));

        using var content = Upload(Build(ImportLimits.MaxRows + 1, "Artikel"));
        var response = await PostImportAsync(_fx.Admin, "articles", content, dryRun: false);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("import_too_many_rows", await ProblemCodeAsync(response));
        Assert.Contains("mehr als 20.000 Datenzeilen", await ProblemDetailAsync(response));
        Assert.Equal(0, await ArticleCountAsync(_fx.W, "LIM-"));
    }

    [Fact]
    public async Task The_error_list_is_cut_at_500_but_the_count_stays_complete()
    {
        var csv = "Sku;Name;LengthMm;WidthMm;HeightMm\r\n" + string.Concat(Enumerable.Range(1, 1200).Select(i => $"CUT-{i};;10;10;10\r\n"));   // alle ohne Namen

        var result = await ImportAsync(_fx.Admin, "articles", csv, dryRun: true);

        Assert.Equal(1200, result.ErrorCount);
        Assert.Equal(ImportLimits.MaxReportedErrors, result.Errors.Count);
        Assert.True(result.ErrorsTruncated);
        Assert.Equal("0 neu, 0 aktualisiert, 1200 Fehler", result.Summary);
        Assert.Equal(2, result.Errors[0].Line);                                                          // die ersten Fehler, in Dateireihenfolge
    }

    [Fact]
    public async Task Empty_broken_or_unusable_uploads_are_clean_400s()
    {
        // 0 Byte
        using (var empty = Upload(Array.Empty<byte>()))
        {
            var response = await PostImportAsync(_fx.Admin, "articles", empty, true);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Die Datei ist leer", await ProblemDetailAsync(response));
        }

        // nur Leerraum: keine Kopfzeile
        using (var blank = Upload("\r\n\r\n"))
        {
            var response = await PostImportAsync(_fx.Admin, "articles", blank, true);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("import_empty", await ProblemCodeAsync(response));
        }

        // nur die Kopfzeile
        using (var headerOnly = Upload(Csv("Sku;Name")))
        {
            var response = await PostImportAsync(_fx.Admin, "articles", headerOnly, true);
            Assert.Equal("import_no_rows", await ProblemCodeAsync(response));
        }

        // nicht geschlossenes Anführungszeichen
        using (var broken = Upload(Csv("Sku;Name", "A;\"offen")))
        {
            var response = await PostImportAsync(_fx.Admin, "articles", broken, true);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("csv_invalid", await ProblemCodeAsync(response));
        }

        // doppelte Spalte
        using (var duplicate = Upload(Csv("Sku;Sku", "A;B")))
            Assert.Equal("import_duplicate_column", await ProblemCodeAsync(await PostImportAsync(_fx.Admin, "articles", duplicate, true)));

        // Pflichtspalten der anderen Arten
        using (var stock = Upload(Csv("Sku;Quantity", "A;1")))
            Assert.Contains("'Location'", await ProblemDetailAsync(await PostImportAsync(_fx.Admin, "stock", stock, true)));
        using (var orders = Upload(Csv("OrderNumber;Sku", "A;1")))
            Assert.Contains("'Quantity'", await ProblemDetailAsync(await PostImportAsync(_fx.Admin, "orders", orders, true)));

        // kein Formularfeld "file" bzw. kein Multipart
        using (var noFile = new MultipartFormDataContent { { new StringContent("x"), "other" } })
        {
            var response = await PostImportAsync(_fx.Admin, "articles", noFile, true);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("keine Datei", await ProblemDetailAsync(response));
        }
        using (var json = new StringContent("{}", Encoding.UTF8, "application/json"))
        {
            var response = await PostImportAsync(_fx.Admin, "articles", json, true);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("keine Datei", await ProblemDetailAsync(response));
        }

        // ungültiges Trennzeichen
        using (var content = Upload(Csv("Sku", "A")))
        {
            var response = await PostImportAsync(_fx.Admin, "articles", content, true, delimiter: "tab");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Trennzeichen", await ProblemDetailAsync(response));
        }
    }

    [Fact]
    public async Task A_row_with_a_shifted_column_cannot_slip_through_as_a_valid_row()
    {
        // Ein Text mit Semikolon ohne Anführungszeichen schiebt alle folgenden Zellen um eine Stelle: die Zeile wird abgelehnt.
        var csv = Csv("Sku;Name;LengthMm;WidthMm;HeightMm", $"{WorldBuilder.Unique("SHIFT")};Schraube; M8;10;10;10");

        var result = await ImportAsync(_fx.Admin, "articles", csv, dryRun: true);

        Assert.Equal(1, result.ErrorCount);
        Assert.Equal("too_many_columns", result.Errors.Single().Code);
        Assert.Contains("Anführungszeichen", result.Errors.Single().Message);
    }
}
