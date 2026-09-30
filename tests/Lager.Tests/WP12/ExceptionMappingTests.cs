using System.Data.Common;
using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using Lager.Api.Errors;
using Lager.Application.Auth;
using Lager.Domain.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Lager.Tests.WP12;

/// <summary>
/// Die Abbildung Exception -> Statuscode/Code/Text ohne Host: sie ist der Vertrag zwischen Domain/Application und HTTP
/// (fachliche Fehler bleiben Exceptions, kein try/catch in den Controllern).
/// </summary>
public class ExceptionMappingTests
{
    private static ProblemMapping Map(Exception exception) => ExceptionProblemMapper.Map(exception);

    /// <summary>Wirft wirklich (TargetSite gesetzt): so unterscheidet der Mapper eigene Regelverstöße von Framework-Fehlern.</summary>
    private static Exception Thrown(Action throwing)
    {
        try { throwing(); }
        catch (Exception ex) { return ex; }
        throw new InvalidOperationException("Es wurde nichts geworfen");
    }

    [Fact]
    public void InvalidOperation_from_our_code_is_a_409_conflict_with_its_message()
    {
        var ex = Thrown(() => throw new InvalidOperationException("Insufficient stock for article X (missing 3)"));

        var mapping = Map(ex);

        Assert.Equal(409, mapping.Status);
        Assert.Equal("conflict", mapping.Code);
        Assert.Equal("Insufficient stock for article X (missing 3)", mapping.Detail);
        Assert.True(mapping.Expected);
    }

    [Fact]
    public void A_code_in_exception_Data_replaces_the_default_code()
    {
        var conflict = Thrown(() => throw new InvalidOperationException("Bestellung existiert") { Data = { ["code"] = "order_exists" } });
        var argument = Thrown(() => throw new ArgumentException("zu schwach") { Data = { ["code"] = "password_policy" } });
        var missing = Thrown(() => throw new KeyNotFoundException("weg") { Data = { ["code"] = "order_missing" } });

        Assert.Equal("order_exists", Map(conflict).Code);
        Assert.Equal("password_policy", Map(argument).Code);
        Assert.Equal(400, Map(argument).Status);
        Assert.Equal("order_missing", Map(missing).Code);
    }

    [Fact]
    public void KeyNotFound_is_a_404()
    {
        var mapping = Map(Thrown(() => throw new KeyNotFoundException("Artikel 7 nicht gefunden")));

        Assert.Equal(404, mapping.Status);
        Assert.Equal("not_found", mapping.Code);
        Assert.Equal("Artikel 7 nicht gefunden", mapping.Detail);
    }

    [Fact]
    public void ArgumentException_is_a_400_and_loses_the_framework_suffix()
    {
        var plain = Map(Thrown(() => throw new ArgumentException("Menge muss > 0 sein", "qty")));
        var range = Map(Thrown(() => throw new ArgumentOutOfRangeException("qty", -5, "Menge liegt außerhalb 1..10")));

        Assert.Equal(400, plain.Status);
        Assert.Equal("validation_failed", plain.Code);
        Assert.Equal("Menge muss > 0 sein", plain.Detail);
        Assert.Equal(400, range.Status);
        Assert.Equal("Menge liegt außerhalb 1..10", range.Detail);
    }

    [Fact]
    public void FormatException_and_JsonException_are_400_without_the_technical_message()
    {
        foreach (var ex in new Exception[]
                 {
                     Thrown(() => Guid.Parse("kein-guid")),
                     Thrown(() => JsonSerializer.Deserialize<int>("{kaputt")),
                 })
        {
            var mapping = Map(ex);
            Assert.Equal(400, mapping.Status);
            Assert.Equal("validation_failed", mapping.Code);
            Assert.DoesNotContain("kein-guid", mapping.Detail);
        }
    }

