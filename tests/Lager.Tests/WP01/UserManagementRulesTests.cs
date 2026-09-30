using System.Net;
using System.Net.Http.Json;
using Lager.Application.Auth;
using Lager.Contracts.Auth;
using Lager.Domain.Auth;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP01;

/// <summary>Schutz vor Selbst-/Letzter-Admin-Aussperrung, strikte Rollennamen, Passwortregeln in der Benutzerverwaltung.</summary>
public class UserManagementRulesTests
{
    // ---- API ------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Admin_cannot_deactivate_himself_or_remove_his_own_admin_role()
    {
        using var factory = new LagerApiFactory();
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        var me = (await admin.GetFromJsonAsync<UserDto>("/api/auth/me"))!;

        var deactivate = await admin.PostAsync($"/api/users/{me.Id}/deactivate", null);
        var demote = await admin.PutAsJsonAsync($"/api/users/{me.Id}", new UpdateUserRequest(new[] { "Picker" }));

        Assert.Equal(HttpStatusCode.Conflict, deactivate.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, demote.StatusCode);
        // Nichts wurde verändert: Konto aktiv, Admin-Rolle da, das Token gilt weiter.
        var after = (await admin.GetFromJsonAsync<UserDto>("/api/auth/me"))!;
        Assert.True(after.IsActive);
        Assert.Contains("Admin", after.Roles);
    }

    [Fact]
    public async Task An_admin_may_deactivate_and_demote_another_admin_as_long_as_one_active_admin_remains()
    {
        using var factory = new LagerApiFactory();
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        var second = await admin.CreateUserAsync(new[] { "Admin" });

        var demote = await admin.PutAsJsonAsync($"/api/users/{second.Id}", new UpdateUserRequest(new[] { "Picker" }));
        var deactivate = await admin.PostAsync($"/api/users/{second.Id}/deactivate", null);

        Assert.Equal(HttpStatusCode.OK, demote.StatusCode);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
    }

