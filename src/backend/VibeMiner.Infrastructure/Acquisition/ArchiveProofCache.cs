using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>Shared bounded authenticated native metadata, including coalesced dynamic-priority requests.</summary>
internal sealed class ArchiveProofCache(long maximumBytes = 32L << 20, int maximumEntries = 4096,
    int maximumPending = 32, TimeProvider? timeProvider = null, int maximumBackgroundPending = 24) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private long bytes, sequence;
    private bool stopping;
    private Task? disposal;
    private sealed class Entry
    {
        internal readonly TaskCompletionSource Retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly SharedAcquisitionWork Work = new();
        internal readonly CancellationTokenSource Cancellation = new();
        internal readonly TaskCompletionSource<object> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Users;
        internal bool Retiring;
        internal bool ProducerRetired;
        internal TaskCompletionSource? Notification;
        internal long Charge, Used;
        internal DateTimeOffset? NegativeUntil;
    }
    internal async Task<T> GetAsync<T>(string key, IAcquisitionWorkContext? context,
        Func<IAcquisitionWorkContext, CancellationToken, Task<(T Value, long Charge)>> create, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Entry entry; bool start = false;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            if (entries.TryGetValue(key, out var expired) && expired.NegativeUntil <= clock.GetUtcNow() &&
                expired.Users == 0 && expired.Retired.Task.IsCompleted)
                Remove(key, expired);
            if (!entries.TryGetValue(key, out entry!))
            {
                while (entries.Count >= maximumEntries && Evict()) { }
                if (entries.Count >= maximumEntries || entries.Values.Count(item => !item.Retired.Task.IsCompleted) >= maximumPending ||
                    context?.Snapshot.Priority == AcquisitionPriority.Background && entries.Values.Count(item =>
                        !item.Retired.Task.IsCompleted && item.Work.Snapshot.Priority == AcquisitionPriority.Background) >= Math.Min(maximumPending, maximumBackgroundPending))
                    throw Error("CONTENT_LOCATOR_CAPACITY", "Shared native archive metadata is at its bounded admission limit.");
                entry = new(); entries.Add(key, entry); start = true;
            }
            if (entry.Retiring)
                throw Error("CONTENT_LOCATOR_CAPACITY", "The previous shared native metadata request is stopping.");
            entry.Users++; entry.Used = ++sequence;
        }
        using var demand = entry.Work.Attach(context);
        if (start) _ = CreateAsync(key, entry, create);
        try { return (T)await entry.Ready.Task.WaitAsync(token); }
        finally
        {
            Task? retirement = null; TaskCompletionSource? notification = null;
            lock (gate)
            {
                entry.Users--; entry.Used = ++sequence;
                if (entry.Users == 0 && !entry.Retired.Task.IsCompleted)
                {
                    if (!entry.Ready.Task.IsCompleted)
                    { entry.Retiring = true; notification = ReserveNotification(entry); }
                    retirement = entry.Retired.Task;
                }
                if (entry.Users == 0 && entry.Retired.Task.IsCompleted && entry.NegativeUntil is null && !entry.Ready.Task.IsCompletedSuccessfully)
                    Remove(key, entry);
                while (bytes > maximumBytes && Evict()) { }
            }
            if (notification is not null) _ = NotifyAsync(key, entry, notification);
            if (retirement is not null) await retirement;
        }
    }
    private async Task CreateAsync<T>(string key, Entry entry,
        Func<IAcquisitionWorkContext, CancellationToken, Task<(T Value, long Charge)>> create)
    {
        try
        {
            var result = await create(entry.Work, entry.Cancellation.Token);
            if (result.Charge < 0 || result.Charge > maximumBytes)
                throw Error("CONTENT_LOCATOR_LIMIT", "One authenticated archive proof exceeds the shared cache budget.");
            lock (gate)
            {
                while (bytes > maximumBytes - result.Charge && Evict()) { }
                if (bytes > maximumBytes - result.Charge)
                    throw Error("CONTENT_LOCATOR_CAPACITY", "Authenticated native archive proofs currently occupy the complete shared cache budget.");
                entry.Charge = result.Charge; bytes += result.Charge;
            }
            entry.Ready.TrySetResult(result.Value!);
        }
        catch (OperationCanceledException) { entry.Ready.TrySetCanceled(entry.Cancellation.Token); }
        catch (DiscoveryException failure) when (failure.Code == "CONTENT_SOURCE_MISSING")
        {
            lock (gate)
            {
                while (bytes > maximumBytes - 256 && Evict()) { }
                if (maximumBytes >= 256 && bytes <= maximumBytes - 256)
                { entry.NegativeUntil = clock.GetUtcNow().AddSeconds(30); entry.Charge = 256; bytes += 256; }
            }
            entry.Ready.TrySetException(failure);
        }
        catch (Exception failure) { entry.Ready.TrySetException(failure); }
        finally
        {
            bool retired;
            lock (gate)
            {
                entry.ProducerRetired = true;
                retired = FinishRetirement(key, entry);
            }
            if (retired) entry.Cancellation.Dispose();
        }
    }

    // Called under gate. Publish the one notification before releasing ownership to either the
    // last consumer or shutdown; neither may mistake an already requested CancelAsync for cleanup.
    private static TaskCompletionSource? ReserveNotification(Entry entry)
    {
        if (entry.Notification is not null) return null;
        return entry.Notification = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private async Task NotifyAsync(string key, Entry entry, TaskCompletionSource notification)
    {
        // CancelAsync invokes callbacks outside our gate, observes callback failures, and allows
        // shutdown to start every bounded notification before awaiting any of their callbacks.
        await AssetCancellation.RequestAsync(entry.Cancellation);
        bool retired;
        lock (gate)
        {
            notification.TrySetResult();
            retired = FinishRetirement(key, entry);
        }
        if (retired) entry.Cancellation.Dispose();
    }
    // Called under gate. Capacity stays charged until BOTH producer and notification retire.
    private bool FinishRetirement(string key, Entry entry)
    {
        if (entry.Retired.Task.IsCompleted || !entry.ProducerRetired || entry.Notification is { Task.IsCompleted: false })
            return false;
        if (entry.Retiring || entry.Users == 0 && entry.NegativeUntil is null && !entry.Ready.Task.IsCompletedSuccessfully)
            Remove(key, entry);
        entry.Retired.TrySetResult();
        while (bytes > maximumBytes && Evict()) { }
        return true;
    }
    private bool Evict()
    {
        var oldest = entries.Where(item => item.Value.Users == 0 && item.Value.Retired.Task.IsCompleted)
            .OrderBy(item => item.Value.Used).FirstOrDefault();
        if (oldest.Value is null) return false;
        Remove(oldest.Key, oldest.Value); return true;
    }
    private void Remove(string key, Entry entry)
    {
        if (entries.GetValueOrDefault(key) != entry) return;
        entries.Remove(key); bytes -= entry.Charge;
    }
    public ValueTask DisposeAsync() => AcquisitionDisposal.Start(gate, ref disposal, DisposeCoreAsync);
    private async Task DisposeCoreAsync()
    {
        KeyValuePair<string, Entry>[] pending;
        List<(string Key, Entry Entry, TaskCompletionSource Notification)> notifications = [];
        lock (gate)
        {
            stopping = true; pending = entries.ToArray();
            foreach (var (key, entry) in pending)
            {
                if (entry.Retired.Task.IsCompleted) continue;
                entry.Retiring = true;
                if (ReserveNotification(entry) is { } notification) notifications.Add((key, entry, notification));
            }
        }
        foreach (var (key, entry, notification) in notifications) _ = NotifyAsync(key, entry, notification);
        await Task.WhenAll(pending.Select(item => item.Value.Retired.Task));
        lock (gate) { entries.Clear(); bytes = 0; }
    }
    private static DiscoveryException Error(string code, string message) => new(code, message, true);
}
