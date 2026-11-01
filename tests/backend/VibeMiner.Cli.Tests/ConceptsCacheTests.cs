using VibeMiner.Builds;
using VibeMiner.Cli.Browse;
using VibeMiner.Concepts.Items;
using VibeMiner.Concepts.Testing;
using VibeMiner.Files;
using VibeMiner.Formats.Tables;
using Xunit;

namespace VibeMiner.Cli.Tests;

/// <summary>The browse session's layer 6 handle cache over the layer 6 fakes.</summary>
public sealed class ConceptsCacheTests
{
    private static BuildRef BuildOf(char c) => new("wow", new string(c, 32), new string('b', 32), "us");
    private static readonly BuildRef A = BuildOf('a'), B = BuildOf('c'), C = BuildOf('d');

    private readonly InMemoryItems items = new();
    private readonly InMemoryItemEffects effects = new();
    private readonly InMemoryCreatures creatures = new();
    private int composed, closed;

    public ConceptsCacheTests()
    {
        foreach (var build in new[] { A, B, C }) items.Add(build, "enUS", [new FakeItem(1, new ItemName(ItemNameState.Valid, "One"))]);
    }

    private ConceptsCache Cache() => new(_ =>
    {
        composed++;
        return Task.FromResult(new ConceptsLease(items, effects, creatures, () => { closed++; return ValueTask.CompletedTask; }));
    });

    private static async Task<IBuildItems> ItemsAsync(ConceptsCache cache, BuildRef build)
    {
        await using var hold = await cache.HoldAsync(build, "enUS", default);
        return (await hold.ItemsAsync(default)).Items!;
    }

    [Fact]
    public async Task ItKeepsTheLastTwoBuildsAndClosesTheOneItPushesOut()
    {
        await using var cache = Cache();
        var a = await ItemsAsync(cache, A);
        Assert.Same(a, await ItemsAsync(cache, A));
        await ItemsAsync(cache, B);
        // A is used again, so C pushes out B.
        await ItemsAsync(cache, A);
        var c = await ItemsAsync(cache, C);
        Assert.Equal([new FilesTarget(C, "enUS"), new FilesTarget(A, "enUS")], cache.Kept);
        Assert.Equal(3, items.Calls(nameof(IItems.ForAsync)));
        await ItemsAsync(cache, B);
        Assert.Equal(4, items.Calls(nameof(IItems.ForAsync)));
        // A went out with that, and its handle was closed; C's is still open.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => a.ListAsync(new(), TablePriority.Interactive, default));
        Assert.Equal(ItemsPageState.Ok, (await c.ListAsync(new(), TablePriority.Interactive, default)).State);
        Assert.Equal(1, composed);
    }

    [Fact]
    public async Task AHeldHandleOutlivesItsEvictionAndTheCacheCloses()
    {
        var cache = Cache();
        var hold = await cache.HoldAsync(A, "enUS", default);
        var a = (await hold.ItemsAsync(default)).Items!;
        await ItemsAsync(cache, B);
        await ItemsAsync(cache, C);
        Assert.Equal(ItemsPageState.Ok, (await a.ListAsync(new(), TablePriority.Interactive, default)).State);
        await hold.DisposeAsync();
        await hold.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => a.ListAsync(new(), TablePriority.Interactive, default));
        Assert.Equal(0, closed);
        await cache.DisposeAsync();
        Assert.Equal(1, closed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => cache.HoldAsync(A, "enUS", default));
    }

    [Fact]
    public async Task AFailedOpenIsNotKeptAndInvalidateComposesAgain()
    {
        await using var cache = Cache();
        var missing = BuildOf('e');
        await using (var hold = await cache.HoldAsync(missing, "enUS", default))
        {
            Assert.Equal(FilesOpenState.NeedsNetwork, (await hold.ItemsAsync(default)).State);
            Assert.Equal(FilesOpenState.NeedsNetwork, (await hold.ItemsAsync(default)).State);
        }
        Assert.Equal(2, items.Calls(nameof(IItems.ForAsync)));
        await ItemsAsync(cache, A);
        await cache.InvalidateAsync();
        Assert.Equal(1, closed);
        Assert.Empty(cache.Kept);
        await ItemsAsync(cache, A);
        Assert.Equal(2, composed);
    }

    /// <summary>A cache whose composes wait for the test: each takes the next gate, in order.</summary>
    private (ConceptsCache Cache, List<TaskCompletionSource> Gates, TaskCompletionSource Composing) Blocked(int gates)
    {
        var list = Enumerable.Range(0, gates).Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToList();
        var composing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cache = new ConceptsCache(async ct =>
        {
            var at = Interlocked.Increment(ref composed) - 1;
            composing.TrySetResult();
            await list[at].Task.WaitAsync(ct);
            return new ConceptsLease(items, effects, creatures, () => { Interlocked.Increment(ref closed); return ValueTask.CompletedTask; });
        });
        return (cache, list, composing);
    }

    [Fact]
    public async Task AComposeAnInvalidateOvertookIsClosedAndComposedAgain()
    {
        // Recheck of #204: N invalidates the files in the background; a compose running then kept the old file system for the session.
        var (cache, gates, composing) = Blocked(2);
        await using var _ = cache;
        var hold = cache.HoldAsync(A, "enUS", default);
        await composing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cache.InvalidateAsync();
        gates[0].SetResult();
        gates[1].SetResult();
        await using (await hold.WaitAsync(TimeSpan.FromSeconds(10))) { }
        Assert.Equal(2, composed);
        Assert.Equal(1, closed);
        Assert.Equal([new FilesTarget(A, "enUS")], cache.Kept);
    }

    [Fact]
    public async Task AComposeADisposeOvertookIsClosedAndTheHoldThrows()
    {
        var (cache, gates, composing) = Blocked(1);
        var hold = cache.HoldAsync(A, "enUS", default);
        await composing.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cache.DisposeAsync();
        gates[0].SetResult();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => hold.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal((1, 1), (composed, closed));
        Assert.Empty(cache.Kept);
    }
}
