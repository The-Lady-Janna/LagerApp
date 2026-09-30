using System.Net;
using System.Text;
using Lager.Application.ImportExport;
using Lager.Contracts.Articles;
using Lager.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using static Lager.Tests.WP24.Wp24Support;

namespace Lager.Tests.WP24;

/// <summary>
/// Artikelimport (<c>POST /api/import/articles</c>): Upsert je SKU, idempotent, Trockenlauf ohne Schreiben, Zeilenfehler mit Zeilennummer
/// und Code, Übernahme nur ohne Fehler (oder mit <c>skipErrors</c>), Excel-DE-Dateien (Windows-1252, Dezimalkomma) und der Kreislauf
/// Export -> Import.
/// </summary>
public class ArticleImportTests : IClassFixture<ImportExportFixture>
{
    private const string Header = "Sku;Name;Description;Gtin;LengthMm;WidthMm;HeightMm;WeightGrams;IsStackable;StackingAxis;StackingIncrementMm;MaxStackCount;" +
                                  "MinStock;ReorderPoint;MaxStock;PurchasePrice;SupplierCode;AlternativeSkus;ValidFrom;ValidUntil";

    private readonly ImportExportFixture _fx;

    public ArticleImportTests(ImportExportFixture fixture) => _fx = fixture;

    private Task<Dictionary<string, Guid>> ConcurrencyTokensAsync(params string[] skus) =>
        _fx.W.DbAsync(async db => await db.Articles.AsNoTracking().Where(a => skus.Contains(a.Sku)).ToDictionaryAsync(a => a.Sku, a => a.ConcurrencyToken));

    [Fact]
    public async Task Dry_run_counts_new_and_changed_articles_and_writes_nothing()
    {
        var prefix = WorldBuilder.Unique("DRY");
        var existing = await CreateArticleAsync(_fx.Admin, NewArticle($"{prefix}-A", "Alt", priceCents: 100));
        var auditBefore = await AuditCountAsync(_fx.W);

        var csv = Csv("Sku;Name;LengthMm;WidthMm;HeightMm;PurchasePrice",
            $"{prefix}-A;Neu benannt;120;80;40;2,50",
            $"{prefix}-B;Zweiter;10;10;10;1.234,56",
            $"{prefix}-C;Dritter;10;10;10;0,99");
        var result = await ImportAsync(_fx.Admin, "articles", csv, dryRun: true);

        Assert.True(result.DryRun);
        Assert.False(result.Applied);
        Assert.Null(result.ImportId);
        Assert.Equal((3, 2, 1, 0, 0), (result.Rows, result.Created, result.Updated, result.Unchanged, result.ErrorCount));
        Assert.Equal("2 neu, 1 aktualisiert, 0 Fehler", result.Summary);
        Assert.Contains("Es wurde nichts geschrieben", result.Message);

        // nichts geschrieben: kein neuer Artikel, der alte unverändert, kein Audit-Eintrag
        var articles = await ArticlesAsync(_fx.Admin);
        Assert.DoesNotContain(articles, a => a.Sku == $"{prefix}-B" || a.Sku == $"{prefix}-C");
        var after = articles.Single(a => a.Sku == existing.Sku);
        Assert.Equal(("Alt", 100), (after.Name, after.PurchasePriceCents));
        Assert.Equal(auditBefore, await AuditCountAsync(_fx.W));
    }

