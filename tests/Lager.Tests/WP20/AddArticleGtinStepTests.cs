using Lager.Application.Abstractions;
using Lager.Domain.Articles;
using Lager.Infrastructure.Persistence;
using Lager.Infrastructure.Persistence.SchemaSteps;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lager.Tests.WP20;

/// <summary>
/// Der Schema-Schritt für Articles.Gtin: Legacy-Datenbanken bekommen Spalte und eindeutigen Index (nur für nicht-NULL-Werte),
/// Altdaten bleiben, der Schritt ist idempotent und ändert eine frisch aus dem Modell angelegte Datenbank nicht.
/// </summary>
public class AddArticleGtinStepTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"lager-wp20-{Guid.NewGuid():N}");

    public AddArticleGtinStepTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* Temp-Verzeichnis, wird beim nächsten Cleanup entfernt */ }
        catch (UnauthorizedAccessException) { /* dito */ }
    }

    private string DbPath => Path.Combine(_dir, "lager.db");

    private LagerDbContext NewContext() =>
        new(new DbContextOptionsBuilder<LagerDbContext>().UseSqlite($"Data Source={DbPath};Pooling=False").Options);

    private long Count(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private void Execute(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string InsertArticle(string sku) =>
        "INSERT INTO Articles (Id, Sku, Name, WeightGrams, MinStock, ReorderPoint, MaxStock, PurchasePriceCents, CreatedAt, UpdatedAt, ConcurrencyToken, " +
        "LengthMm, WidthMm, HeightMm, IsStackable, StackingAxis, StackingIncrementMm) " +
        $"VALUES ('{Guid.NewGuid().ToString().ToUpperInvariant()}', '{sku}', 'Altartikel', 100, 0, 0, 0, 0, '2025-01-01 10:00:00', '2025-01-01 10:00:00', " +
        $"'{Guid.NewGuid().ToString().ToUpperInvariant()}', 10, 10, 10, 0, 2, 0)";

    /// <summary>Zustand vor WP20: die Datenbank aus dem Modell, aber ohne Gtin-Spalte und -Index, mit zwei Altartikeln.</summary>
    private async Task BuildLegacyDatabaseAsync()
    {
        await using (var db = NewContext()) await db.Database.EnsureCreatedAsync();
        Execute($"DROP INDEX {AddArticleGtinStep.IndexName}");
        Execute("ALTER TABLE Articles DROP COLUMN Gtin");
        Execute(InsertArticle("ALT-1"));
        Execute(InsertArticle("ALT-2"));
    }

    private static async Task<bool> HasGtinColumnAsync(LagerDbContext db) =>
        (await SchemaSql.GetColumnsAsync(db, "Articles")).Contains("Gtin");

    [Fact]
    public void The_step_is_found_by_the_catalog_in_the_WP20_range_and_is_critical()
    {
        var steps = SchemaStepCatalog.Discover();

        var step = Assert.Single(steps, s => s.Name == "2000_AddArticleGtin");
        Assert.Equal(2000, step.Order);
        Assert.True(step.IsCritical);
        SchemaStepCatalog.Validate(steps);   // Order und Name eindeutig
    }

    [Fact]
    public async Task A_legacy_database_gets_the_column_and_a_partial_unique_index_and_keeps_its_articles()
    {
        await BuildLegacyDatabaseAsync();
        await using (var db = NewContext())
        {
            Assert.False(await HasGtinColumnAsync(db));
            await SchemaUpgrader.UpgradeAsync(db);
            Assert.True(await HasGtinColumnAsync(db));
            Assert.True(await SchemaSql.GetIndexUniqueAsync(db, "Articles", AddArticleGtinStep.IndexName));
        }

        // Altartikel unverändert, ohne GTIN; der Index gilt nur für nicht-NULL-Werte (partiell).
        Assert.Equal(2, Count("SELECT COUNT(*) FROM Articles WHERE Gtin IS NULL AND Name = 'Altartikel'"));
        Assert.Equal(1, Count($"SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = '{AddArticleGtinStep.IndexName}' AND sql LIKE '%WHERE%'"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM __LagerSchemaVersion WHERE Name = '2000_AddArticleGtin'"));

        // Viele Artikel ohne GTIN sind erlaubt, dieselbe GTIN zweimal nicht.
        Execute(InsertArticle("NEU-1"));
        Execute("UPDATE Articles SET Gtin = '4006381333931' WHERE Sku = 'ALT-1'");
        Assert.ThrowsAny<Exception>(() => Execute("UPDATE Articles SET Gtin = '4006381333931' WHERE Sku = 'ALT-2'"));
    }

    [Fact]
    public async Task A_legacy_database_stays_usable_with_EF_after_the_upgrade()
    {
        await BuildLegacyDatabaseAsync();
        await using (var db = NewContext()) await SchemaUpgrader.UpgradeAsync(db);

        await using (var db = NewContext())
        {
            var article = await db.Articles.SingleAsync(a => a.Sku == "ALT-1");
            Assert.Null(article.Gtin);
            article.SetGtin("4006381333931");
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext())
        {
            var reloaded = await db.Articles.SingleAsync(a => a.Sku == "ALT-1");
            Assert.Equal("4006381333931", reloaded.Gtin);
        }
    }

    [Fact]
    public async Task The_step_is_idempotent_and_leaves_a_database_created_from_the_model_alone()
    {
        // Frisch aus dem Modell: Spalte und Index sind schon da (der Index ist dort ein gewöhnlicher Unique-Index).
        await using (var db = NewContext())
        {
            await db.Database.EnsureCreatedAsync();
            Assert.True(await HasGtinColumnAsync(db));
            var before = Count($"SELECT COUNT(*) FROM sqlite_master WHERE name = '{AddArticleGtinStep.IndexName}'");

            await SchemaUpgrader.UpgradeAsync(db);                 // alle Schritte, auch dieser
            await new AddArticleGtinStep().ApplyAsync(db, NullLogger.Instance);   // und ein zweites Mal ausdrücklich

            Assert.Equal(before, Count($"SELECT COUNT(*) FROM sqlite_master WHERE name = '{AddArticleGtinStep.IndexName}'"));
            Assert.True(await SchemaSql.GetIndexUniqueAsync(db, "Articles", AddArticleGtinStep.IndexName));
        }
    }

    [Fact]
    public async Task The_step_does_nothing_without_an_articles_table()
    {
        await using var db = NewContext();
        await db.Database.OpenConnectionAsync();

        await new AddArticleGtinStep().ApplyAsync(db, NullLogger.Instance);   // keine Ausnahme

        Assert.False(await SchemaSql.TableExistsAsync(db, "Articles"));
    }

    // ---- Repository-Schnittstelle bleibt für bestehende Implementierungen (Test-Fakes) kompatibel ----------------

    private sealed class MinimalArticles : IArticleRepository
    {
        private readonly List<Article> _articles;
        public MinimalArticles(params Article[] articles) => _articles = articles.ToList();

        public Task<Article?> GetAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_articles.FirstOrDefault(a => a.Id == id));
        public Task<IReadOnlyList<Article>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Article>>(_articles);
        public Task AddAsync(Article entity, CancellationToken ct = default) { _articles.Add(entity); return Task.CompletedTask; }
        public void Remove(Article entity) => _articles.Remove(entity);
        public Task<Article?> GetBySkuAsync(string sku, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, Article>> GetManyAsync(IEnumerable<Guid> ids, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public async Task The_new_lookup_members_have_default_implementations_for_existing_fakes()
    {
        var withGtin = new Article("A", "A", Dimensions.Zero, 0, StackingInfo.NotStackable);
        withGtin.SetGtin("036000291452");
        withGtin.SetAlternatives(new[] { "Ersatz-1" });
        var other = new Article("B", "B", Dimensions.Zero, 0, StackingInfo.NotStackable);
        IArticleRepository repository = new MinimalArticles(withGtin, other);

        Assert.Same(withGtin, Assert.Single(await repository.FindByGtinAsync("0036000291452")));   // gleichwertige Länge
        Assert.Empty(await repository.FindByGtinAsync("4006381333931"));
        Assert.Same(withGtin, Assert.Single(await repository.FindByAlternativeSkuAsync(" ersatz-1 ")));
        Assert.Empty(await repository.FindByAlternativeSkuAsync("gibt-es-nicht"));
    }
}
