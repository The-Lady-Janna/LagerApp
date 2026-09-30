using System.Text.Json;
using FluentValidation;
using Lager.Application.Auth;
using Lager.Domain.Auth;
using Microsoft.EntityFrameworkCore;

namespace Lager.Api.Errors;

/// <summary>
/// Ergebnis der Abbildung einer Exception: Statuscode, Fehlercode, Text und optional Feldfehler.
/// <paramref name="Expected"/> = fachlicher/Client-Fehler (Log ohne Stacktrace), sonst Serverfehler (Log als Error).
/// <paramref name="CorrelationIdInBody"/> = false lässt die Korrelations-ID aus dem Body weg (sie steht weiter im Header):
/// die Login-Fehlerantwort muss für unbekannten Nutzer, falsches Passwort und gesperrtes Konto byte-identisch sein.
/// </summary>
public sealed record ProblemMapping(
    int Status,
    string Code,
    string Detail,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    bool Expected = true,
    bool CorrelationIdInBody = true);

/// <summary>
/// Der Vertrag zwischen Domain/Application und HTTP: fachliche Fehler werden weiter als Exception geworfen und hier
/// zentral auf Statuscodes abgebildet - kein try/catch in den Controllern.
/// <list type="bullet">
/// <item><see cref="KeyNotFoundException"/> -> 404 not_found</item>
/// <item><see cref="ArgumentException"/> (auch ArgumentOutOfRange, <see cref="UnknownRoleException"/>), <see cref="FormatException"/>,
/// <see cref="JsonException"/>, <see cref="BadHttpRequestException"/>, FluentValidation-<see cref="ValidationException"/> -> 400 validation_failed</item>
/// <item><see cref="InvalidOperationException"/> (Regelverstoß/Konflikt, auch <see cref="UserRuleViolationException"/>) -> 409 conflict</item>
/// <item><see cref="InvalidCredentialsException"/> -> 401 invalid_credentials</item>
/// <item><see cref="DbUpdateConcurrencyException"/> -> 409 concurrency_conflict (ohne Text der EF-Exception)</item>
/// <item><see cref="DbUpdateException"/> mit Unique-Verletzung -> 409 duplicate, mit Fremdschlüssel-Verletzung -> 409 in_use / invalid_reference</item>
/// <item>alles andere -> 500 internal_error ohne Details</item>
/// </list>
/// Ein eigener, maschinenlesbarer Code steht in <c>exception.Data["code"]</c> (snake_case) und ersetzt den Standardcode.
/// Die Meldung fachlicher Exceptions ist eine bewusst formulierte Regel-Meldung und geht als <c>detail</c> an den Client;
/// bei allen unerwarteten Fehlern (und bei Datenbankfehlern) nie.
/// </summary>
public static class ExceptionProblemMapper
{
    public static ProblemMapping Map(Exception exception)
    {
        switch (exception)
        {
            case DbUpdateConcurrencyException:
                return new ProblemMapping(StatusCodes.Status409Conflict, ProblemCodes.ConcurrencyConflict,
                    "Der Datensatz wurde zwischenzeitlich von jemand anderem geändert. Bitte neu laden und erneut versuchen.");

            case DbUpdateException db when DatabaseErrorClassifier.Classify(db) is { Kind: not DatabaseErrorKind.Other } error:
                return MapDatabaseError(db, error);

            case InvalidCredentialsException:
                return new ProblemMapping(StatusCodes.Status401Unauthorized, CodeOf(exception, AuthErrorCodes.InvalidCredentials), exception.Message,
                    CorrelationIdInBody: false);

            case KeyNotFoundException:
                // Text nur, wenn eigener Code ihn formuliert hat (nicht z. B. "The given key ... was not present" des Frameworks).
                return new ProblemMapping(StatusCodes.Status404NotFound, CodeOf(exception, ProblemCodes.NotFound),
                    IsBusinessRule(exception) && !string.IsNullOrWhiteSpace(exception.Message)
                        ? exception.Message
                        : ProblemCatalog.Detail(StatusCodes.Status404NotFound));

            case ValidationException validation:
                return new ProblemMapping(StatusCodes.Status400BadRequest, CodeOf(exception, ProblemCodes.ValidationFailed),
                    "Die Eingabe ist ungültig.", GroupErrors(validation));

            case BadHttpRequestException bad:
                return MapBadRequest(bad);

            case UnknownRoleException:
                return new ProblemMapping(StatusCodes.Status400BadRequest, CodeOf(exception, ProblemCodes.UnknownRole), CleanMessage(exception));

            case ArgumentException:
                return new ProblemMapping(StatusCodes.Status400BadRequest, CodeOf(exception, ProblemCodes.ValidationFailed), CleanMessage(exception));

            case FormatException or JsonException:
                return new ProblemMapping(StatusCodes.Status400BadRequest, CodeOf(exception, ProblemCodes.ValidationFailed),
                    "Die Anfrage enthält einen ungültigen Wert oder ein ungültiges Format.");

            case UserRuleViolationException:
                return new ProblemMapping(StatusCodes.Status409Conflict, CodeOf(exception, ProblemCodes.UserRuleViolation), exception.Message);

            case InvalidOperationException when IsBusinessRule(exception):
                return new ProblemMapping(StatusCodes.Status409Conflict, CodeOf(exception, ProblemCodes.Conflict), exception.Message);

            default:
                return new ProblemMapping(StatusCodes.Status500InternalServerError, ProblemCodes.InternalError,
                    ProblemCatalog.Detail(StatusCodes.Status500InternalServerError), Expected: false);
        }
    }

