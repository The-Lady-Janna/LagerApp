using Lager.Domain.Articles;
using Lager.Domain.Packing;

namespace Lager.Application.Packing;

public record PackInput(Article Article, int Quantity);

public interface IPackingOptimizer
{
    PackingPlan Plan(Guid orderId, string orderNumber, IEnumerable<PackInput> items, IReadOnlyList<CartonType> cartonTypes);
}
