using VibeMiner.Builds;
using VibeMiner.Concepts.Creatures;
using VibeMiner.Concepts.ItemEffects;
using VibeMiner.Concepts.Items;
using VibeMiner.Files;

namespace VibeMiner.Cli.Browse;

/// <summary>
/// The browse session's layer 6 (#204, D474): one <see cref="ConceptsLease"/> made on first use, and the layer 6 build handles
/// (items, item effects, creatures) of the last <see cref="MaxBuilds"/> build and locale pairs, most recent first. A handle keeps
/// its relation indexes (D471), so a second detail or the next effects page on the same build doesn't scan the tables again.
/// Each handle opens on first use; a failed open is not kept. A pair pushed out, <see cref="InvalidateAsync"/> and
/// <see cref="DisposeAsync"/> close the handles (and the lease) once no <see cref="ConceptsHold"/> holds them. Holds may be taken
/// and released from any thread.
/// </summary>
internal sealed class ConceptsCache(Func<CancellationToken, Task<ConceptsLease>> compose) : IAsyncDisposable
{
    public const int MaxBuilds = 2;

    internal sealed class Shared(ConceptsLease lease)
    {
        public ConceptsLease Lease { get; } = lease;
        public int Holds = 1;
    }

    internal sealed class Entry(Shared shared, FilesTarget target)
    {
        public Shared Shared { get; } = shared;
        public FilesTarget Target { get; } = target;
        public SemaphoreSlim Opening { get; } = new(1, 1);
        public int Holds = 1;
        public ItemsOpenResult? Items;
        public ItemEffectsOpenResult? Effects;
        public CreaturesOpenResult? Creatures;
    }

    private readonly Lock gate = new();
    private readonly SemaphoreSlim opening = new(1, 1);
    private readonly LinkedList<Entry> entries = new();
    private Shared? shared;
    // Bumped by every drop, so a compose that a drop overtook is noticed.
    private int generation;
    private bool disposed;

    /// <summary>The build and locale pairs kept, most recent first. For tests.</summary>
    internal IReadOnlyList<FilesTarget> Kept { get { lock (gate) return [.. entries.Select(e => e.Target)]; } }

