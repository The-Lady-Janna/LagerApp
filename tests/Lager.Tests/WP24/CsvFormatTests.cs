using System.Text;
using Lager.Application.ImportExport;

namespace Lager.Tests.WP24;

/// <summary>
/// Der eigene RFC-4180-Leser und -Schreiber ohne Host: Anführungszeichen, Trennzeichen, Zeilenumbrüche in Feldern, BOM,
/// Formel-Injection-Schutz und der verlustfreie Kreislauf Schreiben -> Lesen.
/// </summary>
public class CsvReaderTests
{
    [Fact]
    public void Quoted_fields_keep_delimiters_doubled_quotes_and_line_breaks()
    {
        var text = "Sku;Name;Notiz\r\n" +
                   "A-1;\"Schraube; M8\";\"Sie sagte \"\"hallo\"\"\"\r\n" +
                   "A-2;Mutter;\"zwei\r\nZeilen\"\r\n";

        var records = CsvReader.Parse(text, ';');

        Assert.Equal(3, records.Count);
        Assert.Equal(new[] { "Sku", "Name", "Notiz" }, records[0].Fields);
        Assert.Equal(new[] { "A-1", "Schraube; M8", "Sie sagte \"hallo\"" }, records[1].Fields);
        Assert.Equal(new[] { "A-2", "Mutter", "zwei\r\nZeilen" }, records[2].Fields);
    }

    [Fact]
    public void Records_report_the_line_where_they_start_even_after_multiline_fields()
    {
        var records = CsvReader.Parse("a;b\n1;\"x\ny\"\n3;4\n", ';');

        Assert.Equal(new[] { 1, 2, 4 }, records.Select(r => r.Line).ToArray());
    }

    [Fact]
    public void A_BOM_is_dropped_and_CRLF_LF_and_CR_all_end_a_record()
    {
        var records = CsvReader.Parse("﻿a;b\r\n1;2\n3;4\r5;6", ';');

        Assert.Equal(4, records.Count);
        Assert.Equal("a", records[0].Fields[0]);          // kein unsichtbares Zeichen im Spaltennamen
        Assert.Equal(new[] { "5", "6" }, records[3].Fields);  // letzte Zeile ohne Zeilenende
    }

    [Fact]
    public void Blank_lines_are_skipped_but_a_line_with_an_empty_quoted_field_is_a_record()
    {
        var records = CsvReader.Parse("a\n\n\"\"\n\nb\n", ';');

        Assert.Equal(new[] { "a", "", "b" }, records.Select(r => r.Fields[0]).ToArray());
    }

    [Fact]
    public void Trailing_delimiter_yields_an_empty_last_field_and_comma_works_as_delimiter()
    {
        Assert.Equal(new[] { "a", "b", "" }, CsvReader.Parse("a;b;", ';').Single().Fields);
        Assert.Equal(new[] { "1", "2,5", "x" }, CsvReader.Parse("1,\"2,5\",x", ',').Single().Fields);
    }

    [Fact]
    public void A_quote_in_the_middle_of_an_unquoted_field_is_an_ordinary_character()
    {
        Assert.Equal(new[] { "5\" Rohr", "x" }, CsvReader.Parse("5\" Rohr;x", ';').Single().Fields);
    }

    [Theory]
    [InlineData("a;\"nie geschlossen\nb;c")]       // Datei endet im Anführungszeichen
    [InlineData("a;\"text\"rest;c")]                // Text hinter dem schließenden Anführungszeichen
    public void Malformed_quotes_are_rejected_instead_of_silently_misreading_rows(string text)
    {
        var ex = Assert.Throws<CsvFormatException>(() => CsvReader.Parse(text, ';'));

        Assert.Equal("csv_invalid", ex.Data["code"]);
        Assert.Contains("Zeile", ex.Message);
    }
}

public class CsvWriterTests
{
    private static string Row(CsvFormat format, params CsvCell[] cells) => CsvWriter.FormatRow(format, cells);

    [Fact]
    public void A_field_is_quoted_only_when_needed_and_a_row_ends_with_CRLF()
    {
        var row = Row(CsvFormat.Semicolon, "plain", "a;b", "say \"x\"", "zwei\nZeilen", " führend", "", (string?)null);

        Assert.Equal("plain;\"a;b\";\"say \"\"x\"\"\";\"zwei\nZeilen\";\" führend\";;\r\n", row);
    }

