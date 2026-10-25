using System.Globalization;
using VibeMiner.Domain;

namespace VibeMiner.Application;

/// <summary>FetchedDefinitions true means the definition is chosen by D081 and the Settings switch: the Tables view and
/// every feature prerequisite set it (D101), so they read a table the same way. Feature names the feature asking, only so
/// messages can say who needs the table; requests that differ only in Feature share one preparation.</summary>
public sealed record TableDataRequest(string ProductId, string BuildIdentity, string SourceRegion,
    string TableId, string ResearchLocale, bool FetchedDefinitions = false, string? Feature = null);
public sealed record TableRowsRequest(string ProductId, string BuildIdentity, string SourceRegion,
    string TableId, string ResearchLocale, string ArtifactId, int Limit = 50, string? After = null,
    string? RecordId = null);
public interface ITableDataStore
{
    Task<TableDataSnapshot> PrepareAsync(TableDataRequest request, Func<string, Task> progress, CancellationToken token);
    Task<TableDataReadiness> ReadAsync(TableDataRequest request, CancellationToken token);
    Task<TableDataPage> ReadRowsAsync(TableRowsRequest request, CancellationToken token);
}

/// <summary>One shared table producer. Navigation does not cancel its consumers.</summary>
public sealed class TableDataService(ITableDataStore store) : IAsyncDisposable
{
    private const int MaximumConsumers = 32;
    private readonly object gate = new();
    private Preparation? active;
    private bool closed;

    private sealed class Preparation(TableDataRequest request)
    {
        public TableDataRequest Request { get; } = request;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<TableDataSnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<long, Observer> Observers { get; } = new();
        public SemaphoreSlim Dispatch { get; } = new(1, 1);
        public long NextObserver;
        public int Consumers;
        public bool Stopping;
        public bool ProgressClosed;
    }

    private sealed class Observer(Func<string, Task> callback)
    {
        private readonly SemaphoreSlim serial = new(1, 1);
        private bool closed;
        public async Task ReportAsync(string stage)
        {
            await serial.WaitAsync();
            try
            {
                if (closed) return;
                try { await callback(stage); }
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

    public async Task<TableDataSnapshot> PrepareAsync(TableDataRequest request, Func<string, Task>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        cancellationToken.ThrowIfCancellationRequested();
        Preparation preparation; bool start; long observer;
        Observer? subscription = progress is null ? null : new(progress);
        lock (gate)
        {
            ThrowIfClosed();
            if (active is not null && (active.Request with { Feature = null } != request with { Feature = null } || active.Stopping || active.Consumers >= MaximumConsumers))
                throw new DiscoveryException("TABLE_DATA_BUSY", "Another table operation is active or stopping. Wait for it to finish before loading another table.", true);
            start = active is null;
            preparation = active ??= new(request);
            preparation.Consumers++;
            observer = ++preparation.NextObserver;
            if (subscription is not null) preparation.Observers.Add(observer, subscription);
        }
        if (start) _ = RunAsync(preparation);
        var detached = false;
        try { return await preparation.Completion.Task.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            bool last; bool retired;
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
                // A completed durable publication may win cancellation; cleanup still precedes retirement.
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

    public Task<TableDataReadiness> ReadAsync(TableDataRequest request, CancellationToken token = default)
    {
        Validate(request); token.ThrowIfCancellationRequested();
        lock (gate) ThrowIfClosed();
        return store.ReadAsync(request, token);
    }
    public Task<TableDataPage> ReadRowsAsync(TableRowsRequest request, CancellationToken token = default)
    {
        Validate(request); token.ThrowIfCancellationRequested();
        lock (gate) ThrowIfClosed();
        return store.ReadRowsAsync(request, token);
    }

    /// <summary>Observe retirement without acquiring or canceling the active table's demand.</summary>
    public async Task WaitForAvailabilityAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Preparation? preparation;
        lock (gate) { ThrowIfClosed(); preparation = active; }
        if (preparation is null) return;
        try { await preparation.Completion.Task.WaitAsync(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Availability observes retirement, not the unrelated table's outcome.
            // A subsequent prerequisite probe decides whether its own input is ready.
        }
        lock (gate) ThrowIfClosed();
    }

    private async Task RunAsync(Preparation preparation)
    {
        TableDataSnapshot? result = null; Exception? failure = null;
        try { result = await store.PrepareAsync(preparation.Request, stage => ReportAsync(preparation, stage), preparation.Cancellation.Token); }
        catch (Exception exception) { failure = exception; }
        // Drain even a store callback it failed to await, then prevent any later callback from repainting a terminal request.
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
    private async Task ReportAsync(Preparation preparation, string stage)
    {
        if (string.IsNullOrWhiteSpace(stage) || stage.Length > 128 || stage.Any(char.IsControl))
            throw new DiscoveryException("TABLE_DATA_PROGRESS_INVALID", "Table progress must be bounded stage text.");
        // Concurrent intermediate reports coalesce instead of forming an unbounded subscriber queue.
        if (!await preparation.Dispatch.WaitAsync(0)) return;
        try
        {
            Observer[] observers;
            lock (gate)
            {
                if (preparation.ProgressClosed || preparation.Stopping) return;
                observers = preparation.Observers.Values.ToArray();
            }
            foreach (var observer in observers) await observer.ReportAsync(stage);
        }
        finally { preparation.Dispatch.Release(); }
    }

    public static void Validate(TableDataRequest request)
    {
        if (request is null || request.BuildIdentity is null || request.SourceRegion is null ||
            request.SourceRegion.Length is < 2 or > 8 || request.SourceRegion.Any(ch => ch is < 'a' or > 'z'))
            throw Invalid();
        WorkspaceService.Validate(new(request.ProductId, request.BuildIdentity, request.SourceRegion));
        if (!Hex(request.TableId, 8) || request.ResearchLocale != "enUS" || request.Feature is { Length: < 1 or > 32 }) throw Invalid();
    }
    public static void Validate(TableRowsRequest request)
    {
        if (request is null) throw Invalid();
        Validate(new TableDataRequest(request.ProductId, request.BuildIdentity, request.SourceRegion, request.TableId, request.ResearchLocale));
        if (!Hex(request.ArtifactId, 64) || request.Limit is < 1 or > 50 ||
            request.After is not null && (!Decimal(request.After, 19) || !long.TryParse(request.After, NumberStyles.None, CultureInfo.InvariantCulture, out _)) ||
            request.RecordId is not null && (!Decimal(request.RecordId, 10) || !uint.TryParse(request.RecordId, NumberStyles.None, CultureInfo.InvariantCulture, out _))) throw Invalid();
    }
    private static bool Hex(string? value, int width) => value is not null && value.Length == width &&
        value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Decimal(string value, int width) => value.Length >= 1 && value.Length <= width &&
        (value.Length == 1 || value[0] != '0') && value.All(ch => ch is >= '0' and <= '9');
    private static DiscoveryException Invalid() => new("INVALID_REQUEST", "Choose an exact table, enUS research locale and bounded artifact row query.");
    private void ThrowIfClosed()
    {
        if (closed) throw new DiscoveryException("ENGINE_STOPPING", "The engine is stopping.", true);
    }
    private static async Task RequestCancellationAsync(Preparation preparation)
    {
        try { await preparation.Cancellation.CancelAsync(); }
        catch (ObjectDisposedException) { /* The producer already retired. */ }
        catch (AggregateException) { /* Observe faulty cancellation callbacks, then still await producer cleanup. */ }
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
