using System.Security.Cryptography;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Host;

public sealed record CreatureGeometryLeaseDescriptor(string LeaseId, CreatureGeometryManifest Manifest);

/// <summary>Opaque handles for at most two App reads, including opening, draining and failed cleanup.
/// The App alone owns the absolute read deadline. No cancellation or dispose request is retirement proof.</summary>
public sealed class CreatureGeometryLeases : IAsyncDisposable
{
    public const int MaximumLeases = 2;
    private readonly Func<CreatureGeometryOpenRequest, CancellationToken, Task<ICreatureGeometryReadLease>> open;
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly Task worker;
    private bool closed;
    private Exception? cleanupFailure;
    private Task? disposal;

    private sealed class Entry(string id)
    {
        internal readonly string Id = id;
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource OpenFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ICreatureGeometryReadLease? Lease;
        internal Task? Authority, Observation, Attempt;
        internal Exception? Failure;
        internal bool CloseRequested, Retiring;
    }

    public CreatureGeometryLeases(CreatureGeometryService geometry)
        : this((geometry ?? throw new ArgumentNullException(nameof(geometry))).OpenAsync) { }

    internal CreatureGeometryLeases(Func<CreatureGeometryOpenRequest, CancellationToken, Task<ICreatureGeometryReadLease>> open)
    {
        this.open = open ?? throw new ArgumentNullException(nameof(open));
        worker = RunAsync();
    }

    public async Task<CreatureGeometryLeaseDescriptor> OpenAsync(CreatureGeometryOpenRequest request, CancellationToken token)
    {
        CreatureGeometryValidation.Validate(request);
        request = request with { Selection = request.Selection with { Context = request.Selection.Context with { } } };
        token.ThrowIfCancellationRequested();
        Entry entry;
        lock (gate)
        {
            if (closed) throw new DiscoveryException("ENGINE_STOPPING", "Geometry reads are stopping.", true);
            if (entries.Count >= MaximumLeases)
                throw new DiscoveryException("CREATURE_GEOMETRY_BUSY", "Geometry reads are at their bounded capacity or retiring.", true);
            string id;
            do { id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)); } while (entries.ContainsKey(id));
            entry = new(id); entries.Add(id, entry);
        }
        var registration = token.Register(() => RequestClose(entry));
        CreatureGeometryLeaseDescriptor? descriptor = null;
        Exception? failure = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var lease = await open(request, entry.Cancellation.Token);
            // Attach before reading untrusted adapter getters: even malformed returned ownership must close.
            lock (gate) entry.Lease = lease;
            if (lease is null) throw InvalidLease();
            entry.Authority = lease.Retirement ?? throw InvalidLease();
            entry.Observation = ObserveRetirementAsync(entry, entry.Authority);
            var manifest = CreatureGeometryValidation.Snapshot(lease.Manifest);
            var stream = lease.Content;
            if (manifest.ArtifactId != request.GeometryArtifactId || stream is null || !stream.CanRead ||
                !stream.CanSeek || stream.CanWrite || stream.Position != 0 || stream.Length != manifest.ByteLength)
                throw InvalidLease();
            descriptor = new(entry.Id, manifest);
        }
        catch (Exception error)
        {
            failure = error;
            if (error is DiscoveryException { Code: "CREATURE_GEOMETRY_CLEANUP_FAILED" })
                lock (gate) entry.Failure ??= error;
            RequestClose(entry);
        }
        finally
        {
            await registration.DisposeAsync();
            entry.OpenFinished.TrySetResult();
        }
        bool mustClose;
        lock (gate)
        {
            mustClose = closed || entry.CloseRequested || token.IsCancellationRequested;
            if (mustClose) { entry.CloseRequested = true; SignalLocked(); }
        }
        if (mustClose || failure is not null)
        {
            await entry.Completed.Task;
            if (entry.Failure is not null) throw CleanupFailed();
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            throw new OperationCanceledException(token);
        }
        return descriptor!;
    }

    public async Task CloseAsync(string leaseId)
    {
        if (!Canonical(leaseId)) throw new DiscoveryException("INVALID_REQUEST", "Choose a canonical geometry read handle.");
        Entry? entry;
        lock (gate)
        {
            if (!entries.TryGetValue(leaseId, out entry)) return;
            // A retained failed entry survives worker shutdown. Its completed result needs no wake.
            if (!entry.Completed.Task.IsCompleted) { entry.CloseRequested = true; SignalLocked(); }
        }
        await entry.Completed.Task;
        if (entry.Failure is not null) throw CleanupFailed();
    }

    private async Task ObserveRetirementAsync(Entry entry, Task retirement)
    {
        try { await retirement; }
        catch (Exception) { /* The retirement owner below observes and retains the authoritative failure. */ }
        RequestClose(entry);
    }

    private async Task RunAsync()
    {
        while (true)
        {
            await wake.WaitAsync();
            Entry[] start;
            lock (gate)
            {
                start = entries.Values.Where(e => e.CloseRequested && !e.Retiring).ToArray();
                foreach (var entry in start) entry.Retiring = true;
                if (closed && start.Length == 0 && entries.Values.All(e => e.Completed.Task.IsCompleted)) return;
            }
            // Start every bounded peer before awaiting any cleanup; one callback may depend on another close.
            foreach (var entry in start) entry.Attempt = RetireAsync(entry);
        }
    }

    private async Task RetireAsync(Entry entry)
    {
        Exception? failure = entry.Failure;
        var notification = NotifyAsync(entry.Cancellation);
        await entry.OpenFinished.Task;
        if (entry.Lease is { } lease)
        {
            try { await lease.DisposeAsync(); }
            catch (Exception error) { failure ??= error; }
            if (entry.Authority is { } retirement)
            {
                try { await retirement; }
                catch (Exception error) { failure ??= error; }
            }
            else failure ??= InvalidLease();
            if (entry.Observation is { } observation) await observation;
        }
        await notification;
        entry.Cancellation.Dispose();
        lock (gate)
        {
            failure ??= entry.Failure;
            if (failure is null) entries.Remove(entry.Id);
            else
            {
                entry.Failure = failure; cleanupFailure ??= failure; closed = true;
                foreach (var peer in entries.Values) peer.CloseRequested = true;
            }
            entry.Completed.TrySetResult(); SignalLocked();
        }
    }

    private static async Task NotifyAsync(CancellationTokenSource cancellation)
    {
        try { await cancellation.CancelAsync(); }
        catch (AggregateException) { /* Throwing callbacks cannot skip actual source/read retirement. */ }
    }
    private void RequestClose(Entry entry)
    { lock (gate) { entry.CloseRequested = true; SignalLocked(); } }
    private void SignalLocked() { if (wake.CurrentCount == 0) wake.Release(); }
    private static bool Canonical(string? value) => value?.Length == 32 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static DiscoveryException InvalidLease() => new("CREATURE_GEOMETRY_RESULT_INVALID", "The approved geometry read has an invalid proof descriptor.");
    private static DiscoveryException CleanupFailed() => new("CREATURE_GEOMETRY_CLEANUP_FAILED", "A geometry read could not retire cleanly.", true);

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            closed = true;
            foreach (var entry in entries.Values) entry.CloseRequested = true;
            SignalLocked();
            return new(disposal = ShutdownAsync());
        }
    }
    private async Task ShutdownAsync()
    {
        await worker;
        // The worker exits only after every actual attempt, including cancellation notification, has retired.
        wake.Dispose();
        if (cleanupFailure is not null) throw CleanupFailed();
    }
}
