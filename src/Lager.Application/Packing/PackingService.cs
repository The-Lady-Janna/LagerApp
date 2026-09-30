using Lager.Application.Abstractions;
using Lager.Contracts.Packing;
using Lager.Domain.Articles;
using Lager.Domain.Packing;

namespace Lager.Application.Packing;

public class PackingService
{
    /// <summary>Tiefe, bis zu der Bundles in Bundles aufgelöst werden (schützt vor Zyklen A → B → A).</summary>
    private const int MaxBundleDepth = 5;

    private readonly IOrderRepository _orders;
    private readonly IArticleRepository _articles;
    private readonly IPackingOptimizer _optimizer;

    public PackingService(IOrderRepository orders, IArticleRepository articles, IPackingOptimizer optimizer)
    {
        _orders = orders;
        _articles = articles;
        _optimizer = optimizer;
    }

    /// <summary>
    /// Verpackungsvorschlag für eine Bestellung; null, wenn es die Bestellung nicht gibt. Bundle-Artikel werden wie
    /// bei der Pickliste in ihre Komponenten aufgelöst (das Bundle selbst hat keinen Bestand und keinen eigenen
    /// Karton-Platzbedarf). Artikel, die nicht (mehr) existieren, brechen den Plan nicht ab, sondern erscheinen in
    /// <see cref="PackingPlanDto.Warnings"/>.
    /// </summary>
    public async Task<PackingPlanDto?> PlanForOrderAsync(Guid orderId, CancellationToken ct = default)
    {
        var order = await _orders.GetAsync(orderId, ct);
        if (order is null) return null;

        var warnings = new List<string>();
        var articles = await LoadArticlesWithComponentsAsync(order.Lines.Select(l => l.ArticleId), ct);

        var inputs = new List<PackInput>();
        var missing = new HashSet<Guid>();
        foreach (var line in order.Lines)
            Expand(line.ArticleId, line.Quantity, 0, articles, inputs, missing, warnings);

        foreach (var id in missing.OrderBy(i => i))
            warnings.Insert(0, $"Artikel {id} wurde nicht gefunden und ist im Verpackungsvorschlag nicht enthalten.");

        var plan = _optimizer.Plan(order.Id, order.OrderNumber, inputs, StandardCartons.All);
        warnings.AddRange(plan.Warnings);

        return new PackingPlanDto(
            plan.OrderId,
            plan.OrderNumber,
            plan.Cartons.Select(c => new CartonDto(
                c.Index,
                c.CartonType.Name,
                c.CartonType.InnerLengthMm,
                c.CartonType.InnerWidthMm,
                c.CartonType.InnerHeightMm,
                c.TotalWeightGrams,
                c.FillRatio,
                c.Items.Select(i => new CartonAllocationDto(i.ArticleId, i.ArticleSku, i.Quantity)).ToList())).ToList(),
            plan.Unpacked.Select(i => new CartonAllocationDto(i.ArticleId, i.ArticleSku, i.Quantity)).ToList(),
            warnings);
    }

    /// <summary>Lädt die Artikel der Zeilen und, ebenenweise, die Komponenten der darunter liegenden Bundles.</summary>
    private async Task<Dictionary<Guid, Article>> LoadArticlesWithComponentsAsync(IEnumerable<Guid> lineArticleIds, CancellationToken ct)
    {
        var known = new Dictionary<Guid, Article>();
        var wanted = lineArticleIds.Distinct().ToList();
        for (var level = 0; level <= MaxBundleDepth && wanted.Count > 0; level++)
        {
            var loaded = await _articles.GetManyAsync(wanted, ct);
            foreach (var (id, article) in loaded) known[id] = article;

            wanted = loaded.Values
                .Where(a => a.IsBundle)
                .SelectMany(a => a.BundleComponents.Select(c => c.ComponentArticleId))
                .Where(id => !known.ContainsKey(id))
                .Distinct()
                .ToList();
        }
        return known;
    }

    private static void Expand(
        Guid articleId, int quantity, int depth,
        IReadOnlyDictionary<Guid, Article> articles, List<PackInput> inputs, HashSet<Guid> missing, List<string> warnings)
    {
        if (!articles.TryGetValue(articleId, out var article))
        {
            missing.Add(articleId);
            return;
        }

        if (!article.IsBundle)
        {
            inputs.Add(new PackInput(article, quantity));
            return;
        }

        if (depth >= MaxBundleDepth)
        {
            var warning = $"Bundle {article.Sku} ist zu tief verschachtelt und wird als einzelner Artikel verpackt.";
            if (!warnings.Contains(warning)) warnings.Add(warning);
            inputs.Add(new PackInput(article, quantity));
            return;
        }

        foreach (var component in article.BundleComponents)
        {
            // Riesige Mengen dürfen keinen Überlauf (und damit HTTP 500) auslösen: auf int.MaxValue begrenzen.
            var componentQuantity = (long)component.Quantity * quantity;
            if (componentQuantity > int.MaxValue)
            {
                var warning = $"Die Menge von {article.Sku} ist so groß, dass die Komponentenmenge auf {int.MaxValue} begrenzt wurde.";
                if (!warnings.Contains(warning)) warnings.Add(warning);
                componentQuantity = int.MaxValue;
            }

            Expand(component.ComponentArticleId, (int)componentQuantity, depth + 1, articles, inputs, missing, warnings);
        }
    }
}
