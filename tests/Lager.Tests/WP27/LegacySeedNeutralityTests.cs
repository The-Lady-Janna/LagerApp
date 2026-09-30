using System.Text.RegularExpressions;
using Lager.Api.Seeding;
using Microsoft.EntityFrameworkCore;

namespace Lager.Tests.WP27;

/// <summary>
/// Auch der kleine Altbestand-Datensatz (<c>SeedAsync(db)</c> für den Reseed-Endpunkt und den veralteten Schalter
/// <c>Database:Seed</c>) enthält nur erfundene, neutrale Bezeichnungen: keine Personen-, Firmen- oder Markennamen.
/// </summary>
public class LegacySeedNeutralityTests
{
    /// <summary>Häufige deutsche Familiennamen: ein Kundenverweis wie "Kunde Müller" wirkt wie eine echte Person.</summary>
    private static readonly Regex Surnames = new(
        @"\b(M(ü|ue)ller|Schmidt|Schneider|Fischer|Weber|Meyer|Meier|Wagner|Becker|Schulz|Hoffmann|Schr(ö|oe)der|Koch)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [Fact]
    public async Task Der_Altbestand_Datensatz_nennt_als_Kundenverweis_keine_Personennamen()
    {
        using var db = new DemoDb();
        await using var ctx = await db.CreateEmptyAsync();

        Assert.True(await DemoDataSeeder.SeedAsync(ctx));

        var references = await ctx.Orders.AsNoTracking().Select(o => o.CustomerReference).ToListAsync();
        Assert.Equal(8, references.Count);
        Assert.All(references, r =>
        {
            Assert.False(string.IsNullOrWhiteSpace(r));
            Assert.False(Surnames.IsMatch(r!), $"Kundenverweis '{r}' nennt einen Familiennamen");
        });
    }

    [Fact]
    public void Der_Seeder_Quelltext_enthaelt_keine_Familiennamen()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot.Path, "src", "Lager.Api", "Seeding", "DemoDataSeeder.cs"));

        Assert.False(Surnames.IsMatch(source), $"DemoDataSeeder.cs nennt einen Familiennamen: {Surnames.Match(source).Value}");
    }
}