    [Fact]
    public async Task Import_creates_then_updates_and_an_identical_second_import_changes_nothing()
    {
        var prefix = WorldBuilder.Unique("UPS");
        var supplierId = await _fx.W.AddSupplierAsync();
        var supplierCode = (await _fx.W.DbAsync(db => db.Suppliers.AsNoTracking().FirstAsync(s => s.Id == supplierId))).Code;
        var gtin = NextGtin13();
        var csv = Csv(Header,
            $"{prefix}-1;Schraube M8;Verzinkt, 50 Stück;{gtin};120;80;40;250;ja;X;50;4;5;10;100;12,50;{supplierCode};ALT-1|ALT-2;01.03.2026;30.09.2026",
            $"{prefix}-2;Mutter;;{Gtin8};10;10;10;5;nein;;;;0;0;0;0,05;;;;",
            $"{prefix}-3;Scheibe;;{GtinUpcA};10;10;10;1;;;;;;;;;;;;");

        // 1. Anlegen
        var first = await ImportAsync(_fx.Admin, "articles", csv, dryRun: false);
        Assert.True(first.Applied);
        Assert.Equal((3, 0, 0, 0), (first.Created, first.Updated, first.Unchanged, first.ErrorCount));
        Assert.NotNull(first.ImportId);
        Assert.StartsWith("Übernommen: 3 neu, 0 aktualisiert, 0 Fehler", first.Message);

        var created = (await ArticlesAsync(_fx.Admin)).Where(a => a.Sku.StartsWith(prefix)).OrderBy(a => a.Sku).ToList();
        Assert.Equal(3, created.Count);
        var one = created[0];
        Assert.Equal(("Schraube M8", "Verzinkt, 50 Stück", gtin), (one.Name, one.Description, one.Gtin));
        Assert.Equal((120, 80, 40, 250), (one.Dimensions.LengthMm, one.Dimensions.WidthMm, one.Dimensions.HeightMm, one.WeightGrams));
        Assert.True(one.Stacking.IsStackable);
        Assert.Equal(("X", 50, 4), (one.Stacking.StackingAxis, one.Stacking.StackingIncrementMm, one.Stacking.MaxStackCount!.Value));
        Assert.Equal((5, 10, 100, 1250), (one.MinStock, one.ReorderPoint, one.MaxStock, one.PurchasePriceCents));
        Assert.Equal(supplierId, one.PrimarySupplierId);
        Assert.Equal(new[] { "ALT-1", "ALT-2" }, one.AlternativeSkus);
        Assert.Equal((new DateTime(2026, 3, 1), new DateTime(2026, 9, 30)), (one.ValidFrom!.Value.Date, one.ValidUntil!.Value.Date));
        Assert.Equal((5, 0, false), (created[1].PurchasePriceCents, created[1].MinStock, created[1].Stacking.IsStackable));
        Assert.Equal(Gtin8, created[1].Gtin);
        Assert.Equal(GtinUpcA, created[2].Gtin);

        // 2. dieselbe Datei noch einmal: nichts neu, nichts geändert - auch GTIN, Alt-SKUs und Saison nicht (kein Speichern, kein Audit)
        var skus = created.Select(a => a.Sku).ToArray();
        var tokensBefore = await ConcurrencyTokensAsync(skus);
        var importsBefore = await AuditCountAsync(_fx.W, ImportService.AuditEntityType);
        var second = await ImportAsync(_fx.Admin, "articles", csv, dryRun: false);
        Assert.False(second.Applied);
        Assert.Equal((0, 0, 3, 0), (second.Created, second.Updated, second.Unchanged, second.ErrorCount));
        Assert.Equal("0 neu, 0 aktualisiert, 0 Fehler", second.Summary);
        Assert.Null(second.ImportId);
        Assert.Contains("Nichts zu übernehmen", second.Message);
        Assert.Equal(tokensBefore, await ConcurrencyTokensAsync(skus));                         // die Datensätze wurden nicht angefasst
        Assert.Equal(importsBefore, await AuditCountAsync(_fx.W, ImportService.AuditEntityType));

        // 3. eine Änderung (Preis) und leere Zellen setzen zurück (GTIN entfernen, Alternativen und Saison löschen)
        var changed = Csv(Header,
            $"{prefix}-1;Schraube M8;Verzinkt, 50 Stück;;120;80;40;250;ja;X;50;4;5;10;100;13,00;{supplierCode};;;",
            $"{prefix}-2;Mutter;;{Gtin8};10;10;10;5;nein;;;;0;0;0;0,05;;;;",
            $"{prefix}-3;Scheibe;;{GtinUpcA};10;10;10;1;;;;;;;;;;;;");
        var third = await ImportAsync(_fx.Admin, "articles", changed, dryRun: false);
        Assert.Equal((0, 1, 2, 0), (third.Created, third.Updated, third.Unchanged, third.ErrorCount));
        var tokensAfter = await ConcurrencyTokensAsync(skus);
        Assert.NotEqual(tokensBefore[$"{prefix}-1"], tokensAfter[$"{prefix}-1"]);
        Assert.Equal(tokensBefore[$"{prefix}-2"], tokensAfter[$"{prefix}-2"]);
        var updated = (await ArticlesAsync(_fx.Admin)).Single(a => a.Sku == $"{prefix}-1");
        Assert.Equal(1300, updated.PurchasePriceCents);
        Assert.Null(updated.Gtin);
        Assert.Empty(updated.AlternativeSkus ?? Array.Empty<string>());
        Assert.Null(updated.ValidFrom);
        Assert.Null(updated.ValidUntil);
    }

