using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;

namespace VibeMiner.ObjectAccess.Tests;

public sealed class StoreCommitsTests
{
    private static readonly CancellationToken None = CancellationToken.None;

    [Fact]
    public async Task AReaderThatNeverReadsNeverHoldsUpTheWriter()
    {
        var s = new Scenario();
        var objects = Enumerable.Range(1, 4).Select(i => s.Serve(Synthetic.Config(i))).ToArray();
        using var idle = s.Source.Subscribe(capacity: 1);
        // Each call commits on its own; every one completes although the queue is full after the first.
        foreach (var o in objects.Take(2))
            Assert.True((await s.Source.GetKeyAsync(new(o.Key), s.Context, FetchPriority.Interactive, None)).IsPresent);
        foreach (var o in objects.Skip(2))
            Assert.True((await s.Source.GetKeysAsync([new(o.Key)], s.Context, FetchPriority.Background, null, None))[0].IsPresent);

        Assert.True(idle.Reader.TryRead(out var first));
        Assert.Equal([objects[0].Key], first.Stored);
        Assert.False(idle.Reader.TryRead(out _));
        Assert.True(idle.TakeMissed());
        Assert.False(idle.TakeMissed());
    }

    [Fact]
    public async Task ADamagedStoredCopyIsPublishedRemovedThenStoredAgain()
    {
        var s = new Scenario();
        var o = s.Serve(Synthetic.Config(2));
        s.Store.Add(o);
        s.Store.Corrupt(o.Key);
        using var subscription = s.Source.Subscribe();
        var r = await s.Source.GetKeyAsync(new(o.Key), s.Context, FetchPriority.Interactive, None);
        Assert.Equal((ObjectState.Present, ServedFrom.Cdn), (r.State, r.ServedFrom!.Value));

        Assert.True(subscription.Reader.TryRead(out var removed));
        Assert.Equal((0, o.Key), (removed.Stored.Count, Assert.Single(removed.Removed)));
        Assert.True(subscription.Reader.TryRead(out var restored));
        Assert.Equal((o.Key, 0), (Assert.Single(restored.Stored), restored.Removed.Count));
        Assert.False(subscription.Reader.TryRead(out _));
    }

    [Fact]
    public void TheFeedSkipsEmptyCommitsAndRefusesAZeroCapacity()
    {
        var feed = new StoreCommitFeed();
        Assert.Throws<ArgumentOutOfRangeException>(() => feed.Subscribe(0));
        using var subscription = feed.Subscribe(1);
        feed.Publish([], []);
        Assert.False(subscription.Reader.TryRead(out _));
        Assert.False(subscription.TakeMissed());
    }
}
