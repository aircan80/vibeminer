using System.Buffers;
using System.Globalization;
using System.Text;
using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>One bounded feature producer with independent demand and local query lifetimes.</summary>
public sealed class CreaturesService(ICreaturesStore store, TimeProvider? timeProvider = null) : IAsyncDisposable
{
    private const int MaximumConsumers = 32;
    private readonly object gate = new();
    private Preparation? active;
    private bool closed;
    private int consumers, queries;
    private TaskCompletionSource? consumersRetired, queriesRetired;
    private readonly CancellationTokenSource shutdown = new();

    private sealed class Preparation(CreaturesContext context)
    {
        public CreaturesContext Context { get; } = context;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<CreaturesSnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<long, Observer> Observers { get; } = new();
        public long NextObserver;
        public int Consumers;
        public bool Stopping, ProgressClosed, ProducerFinished;
        public TaskCompletionSource? CancellationCompleted;
    }

    // Each subscription owns one running callback and one replaceable latest value.
    // Yield before callbacks so producer CPU leases never contain observer work.
    private sealed class Observer(Func<CreaturesPreparationProgress, Task> callback, TimeProvider clock)
    {
        private readonly object sync = new();
        private readonly CancellationTokenSource stopping = new();
        private CreaturesPreparationProgress? pending;
        private TaskCompletionSource? drained;
        private bool closed, running;
        private long? previous;

        public void Post(CreaturesPreparationProgress progress)
        {
            bool start;
            lock (sync)
            {
                if (closed) return;
                pending = progress; start = !running;
                if (start) { running = true; drained = new(TaskCreationOptions.RunContinuationsAsynchronously); }
            }
            if (start) _ = PumpAsync();
        }
        private async Task PumpAsync()
        {
            await Task.Yield();
            try
            {
                while (true)
                {
                    if (previous is { } timestamp)
                    {
                        var delay = TimeSpan.FromMilliseconds(100) - clock.GetElapsedTime(timestamp);
                        if (delay > TimeSpan.Zero) await Task.Delay(delay, clock, stopping.Token);
                    }
                    CreaturesPreparationProgress value;
                    lock (sync)
                    {
                        if (closed || pending is null) { running = false; drained!.TrySetResult(); return; }
                        value = pending; pending = null; previous = clock.GetTimestamp();
                    }
                    await callback(value);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // A failed callback withdraws only this observer. Never fault the producer.
                lock (sync) { closed = true; pending = null; running = false; drained!.TrySetResult(); }
            }
        }
        public async Task CloseAsync()
        {
            Task retirement;
            lock (sync) { closed = true; pending = null; retirement = drained?.Task ?? Task.CompletedTask; }
            // Cancellation notifications are arbitrary code and must run outside locks.
            try { await stopping.CancelAsync(); } catch (AggregateException) { }
            await retirement;
            stopping.Dispose();
        }
    }

