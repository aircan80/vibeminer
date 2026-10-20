using System.Reflection;
using Xunit;

namespace VibeMiner.Backend.Tests;

/// <summary>
/// GC.GetTotalMemory reports the whole process heap. A delta between two samples only
/// measures one test when nothing else in the process runs in between, so every test that
/// samples it has to sit in a collection with parallelization disabled. xUnit runs those
/// collections one at a time after the parallel ones, which also keeps this collection
/// clear of the "Items projection builder" gate.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ManagedHeapBudgetCollection
{
    public const string Name = "Managed heap budget";
}

public sealed class ManagedHeapBudgetTests
{
    [Fact]
    public void EveryProcessWideHeapSampleRunsInANonParallelCollection()
    {
        var target = typeof(GC).GetMethod(nameof(GC.GetTotalMemory), [typeof(bool)])!;
        var assembly = typeof(ManagedHeapBudgetTests).Assembly;
        var isolated = assembly.GetTypes()
            .Select(type => type.GetCustomAttributesData().SingleOrDefault(a => a.AttributeType == typeof(CollectionDefinitionAttribute)))
            .Where(a => a is not null && a.NamedArguments.Any(n => n.MemberName == nameof(CollectionDefinitionAttribute.DisableParallelization) && (bool)n.TypedValue.Value!))
            .Select(a => (string)a!.ConstructorArguments[0].Value!).ToHashSet(StringComparer.Ordinal);
        var samplers = assembly.GetTypes().Where(type => Calls(type, target)).Select(Outermost).Distinct().ToArray();
        // The scan has to find the known samplers, or a passing result proves nothing.
        Assert.Contains(typeof(ItemEffectsStoreResourceTests), samplers);
        Assert.Contains(typeof(ItemEffectsConfigurationBudgetTests), samplers);
        Assert.All(samplers, type =>
        {
            var collection = type.GetCustomAttributesData().SingleOrDefault(a => a.AttributeType == typeof(CollectionAttribute));
            Assert.True(collection is not null && isolated.Contains((string)collection.ConstructorArguments[0].Value!),
                $"{type.Name} samples GC.GetTotalMemory but can run beside other tests.");
        });
    }

    private static Type Outermost(Type type)
    {
        while (type.DeclaringType is not null) type = type.DeclaringType;
        return type;
    }

    private static bool Calls(Type type, MethodInfo target)
    {
        const BindingFlags all = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il is null) continue;
            for (var i = 0; i + 4 < il.Length; i++)
            {
                // call/callvirt followed by a MemberRef token (0x0A in the top byte).
                if (il[i] is not (0x28 or 0x6F) || il[i + 4] != 0x0A) continue;
                try
                {
                    if (method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)) == target) return true;
                }
                catch (Exception error) when (error is ArgumentException or BadImageFormatException) { }
            }
        }
        return false;
    }
}
