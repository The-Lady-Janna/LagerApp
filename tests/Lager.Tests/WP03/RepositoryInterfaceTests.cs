using System.Reflection;
using Lager.Application.Abstractions;

namespace Lager.Tests.WP03;

/// <summary>
/// CS0108-Regression: Ein Interface darf kein Member mit gleicher Signatur erneut deklarieren, das es schon
/// von <c>IRepository&lt;T&gt;</c> (oder einem anderen Basis-Interface) erbt. Früher taten das
/// IPickWaveRepository, IPurchaseOrderRepository, IReplenishmentRepository, IReturnRepository und
/// IUserRepository mit <c>ListAsync(CancellationToken)</c> und erzeugten fünf Compiler-Warnungen.
/// </summary>
public class RepositoryInterfaceTests
{
    [Fact]
    public void Application_interfaces_do_not_hide_inherited_members()
    {
        var interfaces = typeof(IRepository<>).Assembly.GetTypes()
            .Where(t => t.IsInterface)
            .ToList();
        Assert.Contains(typeof(IUserRepository), interfaces); // Sicherheitsnetz: der Scan sieht die Repository-Interfaces

        var offenders = new List<string>();
        foreach (var itf in interfaces)
        {
            var inherited = itf.GetInterfaces().SelectMany(i => i.GetMethods()).ToList();
            var declared = itf.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (var method in declared)
            {
                var parameterTypes = method.GetParameters().Select(p => p.ParameterType).ToArray();
                var hidden = inherited.Any(b =>
                    b.Name == method.Name &&
                    b.GetParameters().Select(p => p.ParameterType).SequenceEqual(parameterTypes));
                if (hidden)
                    offenders.Add($"{itf.Name}.{method.Name}({string.Join(", ", parameterTypes.Select(t => t.Name))})");
            }
        }

        Assert.True(offenders.Count == 0,
            "Interfaces deklarieren geerbte Member erneut (CS0108): " + string.Join("; ", offenders));
    }
}
