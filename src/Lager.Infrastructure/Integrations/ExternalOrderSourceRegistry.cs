using Lager.Application.Abstractions;

namespace Lager.Infrastructure.Integrations;

public class ExternalOrderSourceRegistry : IExternalOrderSourceRegistry
{
    public IReadOnlyList<IExternalOrderSource> All { get; }
    public ExternalOrderSourceRegistry(IEnumerable<IExternalOrderSource> sources) => All = sources.ToList();
}
