using System.Reflection;

namespace Lager.Infrastructure.Persistence.SchemaSteps;

/// <summary>
/// Findet die <see cref="ISchemaUpgradeStep"/>-Implementierungen per Reflection. Damit reicht eine neue Datei in
/// diesem Ordner, um einen Schritt wirksam zu machen; eine Registrierungsliste gibt es nicht.
/// </summary>
public static class SchemaStepCatalog
{
    /// <summary>Namespace (samt Unter-Namespaces), aus dem der Upgrader seine Schritte sammelt.</summary>
    public const string StepNamespace = "Lager.Infrastructure.Persistence.SchemaSteps";

    /// <summary>Die Schritte der Infrastructure-Assembly, sortiert nach <see cref="ISchemaUpgradeStep.Order"/>.</summary>
    public static IReadOnlyList<ISchemaUpgradeStep> Discover() =>
        Discover(typeof(ISchemaUpgradeStep).Assembly, StepNamespace);

    /// <summary>
    /// Sammelt alle konkreten Implementierungen (parameterloser Konstruktor, auch nicht öffentlich) aus
    /// <paramref name="assembly"/>, optional beschränkt auf einen Namespace samt Unter-Namespaces.
    /// Ergebnis: sortiert nach Order, bei Gleichstand nach Name.
    /// </summary>
    public static IReadOnlyList<ISchemaUpgradeStep> Discover(Assembly assembly, string? @namespace)
    {
        var steps = new List<ISchemaUpgradeStep>();
        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract || type.IsInterface || !typeof(ISchemaUpgradeStep).IsAssignableFrom(type)) continue;
            if (@namespace is not null && !InNamespace(type, @namespace)) continue;

            if (type.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null, Type.EmptyTypes, modifiers: null) is null)
                throw new InvalidOperationException($"Schema-Step {type.FullName} braucht einen parameterlosen Konstruktor.");

            steps.Add((ISchemaUpgradeStep)Activator.CreateInstance(type, nonPublic: true)!);
        }

        return steps
            .OrderBy(s => s.Order)
            .ThenBy(s => s.Name, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Prüft eine Schrittfolge: Name gesetzt und eindeutig, Order eindeutig (die Reihenfolge wäre sonst
    /// zufällig). Wirft <see cref="InvalidOperationException"/> mit einer Meldung, die die Schritte nennt.
    /// </summary>
    public static void Validate(IEnumerable<ISchemaUpgradeStep> steps)
    {
        var seenNames = new Dictionary<string, ISchemaUpgradeStep>(StringComparer.OrdinalIgnoreCase);
        var seenOrders = new Dictionary<int, ISchemaUpgradeStep>();
        foreach (var step in steps)
        {
            if (string.IsNullOrWhiteSpace(step.Name))
                throw new InvalidOperationException($"Schema-Step {step.GetType().FullName} hat keinen Namen.");
            if (seenNames.TryGetValue(step.Name, out var sameName))
                throw new InvalidOperationException(
                    $"Schema-Step-Name '{step.Name}' ist doppelt vergeben ({sameName.GetType().FullName} und {step.GetType().FullName}).");
            if (seenOrders.TryGetValue(step.Order, out var sameOrder))
                throw new InvalidOperationException(
                    $"Schema-Step-Order {step.Order} ist doppelt vergeben ({sameOrder.GetType().FullName} und {step.GetType().FullName}).");
            seenNames[step.Name] = step;
            seenOrders[step.Order] = step;
        }
    }

    private static bool InNamespace(Type type, string @namespace) =>
        type.Namespace is { } ns && (ns == @namespace || ns.StartsWith(@namespace + ".", StringComparison.Ordinal));
}
