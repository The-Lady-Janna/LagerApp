using System.Text;

namespace Lager.Application.Auth;

/// <summary>
/// Zentrale Passwort-Regeln für Passwortwechsel, Admin-Reset, neue Benutzer und das
/// Bootstrap-Passwort: mindestens <see cref="MinLength"/> Zeichen, höchstens
/// <see cref="MaxBytes"/> Bytes (BCrypt beachtet nur die ersten 72 Bytes - längere Eingaben
/// würden still abgeschnitten), nicht gleich dem Benutzernamen, nicht gleich dem bisherigen Passwort.
/// Verstöße werfen eine <see cref="ArgumentException"/> mit Data["code"] = "password_policy".
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 10;
    public const int MaxBytes = 72;

    /// <param name="password">Das gewünschte neue Passwort.</param>
    /// <param name="username">Benutzername, dem das Passwort nicht entsprechen darf (optional).</param>
    /// <param name="currentPassword">Bisheriges Passwort im Klartext, dem das neue nicht entsprechen darf (optional).</param>
    public static void Validate(string? password, string? username = null, string? currentPassword = null)
    {
        var error = Check(password, username, currentPassword);
        if (error is null) return;

        var ex = new ArgumentException(error);
        ex.Data["code"] = AuthErrorCodes.PasswordPolicy;
        throw ex;
    }

    /// <summary>Liefert die Fehlermeldung oder null, wenn das Passwort die Regeln erfüllt.</summary>
    public static string? Check(string? password, string? username = null, string? currentPassword = null)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinLength)
            return $"Passwort muss mindestens {MinLength} Zeichen haben";
        if (Encoding.UTF8.GetByteCount(password) > MaxBytes)
            return $"Passwort darf höchstens {MaxBytes} Bytes lang sein";
        if (!string.IsNullOrWhiteSpace(username)
            && string.Equals(password.Trim(), username.Trim(), StringComparison.OrdinalIgnoreCase))
            return "Passwort darf nicht dem Benutzernamen entsprechen";
        if (currentPassword is not null && string.Equals(password, currentPassword, StringComparison.Ordinal))
            return "Das neue Passwort darf nicht dem bisherigen entsprechen";
        return null;
    }
}
