using Lager.Infrastructure.Persistence;
using Lager.Infrastructure.Persistence.SchemaSteps;
using Microsoft.Extensions.Logging;

// Die Test-Steps liegen in eigenen Namespaces, damit die Discovery-Tests sie gezielt (und nur sie) finden.
namespace Lager.Tests.WP09.DummySteps
{
    /// <summary>Ein Step wie ihn ein späteres Paket anlegt: nur diese Datei, keine Registrierung, keine Änderung am Upgrader.</summary>
    public sealed class Wp09DummyTableStep : ISchemaUpgradeStep
    {
        public int Order => 9002;
        public string Name => "9002_Wp09DummyTable";

        public async Task ApplyAsync(LagerDbContext db, ILogger logger)
        {
            await SchemaSql.ExecuteAsync(db, "CREATE TABLE IF NOT EXISTS Wp09Dummy (Id INTEGER PRIMARY KEY, Note TEXT)");
            await SchemaSql.ExecuteAsync(db, "INSERT INTO Wp09Dummy (Note) VALUES (@note)", ("@note", "angewendet"));
        }
    }

    /// <summary>Bewusst mit kleinerer Order als der Step oben, aber in der Datei danach: die Sortierung entscheidet, nicht die Reihenfolge im Code.</summary>
    public sealed class Wp09DummyColumnStep : ISchemaUpgradeStep
    {
        public int Order => 9001;
        public string Name => "9001_Wp09DummyColumn";

        public Task ApplyAsync(LagerDbContext db, ILogger logger) =>
            SchemaSql.EnsureColumnAsync(db, "Articles", "Wp09Marker", "TEXT NULL", "VARCHAR(20) NULL");
    }
}

namespace Lager.Tests.WP09.FailingSteps
{
    /// <summary>Legt eine Tabelle an und scheitert dann: bei SQLite muss alles zurückgerollt werden.</summary>
    public sealed class Wp09CriticalFailingStep : ISchemaUpgradeStep
    {
        public int Order => 9100;
        public string Name => "9100_Wp09CriticalFailing";

        public async Task ApplyAsync(LagerDbContext db, ILogger logger)
        {
            await SchemaSql.ExecuteAsync(db, "CREATE TABLE Wp09Half (Id INTEGER PRIMARY KEY)");
            throw new InvalidOperationException("Absichtlicher Fehler im kritischen Step");
        }
    }

    public sealed class Wp09NonCriticalFailingStep : ISchemaUpgradeStep
    {
        public int Attempts { get; private set; }

        public int Order => 9101;
        public string Name => "9101_Wp09NonCriticalFailing";
        public bool IsCritical => false;

        public Task ApplyAsync(LagerDbContext db, ILogger logger)
        {
            Attempts++;
            throw new InvalidOperationException("Absichtlicher Fehler im nicht kritischen Step");
        }
    }

    public sealed class Wp09OrderClashStep : ISchemaUpgradeStep
    {
        public int Order => 9002; // wie Wp09DummyTableStep
        public string Name => "9999_Wp09OrderClash";
        public Task ApplyAsync(LagerDbContext db, ILogger logger) => Task.CompletedTask;
    }
}

namespace Lager.Tests.WP09
{
    using Lager.Tests.WP09.DummySteps;
    using Lager.Tests.WP09.FailingSteps;

    public class SchemaStepTests
    {
        private static readonly System.Reflection.Assembly TestAssembly = typeof(SchemaStepTests).Assembly;

        [Fact]
        public void Discovery_findet_die_Steps_des_Namespace_sortiert_nach_Order_ohne_Registrierung()
        {
            var steps = SchemaStepCatalog.Discover(TestAssembly, "Lager.Tests.WP09.DummySteps");

            Assert.Equal(new[] { "9001_Wp09DummyColumn", "9002_Wp09DummyTable" }, steps.Select(s => s.Name));
        }

        [Fact]
        public void Discovery_der_Infrastructure_liefert_die_eingebauten_Steps_eindeutig_und_sortiert()
        {
            var steps = SchemaStepCatalog.Discover();

            // Spätere Pakete legen weitere Steps an: geprüft wird, dass die eingebauten dabei sind, alle gültig sind und die
            // Reihenfolge stimmt - nicht, dass es genau diese vier gibt.
            var names = steps.Select(s => s.Name).ToList();
            foreach (var builtIn in new[] { "0010_NormalizeConcurrencyTokenCase", "0020_DedupeStockItems", "0030_CaseInsensitiveUniqueIndexes", "0040_MySqlLongTextColumns" })
                Assert.Contains(builtIn, names);
            SchemaStepCatalog.Validate(steps);
            Assert.All(steps, s => Assert.True(s.Order > 0));
            Assert.Equal(steps.Select(s => s.Order).OrderBy(o => o), steps.Select(s => s.Order));

            // Bekannt für den Restore-Check: Baseline plus alle gefundenen Steps.
            var known = SchemaUpgrader.GetKnownStepNames();
            Assert.Contains(SchemaUpgrader.BaselineStepName, known);
            Assert.All(steps, s => Assert.Contains(s.Name, known));
        }

