using System.Threading.Channels;
using VibeMiner.Objects;

namespace VibeMiner.ObjectAccess;

/// <summary>What one commit changed in the profile's own store. Published after the commit, so <see cref="IObjectSource.HasKeysAsync"/> already agrees.</summary>
public sealed record StoreCommit(IReadOnlyList<ObjectKey> Stored, IReadOnlyList<ObjectKey> Removed);

/// <summary>
/// Commits to the profile's own store made through this object source, in this process. Another process's writes are never
/// reported, so a reader that must see them measures again. Implemented by <see cref="ObjectSource"/> and the in-memory fake;
/// a CDN-only or local-only source doesn't write and has none.
/// </summary>
public interface IStoreCommits
{
    /// <summary>A new subscription with its own queue of at most <paramref name="capacity"/> commits. Dispose it to stop.</summary>
    StoreCommitSubscription Subscribe(int capacity = StoreCommitFeed.DefaultCapacity);
}

/// <summary>
/// One reader's queue. The writer never waits for it: a commit that finds the queue full is dropped and <see cref="TakeMissed"/>
/// turns true, so the reader drains <see cref="Reader"/>, then checks it and measures again.
/// </summary>
public sealed class StoreCommitSubscription : IDisposable
{
    private readonly StoreCommitFeed feed;
    private readonly Channel<StoreCommit> channel;
    private int missed;

    internal StoreCommitSubscription(StoreCommitFeed feed, int capacity)
    {
        this.feed = feed;
        channel = Channel.CreateBounded<StoreCommit>(new BoundedChannelOptions(capacity)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait });
    }

    public ChannelReader<StoreCommit> Reader => channel.Reader;

    /// <summary>True once after commits were dropped because the queue was full, and clears it.</summary>
    public bool TakeMissed() => Interlocked.Exchange(ref missed, 0) == 1;

    internal void Offer(StoreCommit commit)
    {
        // TryWrite never blocks; a full queue (or a disposed subscription) loses the commit, which the reader learns here.
        if (!channel.Writer.TryWrite(commit)) Volatile.Write(ref missed, 1);
    }

    public void Dispose()
    {
        feed.Remove(this);
        channel.Writer.TryComplete();
    }
}

/// <summary>The publishing side, for implementations of <see cref="IStoreCommits"/>.</summary>
public sealed class StoreCommitFeed : IStoreCommits
{
    public const int DefaultCapacity = 1024;
    private readonly Lock gate = new();
    private StoreCommitSubscription[] subscribers = [];

    public StoreCommitSubscription Subscribe(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        var s = new StoreCommitSubscription(this, capacity);
        lock (gate) subscribers = [.. subscribers, s];
        return s;
    }

    internal void Remove(StoreCommitSubscription s)
    {
        lock (gate) subscribers = subscribers.Where(x => x != s).ToArray();
    }

    /// <summary>Hands the commit to every subscriber's queue. Never blocks and never throws; nothing happens for an empty commit.</summary>
    public void Publish(IReadOnlyList<ObjectKey> stored, IReadOnlyList<ObjectKey> removed)
    {
        if (stored.Count == 0 && removed.Count == 0) return;
        var current = Volatile.Read(ref subscribers);
        if (current.Length == 0) return;
        var commit = new StoreCommit(stored.ToArray(), removed.ToArray());
        foreach (var s in current) s.Offer(commit);
    }
}