    [Fact]
    public void BadHttpRequest_keeps_its_client_error_status()
    {
        Assert.Equal(400, Map(new BadHttpRequestException("Unexpected end of request content.")).Status);

        var tooLarge = Map(new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge));
        Assert.Equal(413, tooLarge.Status);
        Assert.Equal("payload_too_large", tooLarge.Code);
    }

    [Fact]
    public void Auth_exceptions_keep_their_contract_codes()
    {
        var credentials = Map(new InvalidCredentialsException());
        var rule = Map(Thrown(() => throw new UserRuleViolationException("Der letzte Admin bleibt")));
        var role = Map(Thrown(() => throw new UnknownRoleException("Rolle 'Chef' gibt es nicht")));

        Assert.Equal((401, "invalid_credentials"), (credentials.Status, credentials.Code));
        Assert.Equal((409, "user_rule_violation", "Der letzte Admin bleibt"), (rule.Status, rule.Code, rule.Detail));
        Assert.Equal((400, "unknown_role", "Rolle 'Chef' gibt es nicht"), (role.Status, role.Code, role.Detail));
    }

    [Fact]
    public void InvalidOperation_thrown_by_the_framework_is_a_bug_and_stays_a_500()
    {
        // Diese Ausnahmen wirft nicht unser Code (LINQ, Dispose), sie dürfen nicht als fachlicher Konflikt getarnt werden.
        var noElements = Thrown(() => new List<int>().First());
        var disposed = Thrown(() =>
        {
            var stream = new MemoryStream();
            stream.Dispose();
            stream.ReadByte();
        });

        foreach (var ex in new[] { noElements, disposed })
        {
            var mapping = Map(ex);
            Assert.Equal(500, mapping.Status);
            Assert.Equal("internal_error", mapping.Code);
            Assert.False(mapping.Expected);
        }
    }

    [Fact]
    public void Unknown_exceptions_are_a_500_and_reveal_nothing()
    {
        var mapping = Map(Thrown(() => throw new NullReferenceException("Server=intern;Passwort=geheim")));

        Assert.Equal(500, mapping.Status);
        Assert.Equal("internal_error", mapping.Code);
        Assert.DoesNotContain("geheim", mapping.Detail);
        Assert.DoesNotContain("NullReference", mapping.Detail);
        Assert.False(mapping.Expected);
    }

    [Fact]
    public void Concurrency_conflicts_are_a_409_without_the_ef_message()
    {
        var mapping = Map(new DbUpdateConcurrencyException("UPDATE \"StockItems\" SET Quantity = 5 WHERE ConcurrencyToken = 'x'"));

        Assert.Equal((409, "concurrency_conflict"), (mapping.Status, mapping.Code));
        Assert.DoesNotContain("UPDATE", mapping.Detail);
    }

    [Theory]
    [InlineData("SQLite Error 19: 'UNIQUE constraint failed: Articles.Sku'.", "SKU")]
    [InlineData("SQLite Error 19: 'UNIQUE constraint failed: StockItems.ArticleId, StockItems.StorageLocationId, StockItems.LotNumber'.", "Artikel, Lagerplatz, Charge")]
    [InlineData("SQLite Error 19: 'UNIQUE constraint failed: index 'IX_Articles_Sku''.", "SKU")]
    [InlineData("Duplicate entry 'ART-1' for key 'Articles.IX_Articles_Sku'", "SKU")]
    [InlineData("Duplicate entry 'x' for key 'IX_Orders_OrderNumber'", "Bestellnummer")]
    public void Unique_violations_are_a_409_duplicate_naming_the_columns(string providerMessage, string expectedColumns)
    {
        var mapping = Map(new DbUpdateException("Speichern fehlgeschlagen", new FakeDbException(providerMessage)));

        Assert.Equal((409, "duplicate"), (mapping.Status, mapping.Code));
        Assert.Contains(expectedColumns, mapping.Detail);
        Assert.DoesNotContain("constraint", mapping.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Duplicate entry", mapping.Detail);
    }

    [Theory]
    [InlineData("SQLite Error 19: 'FOREIGN KEY constraint failed'.")]
    [InlineData("Cannot delete or update a parent row: a foreign key constraint fails (`lager`.`StockItems`, CONSTRAINT ...)")]
    [InlineData("Cannot add or update a child row: a foreign key constraint fails (`lager`.`Orders`, CONSTRAINT ...)")]
    public void Foreign_key_violations_are_a_409_with_a_speaking_text(string providerMessage)
    {
        var mapping = Map(new DbUpdateException("Speichern fehlgeschlagen", new FakeDbException(providerMessage)));

        Assert.Equal(409, mapping.Status);
        Assert.Contains(mapping.Code, new[] { "in_use", "invalid_reference" });
        Assert.DoesNotContain("constraint", mapping.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(mapping.Detail));
    }

    [Fact]
    public void Other_database_errors_stay_a_500()
    {
        var notNull = Map(new DbUpdateException("x", new FakeDbException("SQLite Error 19: 'NOT NULL constraint failed: Articles.Name'.")));
        var connection = Map(new DbUpdateException("x", new FakeDbException("unable to open database file")));

        Assert.Equal(500, notNull.Status);
        Assert.Equal(500, connection.Status);
    }

    [Fact]
    public void FluentValidation_exceptions_become_a_400_with_field_errors()
    {
        var failures = new[]
        {
            new ValidationFailure("Sku", "SKU darf nicht leer sein."),
            new ValidationFailure("Sku", "SKU ist zu lang."),
            new ValidationFailure("Name", "Name darf nicht leer sein."),
        };

        var mapping = Map(new ValidationException(failures));

        Assert.Equal((400, "validation_failed"), (mapping.Status, mapping.Code));
        Assert.Equal(new[] { "SKU darf nicht leer sein.", "SKU ist zu lang." }, mapping.Errors!["Sku"]);
        Assert.Equal(new[] { "Name darf nicht leer sein." }, mapping.Errors["Name"]);
    }

    private sealed class FakeDbException : DbException
    {
        public FakeDbException(string message) : base(message) { }
    }
}