    [Fact]
    public void Numbers_and_dates_are_formatted_by_the_program_and_follow_the_delimiter()
    {
        var cells = new CsvCell[] { 42, -5, CsvCell.Decimal(1234.5m, 2), CsvCell.Date(new DateTime(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc)), true };

        Assert.Equal("42;-5;1234,50;2026-09-30;true\r\n", CsvWriter.FormatRow(CsvFormat.Semicolon, cells));
        Assert.Equal("42,-5,1234.50,2026-09-30,true\r\n", CsvWriter.FormatRow(CsvFormat.Comma, cells));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://evil.example\";\"Klick\")")]
    [InlineData("+1+1")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1)")]
    [InlineData("\tTab")]
    [InlineData("\rCR")]
    public void Texts_that_a_spreadsheet_would_evaluate_get_an_apostrophe(string text)
    {
        var row = Row(CsvFormat.Semicolon, text);

        var cell = CsvReader.Parse(row, ';').Single().Fields.Single();
        Assert.Equal("'" + text, cell);
        Assert.False(CsvText.IsFormulaTrigger(cell[0]));
    }

    [Fact]
    public void Only_user_text_is_neutralized_numbers_like_a_negative_movement_stay_numbers()
    {
        var row = Row(CsvFormat.Semicolon, "=x", -5, CsvCell.Verbatim("Adjust"), "harmlos");

        Assert.Equal("'=x;-5;Adjust;harmlos\r\n", row);
    }

    [Fact]
    public async Task Header_names_are_written_as_they_are()
    {
        var writer = new StringWriter();
        await new CsvWriter(writer, CsvFormat.Semicolon).WriteHeaderAsync(new[] { "Sku", "Name" });

        Assert.Equal("Sku;Name\r\n", writer.ToString());
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("'=1+1")]       // beginnt schon mit einem Apostroph: muss beim Zurücklesen eindeutig bleiben
    [InlineData("''@x")]
    [InlineData("'normal")]     // Apostroph ohne Formelzeichen dahinter gehört zum Text
    [InlineData("a;b\"c\r\nd")]
    [InlineData("  Leerraum  ")]
    [InlineData("Größe ÄÖÜ ß €")]
    public void What_is_written_and_read_back_through_the_table_is_the_original_text(string text)
    {
        var line = Row(CsvFormat.Semicolon, "Sku", text);
        var table = CsvTable.Parse("Sku;Text\r\n" + line, CsvFormat.Semicolon, 10);

        // CsvTable.Cell trimmt (Excel-Zellen mit Leerraum am Rand sind fast immer versehentlich): der Rest muss identisch sein.
        Assert.Equal(text.Trim(), CsvTable.Cell(table.Rows.Single(), 1));
    }
}

public class CsvExporterTests
{
    /// <summary>Ein Writer, der jeden Schreibaufruf ins gemeinsame Protokoll einträgt.</summary>
    private sealed class LoggingWriter : StringWriter
    {
        private readonly List<string> _log;

        public LoggingWriter(List<string> log) => _log = log;

        public override Task WriteAsync(string? value)
        {
            _log.Add("write");
            return base.WriteAsync(value);
        }
    }

    [Fact]
    public async Task The_exporter_pulls_and_writes_row_by_row_without_collecting_the_source()
    {
        var log = new List<string>();

        async IAsyncEnumerable<StockExportRow> Source()
        {
            for (var i = 1; i <= 3; i++)
            {
                log.Add($"pull {i}");
                yield return new StockExportRow($"S-{i}", "Name", "BIN-1", i, null, null);
                await Task.Yield();
            }
        }

        var writer = new LoggingWriter(log);
        await CsvExporter.WriteStockAsync(new CsvWriter(writer, CsvFormat.Semicolon), Source(), CancellationToken.None);

        // Kopfzeile, dann je Zeile: holen, sofort schreiben - nie erst alle holen (keine Vollmaterialisierung großer Tabellen)
        Assert.Equal(new[] { "write", "pull 1", "write", "pull 2", "write", "pull 3", "write" }, log);
        Assert.Equal("Sku;ArticleName;Location;Quantity;LotNumber;ExpiryDate\r\nS-1;Name;BIN-1;1;;\r\nS-2;Name;BIN-1;2;;\r\nS-3;Name;BIN-1;3;;\r\n", writer.ToString());
    }

