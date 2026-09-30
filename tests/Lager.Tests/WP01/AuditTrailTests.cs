using System.Text.Json;
using Lager.Domain.Articles;
using Lager.Domain.Auditing;
using Lager.Domain.Auth;
using Lager.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lager.Tests.WP01;

/// <summary>
/// AuditingInterceptor gegen eine echte SQLite-Datenbank (In-Memory): Owned Types, Deletes, Benutzer ohne
/// PasswordHash-Werte und die Zuordnung des Akteurs.
/// </summary>
public sealed class AuditTrailTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public AuditTrailTests() => _connection.Open();
    public void Dispose() => _connection.Dispose();

    private LagerDbContext NewContext(FakeCurrentUser? currentUser)
    {
        var options = new DbContextOptionsBuilder<LagerDbContext>()
            .UseSqlite(_connection)
            .AddInterceptors(new AuditingInterceptor(currentUser))
            .Options;
        var db = new LagerDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private static Article NewArticle(string sku = "SKU-1") =>
        new(sku, "Schraube", new Dimensions(10, 20, 30), 5, StackingInfo.NotStackable);

    private static async Task<List<AuditEntry>> AuditsFor(LagerDbContext db, string entityType) =>
        (await db.AuditEntries.AsNoTracking().OrderBy(a => a.At).ToListAsync()).Where(a => a.EntityType == entityType).ToList();

    [Fact]
    public async Task Owned_type_changes_appear_in_the_diff_of_their_owner()
    {
        await using var db = NewContext(new FakeCurrentUser(Guid.NewGuid(), "lena"));
        var article = NewArticle();
        db.Articles.Add(article);
        await db.SaveChangesAsync();

        // Nur die Abmessungen ändern (Name bleibt): der Besitzer selbst hat keine geänderte Eigenschaft.
        article.Update(article.Name, article.Description, new Dimensions(10, 20, 99), article.WeightGrams, article.Stacking);
        await db.SaveChangesAsync();

        var audits = await AuditsFor(db, nameof(Article));
        Assert.Equal(new[] { "Added", "Modified" }, audits.Select(a => a.Operation).ToArray());
        Assert.Contains("Dimensions.LengthMm", audits[0].ChangesJson); // Added: Owned Types sind dabei
        using var diff = JsonDocument.Parse(audits[1].ChangesJson!);
        var height = diff.RootElement.GetProperty("Dimensions.HeightMm");
        Assert.Equal(30, height.GetProperty("old").GetInt32());
        Assert.Equal(99, height.GetProperty("new").GetInt32());
        Assert.False(diff.RootElement.TryGetProperty("Dimensions.LengthMm", out _)); // unveränderte Werte fehlen
        Assert.All(audits, a => Assert.Equal("lena", a.User));
    }

    [Fact]
    public async Task Deletes_record_the_last_state_instead_of_nothing()
    {
        await using var db = NewContext(new FakeCurrentUser(Guid.NewGuid(), "lena"));
        var article = NewArticle("SKU-DEL");
        db.Articles.Add(article);
        await db.SaveChangesAsync();

        db.Articles.Remove(article);
        await db.SaveChangesAsync();

        var deleted = (await AuditsFor(db, nameof(Article))).Single(a => a.Operation == "Deleted");
        Assert.NotNull(deleted.ChangesJson);
        using var state = JsonDocument.Parse(deleted.ChangesJson!);
        Assert.Equal("SKU-DEL", state.RootElement.GetProperty("Sku").GetString());
        Assert.Equal(20, state.RootElement.GetProperty("Dimensions.WidthMm").GetInt32());
    }

    [Fact]
    public async Task User_changes_are_audited_but_the_password_hash_value_never_appears()
    {
        await using var db = NewContext(new FakeCurrentUser(Guid.NewGuid(), "admin"));
        var user = new User("anna", "$2a$12$geheimer-hash-wert-1", Role.Picker);
        db.Users.Add(user);
        await db.SaveChangesAsync();

        user.SetPasswordHash("$2a$12$geheimer-hash-wert-2");
        user.SetRoles(Role.Picker | Role.Packer);
        await db.SaveChangesAsync();
        user.OnFailedLogin();      // Login-Buchhaltung: kein Audit-Eintrag (gehört ins Sicherheits-Log)
        user.OnSuccessfulLogin();
        await db.SaveChangesAsync();

        var audits = await AuditsFor(db, nameof(User));
        Assert.Equal(new[] { "Added", "Modified" }, audits.Select(a => a.Operation).ToArray());
        Assert.All(audits, a => Assert.DoesNotContain("geheimer-hash", a.ChangesJson));
        Assert.Contains("PasswordHash", audits[1].ChangesJson); // dass er sich geändert hat, steht drin
        Assert.Contains("Roles", audits[1].ChangesJson);
        Assert.DoesNotContain("LastLoginAt", audits[1].ChangesJson);
    }

    [Fact]
    public async Task Actor_is_anonymous_for_unauthenticated_requests_and_system_only_outside_requests()
    {
        var actors = new List<string?>();
        foreach (var currentUser in new FakeCurrentUser?[]
                 {
                     new(userId: null),                                 // Request ohne Anmeldung
                     new(userId: null, isSystemContext: true),          // Startup/Seeder/Hintergrund
                     null,                                              // gar kein Benutzerkontext registriert
                 })
        {
            await using var db = NewContext(currentUser);
            db.Articles.Add(NewArticle("SKU-" + Guid.NewGuid().ToString("N")[..6]));
            await db.SaveChangesAsync();
            actors.Add((await db.AuditEntries.AsNoTracking().OrderByDescending(a => a.At).FirstAsync()).User);
        }

        Assert.Equal(new[] { "anonymous", "system", "system" }, actors);
    }
}