    [Fact]
    public async Task A_column_that_is_missing_from_the_file_leaves_that_field_alone()
    {
        var sku = WorldBuilder.Unique("COL");
        var gtin = NextGtin13();
        await CreateArticleAsync(_fx.Admin, NewArticle(sku, "Bleibt", gtin, new[] { "X-1" }, priceCents: 100, description: "Beschreibung", min: 3, reorder: 6, max: 9));

        var result = await ImportAsync(_fx.Admin, "articles", Csv("Sku;PurchasePrice", $"{sku};7,77"), dryRun: false);

        Assert.Equal((0, 1), (result.Created, result.Updated));
        var article = (await ArticlesAsync(_fx.Admin)).Single(a => a.Sku == sku);
        Assert.Equal(777, article.PurchasePriceCents);
        Assert.Equal(("Bleibt", "Beschreibung", gtin), (article.Name, article.Description, article.Gtin));
        Assert.Equal((3, 6, 9), (article.MinStock, article.ReorderPoint, article.MaxStock));
        Assert.Equal(new[] { "X-1" }, article.AlternativeSkus);
    }

    [Fact]
    public async Task Each_bad_row_is_reported_with_its_line_and_code_and_unknown_columns_are_warned_about()
    {
        var prefix = WorldBuilder.Unique("ERR");
        var ownerGtin = NextGtin13();
        await CreateArticleAsync(_fx.Admin, NewArticle($"{prefix}-OWNER", gtin: ownerGtin));

        var csv = Csv("Sku;Name;LengthMm;WidthMm;HeightMm;Gtin;PurchasePrice;SupplierCode;MinStock;ReorderPoint;Mindestbestand",
            $"{prefix}-OK;In Ordnung;10;10;10;;1,00;;;;",                          // Zeile 2: gültig
            $"{prefix}-SUP;Lieferant;10;10;10;;1,00;GIBT-ES-NICHT;;;",             // 3
            $"{prefix}-GTIN;Prüfziffer;10;10;10;4006381333932;1,00;;;;",           // 4: falsche Prüfziffer
            $"{prefix}-OK;Doppelt;10;10;10;;1,00;;;;",                             // 5: SKU steht schon in Zeile 2
            $"{prefix}-DIM;Ohne Maße;;;;;1,00;;;;",                                // 6
            $"{prefix}-DUP;Gleiche GTIN;10;10;10;{ownerGtin};1,00;;;;",            // 7: gehört schon einem Artikel
            $"{prefix}-PRICE;Preis;10;10;10;;1,005;;;;",                           // 8: drei Nachkommastellen, kein stilles Runden
            $"{prefix}-NAME;;10;10;10;;1,00;;;;",                                  // 9
            $"{prefix}-WIDE;Text;10;10;10;;1,00;;;;;ÜBERSCHUSS",                   // 10: ein Zeichen mehr als die Kopfzeile Spalten hat
            $"{prefix}-LEVEL;Schwellen;10;10;10;;1,00;;9;3;");                     // 11: Meldebestand unter Mindestbestand
        var result = await ImportAsync(_fx.Admin, "articles", csv, dryRun: true);

        var codes = result.Errors.ToDictionary(e => e.Line, e => e.Code);
        Assert.Equal(new Dictionary<int, string>
        {
            [3] = "unknown_supplier", [4] = "invalid_gtin", [5] = "duplicate_sku_in_file", [6] = "invalid_dimension", [7] = "duplicate_gtin",
            [8] = "invalid_price", [9] = "name_missing", [10] = "too_many_columns", [11] = "invalid_stock_level",
        }, codes);
        Assert.Equal((1, 9), (result.Created, result.ErrorCount));
        Assert.Equal("1 neu, 0 aktualisiert, 9 Fehler", result.Summary);
        Assert.Equal($"{prefix}-GTIN", result.Errors.Single(e => e.Line == 4).Key);
        Assert.Contains("Prüfziffer", result.Errors.Single(e => e.Line == 4).Message);
        Assert.Contains("GIBT-ES-NICHT", result.Errors.Single(e => e.Line == 3).Message);
        Assert.Contains(result.Warnings, w => w.Contains("Mindestbestand") && w.Contains("unbekannt"));     // Tippfehler im Spaltennamen fällt auf
    }