    [Fact]
    public async Task Unknown_role_names_are_rejected_with_400_instead_of_being_ignored()
    {
        using var factory = new LagerApiFactory();
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        var user = await admin.CreateUserAsync(new[] { "Picker" });

        var create = await admin.PostAsJsonAsync("/api/users",
            new CreateUserRequest(WP01Api.NewUsername(), WP01Api.UserPassword, new[] { "Lagermeister" }));
        var update = await admin.PutAsJsonAsync($"/api/users/{user.Id}", new UpdateUserRequest(new[] { "Picker", "Amdin" }));
        var numeric = await admin.PutAsJsonAsync($"/api/users/{user.Id}", new UpdateUserRequest(new[] { "32" }));

        Assert.Equal(HttpStatusCode.BadRequest, create.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        Assert.Equal("unknown_role", await update.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.BadRequest, numeric.StatusCode);
        // Der Nutzer behält seine Rolle (keine halbe Änderung).
        var users = await admin.GetFromJsonAsync<List<UserDto>>("/api/users");
        Assert.Equal(new[] { "Picker" }, users!.Single(u => u.Id == user.Id).Roles);
    }

    [Fact]
    public async Task New_users_and_admin_resets_must_satisfy_the_password_policy()
    {
        using var factory = new LagerApiFactory();
        var admin = await factory.CreateClient().AsReadyAdminAsync();
        var user = await admin.CreateUserAsync(new[] { "Picker" });
        var username = WP01Api.NewUsername();

        var tooShort = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest(username, "kurz-1234", new[] { "Picker" }));
        var sameAsName = await admin.PostAsJsonAsync("/api/users", new CreateUserRequest(username, username, new[] { "Picker" }));
        var resetSame = await admin.PostAsJsonAsync($"/api/users/{user.Id}/reset-password", new AdminResetPasswordRequest(user.Password));
        var resetShort = await admin.PostAsJsonAsync($"/api/users/{user.Id}/reset-password", new AdminResetPasswordRequest("kurz"));

        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, sameAsName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, resetSame.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, resetShort.StatusCode);
    }

    // ---- Service-Ebene: die Regel "letzter aktiver Admin" gilt unabhängig vom Akteur --------------------------------

    [Fact]
    public async Task The_last_active_admin_can_neither_be_deactivated_nor_lose_the_admin_role()
    {
        var users = new FakeUserRepository();
        var lastAdmin = new User("chef", "hash:x", Role.Admin);
        var inactiveAdmin = new User("ex-chef", "hash:y", Role.Admin);
        inactiveAdmin.Deactivate(); // zählt nicht als aktiver Admin
        users.Users.AddRange(new[] { lastAdmin, inactiveAdmin, new User("hilfe", "hash:z", Role.Manager) });
        // Akteur ist ein anderer Benutzer (z. B. Hintergrundjob): die Regel darf trotzdem nicht fallen.
        var service = new UserService(users, new FakePasswordHasher(), new FakeUnitOfWork(), new FakeCurrentUser(Guid.NewGuid()));

        await Assert.ThrowsAsync<UserRuleViolationException>(() => service.SetActiveAsync(lastAdmin.Id, false));
        await Assert.ThrowsAsync<UserRuleViolationException>(() =>
            service.UpdateAsync(lastAdmin.Id, new UpdateUserRequest(new[] { "Manager" })));

        Assert.True(lastAdmin.IsActive);
        Assert.True(lastAdmin.HasRole(Role.Admin));
        // Mit einem zweiten aktiven Admin ist es erlaubt.
        users.Users.Add(new User("zweiter", "hash:w", Role.Admin));
        Assert.False((await service.SetActiveAsync(lastAdmin.Id, false))!.IsActive);
    }

    [Fact]
    public async Task Two_admins_deactivating_each_other_at_the_same_time_cannot_leave_the_system_without_an_active_admin()
    {
        // Prüfen und Speichern sind nicht atomar: ohne Sperre sähen beide Anfragen noch den jeweils anderen aktiven
        // Admin, beide Prüfungen bestünden. Die Latenz lässt die beiden Aufrufe wie bei einer echten Datenbank überlappen.
        var users = new FakeUserRepository { Latency = TimeSpan.FromMilliseconds(40) };
        var adminA = new User("admin-a", "hash:a", Role.Admin);
        var adminB = new User("admin-b", "hash:b", Role.Admin);
        users.Users.AddRange(new[] { adminA, adminB });
        var asA = new UserService(users, new FakePasswordHasher(), new FakeUnitOfWork(), new FakeCurrentUser(adminA.Id));
        var asB = new UserService(users, new FakePasswordHasher(), new FakeUnitOfWork(), new FakeCurrentUser(adminB.Id));

        async Task<bool> Succeeds(Task<UserDto?> call)
        {
            try { await call; return true; }
            catch (UserRuleViolationException) { return false; }
        }

        var results = await Task.WhenAll(
            Succeeds(asA.SetActiveAsync(adminB.Id, false)),
            Succeeds(asB.SetActiveAsync(adminA.Id, false)));

        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(1, users.Users.Count(u => u.IsActive && u.HasRole(Role.Admin)));

        // Dasselbe für den Rollenentzug: A und B nehmen sich gegenseitig die Admin-Rolle (jeweils der andere ist "fremd").
        var rolesA = new User("rollen-a", "hash:a", Role.Admin);
        var rolesB = new User("rollen-b", "hash:b", Role.Admin);
        var rolesRepo = new FakeUserRepository { Latency = TimeSpan.FromMilliseconds(40) };
        rolesRepo.Users.AddRange(new[] { rolesA, rolesB });
        var rolesAsA = new UserService(rolesRepo, new FakePasswordHasher(), new FakeUnitOfWork(), new FakeCurrentUser(rolesA.Id));
        var rolesAsB = new UserService(rolesRepo, new FakePasswordHasher(), new FakeUnitOfWork(), new FakeCurrentUser(rolesB.Id));

        var roleResults = await Task.WhenAll(
            Succeeds(rolesAsA.UpdateAsync(rolesB.Id, new UpdateUserRequest(new[] { "Picker" }))),
            Succeeds(rolesAsB.UpdateAsync(rolesA.Id, new UpdateUserRequest(new[] { "Picker" }))));

        Assert.Equal(1, roleResults.Count(r => r));
        Assert.Equal(1, rolesRepo.Users.Count(u => u.IsActive && u.HasRole(Role.Admin)));
    }

    [Fact]
    public async Task Changes_to_users_are_written_to_the_security_log_without_secrets()
    {
        var users = new FakeUserRepository();
        var audit = new RecordingAudit();
        var service = new UserService(users, new FakePasswordHasher(), new FakeUnitOfWork(), null, audit);

        var created = await service.CreateAsync(new CreateUserRequest("neuer-nutzer", "Geheimes-Passwort-2025", new[] { "Picker" }));
        await service.UpdateAsync(created.Id, new UpdateUserRequest(new[] { "Picker", "Packer" }));
        await service.ResetPasswordAsync(created.Id, new AdminResetPasswordRequest("Anderes-Geheimes-2025"));
        await service.SetActiveAsync(created.Id, false);

        Assert.Equal(
            new[] { SecurityEvent.UserCreated, SecurityEvent.RolesChanged, SecurityEvent.PasswordReset, SecurityEvent.UserDeactivated },
            audit.Events.Select(e => e.Event).ToArray());
        Assert.DoesNotContain(audit.Events, e => (e.Detail ?? "").Contains("Geheim") || (e.Detail ?? "").Contains("hash:"));
    }
}
