namespace Lager.Domain.Auth;

/// <summary>
/// Ein Rollenname aus einer Anfrage ist keine bekannte Rolle (Tippfehler, Zahl,
/// Komma-Liste, "None"). Ist eine <see cref="ArgumentException"/>, damit bestehende
/// Aufrufer sie wie jede andere Eingabevalidierung als 400 behandeln.
/// </summary>
public class UnknownRoleException : ArgumentException
{
    public UnknownRoleException(string message) : base(message) { }
}

/// <summary>
/// Eine Benutzerverwaltungs-Regel wurde verletzt (letzter aktiver Admin, Selbst-Deaktivierung,
/// Selbst-Entzug der Admin-Rolle). Ist eine <see cref="InvalidOperationException"/>: die Anfrage
/// ist formal gültig, kollidiert aber mit dem Zustand des Systems (HTTP 409).
/// </summary>
public class UserRuleViolationException : InvalidOperationException
{
    public UserRuleViolationException(string message) : base(message) { }
}
