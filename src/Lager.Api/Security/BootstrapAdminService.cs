using System.Security.Cryptography;
using Lager.Application.Abstractions;
using Lager.Application.Auth;
using Lager.Domain.Auth;

namespace Lager.Api.Security;

public sealed record BootstrapAdminResult(bool Created, string? Username, bool PasswordWasGenerated);

/// <summary>
/// Legt beim allerersten Start (Users-Tabelle leer) den Bootstrap-Admin an, damit man sich überhaupt
/// anmelden kann. Es gibt kein Default-Passwort mehr:
///   - <c>Auth:BootstrapAdminPassword</c> gesetzt (Umgebungsvariable/Secret) -> dieses Passwort, es wird nie geloggt;
///   - sonst ein kryptografisch zufälliges Einmalpasswort, das genau EINMAL auf der Konsole (stdout)
///     ausgegeben wird. In das Datei-Log kommt es bewusst nicht - dort würde es im Klartext liegen bleiben;
///     das Serilog-Warning nennt nur, DASS eines erzeugt wurde.
/// In beiden Fällen gilt MustChangePassword: der Erst-Login darf nur das Passwort ändern.
/// </summary>
public static class BootstrapAdminService
{
    public const int GeneratedPasswordLength = 20;

    // Ohne leicht verwechselbare Zeichen (0/O, 1/l/I); nur Buchstaben und Ziffern, damit das Passwort
    // in jeder Shell/JSON-Umgebung ohne Quoting kopierbar ist. 20 Zeichen aus 57 = rund 116 Bit.
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";

    /// <summary>Zufallspasswort aus dem kryptografischen Generator (gleichverteilt, ohne Modulo-Verzerrung).</summary>
    public static string GeneratePassword()
    {
        var chars = new char[GeneratedPasswordLength];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return new string(chars);
    }

    public static Task<BootstrapAdminResult> EnsureAdminAsync(
        IServiceProvider scopedServices, IConfiguration config, ILogger logger, CancellationToken ct = default) =>
        EnsureAdminAsync(
            scopedServices.GetRequiredService<IUserRepository>(),
            scopedServices.GetRequiredService<IPasswordHasher>(),
            scopedServices.GetRequiredService<IUnitOfWork>(),
            config, logger, Console.Out, ct);

    /// <param name="console">Ziel für die einmalige Ausgabe des Einmalpasswortes (stdout).</param>
    public static async Task<BootstrapAdminResult> EnsureAdminAsync(
        IUserRepository users, IPasswordHasher hasher, IUnitOfWork uow,
        IConfiguration config, ILogger logger, TextWriter console, CancellationToken ct = default)
    {
        if (await users.AnyAsync(ct)) return new BootstrapAdminResult(false, null, false);

        var username = config["Auth:BootstrapAdminUsername"];
        if (string.IsNullOrWhiteSpace(username)) username = "admin";
        username = username.Trim().ToLowerInvariant();

        var configured = config["Auth:BootstrapAdminPassword"];
        var generated = string.IsNullOrEmpty(configured);
        var password = generated ? GeneratePassword() : configured!;

        // Ein vom Betreiber gesetztes Passwort muss dieselben Regeln erfüllen wie jedes andere; lieber
        // der Start bricht mit klarer Meldung ab, als dass ein schwaches Passwort das Konto öffnet.
        var problem = PasswordPolicy.Check(password, username);
        if (problem is not null)
            throw new InvalidOperationException(
                $"Auth:BootstrapAdminPassword ist nicht zulässig: {problem}. " +
                "Setze ein stärkeres Passwort oder lass den Wert weg, dann wird ein Einmalpasswort erzeugt.");

        var admin = new User(username, hasher.Hash(password), Role.Admin, displayName: "Bootstrap Admin", mustChangePassword: true);
        await users.AddAsync(admin, ct);
        await uow.SaveChangesAsync(ct);

        if (generated)
        {
            console.WriteLine();
            console.WriteLine("==================================================================");
            console.WriteLine(" Lager: Bootstrap-Admin angelegt (Einmalpasswort, wird nur jetzt angezeigt)");
            console.WriteLine($"   Benutzer : {username}");
            console.WriteLine($"   Passwort : {password}");
            console.WriteLine(" Das Passwort muss beim ersten Login geändert werden.");
            console.WriteLine("==================================================================");
            console.WriteLine();
            console.Flush();

            logger.LogWarning(
                "Bootstrap-Admin '{Username}' angelegt. Das Einmalpasswort wurde einmalig auf der Konsole (stdout) ausgegeben und wird nirgends gespeichert; es muss beim ersten Login geändert werden. " +
                "Ohne Zugriff auf die Konsole vorher Auth:BootstrapAdminPassword setzen.",
                username);
        }
        else
        {
            logger.LogWarning(
                "Bootstrap-Admin '{Username}' angelegt mit dem konfigurierten Passwort (Auth:BootstrapAdminPassword); es muss beim ersten Login geändert werden.",
                username);
        }

        return new BootstrapAdminResult(true, username, generated);
    }
}
