using System.Buffers;
using System.Globalization;
using System.Text;
using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>One bounded feature producer with independent demand and local query lifetimes.</summary>
public sealed class ItemsService(IItemsStore store) : IAsyncDisposable
{
    private const int MaximumConsumers = 32;
    private readonly object gate = new();
    private Preparation? active;
    private bool closed;

    private sealed class Preparation(ItemsContext context)
    {
        public ItemsContext Context { get; } = context;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<ItemsSnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<long, Observer> Observers { get; } = new();
        public SemaphoreSlim Dispatch { get; } = new(1, 1);
        public long NextObserver;
        public int Consumers;
        public bool Stopping, ProgressClosed;
    }

    private sealed class Observer(Func<ItemsPreparationProgress, Task> callback)
    {
        private readonly SemaphoreSlim serial = new(1, 1);
        private bool closed;
        public async Task ReportAsync(ItemsPreparationProgress progress)
        {
            await serial.WaitAsync();
            try
            {
                if (closed) return;
                try { await callback(progress); }
                catch (Exception exception) when (exception is not OutOfMemoryException) { closed = true; }
            }
            finally { serial.Release(); }
        }
        public async Task CloseAsync()
        {
            await serial.WaitAsync();
            try { closed = true; }
            finally { serial.Release(); }
        }
    }

    public async Task<ItemsSnapshot> PrepareAsync(ItemsContext context,
        Func<ItemsPreparationProgress, Task>? progress = null, CancellationToken cancellationToken = default)
    {
        Validate(context); cancellationToken.ThrowIfCancellationRequested();
        Preparation preparation; bool start; long observer;
        Observer? subscription = progress is null ? null : new(progress);
        lock (gate)
        {
            ThrowIfClosed();
            if (active is not null && (active.Context != context || active.Stopping || active.Consumers >= MaximumConsumers))
                throw new DiscoveryException("ITEMS_BUSY", "Another Items preparation is active, stopping or at its bounded consumer capacity.", true);
            start = active is null;
            preparation = active ??= new(context);
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
            if (!detached)
            {
                lock (gate) { preparation.Observers.Remove(observer); preparation.Consumers--; }
                if (subscription is not null) await subscription.CloseAsync();
            }
        }
    }

    public Task<ItemsReadiness> ReadAsync(ItemsContext context, CancellationToken token = default)
    {
        Validate(context); token.ThrowIfCancellationRequested();
        lock (gate) ThrowIfClosed();
        return store.ReadAsync(context, token);
    }
    public Task<ItemsPage> ReadPageAsync(ItemsPageRequest request, CancellationToken token = default)
    {
        Validate(request); token.ThrowIfCancellationRequested();
        lock (gate) ThrowIfClosed();
        return store.ReadPageAsync(request, token);
    }
    public Task<ItemInspectResult> InspectAsync(ItemInspectRequest request, CancellationToken token = default)
    {
        Validate(request); token.ThrowIfCancellationRequested();
        lock (gate) ThrowIfClosed();
        return store.InspectAsync(request, token);
    }

    private async Task RunAsync(Preparation preparation)
    {
        ItemsSnapshot? result = null; Exception? failure = null;
        try { result = await store.PrepareAsync(preparation.Context, value => ReportAsync(preparation, value), preparation.Cancellation.Token); }
        catch (Exception exception) { failure = exception; }
        await preparation.Dispatch.WaitAsync();
        try { lock (gate) preparation.ProgressClosed = true; }
        finally { preparation.Dispatch.Release(); }
        lock (gate)
        {
            if (failure is OperationCanceledException canceled) preparation.Completion.TrySetCanceled(canceled.CancellationToken);
            else if (failure is not null) preparation.Completion.TrySetException(failure);
            else preparation.Completion.TrySetResult(result!);
            if (active == preparation) active = null;
            preparation.Cancellation.Dispose();
        }
    }

    private async Task ReportAsync(Preparation preparation, ItemsPreparationProgress progress)
    {
        if (progress is null || !Stage(progress.Stage, 128) || !Stage(progress.Unit, 32) ||
            progress.Completed is < 0 || progress.Total is < 0 ||
            progress.Completed is { } done && progress.Total is { } total && done > total ||
            progress.TableId is not null && !Hex(progress.TableId, 8))
            throw new DiscoveryException("ITEMS_PROGRESS_INVALID", "Items progress requires bounded stage text and honest nonnegative current-stage counters.");
        if (!await preparation.Dispatch.WaitAsync(0)) return;
        try
        {
            Observer[] observers;
            lock (gate)
            {
                if (preparation.ProgressClosed || preparation.Stopping) return;
                observers = preparation.Observers.Values.ToArray();
            }
            foreach (var observer in observers) await observer.ReportAsync(progress);
        }
        finally { preparation.Dispatch.Release(); }
    }

    public static void Validate(ItemsContext context)
    {
        if (context is null || context.ProductId is null || !Hex(context.BuildIdentity, 64) ||
            context.SourceRegion is null || context.SourceRegion.Length is < 2 or > 8 ||
            context.SourceRegion.Any(ch => ch is < 'a' or > 'z') || context.ResearchLocale != "enUS") throw Invalid();
        ProductRegistry.Validate(context.ProductId);
    }
    public static void Validate(ItemsPageRequest request)
    {
        if (request is null) throw Invalid();
        Validate(request.Context);
        if (!Hex(request.ArtifactId, 64) || request.Limit is < 1 or > 50 || !Query(request.Query) ||
            request.ExactItemId is not null && (!ItemId(request.ExactItemId) || request.Query.Length != 0) ||
            request.After is not null && !Cursor(request.After)) throw Invalid();
    }
    public static void Validate(ItemInspectRequest request)
    {
        if (request is null) throw Invalid();
        Validate(request.Context);
        if (!Hex(request.ArtifactId, 64) || !ItemId(request.ItemId)) throw Invalid();
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
        "Choose an exact enUS Items artifact, a bounded literal name query or unsigned item ID and a valid page cursor.");
    private void ThrowIfClosed()
    {
        if (closed) throw new DiscoveryException("ENGINE_STOPPING", "The engine is stopping.", true);
    }
    private static async Task RequestCancellationAsync(Preparation preparation)
    {
        try { await preparation.Cancellation.CancelAsync(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { /* Observe callback failure and still await producer cleanup. */ }
    }
    public async ValueTask DisposeAsync()
    {
        Preparation? preparation;
        lock (gate) { closed = true; preparation = active; if (preparation is not null) preparation.Stopping = true; }
        if (preparation is null) return;
        await RequestCancellationAsync(preparation);
        try { await preparation.Completion.Task; } catch (Exception) { }
    }
}
