namespace Lager.Api.Errors;

/// <summary>
/// Maschinenlesbare Fehlercodes (Feld <c>code</c> jeder Fehlerantwort, snake_case). Das Frontend wertet sie aus,
/// der Text steht in <c>detail</c>. Fachliche Fehler dürfen einen eigenen Code über
/// <c>exception.Data["code"]</c> mitgeben (siehe <see cref="ExceptionProblemMapper"/>); ohne Angabe gilt
/// der Standardcode des Statuscodes.
/// </summary>
public static class ProblemCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string NotFound = "not_found";
    public const string Conflict = "conflict";
    public const string ConcurrencyConflict = "concurrency_conflict";
    /// <summary>Unique-Verletzung (SKU, Bestellnummer, Benutzername ...).</summary>
    public const string Duplicate = "duplicate";
    /// <summary>Löschen scheitert, weil der Datensatz noch von Bestand oder Belegen referenziert wird.</summary>
    public const string InUse = "in_use";
    /// <summary>Verweis auf einen nicht vorhandenen Datensatz (Kunde, Lieferant, Artikel, Auftrag ...).</summary>
    public const string InvalidReference = "invalid_reference";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string PayloadTooLarge = "payload_too_large";
    public const string TooManyRequests = "too_many_requests";
    public const string InternalError = "internal_error";

    // Codes der Benutzerverwaltung (aus WP01, das Frontend wertet sie aus).
    public const string UserRuleViolation = "user_rule_violation";
    public const string UnknownRole = "unknown_role";

    /// <summary>Standardcode zu einem HTTP-Statuscode.</summary>
    public static string ForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => ValidationFailed,
        StatusCodes.Status401Unauthorized => Unauthorized,
        StatusCodes.Status403Forbidden => Forbidden,
        StatusCodes.Status404NotFound => NotFound,
        StatusCodes.Status405MethodNotAllowed => MethodNotAllowed,
        StatusCodes.Status409Conflict => Conflict,
        StatusCodes.Status413PayloadTooLarge => PayloadTooLarge,
        StatusCodes.Status415UnsupportedMediaType => UnsupportedMediaType,
        StatusCodes.Status429TooManyRequests => TooManyRequests,
        >= 500 => InternalError,
        _ => ValidationFailed,
    };
}

/// <summary>Deutsche Standardtexte je Statuscode (Titel und - wo der Server keinen eigenen Text hat - Detail).</summary>
public static class ProblemCatalog
{
    public static string Title(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "Ungültige Anfrage",
        StatusCodes.Status401Unauthorized => "Nicht angemeldet",
        StatusCodes.Status403Forbidden => "Keine Berechtigung",
        StatusCodes.Status404NotFound => "Nicht gefunden",
        StatusCodes.Status405MethodNotAllowed => "Methode nicht erlaubt",
        StatusCodes.Status406NotAcceptable => "Format nicht verfügbar",
        StatusCodes.Status409Conflict => "Konflikt",
        StatusCodes.Status413PayloadTooLarge => "Anfrage zu groß",
        StatusCodes.Status415UnsupportedMediaType => "Nicht unterstützter Inhaltstyp",
        StatusCodes.Status422UnprocessableEntity => "Nicht verarbeitbar",
        StatusCodes.Status429TooManyRequests => "Zu viele Anfragen",
        >= 500 => "Interner Serverfehler",
        _ => "Fehler",
    };

    public static string Detail(int status) => status switch
    {
        StatusCodes.Status400BadRequest => "Die Anfrage ist ungültig.",
        StatusCodes.Status401Unauthorized => "Anmeldung erforderlich oder Sitzung abgelaufen. Bitte neu anmelden.",
        StatusCodes.Status403Forbidden => "Für diese Aktion fehlt die Berechtigung.",
        StatusCodes.Status404NotFound => "Die angeforderte Ressource wurde nicht gefunden.",
        StatusCodes.Status405MethodNotAllowed => "Diese HTTP-Methode ist für die Adresse nicht erlaubt.",
        StatusCodes.Status406NotAcceptable => "Das gewünschte Antwortformat wird nicht angeboten.",
        StatusCodes.Status409Conflict => "Die Aktion steht im Konflikt mit dem aktuellen Zustand.",
        StatusCodes.Status413PayloadTooLarge => "Die Anfrage ist zu groß.",
        StatusCodes.Status415UnsupportedMediaType => "Der Inhaltstyp der Anfrage wird nicht unterstützt (erwartet: application/json).",
        StatusCodes.Status422UnprocessableEntity => "Die Anfrage kann nicht verarbeitet werden.",
        StatusCodes.Status429TooManyRequests => "Zu viele Anfragen. Bitte später erneut versuchen.",
        >= 500 => "Es ist ein interner Fehler aufgetreten. Bitte die Referenz-ID angeben, wenn der Fehler gemeldet wird.",
        _ => "Die Anfrage konnte nicht verarbeitet werden.",
    };
}
