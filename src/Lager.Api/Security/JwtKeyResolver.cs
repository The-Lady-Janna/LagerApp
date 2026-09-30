using System.Security.Cryptography;
using System.Text;
using Lager.Infrastructure.Auth;

namespace Lager.Api.Security;

/// <summary>Woher der Signing-Key stammt (für die Start-Meldung; der Key selbst wird nie geloggt).</summary>
public enum JwtKeySource
{
    /// <summary>Jwt:SigningKey (Umgebungsvariable Jwt__SigningKey, Secret-Store).</summary>
    Configuration,
    /// <summary>Jwt:KeyFile, bereits vorhanden.</summary>
    KeyFile,
    /// <summary>Jwt:KeyFile, beim ersten Start neu angelegt.</summary>
    KeyFileCreated,
    /// <summary>Nur Development: flüchtiger Zufallskey, alle Tokens verfallen beim Neustart.</summary>
    EphemeralDevelopment,
}

public sealed record JwtKeyResolution(byte[] Key, JwtKeySource Source, string? FilePath = null);

/// <summary>
/// Löst den JWT-Signing-Key beim Start auf - in dieser Reihenfolge:
///   1. <c>Jwt:SigningKey</c> (Umgebungsvariable / Secret-Store, nie in appsettings.json),
///   2. <c>Jwt:KeyFile</c>: Datei lesen bzw. beim ersten Start mit 64 Zufallsbytes (Base64) anlegen -
///      gedacht für ein Docker-Volume, damit der Key Neustarts und Container-Wechsel überlebt,
///   3. nur Development: flüchtiger Zufallskey.
/// Sonst bricht der Start mit einer verständlichen Meldung ab. In jeder Umgebung abgelehnt werden
/// Keys unter 32 Bytes und der bekannte DEV-Key aus dem Repository (<see cref="JwtKeyGuard"/>).
/// </summary>
public static class JwtKeyResolver
{
    private const int GeneratedKeyBytes = 64;

    public static JwtKeyResolution Resolve(IConfiguration config, bool isDevelopment, string contentRootPath)
    {
        var configured = config["Jwt:SigningKey"];
        if (!string.IsNullOrWhiteSpace(configured))
            return Accept(Encoding.UTF8.GetBytes(configured.Trim()), "Jwt:SigningKey", JwtKeySource.Configuration);

        var keyFile = config["Jwt:KeyFile"];
        if (!string.IsNullOrWhiteSpace(keyFile))
        {
            var path = Path.GetFullPath(keyFile.Trim(), contentRootPath);
            var existed = File.Exists(path);
            var secret = existed ? ReadKeyFile(path) : CreateKeyFile(path);
            return Accept(Encoding.UTF8.GetBytes(secret), $"Jwt:KeyFile ({path})",
                existed ? JwtKeySource.KeyFile : JwtKeySource.KeyFileCreated, path);
        }

        if (isDevelopment)
            return new JwtKeyResolution(RandomNumberGenerator.GetBytes(GeneratedKeyBytes), JwtKeySource.EphemeralDevelopment);

        throw new InvalidOperationException(
            "Es ist kein JWT-Signing-Key konfiguriert. Ohne ihn startet die API außerhalb von Development nicht. " +
            "Setze Jwt:SigningKey (Umgebungsvariable Jwt__SigningKey oder Secret-Store, mindestens 32 Bytes, " +
            "z. B. `openssl rand -base64 48`) ODER Jwt:KeyFile mit dem Pfad einer Datei " +
            "(wird beim ersten Start mit einem Zufallskey angelegt, z. B. auf einem Docker-Volume).");
    }

    private static JwtKeyResolution Accept(byte[] key, string source, JwtKeySource kind, string? filePath = null)
    {
        JwtKeyGuard.EnsureAcceptable(key, source);
        return new JwtKeyResolution(key, kind, filePath);
    }

    private static string ReadKeyFile(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Jwt:KeyFile '{path}' konnte nicht gelesen werden: {ex.Message}", ex);
        }
    }

    private static string CreateKeyFile(string path)
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(GeneratedKeyBytes));
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // CreateNew: schlägt fehl, wenn ein parallel startender Prozess die Datei schon angelegt hat.
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
            };
            // Unter Unix wird sie von Anfang an nur für den Besitzer lesbar angelegt (0600).
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

            using var stream = new FileStream(path, options);
            stream.Write(Encoding.UTF8.GetBytes(secret));
            return secret;
        }
        catch (IOException) when (File.Exists(path))
        {
            return ReadKeyFile(path); // ein anderer Start war schneller - dessen Key gilt
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"Jwt:KeyFile '{path}' konnte nicht angelegt werden: {ex.Message}", ex);
        }
    }
}
