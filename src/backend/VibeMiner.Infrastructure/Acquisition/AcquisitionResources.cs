using VibeMiner.Application;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>One engine-owned budget; priority is reevaluated while waiting and while permits are held.</summary>
public sealed class AcquisitionResources
{
    private readonly PriorityBudget http;
    private readonly PriorityBudget cpu;
    private readonly PriorityBudget staging;
    public AcquisitionResources(int maximumHttp = 4, int maximumBackgroundHttp = 2,
        int maximumCpu = 2, int maximumBackgroundCpu = 1,
        long maximumStagingBytes = 2L << 30, long maximumBackgroundStagingBytes = 512L << 20)
    {
        MaximumHttp = maximumHttp; MaximumBackgroundHttp = maximumBackgroundHttp;
        http = new(maximumHttp, maximumBackgroundHttp);
        cpu = new(maximumCpu, maximumBackgroundCpu);
        staging = new(maximumStagingBytes, maximumBackgroundStagingBytes);
    }
    /// <summary>HTTP permits: downloads at once in total, and how many of them background work may hold.</summary>
    public int MaximumHttp { get; }
    public int MaximumBackgroundHttp { get; }
    public Task<IDisposable> EnterHttpAsync(IAcquisitionWorkContext? context, CancellationToken token) => http.EnterAsync(1, context, token);
    public Task<IDisposable> EnterCpuAsync(IAcquisitionWorkContext? context, CancellationToken token) => cpu.EnterAsync(1, context, token);
    public Task<IDisposable> ReserveStagingAsync(long bytes, IAcquisitionWorkContext? context, CancellationToken token) => staging.EnterAsync(bytes, context, token, reportStagingWait: true);

    private sealed class PriorityBudget(long maximum, long backgroundMaximum)
    {
        private readonly object gate = new();
        private readonly List<Entry> waiting = [];
        private readonly List<Entry> active = [];
        private sealed class Entry(long weight, IAcquisitionWorkContext? context)
        {
            internal readonly long Weight = weight;
            internal readonly IAcquisitionWorkContext? Context = context;
            internal readonly TaskCompletionSource<IDisposable> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal readonly CancellationTokenSource Monitor = new();
            internal AcquisitionPriority Priority => Context?.Snapshot.Priority ?? AcquisitionPriority.Interactive;
        }
        internal async Task<IDisposable> EnterAsync(long weight, IAcquisitionWorkContext? context, CancellationToken token, bool reportStagingWait = false)
        {
            if (weight < 1 || weight > maximum || context?.Snapshot.Priority == AcquisitionPriority.Background && weight > backgroundMaximum)
                throw new DiscoveryException("ACQUISITION_CAPACITY", "The complete operation exceeds its staging or worker allowance.", true);
            token.ThrowIfCancellationRequested();
            var entry = new Entry(weight, context);
            lock (gate) { waiting.Add(entry); Grant(); }
            using var cancellation = token.Register(() => Cancel(entry, token));
            _ = MonitorAsync(entry);
            if (reportStagingWait && context is not null && !entry.Completion.Task.IsCompleted)
            {
                try { await context.ReportAsync(new("Waiting for temporary storage", "bytes")); }
                catch
                {
                    Cancel(entry, token);
                    if (entry.Completion.Task.IsCompletedSuccessfully)
                        (await entry.Completion.Task).Dispose();
                    throw;
                }
            }
            return await entry.Completion.Task;
        }
        private async Task MonitorAsync(Entry entry)
        {
            if (entry.Context is null) return;
            try
            {
                while (!entry.Monitor.IsCancellationRequested)
                {
                    var revision = entry.Context.Snapshot.Revision;
                    lock (gate) Grant();
                    await entry.Context.WaitForChangeAsync(revision, entry.Monitor.Token);
                }
            }
            catch (OperationCanceledException) when (entry.Monitor.IsCancellationRequested) { }
        }
        private void Cancel(Entry entry, CancellationToken token)
        {
            lock (gate)
            {
                if (!waiting.Remove(entry)) return;
                entry.Monitor.Cancel(); entry.Completion.TrySetCanceled(token); Grant();
            }
        }
        private void Grant()
        {
            foreach (var entry in waiting.OrderByDescending(item => item.Priority).ToArray())
            {
                if (active.Sum(item => item.Weight) > maximum - entry.Weight) continue;
                if (entry.Priority == AcquisitionPriority.Background &&
                    active.Where(item => item.Priority == AcquisitionPriority.Background).Sum(item => item.Weight) > backgroundMaximum - entry.Weight) continue;
                waiting.Remove(entry); active.Add(entry);
                entry.Completion.TrySetResult(new Release(() =>
                {
                    lock (gate) { active.Remove(entry); entry.Monitor.Cancel(); Grant(); }
                }));
            }
        }
    }
    private sealed class Release(Action release) : IDisposable
    {
        private Action? action = release;
        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
    }
}
