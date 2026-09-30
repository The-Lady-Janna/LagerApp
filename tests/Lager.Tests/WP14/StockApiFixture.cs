using Lager.Tests.Infrastructure;

namespace Lager.Tests.WP14;

/// <summary>
/// Eine API-Instanz (eigene SQLite-Datei, Seed=false) pro Testklasse. Die Tests einer Klasse legen ihre Daten mit
/// eindeutigen Namen an und stören sich deshalb nicht.
/// </summary>
public sealed class StockApiFixture : IDisposable
{
    public LagerApiFactory Factory { get; } = new();
    public StockWorld World { get; }

    public StockApiFixture() => World = new StockWorld(Factory);

    public void Dispose() => Factory.Dispose();
}

/// <summary>Kurzformen für die Fehlerprüfung nach dem Vertrag: Typ + maschinenlesbarer Code in <c>Data["code"]</c>.</summary>
internal static class ErrorAssert
{
    public static async Task<TException> ThrowsWithCodeAsync<TException>(string code, Func<Task> action) where TException : Exception
    {
        var ex = await Assert.ThrowsAsync<TException>(action);
        Assert.Equal(code, ex.Data["code"]);
        return ex;
    }
}
