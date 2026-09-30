using Lager.Application.Abstractions;

namespace Lager.Infrastructure.Auth;

/// <summary>
/// BCrypt with work-factor 12. Bcrypt's hash format includes the algorithm
/// version, cost factor and salt — that's why we don't need a separate salt
/// column. Work-factor 12 = ~250 ms per hash on a modern server, which is
/// the OWASP recommendation for interactive login flows.
/// </summary>
public class BCryptPasswordHasher : IPasswordHasher
{
    private const int WorkFactor = 12;

    /// <summary>
    /// Fester Hash für <see cref="VerifyDummy"/>, mit demselben Work-Factor wie echte Hashes.
    /// Der explizite statische Konstruktor erzwingt die einmalige Berechnung beim Erzeugen des
    /// Hasher-Singletons - also bevor ein Login-Request ihn benutzt, nicht erst im zeitkritischen Pfad.
    /// </summary>
    private static readonly string DummyHash;

    static BCryptPasswordHasher()
    {
        DummyHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString("N"), workFactor: WorkFactor);
    }

    public string Hash(string plainPassword) =>
        BCrypt.Net.BCrypt.HashPassword(plainPassword, workFactor: WorkFactor);

    public bool Verify(string plainPassword, string hash)
    {
        try { return BCrypt.Net.BCrypt.Verify(plainPassword, hash); }
        catch (BCrypt.Net.SaltParseException) { return false; }
        catch (ArgumentException) { return false; }
    }

    public bool VerifyDummy(string plainPassword)
    {
        Verify(plainPassword, DummyHash);
        return false;
    }
}