    /// <summary>
    /// A hold on the pair's handles, composing layer 6 on first use. Dispose it when the load ends. A compose that a concurrent
    /// <see cref="InvalidateAsync"/> overtook is closed and composed again (at most <see cref="MaxComposes"/> times); one a
    /// <see cref="DisposeAsync"/> overtook is closed and the call throws <see cref="ObjectDisposedException"/>.
    /// </summary>
    public async Task<ConceptsHold> HoldAsync(BuildRef build, string locale, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(build);
        lock (gate) ObjectDisposedException.ThrowIf(disposed, this);
        await opening.WaitAsync(ct).ConfigureAwait(false);
        var evicted = new List<Entry>();
        try
        {
            for (var attempt = 0; attempt < MaxComposes; attempt++)
            {
                Shared? have;
                int seen;
                lock (gate)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    have = shared;
                    seen = generation;
                }
                var fresh = have is null ? await compose(ct).ConfigureAwait(false) : null;
                Entry? entry = null;
                bool gone;
                lock (gate)
                {
                    // A drop since the read above (generation), or a disposal, means the lease read or composed is stale.
                    gone = disposed;
                    if (!gone && generation == seen && ReferenceEquals(shared, have))
                    {
                        if (fresh is not null) shared = have = new Shared(fresh);
                        entry = Insert(have!, new FilesTarget(build, locale), evicted);
                    }
                }
                if (entry is not null) return new ConceptsHold(this, entry);
                if (fresh is not null) await fresh.DisposeAsync().ConfigureAwait(false);
                ObjectDisposedException.ThrowIf(gone, this);
            }
            throw new InvalidOperationException($"Layer 6 was dropped {MaxComposes} times while it was composed.");
        }
        finally
        {
            opening.Release();
            foreach (var old in evicted) await ReleaseAsync(old).ConfigureAwait(false);
        }
    }

    /// <summary>How many times one hold composes when drops keep overtaking it.</summary>
    internal const int MaxComposes = 8;

    /// <summary>Under the gate: the pair's entry, made most recent, with one more hold; a new one pushes the oldest past <see cref="MaxBuilds"/> into <paramref name="evicted"/>.</summary>
    private Entry Insert(Shared have, FilesTarget target, List<Entry> evicted)
    {
        var node = entries.First;
        while (node is not null && node.Value.Target != target) node = node.Next;
        Entry entry;
        if (node is not null)
        {
            entries.Remove(node);
            entries.AddFirst(node);
            entry = node.Value;
        }
        else
        {
            entry = new Entry(have, target);
            have.Holds++;
            entries.AddFirst(entry);
            while (entries.Count > MaxBuilds)
            {
                evicted.Add(entries.Last!.Value);
                entries.RemoveLast();
            }
        }
        entry.Holds++;
        return entry;
    }

    /// <summary>Drops the lease and every kept pair: the next hold composes again. Holds already given keep theirs.</summary>
    public ValueTask InvalidateAsync() => DropAsync(dispose: false);

    /// <summary>Closes the kept handles and the lease, each once no hold has them. Later holds throw.</summary>
    public ValueTask DisposeAsync() => DropAsync(dispose: true);

    private async ValueTask DropAsync(bool dispose)
    {
        Entry[] dropped; Shared? lease;
        lock (gate)
        {
            if (disposed) return;
            disposed = dispose;
            generation++;
            dropped = [.. entries];
            entries.Clear();
            lease = shared;
            shared = null;
        }
        try { foreach (var entry in dropped) await ReleaseAsync(entry).ConfigureAwait(false); }
        finally { if (lease is not null) await ReleaseAsync(lease).ConfigureAwait(false); }
    }

    internal async ValueTask ReleaseAsync(Entry entry)
    {
        bool last;
        lock (gate) last = --entry.Holds == 0;
        if (!last) return;
        try
        {
            try { if (entry.Items?.Items is { } items) await items.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                try { if (entry.Effects?.Effects is { } effects) await effects.DisposeAsync().ConfigureAwait(false); }
                finally { if (entry.Creatures?.Creatures is { } creatures) await creatures.DisposeAsync().ConfigureAwait(false); }
            }
        }
        finally
        {
            entry.Opening.Dispose();
            await ReleaseAsync(entry.Shared).ConfigureAwait(false);
        }
    }

    private async ValueTask ReleaseAsync(Shared on)
    {
        bool last;
        lock (gate) last = --on.Holds == 0;
        if (last) await on.Lease.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>
/// A hold on one build and locale's layer 6 handles (<see cref="ConceptsCache.HoldAsync"/>). The handles in the results it gives
/// belong to the cache: don't dispose them. Dispose the hold when the load ends; twice is harmless.
/// </summary>
internal sealed class ConceptsHold : IAsyncDisposable
{
    private readonly ConceptsCache owner;
    private readonly ConceptsCache.Entry entry;
    private int disposed;

    internal ConceptsHold(ConceptsCache owner, ConceptsCache.Entry entry) { this.owner = owner; this.entry = entry; }

    public Task<ItemsOpenResult> ItemsAsync(CancellationToken ct) =>
        OpenAsync(() => entry.Items, r => r.State == FilesOpenState.Open, r => entry.Items = r,
            token => entry.Shared.Lease.Items.ForAsync(entry.Target.Build, entry.Target.Locale, token), ct);

    public Task<ItemEffectsOpenResult> EffectsAsync(CancellationToken ct) =>
        OpenAsync(() => entry.Effects, r => r.State == FilesOpenState.Open, r => entry.Effects = r,
            token => entry.Shared.Lease.Effects.ForAsync(entry.Target.Build, entry.Target.Locale, token), ct);

    public Task<CreaturesOpenResult> CreaturesAsync(CancellationToken ct) =>
        OpenAsync(() => entry.Creatures, r => r.State == FilesOpenState.Open, r => entry.Creatures = r,
            token => entry.Shared.Lease.Creatures.ForAsync(entry.Target.Build, entry.Target.Locale, token), ct);

    /// <summary>The kept open result, else a new open, kept only when it is Open.</summary>
    private async Task<T> OpenAsync<T>(Func<T?> kept, Func<T, bool> open, Action<T> keep, Func<CancellationToken, Task<T>> make, CancellationToken ct)
        where T : class
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        await entry.Opening.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (kept() is { } have) return have;
            var made = await make(ct).ConfigureAwait(false);
            if (open(made)) keep(made);
            return made;
        }
        finally { entry.Opening.Release(); }
    }

    public ValueTask DisposeAsync() => Interlocked.Exchange(ref disposed, 1) != 0 ? ValueTask.CompletedTask : owner.ReleaseAsync(entry);
}