    public async Task<CreaturesSnapshot> PrepareAsync(CreaturesContext context,
        Func<CreaturesPreparationProgress, Task>? progress = null, CancellationToken cancellationToken = default)
    {
        Validate(context); cancellationToken.ThrowIfCancellationRequested();
        Preparation preparation; bool start; long observer;
        Observer? subscription = progress is null ? null : new(progress, timeProvider ?? TimeProvider.System);
        lock (gate)
        {
            ThrowIfClosed();
            if (consumers >= MaximumConsumers || active is not null && (active.Context != context || active.Stopping))
                throw new DiscoveryException("CREATURES_BUSY", "Another Creatures preparation is active, stopping or at its bounded consumer capacity.", true);
            start = active is null;
            preparation = active ??= new(context);
            if (consumers++ == 0) consumersRetired = new(TaskCreationOptions.RunContinuationsAsynchronously);
            preparation.Consumers++;
            observer = ++preparation.NextObserver;
            if (subscription is not null) preparation.Observers.Add(observer, subscription);
        }
        if (start) _ = RunAsync(preparation);
        var detached = false;
        try { return await preparation.Completion.Task.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            bool last, retired;
            lock (gate)
            {
                detached = true; preparation.Observers.Remove(observer); preparation.Consumers--;
                last = active == preparation && preparation.Consumers == 0;
                if (last) preparation.Stopping = true;
                retired = preparation.Completion.Task.IsCompleted;
            }
            if (last) await RequestCancellationAsync(preparation);
            if (subscription is not null) await subscription.CloseAsync();
            if (retired) return await preparation.Completion.Task;
            if (last)
            {
                try { return await preparation.Completion.Task; }
                catch (OperationCanceledException) { throw new OperationCanceledException(cancellationToken); }
            }
            throw;
        }
        finally
        {
            try
            {
                if (!detached)
                {
                    lock (gate) { preparation.Observers.Remove(observer); preparation.Consumers--; }
                    if (subscription is not null) await subscription.CloseAsync();
                }
            }
            finally { lock (gate) { if (--consumers == 0) consumersRetired!.TrySetResult(); } }
        }
    }

    public Task<CreaturesReadiness> ReadAsync(CreaturesContext context, CancellationToken token = default)
    { Validate(context); return QueryAsync(ct => store.ReadAsync(context, ct), token); }
    public Task<CreaturesPage> ReadPageAsync(CreaturesPageRequest request, CancellationToken token = default)
    { Validate(request); return QueryAsync(ct => store.ReadPageAsync(request, ct), token); }
    public Task<CreatureInspectResult> InspectAsync(CreatureInspectRequest request, CancellationToken token = default)
    { Validate(request); return QueryAsync(ct => store.InspectAsync(request, ct), token); }
    private async Task<T> QueryAsync<T>(Func<CancellationToken, Task<T>> query, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            ThrowIfClosed();
            if (queries == 4) throw new DiscoveryException("CREATURES_QUERY_BUSY", "Creature queries are at their bounded capacity or retiring.", true);
            if (queries++ == 0) queriesRetired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token);
        try { return await query(lifetime.Token); }
        finally { lock (gate) { if (--queries == 0) queriesRetired!.TrySetResult(); } }
    }

    private async Task RunAsync(Preparation preparation)
    {
        CreaturesSnapshot? result = null; Exception? failure = null;
        try { result = await store.PrepareAsync(preparation.Context, value => ReportAsync(preparation, value), preparation.Cancellation.Token); }
        catch (Exception exception) { failure = exception; }
        Task cancellationDrain;
        lock (gate)
        {
            preparation.ProgressClosed = true; preparation.ProducerFinished = true;
            cancellationDrain = preparation.CancellationCompleted?.Task ?? Task.CompletedTask;
        }
        await cancellationDrain;
        lock (gate)
        {
            if (failure is OperationCanceledException canceled) preparation.Completion.TrySetCanceled(canceled.CancellationToken);
            else if (failure is not null) preparation.Completion.TrySetException(failure);
            else preparation.Completion.TrySetResult(result!);
            if (active == preparation) active = null;
            preparation.Cancellation.Dispose();
        }
    }

    private Task ReportAsync(Preparation preparation, CreaturesPreparationProgress progress)
    {
        if (progress is null || !Stage(progress.Stage, 128) || !Stage(progress.Unit, 32) ||
            progress.Completed is < 0 || progress.Total is < 0 ||
            progress.Completed is { } done && progress.Total is { } total && done > total ||
            progress.TableId is not null && !Hex(progress.TableId, 8))
            throw new DiscoveryException("CREATURES_PROGRESS_INVALID", "Creatures progress requires bounded stage text and honest nonnegative current-stage counters.");
        lock (gate)
        {
            if (!preparation.ProgressClosed && !preparation.Stopping)
                foreach (var observer in preparation.Observers.Values) observer.Post(progress);
        }
        return Task.CompletedTask;
    }