    [Fact]
    public async Task Errors_block_the_import_unless_skipErrors_is_set_then_only_the_good_rows_are_written()
    {
        var prefix = WorldBuilder.Unique("SKIP");
        var csv = Csv("Sku;Name;LengthMm;WidthMm;HeightMm",
            $"{prefix}-1;Gut 1;10;10;10",
            $"{prefix}-2;;10;10;10",
            $"{prefix}-3;Gut 3;10;10;10");

        var blocked = await ImportAsync(_fx.Admin, "articles", csv, dryRun: false);
        Assert.False(blocked.Applied);
        Assert.Equal(1, blocked.ErrorCount);
        Assert.Contains("Nichts übernommen", blocked.Message);
        Assert.Equal(0, await ArticleCountAsync(_fx.W, prefix));                                 // nichts, auch nicht die guten Zeilen

        var skipped = await ImportAsync(_fx.Admin, "articles", csv, dryRun: false, skipErrors: true);
        Assert.True(skipped.Applied);
        Assert.Equal((2, 0, 1), (skipped.Created, skipped.Updated, skipped.ErrorCount));
        Assert.Contains("1 fehlerhafte Zeilen wurden ausgelassen", skipped.Message);
        Assert.Equal(new[] { $"{prefix}-1", $"{prefix}-3" },
            (await ArticlesAsync(_fx.Admin)).Where(a => a.Sku.StartsWith(prefix)).Select(a => a.Sku).Order().ToArray());
    }

    [Fact]
    public async Task Exporting_and_importing_the_same_articles_reports_nothing_to_do()
    {
        var prefix = WorldBuilder.Unique("RT");
        var supplierId = await _fx.W.AddSupplierAsync();
        await CreateArticleAsync(_fx.Admin, NewArticle($"{prefix}-1", "Größe ÄÖÜ", NextGtin13(), new[] { "A-B" }, 1999, supplierId,
            new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc),
            new StackingInfoDto(true, "Y", 25, 6), "Mehrzeilig\nBeschreibung; mit \"Zeichen\"", 1, 2, 3));
        await CreateArticleAsync(_fx.Admin, NewArticle($"{prefix}-2", "=1+1", description: "  Leerraum  "));
        await CreateArticleAsync(_fx.Admin, NewArticle($"{prefix}-3", "'-Apostroph"));

        var bytes = await ExportBytesAsync(_fx.Admin, "/api/export/articles.csv");
        using var content = Upload(bytes, "articles.csv");
        var result = await ReadResultAsync(await PostImportAsync(_fx.Admin, "articles", content, dryRun: true));

