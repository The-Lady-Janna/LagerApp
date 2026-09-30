namespace Lager.Infrastructure.Auth;

/// <summary>Namen der Token-Claims, die nicht zum JWT-Standard gehören.</summary>
public static class AuthClaims
{
    /// <summary>Fingerabdruck des Passwort-Hashes (siehe User.GetSecurityStamp) - Grundlage des Token-Widerrufs.</summary>
    public const string SecurityStamp = "sstamp";
}
