using System.Text;

namespace Lager.Infrastructure.Auth;

/// <summary>
/// Mindestanforderungen an den JWT-Signing-Key, in JEDER Umgebung: mindestens 32 Bytes (256 bit
/// für HMAC-SHA256) und nicht der früher im Repository eingecheckte, öffentlich bekannte DEV-Key.
/// </summary>
public static class JwtKeyGuard
{
    public const int MinKeyBytes = 32;

    /// <summary>Präfix des früheren Repo-Keys. Jeder Key, der so beginnt, ist öffentlich bekannt.</summary>
    private const string KnownInsecurePrefix = "DEV-ONLY";

    /// <summary>Wirft <see cref="InvalidOperationException"/> mit verständlicher Meldung, wenn der Key nicht zulässig ist.</summary>
    /// <param name="key">Key-Material.</param>
    /// <param name="source">Herkunft für die Fehlermeldung, z. B. "Jwt:SigningKey".</param>
    public static void EnsureAcceptable(byte[] key, string source)
    {
        if (key.Length < MinKeyBytes)
            throw new InvalidOperationException(
                $"Der JWT-Signing-Key aus {source} ist zu kurz ({key.Length} Bytes, mindestens {MinKeyBytes} erforderlich). " +
                "Erzeuge einen zufälligen Key, z. B. mit `openssl rand -base64 48`.");

        if (Encoding.UTF8.GetString(key).StartsWith(KnownInsecurePrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Der JWT-Signing-Key aus {source} ist der bekannte Entwicklungs-Key aus dem öffentlichen Repository und " +
                "damit wertlos (jeder könnte Tokens fälschen). Setze einen eigenen zufälligen Key " +
                "(Jwt__SigningKey als Umgebungsvariable/Secret oder Jwt:KeyFile).");
    }
}