        Assert.Equal((0, 0, 0), (result.Created, result.Updated, result.ErrorCount));
        Assert.Equal("0 neu, 0 aktualisiert, 0 Fehler", result.Summary);
        Assert.True(result.Unchanged >= 3);
        Assert.Equal(result.Rows, result.Unchanged);
    }

    [Fact]
    public async Task Import_into_an_empty_system_reproduces_the_exported_articles_exactly()
    {
        var prefix = WorldBuilder.Unique("COPY");
        var supplierId = await _fx.W.AddSupplierAsync();
        await CreateArticleAsync(_fx.Admin, NewArticle($"{prefix}-1", "Komplett", NextGtin13(), new[] { "ALT-A", "ALT-B" }, 4250, supplierId,
            new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 8, 31, 0, 0, 0, DateTimeKind.Utc),
            new StackingInfoDto(true, "X", 10, 3), "Text", 4, 8, 16));
        await CreateArticleAsync(_fx.Admin, NewArticle($"{prefix}-2", "Minimal"));
        var exported = OnlyRowsOf(CsvText.Decode(await ExportBytesAsync(_fx.Admin, "/api/export/articles.csv")), prefix, dropSupplier: false);

        using var other = new LagerApiFactory();
        var otherAdmin = await other.CreateClient().AsReadyAdminAsync();

        // Der Lieferant fehlt im leeren System: die Zeile mit dem Lieferantencode ist ein Zeilenfehler und wird genannt.
        var preview = await ImportAsync(otherAdmin, "articles", exported, dryRun: true);
        Assert.Contains(preview.Errors, e => e.Code == "unknown_supplier" && e.Key == $"{prefix}-1");

        // Ohne Lieferantencode lässt sich die Datei übernehmen; danach stimmt alles überein.
        var applied = await ImportAsync(otherAdmin, "articles", OnlyRowsOf(exported, prefix, dropSupplier: true), dryRun: false);
        Assert.True(applied.Applied);
        Assert.Equal((2, 0), (applied.Created, applied.ErrorCount));

        var original = (await ArticlesAsync(_fx.Admin)).Where(a => a.Sku.StartsWith(prefix)).OrderBy(a => a.Sku).ToList();
        var copy = (await ArticlesAsync(otherAdmin)).Where(a => a.Sku.StartsWith(prefix)).OrderBy(a => a.Sku).ToList();
        Assert.Equal(2, copy.Count);
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(
                (original[i].Sku, original[i].Name, original[i].Description, original[i].Gtin, original[i].Dimensions, original[i].WeightGrams, original[i].Stacking,
                 original[i].MinStock, original[i].ReorderPoint, original[i].MaxStock, original[i].PurchasePriceCents, original[i].ValidFrom, original[i].ValidUntil),
                (copy[i].Sku, copy[i].Name, copy[i].Description, copy[i].Gtin, copy[i].Dimensions, copy[i].WeightGrams, copy[i].Stacking,
                 copy[i].MinStock, copy[i].ReorderPoint, copy[i].MaxStock, copy[i].PurchasePriceCents, copy[i].ValidFrom, copy[i].ValidUntil));
            Assert.Equal(original[i].AlternativeSkus, copy[i].AlternativeSkus);
        }
    }

    /// <summary>Kopfzeile plus die Zeilen der Exportdatei, deren SKU mit <paramref name="prefix"/> beginnt; auf Wunsch ohne Lieferantencode (Spalte 16).</summary>
    private static string OnlyRowsOf(string exportedCsv, string prefix, bool dropSupplier)
    {
        var records = CsvReader.Parse(exportedCsv, ';');
        var lines = records.Where((r, i) => i == 0 || r.Fields[0].StartsWith(prefix)).Select(r =>
        {
            var fields = r.Fields.ToArray();
            if (dropSupplier) fields[16] = "";
            // Verbatim: die Zellen tragen schon das Schutz-Apostroph des Exports, es darf nicht doppelt gesetzt werden.
            return CsvWriter.FormatRow(CsvFormat.Semicolon, fields.Select(f => CsvCell.Verbatim(f)).ToArray());
        });
        return string.Concat(lines);
    }

    [Fact]
    public async Task An_Excel_DE_file_in_Windows1252_with_thousands_dots_and_a_separator_hint_line_imports_correctly()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var sku = WorldBuilder.Unique("XLS");
        var csv = Csv("sep=;", "Artikelnummer;Name;Länge;LengthMm;WidthMm;HeightMm;Preis;PurchasePrice",
            $"{sku};Größe Ä ß € Straße;5;10;10;10;egal;1.234,56");

        // "Artikelnummer" ist kein bekannter Spaltenname: die Pflichtspalte Sku fehlt, und die Meldung nennt die gefundenen Spalten
        using var content = Upload(Encoding.GetEncoding(1252).GetBytes(csv));
        var response = await PostImportAsync(_fx.Admin, "articles", content, dryRun: false);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("import_missing_column", await ProblemCodeAsync(response));
        var detail = await ProblemDetailAsync(response);
        Assert.Contains("'Artikelnummer'", detail);
        Assert.Contains("'Länge'", detail);                                                        // Windows-1252 korrekt gelesen: der Umlaut stimmt

        using var retry = Upload(Encoding.GetEncoding(1252).GetBytes(csv.Replace("Artikelnummer", "SKU")));
        var result = await ReadResultAsync(await PostImportAsync(_fx.Admin, "articles", retry, dryRun: false));
        Assert.Equal(1, result.Created);
        Assert.Contains(result.Warnings, w => w.Contains("'Länge'"));
        var article = (await ArticlesAsync(_fx.Admin)).Single(a => a.Sku == sku);
        Assert.Equal("Größe Ä ß € Straße", article.Name);
        Assert.Equal(123456, article.PurchasePriceCents);
    }

    [Fact]
    public async Task The_comma_delimiter_reads_quoted_commas_and_decimal_points_and_a_wrong_delimiter_is_explained()
    {
        var sku = WorldBuilder.Unique("CMA");
        var commaCsv = Csv("Sku,Name,LengthMm,WidthMm,HeightMm,PurchasePrice", $"{sku},\"Schraube, M8\",10,10,10,12.50");

        var applied = await ImportAsync(_fx.Admin, "articles", commaCsv, dryRun: false, delimiter: "comma");
        Assert.Equal(("comma", 1), (applied.Delimiter, applied.Created));
        var article = (await ArticlesAsync(_fx.Admin)).Single(a => a.Sku == sku);
        Assert.Equal(("Schraube, M8", 1250), (article.Name, article.PurchasePriceCents));

        using var wrong = Upload(commaCsv);
        var response = await PostImportAsync(_fx.Admin, "articles", wrong, dryRun: true);            // Standard: Semikolon, die Datei nutzt Kommas
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("import_missing_column", await ProblemCodeAsync(response));
        Assert.Contains("Trennzeichen", await ProblemDetailAsync(response));
    }
}
