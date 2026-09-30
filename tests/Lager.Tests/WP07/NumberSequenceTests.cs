using Lager.Application.Abstractions;
using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP07;

/// <summary>
/// Der Nummernkreis der Picklisten und Wellen zählt atomar in der Datenbank: parallele Aufrufer bekommen nie
/// dieselbe Nummer (früher: Lesen-Ändern-Schreiben, der Verlierer scheiterte am Unique-Index mit HTTP 500).
/// </summary>
public class NumberSequenceTests
{
    /// <summary>Startet alle Aufgaben gleichzeitig (Startschuss), damit sie wirklich um die Zähler-Zeile konkurrieren.</summary>
    private static async Task<T[]> InParallelAsync<T>(int count, Func<int, Task<T>> work)
    {
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, count).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            return await work(i);
        })).ToArray();
        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Ten_parallel_callers_get_ten_distinct_consecutive_pick_list_numbers()
    {
        using var factory = new LagerApiFactory();
        var world = new PickWorld(factory);

        // Die Zähler-Zeile existiert noch nicht: auch das gleichzeitige Erst-Anlegen muss funktionieren.
        var numbers = await InParallelAsync(10, async _ =>
        {
            using var scope = world.NewScope();
            return await scope.Get<IPickListRepository>().NextSequenceAsync();
        });

        Assert.Equal(10, numbers.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, 10).Select(n => (long)n), numbers.Order());
    }

    [Fact]
    public async Task Pick_list_and_wave_sequences_count_independently()
    {
        using var factory = new LagerApiFactory();
        var world = new PickWorld(factory);

        var lists = await InParallelAsync(5, async _ =>
        {
            using var scope = world.NewScope();
            return await scope.Get<IPickListRepository>().NextSequenceAsync();
        });
        var waves = await InParallelAsync(5, async _ =>
        {
            using var scope = world.NewScope();
            return await scope.Get<IPickWaveRepository>().NextSequenceAsync();
        });

        Assert.Equal(Enumerable.Range(1, 5).Select(n => (long)n), lists.Order());
        Assert.Equal(Enumerable.Range(1, 5).Select(n => (long)n), waves.Order());
    }

    [Fact]
    public async Task Reset_starts_the_pick_list_numbering_over_again()
    {
        using var factory = new LagerApiFactory();
        var world = new PickWorld(factory);

        using (var scope = world.NewScope())
        {
            var repo = scope.Get<IPickListRepository>();
            Assert.Equal(1, await repo.NextSequenceAsync());
            Assert.Equal(2, await repo.NextSequenceAsync());
            await repo.ResetSequenceAsync();
            Assert.Equal(1, await repo.NextSequenceAsync());
        }

        // ein Reset ohne vorhandene Zähler-Zeile ist ein No-op, kein Fehler
        using var fresh = new LagerApiFactory();
        var freshWorld = new PickWorld(fresh);
        using var freshScope = freshWorld.NewScope();
        await freshScope.Get<IPickListRepository>().ResetSequenceAsync();
        Assert.Equal(1, await freshScope.Get<IPickListRepository>().NextSequenceAsync());
    }

    [Fact]
    public async Task Parallel_generation_of_pick_lists_yields_distinct_numbers_without_a_single_failure()
    {
        using var factory = new LagerApiFactory();
        var world = new PickWorld(factory);
        var site = await world.AddWarehouseAsync();
        var bin = await world.AddBinAsync(site, PickWorld.Unique("BIN"));
        var article = await world.AddArticleAsync();
        await world.AddStockAsync(article, bin, 1_000);
        var orders = new List<Guid>();
        for (var i = 0; i < 6; i++) orders.Add(await world.AddOrderAsync((article, 1)));

        var lists = await InParallelAsync(orders.Count, i => world.GenerateAsync(orders[i]));

        Assert.Equal(6, lists.Select(l => l.PickListNumber).Distinct().Count());
        Assert.Equal(6, await world.PickListCountAsync());
    }
}
