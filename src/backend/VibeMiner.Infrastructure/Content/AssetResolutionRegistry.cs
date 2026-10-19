using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

internal sealed record AssetSourceContextKey(WorkspaceRequest Request, string Locale,
    OpeningSourceReference Reference, string BuildConfigurationKey, string CdnConfigurationKey,
    string ConfigurationRevision = ConfigurationWorkspacePreparer.VerifierRevision,
    string EncodingRevision = ResolverArtifacts.EncodingRevision,
    string RootRevision = AssetResolutionSessions.RootArtifactRevision,
    string SourceRevision = MetadataAcquirer.SourceRevision,
    string VariantPolicy = AssetResolutionSession.VariantPolicy,
    string EncodingSelectionPolicy = AssetResolutionSession.EncodingSelectionPolicy)
{
    internal void RequirePrepared(CapturedOpeningContext prepared)
    {
        if (prepared.Reference != Reference || prepared.Build.BuildConfig != BuildConfigurationKey ||
            prepared.Build.CdnConfig != CdnConfigurationKey || prepared.Build.Id != Request.BuildIdentity ||
            prepared.Build.ProductId != Request.ProductId)
            throw new DiscoveryException("ASSET_SOURCE_CHANGED", "The captured asset source changed during preparation; retry with the current exact context.", true);
    }
}

/// <summary>One engine-owned registry: two opening/held/draining contexts, eight feature handles,
/// eight total batch subscriptions, one fair ROOT queue and one separate cold-preparation owner.</summary>
internal sealed class AssetResolutionRegistry : IAsyncDisposable
{
    internal const int MaxContexts = 2, MaxHandles = 8, MaxSubscriptions = 8;
    private readonly object gate = new();
    private readonly CancellationTokenSource shutdown;
    private readonly SemaphoreSlim preparation = new(1, 1);
    private readonly AssetRootTurns turns;
    private readonly Dictionary<AssetSourceContextKey, Context> contexts = [];
    private readonly HashSet<Feature> features = [];
    private int subscriptions;
    private bool closed;
    private Task? disposal;

    internal AssetResolutionRegistry(CancellationToken ownerShutdown = default)
    { shutdown = CancellationTokenSource.CreateLinkedTokenSource(ownerShutdown); turns = new(gate); }

    internal sealed class Feature
    {
        internal Context? Context;
        internal readonly CancellationTokenSource Stop = new();
        internal readonly HashSet<Subscription> Subscriptions = [];
        internal readonly TaskCompletionSource Retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Opening = true, Closed;
        internal Task? Disposal;
        internal Exception? ReleaseFailure;
    }
    internal sealed class Context(AssetSourceContextKey? key)
    {
        internal readonly AssetSourceContextKey? Key = key;
        internal readonly CancellationTokenSource Stop = new();
        internal readonly HashSet<AssetProgressObserver> Observers = [];
        internal Task<AssetResolutionSource> Opening = null!;
        internal AssetResolutionSource? Source;
        internal int References;
        internal bool Draining;
        internal Task? Drain;
    }
    internal sealed class Subscription(Feature feature, uint[] ids, AssetProgressObserver observer)
    {
        internal readonly Feature Feature = feature;
        internal readonly uint[] Ids = ids;
        internal readonly AssetProgressObserver Observer = observer;
        internal readonly TaskCompletionSource Retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Batch? Batch;
    }
    internal sealed class Batch(Context context, uint[] ids)
    {
        internal readonly Context Context = context;
        internal readonly HashSet<uint> Targets = [.. ids];
        internal readonly HashSet<Subscription> Consumers = [];
        internal readonly CancellationTokenSource Stop = new();
        internal uint[] Frozen = [];
        internal bool Stopping;
        internal Task<AssetResolutionBatch> Completion = null!;
        internal AssetCancellation.Deadline Deadline = null!;
    }

