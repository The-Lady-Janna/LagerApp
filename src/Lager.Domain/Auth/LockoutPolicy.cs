namespace Lager.Domain.Auth;

/// <summary>
/// Schwelle und Dauer der Kontosperre nach fehlgeschlagenen Anmeldungen.
/// Wird vom Host aus <c>Auth:MaxFailedAttempts</c> / <c>Auth:LockoutMinutes</c> gebaut;
/// ohne Konfiguration gilt <see cref="Default"/> (5 Versuche, 15 Minuten).
/// </summary>
public sealed class LockoutPolicy
{
    public static LockoutPolicy Default { get; } = new(5, TimeSpan.FromMinutes(15));

    public int MaxFailedAttempts { get; }
    public TimeSpan Duration { get; }

    public LockoutPolicy(int maxFailedAttempts, TimeSpan duration)
    {
        if (maxFailedAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxFailedAttempts), "Mindestens 1 Fehlversuch bis zur Sperre");
        if (duration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(duration), "Sperrdauer muss positiv sein");

        MaxFailedAttempts = maxFailedAttempts;
        Duration = duration;
    }
}
