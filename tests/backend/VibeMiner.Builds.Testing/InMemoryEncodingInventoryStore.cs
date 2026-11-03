using VibeMiner.Cdn;

namespace VibeMiner.Builds.Testing;

public sealed record InMemoryEncodingInventorySnapshot(long Generation, EncodingInventoryContext Context, IReadOnlyList<CdnKey> Keys);

/// <summary>Independent generation/fencing model for publisher tests and upper consumers; no SQL, files or payloads.</summary>
public sealed class InMemoryEncodingInventoryStore : IEncodingInventoryStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, long> latest = [];
    private readonly Dictionary<string, InMemoryEncodingInventorySnapshot> active = [];
    private readonly List<int> appendSizes = [];
    private long next;
    private int begins, abandons;
    public int BeginCalls => Volatile.Read(ref begins);
    public int AbandonCalls => Volatile.Read(ref abandons);
    public IReadOnlyList<int> AppendSizes { get { lock (gate) return appendSizes.ToArray(); } }
    public Func<EncodingInventoryContext, IReadOnlyList<CdnKey>, CancellationToken, Task>? BeforeAppend { get; set; }
    public Func<EncodingInventoryContext, CancellationToken, Task>? BeforePublish { get; set; }
    /// <summary>Runs after the real in-memory commit. Cancellation while held cannot roll it back.</summary>
    public Func<EncodingInventoryContext, long, Task>? AfterPublish { get; set; }
    public InMemoryEncodingInventorySnapshot? ReadActive(string buildId) { lock (gate) return active.GetValueOrDefault(buildId); }
    public Task<IEncodingInventoryWrite> BeginAsync(EncodingInventoryContext context, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(context);
        lock (gate) { var generation = checked(++next); latest[context.Identity.Id] = generation; Interlocked.Increment(ref begins); return Task.FromResult<IEncodingInventoryWrite>(new Write(this, context, generation)); }
    }
    private sealed class Write(InMemoryEncodingInventoryStore store, EncodingInventoryContext context, long generation) : IEncodingInventoryWrite
    {
        private readonly SortedSet<CdnKey> keys = [];
        private bool finished, poisoned;
        private void Current()
        {
            if (finished) throw new EncodingInventoryException(EncodingInventoryCodes.Closed, "The inventory write is closed.");
            if (poisoned || store.latest.GetValueOrDefault(context.Identity.Id) != generation)
                throw new EncodingInventoryException(EncodingInventoryCodes.Superseded, "The generation was superseded.");
        }
        public async Task AppendAsync(IReadOnlyList<CdnKey> batch, CancellationToken ct = default)
        {
            try
            {
                ct.ThrowIfCancellationRequested(); ArgumentNullException.ThrowIfNull(batch);
                if (batch.Count > 1024) { await AbandonAsync(); throw new EncodingInventoryException(EncodingInventoryCodes.Validation, "An append is at most 1024 EKeys."); }
                var copy = batch.ToArray(); if (store.BeforeAppend is { } before) await before(context, copy, ct); ct.ThrowIfCancellationRequested();
                lock (store.gate) { Current(); foreach (var key in copy) keys.Add(key); store.appendSizes.Add(copy.Length); }
            }
            catch { lock (store.gate) poisoned = true; throw; }
        }
        public async Task<long> PublishAsync(CancellationToken ct = default)
        {
            try
            {
                ct.ThrowIfCancellationRequested(); if (store.BeforePublish is { } before) await before(context, ct); ct.ThrowIfCancellationRequested();
                lock (store.gate) { Current(); store.active[context.Identity.Id] = new(generation, context, Array.AsReadOnly(keys.ToArray())); finished = true; }
                if (store.AfterPublish is { } after) await after(context, generation);
                return generation;
            }
            catch (OperationCanceledException) { lock (store.gate) poisoned = true; throw; }
        }
        public Task AbandonAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); lock (store.gate) { if (!finished) { finished = true; Interlocked.Increment(ref store.abandons); } } return Task.CompletedTask;
        }
        public async ValueTask DisposeAsync() => await AbandonAsync();
    }
}

/// <summary>A no-I/O publisher for upper tests. Bounded publication policy is shared with the real service, like OpenCache.</summary>
public sealed class InMemoryEncodingInventoryPublisher : IEncodingInventoryPublisher
{
    private readonly EncodingInventoryPublisher publisher;
    public InMemoryEncodingInventoryPublisher(InMemoryEncodingInventoryStore? store = null, EncodingInventoryPublisherOptions? options = null)
    { Store = store ?? new(); publisher = new(Store, options); }
    public InMemoryEncodingInventoryStore Store { get; }
    public EncodingInventoryPublication Start(BuildIdentity identity, IBuild opened, CancellationToken ct = default) => publisher.Start(identity, opened, ct);
    public ValueTask DisposeAsync() => publisher.DisposeAsync();
}