    internal async Task<AssetResolutionSession> OpenAsync(
        Func<CancellationToken, Task<AssetSourceContextKey>> capture,
        Func<AssetSourceContextKey, Func<string, Task>, CancellationToken, Task<AssetResolutionSource>> prepare,
        Func<string, Task> progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var feature = new Feature();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(closed, this);
            if (features.Count == MaxHandles) throw Capacity("ASSET_HANDLE_CAPACITY", "Asset feature handle capacity is occupied.");
            features.Add(feature);
        }
        var observer = new AssetProgressObserver(progress);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token, feature.Stop.Token, observer.FailureToken);
        var deadline = AssetCancellation.After(lifetime, TimeSpan.FromMinutes(10));
        var transferred = false;
        try
        {
            var key = await capture(lifetime.Token);
            Context context;
            lock (gate)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(closed, this);
                if (!contexts.TryGetValue(key, out context!))
                {
                    if (contexts.Count == MaxContexts) throw Capacity("ASSET_CONTEXT_CAPACITY", "Asset source context capacity is occupied.");
                    context = new(key); contexts.Add(key, context);
                    context.Opening = PrepareAsync(context, prepare);
                }
                if (context.Draining) throw Capacity("ASSET_CONTEXT_CAPACITY", "This exact asset source context is still retiring.");
                context.References++; feature.Context = context; context.Observers.Add(observer);
            }
            var source = await context.Opening.WaitAsync(lifetime.Token);
            lock (gate) context.Observers.Remove(observer);
            await observer.CloseAsync(discard: false);
            if (observer.Failure is { } failure) throw failure;
            await deadline.DisposeAsync();
            token.ThrowIfCancellationRequested();
            feature.Stop.Token.ThrowIfCancellationRequested();
            shutdown.Token.ThrowIfCancellationRequested();
            lifetime.Token.ThrowIfCancellationRequested();
            lock (gate)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                feature.Opening = false; transferred = true;
                return new(this, feature, source.SourceContext);
            }
        }
        catch (OperationCanceledException) when (observer.Failure is not null) { throw observer.Failure; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !shutdown.IsCancellationRequested && !feature.Stop.IsCancellationRequested)
        { throw Capacity("ASSET_SESSION_TIMEOUT", "Asset source preparation exceeded its consumer time budget."); }
        finally
        {
            try
            {
                if (!transferred)
                {
                    lock (gate) feature.Context?.Observers.Remove(observer);
                    await ReleaseFeatureAsync(feature, observer, deadline);
                }
            }
            finally { observer.Dispose(); }
        }
    }

    private async Task<AssetResolutionSource> PrepareAsync(Context context,
        Func<AssetSourceContextKey, Func<string, Task>, CancellationToken, Task<AssetResolutionSource>> prepare)
    {
        await Task.Yield();
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.Stop.Token, shutdown.Token);
        var deadline = AssetCancellation.After(lifetime, TimeSpan.FromMinutes(10));
        var entered = false;
        try
        {
            await preparation.WaitAsync(lifetime.Token); entered = true;
            var source = await prepare(context.Key!, stage => ReportOpening(context, stage), lifetime.Token);
            lock (gate) context.Source = source;
            return source;
        }
        finally
        {
            await deadline.DisposeAsync();
            if (entered) preparation.Release();
        }
    }
    private Task ReportOpening(Context context, string stage)
    {
        AssetProgressObserver[] observers;
        lock (gate) observers = context.Observers.ToArray();
        foreach (var observer in observers) _ = observer.PostAsync(stage);
        return Task.CompletedTask;
    }

    internal Task<T> RootTurnAsync<T>(object owner, Func<CancellationToken, Task<T>> operation, CancellationToken token) =>
        turns.Enqueue(owner, operation, token);

    // Authored direct-source fixtures use an isolated registry, never a second production owner.
    internal Feature Attach(AssetResolutionSource source)
    {
        lock (gate)
        {
            var context = new Context(null) { Source = source, Opening = Task.FromResult(source), References = 1 };
            var feature = new Feature { Context = context, Opening = false }; features.Add(feature); return feature;
        }
    }

    internal async Task<AssetResolutionBatch> ResolveAsync(Feature feature, IReadOnlyCollection<uint> requested,
        Func<string, Task> progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(requested); ArgumentNullException.ThrowIfNull(progress);
        token.ThrowIfCancellationRequested();
        if (requested.Count > RootBatchLookup.MaxTargets) throw InvalidBatch();
        var unique = new HashSet<uint>(); var supplied = 0;
        foreach (var id in requested)
        {
            token.ThrowIfCancellationRequested();
            if (++supplied > RootBatchLookup.MaxTargets) throw InvalidBatch();
            if (id == 0) throw new DiscoveryException("ASSET_INVALID", "A nonzero asset FileDataID is required.");
            unique.Add(id);
        }
        var ids = unique.Order().ToArray();
        var observer = new AssetProgressObserver(progress);
        var subscription = new Subscription(feature, ids, observer);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, shutdown.Token, feature.Stop.Token, observer.FailureToken);
        var deadline = AssetCancellation.After(lifetime, TimeSpan.FromMinutes(10));
        AssetResolutionBatch? cached;
        var admitted = false;
        try
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(closed || feature.Closed, this);
                if (subscriptions == MaxSubscriptions) throw Capacity("ASSET_BATCH_CAPACITY", "Asset batch subscription capacity is occupied.");
                subscriptions++; feature.Subscriptions.Add(subscription); admitted = true;
                var context = feature.Context!;
                if (!context.Source!.TryCached(ids, lifetime.Token, out cached))
                {
                    Batch? batch = turns.ActiveOwner as Batch;
                    if (batch?.Context != context || batch.Stopping || batch.Stop.IsCancellationRequested || !ids.All(batch.Targets.Contains)) batch = null;
                    if (batch is null && turns.TailOwner is Batch tail && tail.Context == context && !tail.Stopping && !tail.Stop.IsCancellationRequested &&
                        tail.Targets.Union(ids).Take(RootBatchLookup.MaxTargets + 1).Count() <= RootBatchLookup.MaxTargets)
                    { batch = tail; batch.Targets.UnionWith(ids); }
                    if (batch is null)
                    {
                        batch = new(context, ids); batch.Deadline = AssetCancellation.After(batch.Stop, TimeSpan.FromMinutes(10));
                        var selected = batch;
                        batch.Completion = turns.Enqueue(batch, ct => context.Source.ResolveAsync(selected.Frozen,
                            stage => ReportBatch(selected, stage), ct), batch.Stop.Token,
                            () => selected.Frozen = selected.Targets.Order().ToArray());
                    }
                    subscription.Batch = batch; batch.Consumers.Add(subscription);
                }
            }
            var result = cached ?? await subscription.Batch!.Completion.WaitAsync(lifetime.Token);
            await observer.CloseAsync(discard: false);
            if (observer.Failure is { } failure) throw failure;
            await deadline.DisposeAsync();
            token.ThrowIfCancellationRequested();
            feature.Stop.Token.ThrowIfCancellationRequested();
            shutdown.Token.ThrowIfCancellationRequested();
            lifetime.Token.ThrowIfCancellationRequested();
            if (cached is not null) return cached;
            var ownResults = result.Results.Where(value => Array.BinarySearch(ids, value.FileDataId) >= 0).ToArray();
            return new(Array.AsReadOnly(ownResults), result.ScannedRoot);
        }
        catch (OperationCanceledException) when (observer.Failure is not null) { throw observer.Failure; }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !shutdown.IsCancellationRequested && !feature.Stop.IsCancellationRequested)
        { throw Capacity("ASSET_RESOLUTION_TIMEOUT", "Asset resolution exceeded its consumer time budget."); }
        finally
        {
            try
            {
                if (admitted) await RetireSubscriptionAsync(subscription, deadline);
                else await deadline.DisposeAsync();
            }
            finally { observer.Dispose(); }
        }
    }
    private async Task RetireSubscriptionAsync(Subscription subscription, AssetCancellation.Deadline deadline)
    {
        var feature = subscription.Feature;
        var observer = subscription.Observer;
        Batch? last = null;
        lock (gate)
        {
            if (subscription.Batch is { } batch)
            {
                batch.Consumers.Remove(subscription);
                if (batch.Consumers.Count == 0) { batch.Stopping = true; last = batch; }
            }
        }
        if (last is not null)
        {
            await AssetCancellation.RequestAsync(last.Stop);
            try { await last.Completion; } catch (Exception error) when (error is not OutOfMemoryException) { }
            await last.Deadline.DisposeAsync(); last.Stop.Dispose();
        }
        await observer.CloseAsync(discard: true);
        await deadline.DisposeAsync();
        lock (gate)
        {
            subscriptions--; feature.Subscriptions.Remove(subscription); subscription.Retired.TrySetResult();
        }
    }
    private Task ReportBatch(Batch batch, string stage)
    {
        AssetProgressObserver[] observers;
        lock (gate) observers = batch.Consumers.Select(value => value.Observer).ToArray();
        foreach (var observer in observers) _ = observer.PostAsync(stage);
        return Task.CompletedTask;
    }

    internal Task DisposeFeatureAsync(Feature feature)
    {
        lock (gate)
        {
            feature.Closed = true;
            if (feature.Disposal is not null) return feature.Disposal;
            if (feature.Retired.Task.IsCompleted)
                return feature.ReleaseFailure is { } failure ? Task.FromException(failure) : Task.CompletedTask;
            return feature.Disposal = DisposeFeatureCoreAsync(feature);
        }
    }
    private async Task DisposeFeatureCoreAsync(Feature feature)
    {
        await AssetCancellation.RequestAsync(feature.Stop);
        bool opening; Task[] pending;
        lock (gate) { opening = feature.Opening; pending = feature.Subscriptions.Select(value => value.Retired.Task).ToArray(); }
        if (opening)
        {
            await feature.Retired.Task;
            if (feature.ReleaseFailure is { } failure) throw failure;
            return;
        }
        await Task.WhenAll(pending);
        await ReleaseFeatureAsync(feature);
    }
    private async Task ReleaseFeatureAsync(Feature feature, AssetProgressObserver? observer = null, AssetCancellation.Deadline? deadline = null)
    {
        Task? drain = null;
        lock (gate)
        {
            feature.Closed = true;
            if (feature.Context is { } context)
            {
                feature.Context = null;
                if (--context.References == 0)
                {
                    context.Draining = true;
                    drain = context.Drain ??= DrainContextAsync(context);
                }
            }
        }
        try
        {
            if (observer is not null) await observer.CloseAsync(discard: true);
            if (deadline is not null) await deadline.DisposeAsync();
            if (drain is not null) await drain;
        }
        catch (Exception error)
        {
            lock (gate) { feature.ReleaseFailure = error; feature.Retired.TrySetResult(); }
            throw;
        }
        lock (gate) { features.Remove(feature); feature.Retired.TrySetResult(); }
        feature.Stop.Dispose();
    }
    private async Task DrainContextAsync(Context context)
    {
        await Task.Yield();
        await AssetCancellation.RequestAsync(context.Stop);
        try { await context.Opening; }
        catch (DiscoveryException error) when (error.Code == "ASSET_SOURCE_CLEANUP") { throw; }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        if (context.Source is not null) await context.Source.DisposeAsync();
        lock (gate) { if (context.Key is not null) contexts.Remove(context.Key); }
        context.Stop.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        lock (gate) { closed = true; return new(disposal ??= DisposeCoreAsync()); }
    }
    private async Task DisposeCoreAsync()
    {
        await AssetCancellation.RequestAsync(shutdown);
        Feature[] held; lock (gate) held = features.ToArray();
        await Task.WhenAll(held.Select(DisposeFeatureAsync));
        preparation.Dispose(); shutdown.Dispose();
    }
    private static DiscoveryException InvalidBatch() => new("ASSET_BATCH_LIMIT", "An asset batch may supply at most 128 FileDataIDs.");
    private static DiscoveryException Capacity(string code, string message) => new(code, message, true);
}
