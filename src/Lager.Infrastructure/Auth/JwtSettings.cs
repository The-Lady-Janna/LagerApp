namespace Lager.Infrastructure.Auth;

/// <summary>
/// Binds to the "Jwt" section in appsettings. Den Signing-Key liefert NICHT die Datei appsettings.json:
/// der Host (Program.cs) löst ihn beim Start über JwtKeyResolver auf (Jwt:SigningKey per Env/Secret,
/// Jwt:KeyFile, nur in Development ein flüchtiger Zufallskey) und prüft ihn mit
/// <see cref="JwtKeyGuard"/> (mindestens 32 Bytes, kein bekannter Repo-Key). Ein ungültiger Key
/// bricht damit den Start ab, statt erst beim ersten Login einen 500er zu erzeugen.
/// </summary>
public class JwtSettings
{
    public string Issuer { get; set; } = "Lager";
    public string Audience { get; set; } = "Lager";

    /// <summary>Key als Text (Jwt:SigningKey). Wird nur genutzt, wenn <see cref="SigningKeyBytes"/> nicht gesetzt ist.</summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>Vom Host aufgelöster und geprüftes Key-Material. Hat Vorrang vor <see cref="SigningKey"/>.</summary>
    public byte[]? SigningKeyBytes { get; set; }

    public int LifetimeMinutes { get; set; } = 480;  // 8 h default
}
