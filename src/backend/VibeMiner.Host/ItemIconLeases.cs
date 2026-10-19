using System.Globalization;
using System.Security.Cryptography;
using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Host;

/// <summary>Only verified PNG identity and geometry cross the control channel; never a path.</summary>
public sealed record ItemIconLeaseDescriptor(string LeaseId, string ArtifactId, string Sha256,
    string ByteLength, int Width, int Height);

/// <summary>
/// Four immediate admissions including opens and draining streams. A single worker retires all
/// reads; expiry uses monotonic elapsed time and never converts cancellation into cleanup proof.
/// </summary>
public sealed class ItemIconLeases : IAsyncDisposable
{
    public const int MaximumLeases = 4;
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    private readonly ItemIconsService icons;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly CancellationTokenSource shutdown = new();
    private readonly ITimer timer;
    private readonly Task worker;
    private Task? disposal;
    private Exception? cleanupFailure;
    private bool closed;
    private enum State { Opening, Open, Closing, Disposing }
    private sealed class Entry(string id, CancellationTokenSource cancellation)
    {
        internal readonly string Id = id;
        internal readonly CancellationTokenSource Cancellation = cancellation;
        internal readonly TaskCompletionSource Retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal State State;
        internal IItemIconReadLease? Lease;
        internal long OpenedAt;
        internal Exception? Failure;
        internal bool OpenFinished, CloseRequested;
    }