    [Fact]
    public void The_file_name_carries_the_UTC_time_without_characters_windows_rejects()
    {
        var local = new DateTime(2026, 9, 30, 12, 15, 0, DateTimeKind.Local);

        Assert.Equal("articles-20260930T101500Z.csv", CsvExporter.FileName("articles", new DateTime(2026, 9, 30, 10, 15, 0, DateTimeKind.Utc)));
        Assert.Equal(local.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'"), CsvExporter.FileName("x", local)[2..^4]);
    }
}

public class CsvTextTests
{
    [Theory]
    [InlineData("12,50", 12.5)]
    [InlineData("12.50", 12.5)]
    [InlineData("1.234,56", 1234.56)]
    [InlineData("1,234.56", 1234.56)]
    [InlineData("1 234,5", 1234.5)]
    [InlineData("12,50 €", 12.5)]
    [InlineData("EUR 3", 3)]
    [InlineData("-5", -5)]
    [InlineData("+7,25", 7.25)]
    [InlineData("0.5", 0.5)]
    [InlineData("1.234", 1234)]          // genau drei Ziffern hinter einem Punkt: Tausendertrenner
    [InlineData("1.234.567", 1234567)]
    [InlineData("1234.50", 1234.5)]
    [InlineData("7", 7)]
    public void Decimals_are_read_in_german_and_english_notation(string text, double expected)
    {
        Assert.True(CsvText.TryParseDecimal(text, out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1e5")]
    [InlineData("1,2,3")]          // kein Tausenderformat
    [InlineData("12..5")]
    [InlineData("1.23,456.7")]     // Tausendergruppen stimmen nicht
    [InlineData("--5")]
    public void Garbage_is_not_a_number(string text)
    {
        Assert.False(CsvText.TryParseDecimal(text, out _));
    }

    [Theory]
    [InlineData("5", 5, true)]
    [InlineData("5,00", 5, true)]
    [InlineData("1.000", 1000, true)]
    [InlineData("5,5", 0, false)]
    [InlineData("3000000000", 0, false)]
    public void Integers_accept_a_decimal_notation_only_when_the_value_is_whole(string text, int expected, bool ok)
    {
        Assert.Equal(ok, CsvText.TryParseInt(text, out var value));
        if (ok) Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("2026-09-30", 2026, 9, 30, 0, 0)]
    [InlineData("30.09.2026", 2026, 9, 30, 0, 0)]
    [InlineData("1.9.2026", 2026, 9, 1, 0, 0)]
    [InlineData("30.09.2026 14:30", 2026, 9, 30, 14, 30)]
    [InlineData("2026-09-30T14:30:00Z", 2026, 9, 30, 14, 30)]
    [InlineData("2026-09-30T16:30:00+02:00", 2026, 9, 30, 14, 30)]
    public void Dates_are_read_as_UTC(string text, int year, int month, int day, int hour, int minute)
    {
        Assert.True(CsvText.TryParseDate(text, out var value));
        Assert.Equal(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc), value);
        Assert.Equal(DateTimeKind.Utc, value.Kind);
    }

    [Theory]
    [InlineData("31.02.2026")]
    [InlineData("morgen")]
    [InlineData("")]
    public void Invalid_dates_are_rejected(string text)
    {
        Assert.False(CsvText.TryParseDate(text, out _));
    }

    [Theory]
    [InlineData("ja", true)]
    [InlineData("TRUE", true)]
    [InlineData("1", true)]
    [InlineData("Nein", false)]
    [InlineData("false", false)]
    public void Booleans_accept_german_and_english_words(string text, bool expected)
    {
        Assert.True(CsvText.TryParseBool(text, out var value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void Decode_honours_a_BOM_then_falls_back_from_UTF8_to_Windows1252_like_Excel_DE()
    {
        var utf8 = new UTF8Encoding(false);
        Assert.Equal("Größe", CsvText.Decode(new byte[] { 0xEF, 0xBB, 0xBF }.Concat(utf8.GetBytes("Größe")).ToArray()));
        Assert.Equal("Größe", CsvText.Decode(utf8.GetBytes("Größe")));

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var windows1252 = Encoding.GetEncoding(1252).GetBytes("Größe 5 €");     // ö/ß/€ sind hier KEIN gültiges UTF-8
        Assert.Equal("Größe 5 €", CsvText.Decode(windows1252));

        var utf16 = new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("Größe")).ToArray();
        Assert.Equal("Größe", CsvText.Decode(utf16));
    }

    [Fact]
    public void Unescape_undoes_Neutralize_for_every_shape()
    {
        foreach (var text in new[] { "=a", "+a", "-a", "@a", "\ta", "'=a", "''-a", "'a", "a", "", "'", "-" })
            Assert.Equal(text, CsvText.Unescape(CsvText.Neutralize(text)));
    }
}

public class CsvTableTests
{
    [Fact]
    public void Columns_are_found_by_name_ignoring_case_spaces_underscores_and_hyphens_in_any_order()
    {
        var table = CsvTable.Parse("Menge;purchase_price;SKU\n5;1,50;A-1\n", CsvFormat.Semicolon, 10);

        Assert.Equal(2, table.IndexOf("Sku"));
        Assert.Equal(1, table.IndexOf("PurchasePrice"));
        Assert.Equal(1, table.IndexOf("Purchase Price"));
        Assert.Equal(-1, table.IndexOf("Gtin"));
        Assert.Equal(0, table.IndexOf("Nope", "Menge"));          // Alias-Liste: der erste Treffer zählt
        Assert.Equal("A-1", CsvTable.Cell(table.Rows.Single(), 2));
    }

    [Fact]
    public void An_Excel_separator_hint_line_and_fully_empty_rows_are_not_data()
    {
        var table = CsvTable.Parse("sep=;\r\nSku;Name\r\nA;Eins\r\n;;\r\n\r\nB;Zwei\r\n;\r\n", CsvFormat.Semicolon, 10);

        Assert.Equal(new[] { "Sku", "Name" }, table.Header);
        Assert.Equal(new[] { "A", "B" }, table.Rows.Select(r => r.Fields[0]).ToArray());
        Assert.Equal(new[] { 3, 6 }, table.Rows.Select(r => r.Line).ToArray());   // Zeilennummern der Datei
    }

    [Fact]
    public void More_rows_than_the_limit_are_a_clear_error_for_the_whole_file()
    {
        var text = "Sku\n" + string.Join("\n", Enumerable.Range(1, 6).Select(i => "S" + i));

        var ok = CsvTable.Parse(text, CsvFormat.Semicolon, 6);
        var ex = Assert.Throws<ArgumentException>(() => CsvTable.Parse(text, CsvFormat.Semicolon, 5));

        Assert.Equal(6, ok.Rows.Count);
        Assert.Equal("import_too_many_rows", ex.Data["code"]);
        Assert.Contains("mehr als 5 Datenzeilen", ex.Message);
    }

    [Theory]
    [InlineData("", "import_empty")]
    [InlineData("Sku;Name\n", "import_no_rows")]
    [InlineData("Sku;Sku\nA;B\n", "import_duplicate_column")]
    public void Empty_files_without_rows_and_duplicate_columns_are_rejected_with_a_code(string text, string code)
    {
        var ex = Assert.Throws<ArgumentException>(() => CsvTable.Parse(text, CsvFormat.Semicolon, 10));

        Assert.Equal(code, ex.Data["code"]);
    }

    [Fact]
    public void A_row_with_more_cells_than_the_header_is_reported_as_a_shifted_row()
    {
        var table = CsvTable.Parse("Sku;Name\nA;Schraube;M8\nB;Mutter;\n", CsvFormat.Semicolon, 10);

        Assert.NotNull(table.OverflowProblem(table.Rows[0]));       // ein Trennzeichen im nicht zitierten Text
        Assert.Null(table.OverflowProblem(table.Rows[1]));          // leere Zellen rechts davon sind harmlos
    }

    [Fact]
    public void Format_parsing_accepts_names_and_characters_and_rejects_the_rest()
    {
        Assert.True(CsvFormat.TryParse(null, out var semicolon));
        Assert.Equal(';', semicolon.Delimiter);
        Assert.True(CsvFormat.TryParse("Comma", out var comma));
        Assert.Equal(',', comma.Delimiter);
        Assert.Equal('.', comma.DecimalSeparator);
        Assert.Equal(',', semicolon.DecimalSeparator);
        Assert.False(CsvFormat.TryParse("tab", out _));
        Assert.Equal("invalid_delimiter", Assert.Throws<ArgumentException>(() => CsvFormat.Parse("tab")).Data["code"]);
    }
}

public class ExportRangeTests
{
    [Fact]
    public void A_to_date_without_time_includes_the_whole_day_a_to_with_time_is_exclusive()
    {
        Assert.True(ExportRange.TryParse("2026-09-01", "2026-09-30", out var days, out _));
        Assert.Equal(new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), days.FromUtc);
        Assert.Equal(new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), days.ToExclusiveUtc);

        Assert.True(ExportRange.TryParse("01.09.2026", "2026-09-30T12:00:00Z", out var timed, out _));
        Assert.Equal(new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc), timed.ToExclusiveUtc);
    }

    [Fact]
    public void An_open_range_is_fine_but_reversed_or_unreadable_ranges_are_errors()
    {
        Assert.True(ExportRange.TryParse(null, "", out var open, out _));
        Assert.Equal(ExportRange.Unbounded, open);

        Assert.True(ExportRange.TryParse("2026-09-30", "2026-09-30", out var oneDay, out _));      // ein Tag
        Assert.Equal(TimeSpan.FromDays(1), oneDay.ToExclusiveUtc - oneDay.FromUtc);

        Assert.False(ExportRange.TryParse("2026-10-01", "2026-09-30", out _, out var reversed));
        Assert.Contains("'von' muss vor 'bis' liegen", reversed);
        Assert.False(ExportRange.TryParse("gestern", null, out _, out var bad));
        Assert.Contains("von: 'gestern' ist kein Datum", bad);
        Assert.Equal("invalid_range", Assert.Throws<ArgumentException>(() => ExportRange.Parse("2026-10-01", "2026-09-30")).Data["code"]);
    }
}

public class AuditBatchTests
{
    [Fact]
    public void Without_an_active_batch_nothing_is_pending()
    {
        var batch = new AuditBatch();

        Assert.False(batch.IsActive);
        Assert.Null(batch.TakePending());
        Assert.Throws<InvalidOperationException>(() => batch.Complete("x"));
    }

