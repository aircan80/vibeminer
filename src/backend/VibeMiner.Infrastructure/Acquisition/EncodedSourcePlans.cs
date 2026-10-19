using System.Security.Cryptography;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

internal sealed record EncodedSourcePlan(EncodedSourceContext Source, CapturedOpeningContext Opening,
    TactConfiguration Cdn, IReadOnlyList<Uri> Routes, IReadOnlyList<string> ArchiveKeys, string Sha256);
internal sealed record EncodedMapLookup(bool IsMember, IReadOnlyList<ContentArchiveLocation> Locations);

/// <summary>Engine-owned immutable source authority and job-held locator registrations, never a per-object research resolver.</summary>
public sealed class EncodedSourcePlans : IAsyncDisposable
{
    private readonly DiscoveryCatalog catalog;
    private readonly VerifiedObjectStore objects;
    private readonly AcquisitionResources resources;
    private readonly int maximumEntries;
    private readonly TimeProvider clock;
    private readonly object gate = new();
    private readonly Dictionary<EncodedSourceContext, Entry> entries = [];
    private readonly Dictionary<EncodedSourceContext, MapRegistration> maps = [];
    private readonly CancellationTokenSource shutdown = new();
    private long sequence;
    private bool stopping;
    private Task? disposal;
    internal readonly ArchiveProofCache ArchiveCache = new();
    internal long ConfigurationLoads { get; private set; }
    private sealed class Entry(EncodedSourceContext source, CancellationToken shutdownToken)
    {
        internal readonly EncodedSourceContext Source = source;
        internal readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken);
        internal readonly TaskCompletionSource Retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly SharedAcquisitionWork Work = new();
        internal readonly TaskCompletionSource<EncodedSourcePlan> Ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Users;
        internal bool Retiring;
        internal long Used;
        internal DateTimeOffset? RetryAt;
    }
    public EncodedSourcePlans(DiscoveryCatalog catalog, VerifiedObjectStore objects, AcquisitionResources resources,
        int maximumEntries = 32, TimeProvider? timeProvider = null)
    {
        if (maximumEntries is < 1 or > 32) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        this.catalog = catalog; this.objects = objects; this.resources = resources; this.maximumEntries = maximumEntries;
        clock = timeProvider ?? TimeProvider.System;
    }

    internal async Task<EncodedSourcePlanLease> AcquireAsync(EncodedSourceContext source,
        CancellationToken token = default, IAcquisitionWorkContext? context = null)
    {
        token.ThrowIfCancellationRequested();
        if (source.SourceReference is null)
        {
            var opening = await catalog.ResolveOpeningAsync(new(source.ProductId, source.BuildIdentity, source.SourceRegion), token);
            source = source with { SourceReference = opening.Reference };
        }
        Entry entry; bool create;
        while (true)
        {
            Task? retiring = null; create = false;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(stopping, this);
                if (entries.TryGetValue(source, out var failed) && failed.Users == 0 && failed.RetryAt <= clock.GetUtcNow())
                    entries.Remove(source);
                if (entries.TryGetValue(source, out entry!) && entry.Retiring)
                    retiring = entry.Retired.Task;
                else
                {
                    if (entry is null)
                    {
                        while (entries.Count >= maximumEntries)
                        {
                            var idle = entries.Values.Where(item => item.Users == 0 && item.Retired.Task.IsCompleted)
                                .OrderBy(item => item.Used).FirstOrDefault();
                            if (idle is null) throw Error("ENCODED_SOURCE_CAPACITY", "All bounded source-plan leases are in use.");
                            entries.Remove(idle.Source);
                        }
                        entry = new(source, shutdown.Token); entries.Add(source, entry); create = true;
                    }
                    entry.Users++; entry.Used = ++sequence;
                }
            }
            if (retiring is null) break;
            await retiring.WaitAsync(token);
        }
        var demand = entry.Work.Attach(context);
        if (create) _ = CreateAsync(entry);
        try
        {
            var plan = await entry.Ready.Task.WaitAsync(token);
            return new(plan, async () => { demand.Dispose(); await ReleaseAsync(entry); });
        }
        catch { demand.Dispose(); await ReleaseAsync(entry); throw; }
    }
    private async ValueTask ReleaseAsync(Entry entry)
    {
        Task? retirement = null; CancellationTokenSource? cancellation = null;
        lock (gate)
        {
            entry.Users--; entry.Used = ++sequence;
            if (entry.Users == 0 && !entry.Retired.Task.IsCompleted)
            {
                if (!entry.Ready.Task.IsCompleted) { entry.Retiring = true; cancellation = entry.Cancellation; }
                retirement = entry.Retired.Task;
            }
        }
        try { cancellation?.Cancel(); } catch (ObjectDisposedException) { /* Creation already retired. */ }
        if (retirement is not null) await retirement;
    }
    private async Task CreateAsync(Entry entry)
    {
        var token = entry.Cancellation.Token;
        try { entry.Ready.TrySetResult(await PrepareAsync(entry.Source, entry.Work, token)); }
        catch (OperationCanceledException) { entry.Ready.TrySetCanceled(token); }
        catch (Exception failure)
        {
            lock (gate) entry.RetryAt = clock.GetUtcNow().AddSeconds(30);
            entry.Ready.TrySetException(failure);
        }
        finally
        {
            lock (gate)
            {
                if (entry.Retiring || entry.Users == 0 && entry.Ready.Task.IsCanceled) entries.Remove(entry.Source);
                entry.Retired.TrySetResult();
            }
            entry.Cancellation.Dispose();
        }
    }
    private async Task<EncodedSourcePlan> PrepareAsync(EncodedSourceContext source, IAcquisitionWorkContext work, CancellationToken token)
    {
        var request = new WorkspaceRequest(source.ProductId, source.BuildIdentity, source.SourceRegion);
        WorkspaceService.Validate(request);
        var opening = source.SourceReference is { } pinned
            ? await catalog.ResolveOpeningAsync(request, pinned, token)
            : await catalog.ResolveOpeningAsync(request, token);
        var retained = await catalog.FindConfigurationAsync(opening.Build.CdnConfig, token)
            ?? throw Error("ENCODED_SOURCE_NOT_PREPARED", "The pinned CDN configuration must be retained before physical acquisition.");
        if (retained.Sha256 != source.CdnConfigurationSha256)
            throw Error("ENCODED_SOURCE_CONTEXT_CONFLICT", "The encoded source context differs from its retained CDN configuration.");
        if (!Uri.TryCreate(retained.SourceUrl, UriKind.Absolute, out var configurationUrl) ||
            !configurationUrl.AbsolutePath.Contains("/config/", StringComparison.Ordinal))
            throw Error("ENCODED_SOURCE_CONTEXT_CONFLICT", "The retained configuration has no original official source receipt.");
        var rawUrl = new Uri(retained.SourceUrl.Replace("/config/", "/data/", StringComparison.Ordinal));
        OfficialSourceTransport.ValidateUri(rawUrl);
        if (!rawUrl.AbsolutePath.EndsWith($"/{opening.Build.CdnConfig[..2]}/{opening.Build.CdnConfig[2..4]}/{opening.Build.CdnConfig}", StringComparison.Ordinal))
            throw Error("ENCODED_SOURCE_CONTEXT_CONFLICT", "The retained CDN configuration URL differs from its advertised raw identity.");
        TactConfiguration cdn; Uri first;
        await work.ReportAsync(new("Verifying pinned source configuration", "bytes", 0, retained.ByteLength));
        using (await resources.EnterCpuAsync(work, token))
        {
            var bytes = await objects.ReadAsync(retained.Sha256, retained.ByteLength, token);
            lock (gate) ConfigurationLoads++;
            if (Convert.ToHexStringLower(MD5.HashData(bytes)) != opening.Build.CdnConfig)
                throw Error("CONFIG_INTEGRITY", "The pinned CDN configuration differs from the exact build's advertised identity.");
            cdn = TactConfigurationParser.Parse(bytes, token);
            first = ConfigurationWorkspacePreparer.ResolveRoute(opening);
        }
        var archives = cdn.Fields.TryGetValue("archives", out var keys) ? keys.ToArray() : [];
        if (archives.Length > 4096 || archives.Any(key => key.Length != 32 || key.Any(ch => ch is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) ||
            archives.Distinct(StringComparer.Ordinal).Count() != archives.Length)
            throw Error("CONTENT_LOCATOR_UNSUPPORTED", "The pinned source has no bounded canonical archive ordinal list.");
        return new(source with { SourceReference = opening.Reference }, opening, cdn,
            Array.AsReadOnly(SelectedFileAcquirer.Routes(opening, first).ToArray()), Array.AsReadOnly(archives), retained.Sha256);
    }

    internal IAsyncDisposable RegisterMap(EncodedSourcePlanLease plan,
        Func<string, long, CancellationToken, Task<EncodedMapLookup>> lookup)
    {
        ArgumentNullException.ThrowIfNull(lookup);
        var registration = new MapRegistration(this, plan.Plan.Source, lookup);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            if (!maps.TryAdd(plan.Plan.Source, registration))
                throw Error("ENCODED_MAP_BUSY", "This pinned source already has a registered locator session.");
        }
        return registration;
    }
    internal async Task<EncodedMapLookup?> LookupMapAsync(EncodedSourcePlanLease plan, string encodingKey,
        long encodedBytes, CancellationToken token)
    {
        MapRegistration? registration;
        lock (gate)
        {
            if (!maps.TryGetValue(plan.Plan.Source, out registration)) return null;
            registration.Users++;
        }
        try { return await registration.Lookup(encodingKey, encodedBytes, token); }
        finally
        {
            lock (gate)
            {
                registration.Users--;
                if (registration.Retired && registration.Users == 0) registration.Drained.TrySetResult();
            }
        }
    }
    private sealed class MapRegistration(EncodedSourcePlans owner, EncodedSourceContext source,
        Func<string, long, CancellationToken, Task<EncodedMapLookup>> lookup) : IAsyncDisposable
    {
        internal readonly Func<string, long, CancellationToken, Task<EncodedMapLookup>> Lookup = lookup;
        internal readonly TaskCompletionSource Drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Users;
        internal bool Retired;
        public ValueTask DisposeAsync()
        {
            lock (owner.gate)
            {
                if (!Retired)
                {
                    Retired = true;
                    if (owner.maps.GetValueOrDefault(source) == this) owner.maps.Remove(source);
                    if (Users == 0) Drained.TrySetResult();
                }
            }
            return new(Drained.Task);
        }
    }
    public ValueTask DisposeAsync() => AcquisitionDisposal.Start(gate, ref disposal, DisposeCoreAsync);
    private async Task DisposeCoreAsync()
    {
        Task[] creations; MapRegistration[] registrations;
        lock (gate)
        {
            stopping = true; creations = entries.Values.Select(entry => entry.Retired.Task).ToArray();
            registrations = maps.Values.ToArray();
        }
        shutdown.Cancel();
        await Task.WhenAll(registrations.Select(async registration => await registration.DisposeAsync()));
        try { await Task.WhenAll(creations); } catch { /* Individual callers retain typed creation failures. */ }
        await ArchiveCache.DisposeAsync();
        lock (gate) entries.Clear();
        shutdown.Dispose();
    }
    private static DiscoveryException Error(string code, string message) => new(code, message, true);
}

