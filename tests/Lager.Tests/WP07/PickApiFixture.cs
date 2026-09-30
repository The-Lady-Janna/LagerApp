using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP07;

/// <summary>
/// Eine API-Instanz (eigene SQLite-Datei, Seed=false) pro Testklasse. Die Tests einer Klasse legen ihre Daten
/// mit eindeutigen Namen an und stören sich deshalb nicht; Tests, die globalen Zustand verändern (alle Picklisten
/// löschen, Nummernkreis), bekommen eine eigene Factory.
/// </summary>
public sealed class PickApiFixture : IDisposable
{
    public LagerApiFactory Factory { get; } = new();
    public PickWorld World { get; }

    public PickApiFixture() => World = new PickWorld(Factory);

    public void Dispose() => Factory.Dispose();
}
