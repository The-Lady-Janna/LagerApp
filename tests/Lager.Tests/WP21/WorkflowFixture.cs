using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP21;

/// <summary>
/// Eine API-Instanz (eigene SQLite-Datei, Seed=false) samt Standard-Lager pro Testklasse. Die Tests einer Klasse legen ihre
/// Artikel und Plätze mit eindeutigen Namen an und stören sich deshalb nicht; Tests, die globalen Zustand verändern
/// (alle Picklisten löschen), bekommen eine eigene Factory.
/// </summary>
public sealed class WorkflowFixture : IAsyncLifetime
{
    public LagerApiFactory Factory { get; } = new();
    public WorldBuilder W { get; }

    /// <summary>Das Standard-Lager (zwei Standard-Plätze, Hot-Pick, Reserve); erst nach <see cref="InitializeAsync"/> gesetzt.</summary>
    public WorldBuilder.World Warehouse { get; private set; } = null!;

    public WorkflowFixture() => W = new WorldBuilder(Factory);

    public async Task InitializeAsync() => Warehouse = await W.BuildAsync();

    public Task DisposeAsync()
    {
        Factory.Dispose();
        return Task.CompletedTask;
    }
}