    private static ProblemMapping MapDatabaseError(DbUpdateException exception, DatabaseError error)
    {
        if (error.Kind == DatabaseErrorKind.UniqueViolation)
        {
            var columns = DatabaseErrorClassifier.DescribeColumns(error.Columns);
            var detail = columns.Length == 0
                ? "Ein Datensatz mit denselben Werten existiert bereits."
                : $"Ein Datensatz mit demselben Wert existiert bereits ({columns}).";
            return new ProblemMapping(StatusCodes.Status409Conflict, ProblemCodes.Duplicate, detail);
        }

        // Fremdschlüssel: beim Löschen "noch in Benutzung" (Bestand, Belege), beim Anlegen/Ändern "Verweis ins Leere".
        return DeletesAnyEntity(exception)
            ? new ProblemMapping(StatusCodes.Status409Conflict, ProblemCodes.InUse,
                "Der Datensatz kann nicht gelöscht werden, weil er noch verwendet wird (z. B. durch Bestand oder Belege).")
            : new ProblemMapping(StatusCodes.Status409Conflict, ProblemCodes.InvalidReference,
                "Die Aktion verweist auf einen nicht vorhandenen Datensatz (z. B. Artikel, Lagerplatz, Kunde, Lieferant oder Auftrag) " +
                "oder der Datensatz wird noch verwendet.");
    }

    private static bool DeletesAnyEntity(DbUpdateException exception)
    {
        try
        {
            return exception.Entries.Any(e => e.State == EntityState.Deleted);
        }
        catch (Exception)
        {
            // Die Einträge lassen sich nicht mehr auslesen (Kontext verworfen): dann bleibt der allgemeine Text.
            return false;
        }
    }

    private static ProblemMapping MapBadRequest(BadHttpRequestException exception)
    {
        var status = exception.StatusCode is >= 400 and < 500 ? exception.StatusCode : StatusCodes.Status400BadRequest;
        var code = status == StatusCodes.Status400BadRequest ? ProblemCodes.ValidationFailed : ProblemCodes.ForStatus(status);
        // Kestrel-Meldungen ("Request body too large.", "Unexpected end of request content.") sind technisch, aber harmlos.
        return new ProblemMapping(status, CodeOf(exception, code), status == StatusCodes.Status400BadRequest
            ? "Die Anfrage ist fehlerhaft oder unvollständig."
            : ProblemCatalog.Detail(status));
    }

    /// <summary>
    /// Eine <see cref="InvalidOperationException"/> ist nur dann ein fachlicher Konflikt, wenn eigener Code sie geworfen hat
    /// (Lager.*-Assemblies) oder ein Code in <c>Data["code"]</c> steht. Wirft das Framework sie (LINQ "Sequence contains no
    /// elements", EF, ObjectDisposedException ...), ist es ein Programmfehler und bleibt ein 500 - sonst würden Bugs als
    /// Client-Fehler getarnt.
    /// </summary>
    private static bool IsBusinessRule(Exception exception)
    {
        if (exception.Data["code"] is string) return true;
        var assembly = exception.TargetSite?.DeclaringType?.Assembly.GetName().Name;
        return assembly is null || assembly.StartsWith("Lager", StringComparison.Ordinal);
    }

    private static string CodeOf(Exception exception, string fallback) =>
        exception.Data["code"] is string { Length: > 0 } code ? code : fallback;

    /// <summary>Meldung ohne den vom Framework angehängten Zusatz " (Parameter 'x')" / "Actual value was …".</summary>
    private static string CleanMessage(Exception exception)
    {
        var message = exception.Message;
        var cut = message.IndexOf(" (Parameter '", StringComparison.Ordinal);
        if (cut >= 0) message = message[..cut];
        var newline = message.IndexOf('\n');
        if (newline >= 0) message = message[..newline];
        message = message.Trim();
        return message.Length == 0 ? ProblemCatalog.Detail(StatusCodes.Status400BadRequest) : message;
    }

    private static Dictionary<string, string[]> GroupErrors(ValidationException validation) =>
        validation.Errors
            .GroupBy(e => e.PropertyName ?? string.Empty)
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());
}