        [Fact]
        public void Doppelte_Order_oder_Name_lassen_die_Validierung_mit_klarer_Meldung_scheitern()
        {
            var sameOrder = Assert.Throws<InvalidOperationException>(() =>
                SchemaStepCatalog.Validate(new ISchemaUpgradeStep[] { new Wp09DummyTableStep(), new Wp09OrderClashStep() }));
            Assert.Contains("Order 9002", sameOrder.Message);

            var sameName = Assert.Throws<InvalidOperationException>(() =>
                SchemaStepCatalog.Validate(new ISchemaUpgradeStep[] { new Wp09DummyTableStep(), new Wp09DummyTableStep() }));
            Assert.Contains("9002_Wp09DummyTable", sameName.Message);
        }

        [Fact]
        public async Task Ein_neuer_Step_wird_ohne_Aenderung_am_Upgrader_genau_einmal_angewendet_und_vermerkt()
        {
            using var db = new StandaloneDb();
            var discovered = SchemaStepCatalog.Discover(TestAssembly, "Lager.Tests.WP09.DummySteps");

            await using (var ctx = db.CreateContext())
            {
                await ctx.Database.EnsureCreatedAsync();
                await SchemaUpgrader.UpgradeAsync(ctx, new ListLogger(), discovered);
                await SchemaUpgrader.UpgradeAsync(ctx, new ListLogger(), discovered); // zweiter Start
            }

            Assert.Equal(1, Wp09Sql.Count(db.DbPath, "SELECT COUNT(*) FROM Wp09Dummy")); // nicht doppelt ausgeführt
            Assert.Equal(1, Wp09Sql.Count(db.DbPath, "SELECT COUNT(*) FROM pragma_table_info('Articles') WHERE name = 'Wp09Marker'"));
            var order = Wp09Sql.Strings(db.DbPath, "SELECT Name FROM __LagerSchemaVersion WHERE Name LIKE '900%' ORDER BY StepOrder");
            Assert.Equal(new[] { "9001_Wp09DummyColumn", "9002_Wp09DummyTable" }, order);
        }

        [Fact]
        public async Task Ein_kritischer_Step_bricht_den_Start_ab_wird_geloggt_und_hinterlaesst_keinen_Halbzustand()
        {
            using var db = new StandaloneDb();
            var logger = new ListLogger();

            await using (var ctx = db.CreateContext())
            {
                await ctx.Database.EnsureCreatedAsync();
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    SchemaUpgrader.UpgradeAsync(ctx, logger, new ISchemaUpgradeStep[] { new Wp09CriticalFailingStep() }));
                Assert.Contains("9100_Wp09CriticalFailing", ex.Message);
                Assert.Contains("Absichtlicher Fehler", ex.Message);
            }

            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Critical && e.Message.Contains("9100_Wp09CriticalFailing"));
            // SQLite: Schritt und Vermerk in einer Transaktion -> die angelegte Tabelle und der Vermerk fehlen.
            Assert.Equal(0, Wp09Sql.Count(db.DbPath, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'Wp09Half'"));
            Assert.Equal(0, Wp09Sql.Count(db.DbPath, "SELECT COUNT(*) FROM __LagerSchemaVersion WHERE Name = '9100_Wp09CriticalFailing'"));
        }

        [Fact]
        public async Task Ein_nicht_kritischer_Step_scheitert_geloggt_wird_nicht_vermerkt_und_beim_naechsten_Start_wiederholt()
        {
            using var db = new StandaloneDb();
            var logger = new ListLogger();
            var step = new Wp09NonCriticalFailingStep();

            await using (var ctx = db.CreateContext())
            {
                await ctx.Database.EnsureCreatedAsync();
                await SchemaUpgrader.UpgradeAsync(ctx, logger, new ISchemaUpgradeStep[] { step }); // wirft nicht
                await SchemaUpgrader.UpgradeAsync(ctx, logger, new ISchemaUpgradeStep[] { step });
            }

            Assert.Equal(2, step.Attempts);
            Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("9101_Wp09NonCriticalFailing"));
            Assert.Equal(0, Wp09Sql.Count(db.DbPath, "SELECT COUNT(*) FROM __LagerSchemaVersion WHERE Name = '9101_Wp09NonCriticalFailing'"));
        }

        [Fact]
        public void SQL_Bezeichner_werden_gegen_die_Whitelist_geprueft()
        {
            Assert.Equal("StockItems", SchemaSql.Ident("StockItems"));
            Assert.Equal("IX_Articles_Sku", SchemaSql.Ident("IX_Articles_Sku"));
            foreach (var bad in new[] { "", "Articles; DROP TABLE Users", "a b", "x'--", "1abc", "Tab\"le", "a.b" })
                Assert.Throws<ArgumentException>(() => SchemaSql.Ident(bad));
        }
    }
}