    public static void Validate(CreaturesContext context)
    {
        if (context is null || context.ProductId is null || !Hex(context.BuildIdentity, 64) ||
            context.SourceRegion is null || context.SourceRegion.Length is < 2 or > 8 ||
            context.SourceRegion.Any(ch => ch is < 'a' or > 'z') || context.ResearchLocale != "enUS") throw Invalid();
        ProductRegistry.Validate(context.ProductId);
    }
    public static void Validate(CreaturesPageRequest request)
    {
        if (request is null) throw Invalid();
        Validate(request.Context);
        if (!Hex(request.ArtifactId, 64) || request.Limit is < 1 or > 50 || !Query(request.Query) ||
            request.ExactCreatureId is not null && (!ItemId(request.ExactCreatureId) || request.Query.Length != 0) ||
            request.After is not null && !Cursor(request.After)) throw Invalid();
    }
    public static void Validate(CreatureInspectRequest request)
    {
        if (request is null) throw Invalid();
        Validate(request.Context);
        if (!Hex(request.ArtifactId, 64) || !ItemId(request.CreatureId)) throw Invalid();
    }
    private static bool Query(string? value)
    {
        if (value is null || value.Length > 256) return false;
        var remaining = value.AsSpan(); var scalars = 0; var bytes = 0;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done ||
                rune.Value == 0 || ++scalars > 128 || (bytes += rune.Utf8SequenceLength) > 512) return false;
            remaining = remaining[consumed..];
        }
        return true;
    }
    private static bool Cursor(string value)
    {
        if (value.Length is < 1 or > 2048 || value.Any(ch =>
            ch is not (>= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_'))) return false;
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var decoded = Convert.FromBase64String(padded);
            return Convert.ToBase64String(decoded).TrimEnd('=').Replace('+', '-').Replace('/', '_') == value;
        }
        catch (FormatException) { return false; }
    }
    private static bool ItemId(string? value) => value is { Length: >= 1 and <= 10 } &&
        (value.Length == 1 || value[0] != '0') && value.All(ch => ch is >= '0' and <= '9') &&
        uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    private static bool Hex(string? value, int width) => value is not null && value.Length == width &&
        value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Stage(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximum && !value.Any(char.IsControl);
    private static DiscoveryException Invalid() => new("INVALID_REQUEST",
        "Choose an exact enUS Creatures artifact, a bounded literal name query or unsigned Creature ID and a valid page cursor.");
    private void ThrowIfClosed()
    {
        if (closed) throw new DiscoveryException("ENGINE_STOPPING", "The engine is stopping.", true);
    }
    private Task RequestCancellationAsync(Preparation preparation)
    {
        TaskCompletionSource notification; bool start;
        lock (gate)
        {
            if (preparation.ProducerFinished) return Task.CompletedTask;
            start = preparation.CancellationCompleted is null;
            notification = preparation.CancellationCompleted ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        if (start) _ = CancelAsync();
        return notification.Task;
        async Task CancelAsync()
        {
            try { await preparation.Cancellation.CancelAsync(); }
            catch (AggregateException) { /* A callback failure cannot bypass actual producer cleanup. */ }
            finally { notification.TrySetResult(); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        Preparation? preparation; Task consumerDrain, queryDrain;
        lock (gate)
        {
            closed = true; preparation = active; if (preparation is not null) preparation.Stopping = true;
            consumerDrain = consumersRetired?.Task ?? Task.CompletedTask; queryDrain = queriesRetired?.Task ?? Task.CompletedTask;
        }
        try { await shutdown.CancelAsync(); } catch (AggregateException) { }
        if (preparation is not null)
        {
            await RequestCancellationAsync(preparation);
            try { await preparation.Completion.Task; } catch (Exception) { }
        }
        await Task.WhenAll(consumerDrain, queryDrain);
    }
}
