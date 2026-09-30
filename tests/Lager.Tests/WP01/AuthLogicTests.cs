using System.IdentityModel.Tokens.Jwt;
using Lager.Application.Auth;
using Lager.Domain.Auth;
using Lager.Infrastructure.Auth;
using Microsoft.Extensions.Options;

namespace Lager.Tests.WP01;

/// <summary>Reine Logik ohne Host: Sperr-Zähler, Passwort-Policy, Rollennamen, Token-Stamp.</summary>
public class AuthLogicTests
{
    private static readonly LockoutPolicy ThreeTries = new(3, TimeSpan.FromMinutes(10));
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // ---- Lockout --------------------------------------------------------------------------------------------------

    [Fact]
    public void Counter_restarts_after_an_expired_lock_so_one_typo_does_not_lock_again()
    {
        var user = new User("anna", "hash", Role.Picker);
        user.OnFailedLogin(ThreeTries, T0);
        user.OnFailedLogin(ThreeTries, T0);
        Assert.True(user.OnFailedLogin(ThreeTries, T0)); // 3. Fehlversuch sperrt
        Assert.True(user.IsLocked(T0.AddMinutes(9)));
        Assert.False(user.IsLocked(T0.AddMinutes(11)));

        var lockedAgain = user.OnFailedLogin(ThreeTries, T0.AddMinutes(11)); // ein einziger Tippfehler nach Ablauf

        Assert.False(lockedAgain);
        Assert.Equal(1, user.FailedLoginAttempts);
        Assert.Null(user.LockedUntil);
        Assert.False(user.IsLocked(T0.AddMinutes(11)));
    }

    [Fact]
    public void A_running_lock_is_not_extended_by_further_failures_and_success_clears_the_counter()
    {
        var user = new User("anna", "hash", Role.Picker);
        for (var i = 0; i < 3; i++) user.OnFailedLogin(ThreeTries, T0);
        var lockedUntil = user.LockedUntil;

        Assert.True(user.OnFailedLogin(ThreeTries, T0.AddMinutes(5)));
        Assert.Equal(lockedUntil, user.LockedUntil);

        user.OnSuccessfulLogin();
        Assert.Equal(0, user.FailedLoginAttempts);
        Assert.Null(user.LockedUntil);
    }

    [Fact]
    public void Lockout_policy_rejects_nonsensical_values()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LockoutPolicy(0, TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LockoutPolicy(3, TimeSpan.Zero));
        Assert.Equal(5, LockoutPolicy.Default.MaxFailedAttempts);
        Assert.Equal(TimeSpan.FromMinutes(15), LockoutPolicy.Default.Duration);
    }

    // ---- Passwort-Policy ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("kurz-123", "anna", null, "mindestens 10")]
    [InlineData("Anna-Anna-Anna", "anna-anna-anna", null, "Benutzernamen")]
    [InlineData("Gleiches-Passwort-1", "anna", "Gleiches-Passwort-1", "bisherigen")]
    public void Password_policy_rejects_short_username_and_unchanged_passwords(string password, string username, string? current, string expectedFragment)
    {
        var error = PasswordPolicy.Check(password, username, current);

        Assert.NotNull(error);
        Assert.Contains(expectedFragment, error);
        var ex = Assert.Throws<ArgumentException>(() => PasswordPolicy.Validate(password, username, current));
        Assert.Equal(AuthErrorCodes.PasswordPolicy, ex.Data["code"]);
    }

    [Fact]
    public void Password_policy_accepts_ten_characters_and_limits_the_bcrypt_input_to_72_bytes()
    {
        Assert.Null(PasswordPolicy.Check("0123456789", "anna"));
        Assert.Null(PasswordPolicy.Check(new string('a', 72), "anna"));
        Assert.NotNull(PasswordPolicy.Check(new string('a', 73), "anna"));   // BCrypt würde still abschneiden
        Assert.NotNull(PasswordPolicy.Check(new string('ä', 37), "anna"));   // 74 Bytes in UTF-8
    }

    // ---- Rollennamen ----------------------------------------------------------------------------------------------

    [Fact]
    public void Role_names_must_match_exactly_ignoring_case_and_never_accept_numbers_lists_or_none()
    {
        Assert.Equal(Role.Picker | Role.Packer, AuthService.CombineRoles(new[] { "picker", "PACKER" }));
        Assert.Equal(Role.None, AuthService.CombineRoles(Array.Empty<string>()));

        foreach (var bad in new[] { "Lagermeister", "32", "Picker,Packer", "None", "", " ", "Admin!" })
            Assert.Throws<UnknownRoleException>(() => AuthService.CombineRoles(new[] { "Picker", bad }));

        // Tokens dürfen nie an einem unbekannten Claim scheitern: die lenient-Variante ignoriert ihn.
        Assert.Equal(Role.Picker, AuthService.CombineRolesLenient(new[] { "Picker", "Lagermeister" }));
    }

    // ---- Token-Stamp ----------------------------------------------------------------------------------------------

    [Fact]
    public void Issued_token_carries_the_security_stamp_that_changes_with_the_password()
    {
        var settings = Options.Create(new JwtSettings { SigningKey = "test-only-signing-key-0123456789-0123456789-0123456789" });
        var service = new JwtTokenService(settings);
        var user = new User("anna", "hash-1", Role.Picker | Role.Packer);

        string StampClaim(string token) =>
            new JwtSecurityTokenHandler().ReadJwtToken(token).Claims.Single(c => c.Type == AuthClaims.SecurityStamp).Value;

        var before = StampClaim(service.IssueToken(user).Token);
        Assert.Equal(user.GetSecurityStamp(), before);

        user.SetPasswordHash("hash-2");
        Assert.NotEqual(before, StampClaim(service.IssueToken(user).Token));
    }

    [Fact]
    public void Token_service_refuses_short_keys_and_the_known_repo_key_even_without_the_host()
    {
        Assert.Throws<InvalidOperationException>(() => new JwtTokenService(Options.Create(new JwtSettings { SigningKey = "zu-kurz" })));
        Assert.Throws<InvalidOperationException>(() => new JwtTokenService(Options.Create(
            new JwtSettings { SigningKey = "DEV-ONLY-please-replace-in-production-with-256bit-secret" })));
        Assert.Throws<InvalidOperationException>(() => new JwtTokenService(Options.Create(new JwtSettings())));
    }
}
