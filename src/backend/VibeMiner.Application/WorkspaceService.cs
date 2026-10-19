using VibeMiner.Domain;

namespace VibeMiner.Application;

public sealed record WorkspaceRequest(string ProductId, string BuildIdentity, string SourceRegion);
public sealed record PreparationStep(int Index, int? Total, string Description);
public sealed record WorkspacePreparationProgress(string Stage, PreparationStep? Step = null);
public sealed record CapturedOpeningContext(Build Build, OpeningSourceReference Reference,
    SourceDocument Versions, SourceDocument Cdns);
public sealed record StoredConfiguration(string Key, string Sha256, int ByteLength, string SourceUrl, string RetrievedAt);
public interface IWorkspaceCatalog
{
    Task<CapturedOpeningContext> ResolveOpeningAsync(WorkspaceRequest request, CancellationToken token);
    Task<bool> HasAdmissionAsync(CapturedOpeningContext context, CancellationToken token);
    Task<StoredConfiguration?> FindConfigurationAsync(string key, CancellationToken token);
    Task RecordConfigurationAsync(StoredConfiguration configuration, CancellationToken token);
    Task<WorkspaceSnapshot> CommitWorkspaceAsync(CapturedOpeningContext context,
        IReadOnlyList<ConfigurationReceipt> configurations, string openedAt, CancellationToken token);
}
public interface IWorkspacePreparer
{
    Task<WorkspaceSnapshot> PrepareAsync(WorkspaceRequest request, Func<string, Task> progress, CancellationToken token);
    Task<WorkspaceSnapshot> PrepareWithProgressAsync(WorkspaceRequest request,
        Func<WorkspacePreparationProgress, Task> progress, CancellationToken token) =>
        PrepareAsync(request, stage => progress(new(stage)), token);
}

/// <summary>One application-owned opening producer. UI navigation does not own its cancellation.</summary>
public sealed class WorkspaceService(IWorkspacePreparer preparer) : IAsyncDisposable
{
    private readonly object gate = new();
    private Opening? active;
    private bool closed;
    private sealed class Opening(WorkspaceRequest request)
    {
        public WorkspaceRequest Request { get; } = request;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<WorkspaceSnapshot> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<long, Observer> Observers { get; } = new();
        public long NextObserver;
        public int Consumers;
    }
    private sealed class Observer(Func<WorkspacePreparationProgress, Task> callback)
    {
        private readonly SemaphoreSlim serial = new(1, 1);
        private bool closed;
        public async Task ReportAsync(WorkspacePreparationProgress stage)
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
    public Task<WorkspaceSnapshot> OpenAsync(WorkspaceRequest request, Func<string, Task>? progress = null,
        CancellationToken cancellationToken = default) =>
        OpenWithProgressAsync(request, progress is null ? null : update => progress(update.Stage), cancellationToken);

    public async Task<WorkspaceSnapshot> OpenWithProgressAsync(WorkspaceRequest request, Func<WorkspacePreparationProgress, Task>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Validate(request);
        cancellationToken.ThrowIfCancellationRequested();
        Opening opening; bool start; long observer; Observer? subscription = progress is null ? null : new Observer(progress);
        lock (gate)
        {
            if (closed) throw new DiscoveryException("ENGINE_STOPPING", "The engine is stopping.", true);
            if (active is not null && active.Request != request)
                throw new DiscoveryException("OPEN_BUSY", "Another build is still opening. Return to it or cancel it first.", true);
            start = active is null;
            opening = active ??= new Opening(request);
            opening.Consumers++;
            observer = ++opening.NextObserver;
            if (subscription is not null) opening.Observers.Add(observer, subscription);
        }
        if (start) _ = RunAsync(opening);
        var detached = false;
        try { return await opening.Completion.Task.WaitAsync(cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            bool last; bool retired;
            lock (gate)
            {
                detached = true; opening.Observers.Remove(observer); opening.Consumers--;
                last = active == opening && opening.Consumers == 0;
                retired = opening.Completion.Task.IsCompleted;
            }
            if (last) RequestCancellation(opening);
            if (subscription is not null) await subscription.CloseAsync();
            if (retired) return await opening.Completion.Task;
            if (last)
            {
                // Cancellation retires the last producer; its durable success may have won the race.
                try { return await opening.Completion.Task; }
                catch (OperationCanceledException) { throw new OperationCanceledException(cancellationToken); }
            }
            throw;
        }
        finally
        {
            if (!detached)
            {
                lock (gate) { opening.Observers.Remove(observer); opening.Consumers--; }
                if (subscription is not null) await subscription.CloseAsync();
            }
        }
    }
    private async Task RunAsync(Opening opening)
    {
        WorkspaceSnapshot? result = null; Exception? failure = null;
        try
        {
            result = await preparer.PrepareWithProgressAsync(opening.Request, async stage =>
            {
                Observer[] observers;
                lock (gate) observers = opening.Observers.Values.ToArray();
                foreach (var observer in observers) await observer.ReportAsync(stage);
            }, opening.Cancellation.Token);
        }
        catch (Exception exception) { failure = exception; }
        lock (gate)
        {
            if (failure is OperationCanceledException canceled) opening.Completion.TrySetCanceled(canceled.CancellationToken);
            else if (failure is not null) opening.Completion.TrySetException(failure);
            else opening.Completion.TrySetResult(result!);
            if (active == opening) active = null;
            opening.Cancellation.Dispose();
        }
    }
    public static void Validate(WorkspaceRequest request)
    {
        ProductRegistry.Validate(request.ProductId);
        if (request.BuildIdentity.Length != 64 || request.BuildIdentity.Any(ch => ch is not (>= '0' and <= '9' or >= 'a' and <= 'f')) || !BuildIdentity.IsValidRegion(request.SourceRegion))
            throw new DiscoveryException("INVALID_REQUEST", "An exact build identity and source region are required.");
    }
    private static void RequestCancellation(Opening opening)
    {
        try { opening.Cancellation.Cancel(); }
        catch (ObjectDisposedException) { /* The producer retired between capture and cancellation. */ }
    }
    public async ValueTask DisposeAsync()
    {
        Opening? opening;
        lock (gate) { closed = true; opening = active; }
        if (opening is not null) RequestCancellation(opening);
        if (opening is not null) try { await opening.Completion.Task; } catch (Exception) { }
    }
}
