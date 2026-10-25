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

/// <summary>Heap samples for tests in the <see cref="ManagedHeapBudgetCollection"/>.</summary>
internal static class ManagedHeap
{
    /// <summary>
    /// Live managed heap after full blocking collections stop changing. GC.GetTotalMemory(true)
    /// stops once two samples are within 5%, which on a 150 MB test process leaves up to ~8 MB of
    /// finalizer-held or trimmed garbage in the "settled" number.
    /// </summary>
    internal static long Settled()
    {
        long previous = -1, change = 0;
        for (var attempt = 0; attempt < 16; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            var current = GC.GetTotalMemory(false);
            if (previous >= 0 && (change = Math.Abs(current - previous)) < 64 << 10) return current;
            previous = current;
        }
        throw new Xunit.Sdk.XunitException($"Managed heap did not settle after 16 full collections; last change {change} bytes.");
    }
}

public sealed class ManagedHeapBudgetTests
{
    [Fact]
    public void EveryProcessWideHeapSampleRunsInANonParallelCollection()
    {
        MethodBase[] targets = [typeof(GC).GetMethod(nameof(GC.GetTotalMemory), [typeof(bool)])!,
            typeof(ManagedHeap).GetMethod(nameof(ManagedHeap.Settled), BindingFlags.Static | BindingFlags.NonPublic)!];
        var assembly = typeof(ManagedHeapBudgetTests).Assembly;
        var isolated = assembly.GetTypes()
            .Select(type => type.GetCustomAttributesData().SingleOrDefault(a => a.AttributeType == typeof(CollectionDefinitionAttribute)))
            .Where(a => a is not null && a.NamedArguments.Any(n => n.MemberName == nameof(CollectionDefinitionAttribute.DisableParallelization) && (bool)n.TypedValue.Value!))
            .Select(a => (string)a!.ConstructorArguments[0].Value!).ToHashSet(StringComparer.Ordinal);
        var samplers = assembly.GetTypes().Where(type => type != typeof(ManagedHeap) && Calls(type, targets))
            .Select(Outermost).Distinct().ToArray();
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

    private static bool Calls(Type type, MethodBase[] targets)
    {
        const BindingFlags all = BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        foreach (var method in type.GetMethods(all).Cast<MethodBase>().Concat(type.GetConstructors(all)))
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            if (il is null) continue;
            for (var i = 0; i + 4 < il.Length; i++)
            {
                // call/callvirt followed by a MemberRef (0x0A, other assemblies) or MethodDef (0x06,
                // this assembly) token in the top byte.
                if (il[i] is not (0x28 or 0x6F) || il[i + 4] is not (0x0A or 0x06)) continue;
                try
                {
                    if (targets.Contains(method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)))) return true;
                }
                catch (Exception error) when (error is ArgumentException or BadImageFormatException) { }
            }
        }
        return false;
    }
}
