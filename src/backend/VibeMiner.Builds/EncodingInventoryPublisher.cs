using System.Threading.Channels;
using VibeMiner.Cdn;

namespace VibeMiner.Builds;

/// <summary>Explicit fresh-generation publication; retains at most 16 opened sources and uses two bounded page workers by default.</summary>
public sealed class EncodingInventoryPublisher : IEncodingInventoryPublisher
{
    public const string Producer = "encoding-ekeys-v1";
    public const string SourceFormat = "encoding-ekeys-source-v1", SourceRevision = "encoding-ekeys-r1";
    private sealed class Flight(EncodingInventoryPublication publication, IEncodingRowSource source, bool reuseEnabled)
    {
        public readonly EncodingInventoryPublication Publication = publication;
        public readonly IEncodingRowSource Source = source;
        public readonly bool ReuseEnabled = reuseEnabled;
        public readonly EncodingInventorySourceDescriptor? Descriptor = reuseEnabled ? EncodingInventorySourceDescriptor.For(publication.Context) : null;
        public readonly CancellationTokenSource Stop = new();
        public CancellationTokenRegistration CallerCancellation;
        public readonly object CancellationGate = new();
        public Task? Cancellation;
        public bool Retiring;
        public volatile bool Superseded;
        public readonly TaskCompletionSource Retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Predecessors = Task.CompletedTask;
        public Task? Work;
    }
    private readonly IEncodingInventoryStore store;
    private readonly EncodingInventoryPublisherOptions options;
    private readonly Channel<Flight> queue;
    private readonly SemaphoreSlim running;
    private readonly SemaphoreSlim beginning = new(1, 1);
    private readonly object gate = new();
    private readonly HashSet<Flight> live = [];
    private readonly Dictionary<string, Flight> latest = new(StringComparer.Ordinal);
    private readonly Task coordinator;
    private bool closed;
    private Task? closing;