    public ItemIconLeases(ItemIconsService icons, TimeProvider? time = null)
    {
        this.icons = icons ?? throw new ArgumentNullException(nameof(icons));
        this.time = time ?? TimeProvider.System;
        timer = this.time.CreateTimer(_ => Signal(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        worker = RunAsync();
    }

    public async Task<ItemIconLeaseDescriptor> OpenAsync(ItemIconReadRequest request, CancellationToken token)
    {
        ItemIconsService.Validate(request);
        token.ThrowIfCancellationRequested();
        Entry entry;
        lock (gate)
        {
            if (closed) throw new DiscoveryException("ENGINE_STOPPING", "Image reads are stopping.", true);
            if (entries.Count >= MaximumLeases)
                throw new DiscoveryException("ICON_IMAGE_BUSY", "Image reads are at their bounded capacity or stopping.", true);
            string id;
            do { id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)); } while (entries.ContainsKey(id));
            entry = new(id, CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token));
            entries.Add(id, entry);
        }
        IItemIconReadLease? lease = null;
        try
        {
            lease = await icons.OpenAsync(request, entry.Cancellation.Token);
            var descriptor = Describe(entry.Id, request, lease);
            lock (gate)
            {
                entry.Lease = lease;
                entry.OpenedAt = time.GetTimestamp();
                entry.State = closed || entry.CloseRequested || entry.Cancellation.IsCancellationRequested ? State.Closing : State.Open;
                if (entry.State == State.Closing) SignalLocked();
            }
            // A source may have committed just as its consumer canceled. It still must retire.
            entry.Cancellation.Token.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (closed || entry.State != State.Open)
                    throw new OperationCanceledException(entry.Cancellation.Token);
            }
            return descriptor;
        }
        catch
        {
            lock (gate)
            {
                if (lease is null)
                {
                    entry.Retired.TrySetResult();
                }
                else
                {
                    entry.Lease = lease;
                    if (!entry.Retired.Task.IsCompleted && entry.State != State.Disposing) entry.State = State.Closing;
                }
                SignalLocked();
            }
            await entry.Retired.Task;
            if (entry.Failure is not null) throw CleanupFailed();
            throw;
        }
        finally
        {
            entry.Cancellation.Dispose();
            lock (gate)
            {
                entry.OpenFinished = true;
                if (entry.Retired.Task.IsCompleted) entries.Remove(entry.Id);
                SignalLocked();
            }
        }
    }

    public async Task CloseAsync(string leaseId)
    {
        if (!CanonicalHex(leaseId, 32))
            throw new DiscoveryException("INVALID_REQUEST", "Choose a canonical image read handle.");
        Entry? entry;
        bool cancelOpening;
        lock (gate)
        {
            if (!entries.TryGetValue(leaseId, out entry)) return;
            cancelOpening = entry.State == State.Opening;
            if (cancelOpening) entry.CloseRequested = true;
            if (entry.State == State.Open) entry.State = State.Closing;
            SignalLocked();
        }
        if (cancelOpening) await CancelAsync(entry.Cancellation);
        await entry.Retired.Task;
        if (entry.Failure is not null) throw CleanupFailed();
    }

    private async Task RunAsync()
    {
        while (true)
        {
            await wake.WaitAsync();
            while (true)
            {
                Entry? next;
                lock (gate)
                {
                    next = entries.Values.FirstOrDefault(entry => entry.State == State.Closing ||
                        entry.State == State.Open && time.GetElapsedTime(entry.OpenedAt) >= Lifetime);
                    if (next is not null) next.State = State.Disposing;
                    else if (closed && entries.Count == 0) return;
                }
                if (next is null) break;
                Exception? failure = null;
                try { await next.Lease!.DisposeAsync(); }
                catch (Exception error) { failure = error; }
                lock (gate)
                {
                    if (next.OpenFinished) entries.Remove(next.Id);
                    if (failure is null) next.Retired.TrySetResult();
                    else
                    {
                        // A faulty disposer cannot create unlimited leaked admissions. Fail closed.
                        cleanupFailure ??= failure;
                        closed = true;
                        foreach (var entry in entries.Values)
                            if (entry.State == State.Open) entry.State = State.Closing;
                        next.Failure = failure;
                        next.Retired.TrySetResult();
                    }
                }
                if (failure is not null) await CancelAsync(shutdown);
            }
        }
    }

    private static ItemIconLeaseDescriptor Describe(string id, ItemIconReadRequest request, IItemIconReadLease lease)
    {
        if (lease is null) throw InvalidImage();
        var artifact = lease.ArtifactId;
        var sha = lease.Sha256;
        var length = lease.ByteLength;
        var width = lease.Width;
        var height = lease.Height;
        if (artifact != request.ImageArtifactId || !CanonicalHex(artifact, 64) || !CanonicalHex(sha, 64) ||
            length is < 1 or > 5 * 1024 * 1024 || width is < 1 or > 1024 || height is < 1 or > 1024 ||
            (long)width * height > 1024 * 1024)
            throw InvalidImage();
        return new(id, artifact, sha, length.ToString(CultureInfo.InvariantCulture), width, height);
    }
    private static DiscoveryException CleanupFailed() => new("ICON_IMAGE_CLEANUP_FAILED", "An image read could not close cleanly.", true);
    private static DiscoveryException InvalidImage() => new("ICON_IMAGE_INVALID", "The approved image read has an invalid proof descriptor.");
    private static bool CanonicalHex(string? value, int length) => value?.Length == length &&
        value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
    private void Signal() { lock (gate) SignalLocked(); }
    private void SignalLocked() { if (wake.CurrentCount == 0) wake.Release(); }
    private static async Task CancelAsync(CancellationTokenSource source)
    {
        try { await source.CancelAsync(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { /* Callback failure cannot skip producer/lease retirement. */ }
    }

    public ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposal is not null) return new(disposal);
            closed = true;
            foreach (var entry in entries.Values)
                if (entry.State == State.Open) entry.State = State.Closing;
            SignalLocked();
            disposal = ShutdownAsync();
            return new(disposal);
        }
    }
    private async Task ShutdownAsync()
    {
        await CancelAsync(shutdown);
        await timer.DisposeAsync();
        await worker;
        shutdown.Dispose();
        wake.Dispose();
        if (cleanupFailure is not null)
            throw CleanupFailed();
    }
}
