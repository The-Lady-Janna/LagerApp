using Lager.Application.Abstractions;
using Lager.Contracts.Auth;
using Lager.Domain.Auth;

namespace Lager.Application.Auth;

/// <summary>
/// Admin-side user management: list, create, update profile/roles, reset
/// password, activate/deactivate. Callers are expected to be in role Admin
/// — enforcement happens at the controller (Authorize attribute).
/// Schutzregeln: Es bleibt immer mindestens ein aktiver Admin, und niemand kann sich selbst
/// deaktivieren oder die eigene Admin-Rolle entziehen (Selbst-Aussperrung).
/// </summary>
public class UserService
{
    private const int MaxUsernameLength = 64; // entspricht der Spaltenbreite in UserConfiguration

    /// <summary>
    /// Serialisiert alle Änderungen, die die Zahl aktiver Administratoren senken können (Rolle entziehen,
    /// deaktivieren). Ohne die Sperre sind "Prüfen" und "Speichern" nicht atomar: zwei Admins, die sich in
    /// derselben Sekunde gegenseitig deaktivieren, sähen beide noch einen weiteren aktiven Admin, beide
    /// Prüfungen bestünden und am Ende gäbe es keinen mehr. Die Sperre gilt je Prozess; die API läuft als
    /// einzelne Instanz (SQLite bzw. ein MySQL-Knoten).
    /// </summary>
    private static readonly SemaphoreSlim AdminRuleLock = new(1, 1);

    private readonly IUserRepository _users;
    private readonly IPasswordHasher _hasher;
    private readonly IUnitOfWork _uow;
    private readonly ICurrentUser? _currentUser;
    private readonly ISecurityAudit _audit;

    public UserService(
        IUserRepository users,
        IPasswordHasher hasher,
        IUnitOfWork uow,
        ICurrentUser? currentUser = null,
        ISecurityAudit? audit = null)
    {
        _users = users;
        _hasher = hasher;
        _uow = uow;
        _currentUser = currentUser;
        _audit = audit ?? NullSecurityAudit.Instance;
    }

    public async Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken ct = default) =>
        (await _users.ListAsync(ct)).Select(AuthService.ToDto).ToList();

    public async Task<UserDto> CreateAsync(CreateUserRequest req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || req.Username.Trim().Length < 3)
            throw new ArgumentException("Username mindestens 3 Zeichen");
        if (req.Username.Trim().Length > MaxUsernameLength)
            throw new ArgumentException($"Username höchstens {MaxUsernameLength} Zeichen");

        var username = req.Username.Trim().ToLowerInvariant();
        PasswordPolicy.Validate(req.Password, username);

        if (await _users.FindByUsernameAsync(username, ct) is not null)
            throw new InvalidOperationException("Username bereits vergeben");

        var roles = AuthService.CombineRoles(req.Roles);
        var hash = _hasher.Hash(req.Password);
        var user = new User(username, hash, roles, req.Email, req.DisplayName, req.MustChangePassword);
        await _users.AddAsync(user, ct);
        await _uow.SaveChangesAsync(ct);
        _audit.Record(SecurityEvent.UserCreated, user.Username, user.Id,
            $"Rollen: {string.Join(",", AuthService.SplitRoles(roles))}");
        return AuthService.ToDto(user);
    }

    public async Task<UserDto?> UpdateAsync(Guid id, UpdateUserRequest req, CancellationToken ct = default)
    {
        await AdminRuleLock.WaitAsync(ct);
        try { return await UpdateCoreAsync(id, req, ct); }
        finally { AdminRuleLock.Release(); }
    }

    private async Task<UserDto?> UpdateCoreAsync(Guid id, UpdateUserRequest req, CancellationToken ct)
    {
        var user = await _users.GetAsync(id, ct);
        if (user is null) return null;

        // Rollennamen zuerst prüfen: ein Tippfehler darf nichts verändern.
        var newRoles = AuthService.CombineRoles(req.Roles);
        var oldRoles = user.Roles;

        var losesAdmin = user.HasRole(Role.Admin) && (newRoles & Role.Admin) == 0;
        if (losesAdmin)
        {
            if (IsSelf(id))
                throw new UserRuleViolationException("Du kannst dir die Admin-Rolle nicht selbst entziehen.");
            if (user.IsActive && await CountOtherActiveAdminsAsync(id, ct) == 0)
                throw new UserRuleViolationException("Dem letzten aktiven Administrator kann die Admin-Rolle nicht entzogen werden.");
        }

        user.UpdateProfile(req.Email, req.DisplayName);
        user.SetRoles(newRoles);
        await _uow.SaveChangesAsync(ct);

        if (oldRoles != newRoles)
            _audit.Record(SecurityEvent.RolesChanged, user.Username, user.Id,
                $"{string.Join(",", AuthService.SplitRoles(oldRoles))} -> {string.Join(",", AuthService.SplitRoles(newRoles))}");
        return AuthService.ToDto(user);
    }

    public async Task<UserDto?> ResetPasswordAsync(Guid id, AdminResetPasswordRequest req, CancellationToken ct = default)
    {
        var user = await _users.GetAsync(id, ct);
        if (user is null) return null;

        PasswordPolicy.Validate(req.NewPassword, user.Username);
        if (_hasher.Verify(req.NewPassword, user.PasswordHash))
        {
            var same = new ArgumentException("Das neue Passwort darf nicht dem bisherigen entsprechen");
            same.Data["code"] = AuthErrorCodes.PasswordPolicy;
            throw same;
        }

        user.SetPasswordHash(_hasher.Hash(req.NewPassword), mustChange: req.MustChangeOnNextLogin);
        user.Unlock();
        await _uow.SaveChangesAsync(ct);
        _audit.Record(SecurityEvent.PasswordReset, user.Username, user.Id,
            req.MustChangeOnNextLogin ? "Wechsel beim nächsten Login erforderlich" : null);
        return AuthService.ToDto(user);
    }

    public async Task<UserDto?> SetActiveAsync(Guid id, bool active, CancellationToken ct = default)
    {
        await AdminRuleLock.WaitAsync(ct);
        try { return await SetActiveCoreAsync(id, active, ct); }
        finally { AdminRuleLock.Release(); }
    }

    private async Task<UserDto?> SetActiveCoreAsync(Guid id, bool active, CancellationToken ct)
    {
        var user = await _users.GetAsync(id, ct);
        if (user is null) return null;

        if (!active)
        {
            if (IsSelf(id))
                throw new UserRuleViolationException("Du kannst dich nicht selbst deaktivieren.");
            if (user.IsActive && user.HasRole(Role.Admin) && await CountOtherActiveAdminsAsync(id, ct) == 0)
                throw new UserRuleViolationException("Der letzte aktive Administrator kann nicht deaktiviert werden.");
        }

        var wasActive = user.IsActive;
        if (active) user.Activate(); else user.Deactivate();
        await _uow.SaveChangesAsync(ct);

        if (wasActive != active)
            _audit.Record(active ? SecurityEvent.UserActivated : SecurityEvent.UserDeactivated, user.Username, user.Id);
        return AuthService.ToDto(user);
    }

    private bool IsSelf(Guid id) => _currentUser?.UserId == id;

    /// <summary>Aktive Admins außer dem angegebenen Benutzer (Benutzerzahl ist klein, daher die vorhandene Liste).</summary>
    private async Task<int> CountOtherActiveAdminsAsync(Guid exceptId, CancellationToken ct) =>
        (await _users.ListAsync(ct)).Count(u => u.Id != exceptId && u.IsActive && u.HasRole(Role.Admin));
}