    [Fact]
    public void An_active_batch_counts_and_hands_out_exactly_one_entry_after_Complete()
    {
        var batch = new AuditBatch();
        using (batch.Begin("CsvImport", "import-1", "Import"))
        {
            Assert.True(batch.IsActive);
            batch.Count("Article", "Added");
            batch.Count("Article", "Added");
            batch.Count("Article", "Modified");
            Assert.Null(batch.TakePending());                      // noch nicht freigegeben

            batch.Complete("CSV-Import: 3 Artikel, Nutzer admin", new Dictionary<string, object?> { ["file"] = "a.csv" });
            var entry = batch.TakePending();

            Assert.NotNull(entry);
            Assert.Equal(("CsvImport", "import-1", "Import"), (entry!.EntityType, entry.EntityId, entry.Operation));
            Assert.Contains("\"summary\":\"CSV-Import: 3 Artikel, Nutzer admin\"", entry.ChangesJson);
            Assert.Contains("\"file\":\"a.csv\"", entry.ChangesJson);
            Assert.Contains("\"Article\":{\"Added\":2,\"Modified\":1}", entry.ChangesJson);
            Assert.Null(batch.TakePending());                      // nur einmal
        }
        Assert.False(batch.IsActive);
    }

    [Fact]
    public void Begin_does_not_nest_and_Dispose_ends_the_batch_even_with_a_pending_entry()
    {
        var batch = new AuditBatch();
        var scope = batch.Begin("CsvImport", "1", "Import");
        Assert.Throws<InvalidOperationException>(() => batch.Begin("CsvImport", "2", "Import"));

        batch.Complete("x");
        scope.Dispose();

        Assert.False(batch.IsActive);
        Assert.Null(batch.TakePending());
        using (batch.Begin("CsvImport", "3", "Import")) Assert.True(batch.IsActive);   // danach wieder möglich
    }
}