    public EncodingInventoryPublisher(IEncodingInventoryStore store, EncodingInventoryPublisherOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store); this.store = store; this.options = options ?? new();
        if (this.options.MaxConcurrent is < 1 or > 2 || this.options.MaxAdmitted < this.options.MaxConcurrent || this.options.MaxAdmitted > 16)
            throw new EncodingInventoryException(EncodingInventoryCodes.Validation, "Publisher bounds are 1-2 workers and at most 16 admitted requests.");
        queue = Channel.CreateBounded<Flight>(new BoundedChannelOptions(this.options.MaxAdmitted) { SingleReader = true });
        running = new(this.options.MaxConcurrent, this.options.MaxConcurrent);
        coordinator = Task.Run(CoordinateAsync);
    }
    public EncodingInventoryPublication Start(BuildIdentity identity, IBuild opened, CancellationToken ct = default) => StartCore(identity, opened, false, ct);
    public EncodingInventoryPublication StartReuse(BuildIdentity identity, IBuild opened, CancellationToken ct = default) => StartCore(identity, opened, true, ct);
    private EncodingInventoryPublication StartCore(BuildIdentity identity, IBuild opened, bool reuseEnabled, CancellationToken ct)
    {
        lock (gate) if (closed) throw Error(EncodingInventoryCodes.Closed, "The encoding inventory publisher is closed.");
        ct.ThrowIfCancellationRequested();
        if (opened is not IEncodingRowSource source) throw Error(EncodingInventoryCodes.Unsupported, "The opened build has no bounded ENCODING row source.");
        var build = opened.Build; var cKey = opened.Metadata.EncodingCKey;
        if (source.Build != build || source.EncodingCKey != cKey) throw Error(EncodingInventoryCodes.Validation, "The ENCODING source differs from the opened build.");
        var context = new EncodingInventoryContext(identity, build, cKey, source.EncodedCount);
        var publication = new EncodingInventoryPublication(context);
        var flight = new Flight(publication, source, reuseEnabled);
        Flight[] previous;
        lock (gate)
        {
            if (closed) { flight.Stop.Dispose(); throw Error(EncodingInventoryCodes.Closed, "The publisher is closed."); }
            if (live.Count >= options.MaxAdmitted) { flight.Stop.Dispose(); throw Error(EncodingInventoryCodes.Busy, "The publisher's bounded admission is full."); }
            // Capture every older live producer, not just the latest request. An intermediate canceled request may retire
            // while an earlier noncancelable publication is still entering its real commit.
            previous = live.Where(f => f.Publication.Context.Identity.Id == context.Identity.Id).ToArray();
            flight.Predecessors = Task.WhenAll(previous.Select(f => f.Retired.Task));
            foreach (var older in previous) older.Superseded = true;
            live.Add(flight); latest[context.Identity.Id] = flight;
            // The forwarding callback schedules observer delivery; it never invokes source callbacks inline.
            flight.CallerCancellation = ct.Register(static state => Cancel((Flight)state!), flight);
            if (!queue.Writer.TryWrite(flight)) throw new InvalidOperationException("An admitted inventory request could not be queued.");
        }
        foreach (var older in previous) Cancel(older);
        return publication;
    }
    private static void Cancel(Flight? flight)
    {
        if (flight is null) return;
        lock (flight.CancellationGate)
        {
            if (flight.Retiring) return;
            // CancelAsync marks the token synchronously but runs observers asynchronously. One retained task per bounded
            // flight is observed during retirement, so a throwing observer cannot orphan an admitted replacement or close.
            flight.Cancellation ??= flight.Stop.CancelAsync();
        }
    }
    private static async Task<bool> DrainCancellationAsync(Flight flight)
    {
        flight.CallerCancellation.Dispose();
        Task? cancellation;
        lock (flight.CancellationGate) { flight.Retiring = true; cancellation = flight.Cancellation; }
        if (cancellation is null) return false;
        try { await cancellation.ConfigureAwait(false); return false; }
        catch (Exception) { return true; } // Retain a bounded diagnostic rather than arbitrary observer exception graphs.
    }
    private async Task CoordinateAsync()
    {
        await foreach (var flight in queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (flight.Descriptor is not null && !flight.Predecessors.IsCompleted)
            {
                // There are at most MaxAdmitted such owned waiters. They hold no worker permit, source lease or Begin gate,
                // and never block the coordinator from preparing an unrelated canonical owner.
                var waiting = PrepareAfterPredecessorsAsync(flight);
                lock (gate) flight.Work = waiting;
                continue;
            }
            await PrepareAsync(flight).ConfigureAwait(false);
        }
    }
    private async Task PrepareAfterPredecessorsAsync(Flight flight)
    {
        // Cancellation cannot turn an unfinished predecessor commit into an empty barrier. Retirement includes actual
        // publication, source/write cleanup and cancellation-observer delivery before a new source proof can be consulted.
        await flight.Predecessors.ConfigureAwait(false);
        await PrepareAsync(flight).ConfigureAwait(false);
    }
    private async Task PrepareAsync(Flight flight)
    {
        await running.WaitAsync().ConfigureAwait(false);
        IEncodingRowLease? lease = null; IEncodingInventoryWrite? write = null;
        try
        {
            var ct = flight.Stop.Token; ct.ThrowIfCancellationRequested();
            var context = flight.Publication.Context;
            if (flight.Descriptor is { } descriptor)
            {
                options.Fault?.Invoke(EncodingInventoryPoint.BeforeReuse); ct.ThrowIfCancellationRequested();
                var matched = await store.TryReuseAsync(context, descriptor, ct).ConfigureAwait(false);
                options.Fault?.Invoke(EncodingInventoryPoint.AfterReuse); ct.ThrowIfCancellationRequested();
                if (matched is { } reusedGeneration)
                {
                    if (reusedGeneration <= 0) throw Error(EncodingInventoryCodes.Corrupt, "The matched generation is invalid.");
                    lock (gate)
                    {
                        if (flight.Superseded || ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                        // Acceptance linearizes this result against newer local requests. It reports the generation
                        // active in the source-match snapshot, never a guarantee about future publications.
                    }
                    await FinishAsync(flight, null, null, context.TotalEKeys, reusedGeneration, null, reused: true).ConfigureAwait(false);
                    return;
                }
            }
            lease = flight.Source.OpenEncodingRows();
            if (lease is null || lease.Build != context.Build || lease.EncodingCKey != context.EncodingCKey || lease.EncodedCount != context.TotalEKeys)
                throw Error(EncodingInventoryCodes.Corrupt, "The retained ENCODING source changed identity or row count.");
            ct.ThrowIfCancellationRequested();
            // Deferred owner reuse can prepare outside the coordinator. Serialize only Begin, rechecking current intent
            // after admission so an older deferred request cannot overtake a newer generation creation.
            await beginning.WaitAsync().ConfigureAwait(false);
            try
            {
                lock (gate)
                {
                    if (flight.Superseded || ct.IsCancellationRequested || !latest.TryGetValue(context.Identity.Id, out var current) || !ReferenceEquals(current, flight))
                        throw new OperationCanceledException(ct);
                }
                write = await store.BeginAsync(context, ct).ConfigureAwait(false);
            }
            finally { beginning.Release(); }
            ct.ThrowIfCancellationRequested();
            var retainedLease = lease; var retainedWrite = write;
            var work = Task.Run(() => RunAsync(flight, retainedLease, retainedWrite));
            lock (gate) flight.Work = work;
        }
        catch (Exception e) { await FinishAsync(flight, lease, write, 0, null, e).ConfigureAwait(false); }
    }
    private async Task RunAsync(Flight flight, IEncodingRowLease lease, IEncodingInventoryWrite write)
    {
        long processed = 0; long? generation = null; Exception? failure = null;
        try
        {
            var ct = flight.Stop.Token; var context = flight.Publication.Context;
            flight.Publication.Update(EncodingInventoryState.Running, 0);
            CdnKey? after = null;
            while (true)
            {
                options.Fault?.Invoke(EncodingInventoryPoint.BeforeReadPage); ct.ThrowIfCancellationRequested();
                var page = await lease.ReadPageAsync(after, 1024, ct).ConfigureAwait(false);
                if (page is null || page.Rows is null || page.Rows.Count > 1024 || (page.HasMore && page.Rows.Count == 0))
                    throw Error(EncodingInventoryCodes.Corrupt, "An ENCODING page violates its bounded traversal contract.");
                if (page.Rows.Count > context.TotalEKeys - processed) throw Error(EncodingInventoryCodes.Corrupt, "ENCODING has more rows than the validated count.");
                var keys = new CdnKey[page.Rows.Count];
                for (var i = 0; i < keys.Length; i++)
                {
                    ct.ThrowIfCancellationRequested(); var row = page.Rows[i];
                    if (row is null || row.EncodedSize < 0 || row.EKey == default || after is { } previous && row.EKey <= previous)
                        throw Error(EncodingInventoryCodes.Corrupt, "ENCODING rows are invalid, duplicated or unordered.");
                    keys[i] = row.EKey; after = row.EKey;
                }
                if (keys.Length > 0)
                {
                    await write.AppendAsync(keys, ct).ConfigureAwait(false);
                    processed = checked(processed + keys.Length);
                    options.Fault?.Invoke(EncodingInventoryPoint.AfterAppend);
                    flight.Publication.Update(EncodingInventoryState.Running, processed);
                }
                if (!page.HasMore) break;
            }
            if (processed != context.TotalEKeys) throw Error(EncodingInventoryCodes.Corrupt, "ENCODING ended before its validated row count.");
            options.Fault?.Invoke(EncodingInventoryPoint.BeforePublish); ct.ThrowIfCancellationRequested();
            generation = flight.Descriptor is { } descriptor
                ? await write.PublishSourceAsync(descriptor, ct).ConfigureAwait(false)
                : await write.PublishAsync(ct).ConfigureAwait(false);
            options.Fault?.Invoke(EncodingInventoryPoint.AfterPublish);
        }
        catch (Exception e) { failure = e; }
        await FinishAsync(flight, lease, write, processed, generation, failure).ConfigureAwait(false);
    }
    private async Task FinishAsync(Flight flight, IEncodingRowLease? lease, IEncodingInventoryWrite? write, long processed, long? generation, Exception? failure, bool reused = false)
    {
        if (write is not null)
        {
            if (generation is null)
            {
                try { await write.AbandonAsync(CancellationToken.None).ConfigureAwait(false); }
                catch (EncodingInventoryException e) when (e.Code == EncodingInventoryCodes.Superseded) { }
                catch (Exception e) { failure = e; }
            }
            try { await write.DisposeAsync().ConfigureAwait(false); } catch (Exception e) { failure = e; }
        }
        if (lease is not null) { try { await lease.DisposeAsync().ConfigureAwait(false); } catch (Exception e) { failure = e; } }
        var cancellationFailed = await DrainCancellationAsync(flight).ConfigureAwait(false);
        var code = failure switch
        {
            EncodingInventoryException e => e.Code,
            BuildFormatException e => e.Code,
            OperationCanceledException => null,
            null => null,
            _ => EncodingInventoryCodes.Storage,
        };
        var message = failure?.Message;
        if (cancellationFailed)
        {
            code = EncodingInventoryCodes.Storage;
            message = "An ENCODING cancellation observer failed; cancellation was still delivered. " + message;
        }
        if (message?.Length > 1024) message = message[..1024];
        if (code?.Length > 128) code = code[..128];
        flight.Stop.Dispose(); running.Release();
        lock (gate)
        {
            var state = generation is not null ? (reused ? EncodingInventoryState.Reused : EncodingInventoryState.Published)
                : flight.Superseded || failure is EncodingInventoryException { Code: EncodingInventoryCodes.Superseded } ? EncodingInventoryState.Superseded
                : failure is OperationCanceledException ? EncodingInventoryState.Canceled : EncodingInventoryState.Failed;
            live.Remove(flight);
            if (latest.TryGetValue(flight.Publication.Context.Identity.Id, out var current) && ReferenceEquals(current, flight)) latest.Remove(flight.Publication.Context.Identity.Id);
            // No callback delivery: one latest progress value and one terminal result, after actual write/source retirement.
            flight.Publication.Complete(new(flight.Publication.Context, state, processed, generation, code, message, flight.Descriptor));
            flight.Retired.TrySetResult();
        }
    }
    public ValueTask DisposeAsync()
    {
        Flight[] cancel;
        lock (gate)
        {
            if (closing is not null) return new(closing);
            closed = true; queue.Writer.TryComplete(); cancel = live.ToArray();
            closing = CloseAsync();
        }
        foreach (var flight in cancel) Cancel(flight);
        return new(closing);
    }
    private async Task CloseAsync()
    {
        await coordinator.ConfigureAwait(false);
        Task[] active;
        lock (gate) active = live.Select(f => f.Retired.Task).ToArray();
        await Task.WhenAll(active).ConfigureAwait(false);
        running.Dispose(); beginning.Dispose();
    }
    private static EncodingInventoryException Error(string code, string message) => new(code, message);
}