internal sealed class EncodedSourcePlanLease(EncodedSourcePlan plan, Func<ValueTask> release) : IAsyncDisposable
{
    private readonly object gate = new();
    private Task? disposal;
    internal EncodedSourcePlan Plan { get; } = plan;
    public ValueTask DisposeAsync() => AcquisitionDisposal.Start(gate, ref disposal, () => release().AsTask());
}

/// <summary>Dynamic highest remaining demand for a shared prerequisite, independent of any one caller's cancellation.</summary>
internal sealed class SharedAcquisitionWork : IAcquisitionWorkContext
{
    private readonly object gate = new();
    private readonly Dictionary<long, IAcquisitionWorkContext?> demands = [];
    private long identity, revision;
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public AcquisitionWorkSnapshot Snapshot
    {
        get
        {
            lock (gate) return new(revision,
                demands.Values.Any(item => item is null || item.Snapshot.Priority == AcquisitionPriority.Interactive)
                    ? AcquisitionPriority.Interactive : AcquisitionPriority.Background, []);
        }
    }
    internal IDisposable Attach(IAcquisitionWorkContext? context)
    {
        long id; var cancellation = new CancellationTokenSource();
        lock (gate) { id = ++identity; demands.Add(id, context); Changed(); }
        if (context is not null) _ = WatchAsync(context, cancellation.Token);
        return new Release(() =>
        {
            lock (gate) { demands.Remove(id); Changed(); }
            cancellation.Cancel(); cancellation.Dispose();
        });
    }
    private async Task WatchAsync(IAcquisitionWorkContext context, CancellationToken token)
    {
        try
        {
            while (true)
            {
                var observed = context.Snapshot.Revision;
                lock (gate) Changed();
                await context.WaitForChangeAsync(observed, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private void Changed() { revision++; var previous = changed; changed = new(TaskCreationOptions.RunContinuationsAsynchronously); previous.TrySetResult(); }
    public Task WaitForChangeAsync(long observedRevision, CancellationToken token = default)
    { lock (gate) return observedRevision != revision ? Task.CompletedTask : changed.Task.WaitAsync(token); }
    public async Task ReportAsync(AcquisitionProgress progress)
    {
        IAcquisitionWorkContext[] targets;
        lock (gate) targets = demands.Values.OfType<IAcquisitionWorkContext>().Distinct().ToArray();
        foreach (var target in targets) await target.ReportAsync(progress);
    }
    private sealed class Release(Action release) : IDisposable
    {
        private Action? action = release;
        public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke();
    }
}

// Publish the one disposal task under a lock, then invoke callbacks only after releasing it.
internal static class AcquisitionDisposal
{
    internal static ValueTask Start(object gate, ref Task? current, Func<Task> dispose)
    {
        TaskCompletionSource? start = null; Task pending;
        lock (gate)
        {
            if (current is null)
            {
                start = new(TaskCreationOptions.RunContinuationsAsynchronously); current = start.Task;
            }
            pending = current;
        }
        if (start is not null) _ = CompleteAsync(dispose, start);
        return new(pending);
    }
    private static async Task CompleteAsync(Func<Task> dispose, TaskCompletionSource completion)
    {
        try { await dispose(); completion.TrySetResult(); }
        catch (OperationCanceledException failure) { completion.TrySetCanceled(failure.CancellationToken); }
        catch (Exception failure) { completion.TrySetException(failure); }
    }
}
