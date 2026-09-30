using Lager.Domain.PickLists;

namespace Lager.Application.Abstractions;

public interface IPickWaveRepository : IRepository<PickWave>
{
    /// <summary>Nächste Wellen-Nummer; atomar wie <see cref="IPickListRepository.NextSequenceAsync"/>.</summary>
    Task<long> NextSequenceAsync(CancellationToken ct = default);

    /// <summary>Tracked: die Welle, in der die Pickliste als erzeugte Liste geführt wird (sonst null).</summary>
    Task<PickWave?> FindByPickListAsync(Guid pickListId, CancellationToken ct = default);
}
