namespace Lager.Application.Abstractions;

/// <summary>
/// Hash + verify wrapper. The hash output is opaque — it embeds algorithm,
/// cost factor and salt so verification doesn't need a separate salt column.
/// Implementation lives in Infrastructure and uses BCrypt.
/// </summary>
public interface IPasswordHasher
{
    string Hash(string plainPassword);
    bool Verify(string plainPassword, string hash);

    /// <summary>
    /// Verify gegen einen festen Dummy-Hash mit demselben Aufwand wie ein echtes <see cref="Verify"/>;
    /// Ergebnis immer false. Der Login ruft es bei unbekanntem/inaktivem/gesperrtem Konto auf, damit
    /// die Antwortzeit nicht verrät, ob der Benutzername existiert.
    /// Die Standard-Implementierung hasht das Passwort (gleiche Kosten wie Verify); der BCrypt-Hasher
    /// überschreibt sie mit einem echten Verify.
    /// </summary>
    bool VerifyDummy(string plainPassword)
    {
        Hash(plainPassword);
        return false;
    }
}
