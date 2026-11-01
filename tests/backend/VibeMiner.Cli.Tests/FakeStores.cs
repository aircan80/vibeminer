using VibeMiner.Builds;
using VibeMiner.Builds.Testing;
using VibeMiner.Composition;
using VibeMiner.ObjectAccess;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;

namespace VibeMiner.Cli.Tests;

/// <summary>A scripted store: what each open returns or throws, and which opens were asked for.</summary>
internal sealed class FakeStores : IStoreOpener
{
    public InMemoryObjectSource Source { get; } = new();
    public StoreStats Stats { get; set; } = new(2, 1234, 56, 3L << 20, 2L << 20, new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));
    public ScrubReport Scrub { get; set; } = new(null, false, new VerifyReport(1234, 3L << 20, 0, 0, [], []));
    public string? ReadOnlyFailure { get; set; }
    public string? WriterFailure { get; set; }
    public Func<CancellationToken, Task>? BeforeScrub { get; set; }
    /// <summary>Thrown by the open instead of a named store failure.</summary>
    public Exception? OpenThrows { get; set; }
    /// <summary>Thrown by a history read whose query has a cursor, so the first page succeeds and a later one fails.</summary>
    public Exception? LaterHistoryPageThrows { get; set; }
    /// <summary>When set, every observation gives this instead of asking the in-memory source (an offline version service).</summary>
    public ObservationResult? ObservationFails { get; set; }
    /// <summary>When set, an observation it answers (non-null) gives that; the rest go to the in-memory source.</summary>
    public Func<ObservationRequest, ObservationResult?>? ObservationScript { get; set; }
    public List<(string Folder, bool Writer)> Opened { get; } = [];
    /// <summary>Whether each writer open was allowed to create a missing store.</summary>
    public List<bool> WriterCreate { get; } = [];
    public int Open { get; private set; }
    /// <summary>When set, every open waits on this after it is recorded and before it fails or succeeds; the flag is true for a writer open.</summary>
    public Func<bool, CancellationToken, Task>? BeforeOpen { get; set; }
    /// <summary>Called as a handle is disposed, before it counts as closed; the flag is true for the writer.</summary>
    public Action<bool>? Disposing { get; set; }
    /// <summary>When set, every handle's objects are this (a wrapper over <see cref="Source"/>, say) instead of the scripted ones.</summary>
    public IObjectSource? Objects { get; set; }
    /// <summary>
    /// When set, the catalog stands in for the one in <c>catalog-v1/</c> of this data folder: one <see cref="InMemoryCatalog"/>
    /// shared by every handle, kept only while <see cref="CatalogMarker"/> exists. When a reset has removed the folder, the next
    /// handle gets a fresh catalog and writes the marker again. Unset, each handle gets its own fresh catalog.
    /// </summary>
    public string? CatalogDataFolder { get; set; }
    /// <summary>How many fresh catalogs handles have created.</summary>
    public int CatalogsCreated { get; private set; }
    public const string CatalogMarkerName = "fake-catalog";
    public string? CatalogMarker => CatalogDataFolder is { } data ? Path.Combine(data, DataFolderPaths.CatalogFolder, CatalogMarkerName) : null;
    private InMemoryCatalog? shared;

    private InMemoryCatalog CreateCatalog(IObjectSource objects)
    {
        if (CatalogMarker is not { } marker) { CatalogsCreated++; return new InMemoryCatalog(objects); }
        if (shared is not null && File.Exists(marker)) return shared;
        CatalogsCreated++;
        shared = new InMemoryCatalog(objects);
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllBytes(marker, []);
        return shared;
    }

    public Task<IStoreHandle> OpenReadOnlyAsync(string folder, CancellationToken ct) => OpenAsync(folder, false, ReadOnlyFailure, ct);
    /// <summary>A writer open asked to create a store gets past a missing one, as the real store does.</summary>
    public Task<IStoreHandle> OpenWriterAsync(string folder, bool create, CancellationToken ct)
    {
        WriterCreate.Add(create);
        return OpenAsync(folder, true, WriterFailure ?? (create && ReadOnlyFailure == ObjectStoreCodes.Missing ? null : ReadOnlyFailure), ct);
    }

    private async Task<IStoreHandle> OpenAsync(string folder, bool writer, string? failure, CancellationToken ct)
    {
        Opened.Add((folder, writer));
        if (BeforeOpen is { } wait) await wait(writer, ct);
        if (OpenThrows is not null) throw OpenThrows;
        if (failure is not null) throw new ObjectStoreException(failure, failure == ObjectStoreCodes.Missing ? $"No object store at {folder}." : "Scripted failure.");
        Open++;
        return new Handle(this, writer);
    }

    private sealed class Handle(FakeStores owner, bool writer) : IStoreHandle
    {
        public bool Writer => writer;
        private InMemoryCatalog? catalog;
        public IObjectSource Objects => owner.Objects is { } objects ? objects : owner.LaterHistoryPageThrows is { } failure ? new FailingHistory(owner.Source, failure)
            : owner.ObservationFails is { } offline ? new FailingObservations(owner.Source, offline)
            : owner.ObservationScript is { } script ? new ScriptedObservations(owner.Source, script) : owner.Source;
        public Task<ICatalog> CatalogAsync(CancellationToken ct) => Task.FromResult<ICatalog>(catalog ??= owner.CreateCatalog(Objects));
        public ValueTask<StoreStats> StatsAsync(CancellationToken ct) => ValueTask.FromResult(owner.Stats);
        public async Task<ScrubReport> ScrubAsync(IProgress<ScanProgress>? progress, CancellationToken ct)
        {
            if (owner.BeforeScrub is { } wait) await wait(ct);
            return owner.Scrub;
        }
        public ValueTask DisposeAsync() { owner.Disposing?.Invoke(writer); owner.Open--; return ValueTask.CompletedTask; }
    }
}

/// <summary>Passes everything to the in-memory source, except a history page after the first, which throws.</summary>
internal sealed class FailingHistory(IObjectSource inner, Exception failure) : IObjectSource
{
    public Task<ObjectResult> GetKeyAsync(ObjectRequest request, CdnContext? cdn, FetchPriority priority, CancellationToken ct) => inner.GetKeyAsync(request, cdn, priority, ct);
    public Task<IReadOnlyList<ObjectResult>> GetKeysAsync(IReadOnlyList<ObjectRequest> requests, CdnContext? cdn, FetchPriority priority, IProgress<ObjectProgress>? progress, CancellationToken ct) =>
        inner.GetKeysAsync(requests, cdn, priority, progress, ct);
    public Task<ObjectResult> GetRangeAsync(ObjectRequest request, long offset, Memory<byte> destination, CdnContext? cdn, FetchPriority priority, CancellationToken ct) =>
        inner.GetRangeAsync(request, offset, destination, cdn, priority, ct);
    public Task<ObservationResult> ObserveAsync(ObservationRequest request, CancellationToken ct) => inner.ObserveAsync(request, ct);
    public Task<ObservationResult> RecordAsync(Observation observation, CancellationToken cancellationToken) => inner.RecordAsync(observation, cancellationToken);
    public Task<long> CountPresentAsync(IReadOnlyList<ObjectKey> keys, CancellationToken ct) => inner.CountPresentAsync(keys, ct);
    public Task<IReadOnlyList<bool>> HasKeysAsync(IReadOnlyList<ObjectKey> keys, CancellationToken ct) => inner.HasKeysAsync(keys, ct);
    public Task<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken ct) =>
        query.After is null ? inner.ReadHistoryAsync(query, ct) : Task.FromException<IReadOnlyList<HistoryEntry>>(failure);
}

/// <summary>Passes everything to the in-memory source, except observations, which all give one scripted result.</summary>
internal sealed class FailingObservations(IObjectSource inner, ObservationResult result) : IObjectSource
{
    public Task<ObjectResult> GetKeyAsync(ObjectRequest request, CdnContext? cdn, FetchPriority priority, CancellationToken ct) => inner.GetKeyAsync(request, cdn, priority, ct);
    public Task<IReadOnlyList<ObjectResult>> GetKeysAsync(IReadOnlyList<ObjectRequest> requests, CdnContext? cdn, FetchPriority priority, IProgress<ObjectProgress>? progress, CancellationToken ct) =>
        inner.GetKeysAsync(requests, cdn, priority, progress, ct);
    public Task<ObjectResult> GetRangeAsync(ObjectRequest request, long offset, Memory<byte> destination, CdnContext? cdn, FetchPriority priority, CancellationToken ct) =>
        inner.GetRangeAsync(request, offset, destination, cdn, priority, ct);
    public Task<ObservationResult> ObserveAsync(ObservationRequest request, CancellationToken ct) => Task.FromResult(result);
    public Task<ObservationResult> RecordAsync(Observation observation, CancellationToken cancellationToken) => inner.RecordAsync(observation, cancellationToken);
    public Task<long> CountPresentAsync(IReadOnlyList<ObjectKey> keys, CancellationToken ct) => inner.CountPresentAsync(keys, ct);
    public Task<IReadOnlyList<bool>> HasKeysAsync(IReadOnlyList<ObjectKey> keys, CancellationToken ct) => inner.HasKeysAsync(keys, ct);
    public Task<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken ct) => inner.ReadHistoryAsync(query, ct);
}

/// <summary>Passes everything to the in-memory source, except the observations the script answers.</summary>
internal sealed class ScriptedObservations(IObjectSource inner, Func<ObservationRequest, ObservationResult?> script) : IObjectSource
{
    public Task<ObjectResult> GetKeyAsync(ObjectRequest request, CdnContext? cdn, FetchPriority priority, CancellationToken ct) => inner.GetKeyAsync(request, cdn, priority, ct);
    public Task<IReadOnlyList<ObjectResult>> GetKeysAsync(IReadOnlyList<ObjectRequest> requests, CdnContext? cdn, FetchPriority priority, IProgress<ObjectProgress>? progress, CancellationToken ct) =>
        inner.GetKeysAsync(requests, cdn, priority, progress, ct);
    public Task<ObjectResult> GetRangeAsync(ObjectRequest request, long offset, Memory<byte> destination, CdnContext? cdn, FetchPriority priority, CancellationToken ct) =>
        inner.GetRangeAsync(request, offset, destination, cdn, priority, ct);
    public Task<ObservationResult> ObserveAsync(ObservationRequest request, CancellationToken ct) =>
        script(request) is { } result ? Task.FromResult(result) : inner.ObserveAsync(request, ct);
    public Task<IReadOnlyList<bool>> HasKeysAsync(IReadOnlyList<ObjectKey> keys, CancellationToken ct) => inner.HasKeysAsync(keys, ct);
    public Task<ObservationResult> RecordAsync(Observation observation, CancellationToken cancellationToken) => inner.RecordAsync(observation, cancellationToken);
    public Task<long> CountPresentAsync(IReadOnlyList<ObjectKey> keys, CancellationToken ct) => inner.CountPresentAsync(keys, ct);
    public Task<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken ct) => inner.ReadHistoryAsync(query, ct);
}

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan by) => now += by;
}

/// <summary>
/// Passes everything to an inner source and counts what goes outside the store: observations by kind, and every GetKey, GetKeys
/// and GetRange call with the <see cref="ServedFrom"/> of each result (null when it wasn't present).
/// </summary>
internal sealed class CountingObjectSource(IObjectSource inner) : IObjectSource
{
    private readonly Lock gate = new();
    private readonly Dictionary<ObservationKind, int> observations = [];
    private readonly List<ServedFrom?> served = [];

    public int GetKeyCalls { get; private set; }
    public int GetKeysCalls { get; private set; }
    public int GetRangeCalls { get; private set; }
    public IReadOnlyDictionary<ObservationKind, int> Observations { get { lock (gate) return new Dictionary<ObservationKind, int>(observations); } }
    public IReadOnlyList<ServedFrom?> Served { get { lock (gate) return [.. served]; } }

    public void Clear()
    {
        lock (gate) { observations.Clear(); served.Clear(); GetKeyCalls = GetKeysCalls = GetRangeCalls = 0; }
    }

    private void Record(params ObjectResult[] results) { lock (gate) served.AddRange(results.Select(r => r.ServedFrom)); }

    public async Task<ObjectResult> GetKeyAsync(ObjectRequest request, CdnContext? cdn, FetchPriority priority, CancellationToken ct)
    {
        lock (gate) GetKeyCalls++;
        var r = await inner.GetKeyAsync(request, cdn, priority, ct);
        Record(r);
        return r;
    }

    public async Task<IReadOnlyList<ObjectResult>> GetKeysAsync(IReadOnlyList<ObjectRequest> requests, CdnContext? cdn, FetchPriority priority, IProgress<ObjectProgress>? progress, CancellationToken ct)
    {
        lock (gate) GetKeysCalls++;
        var r = await inner.GetKeysAsync(requests, cdn, priority, progress, ct);
        Record([.. r]);
        return r;
    }

    public async Task<ObjectResult> GetRangeAsync(ObjectRequest request, long offset, Memory<byte> destination, CdnContext? cdn, FetchPriority priority, CancellationToken ct)
    {
        lock (gate) GetRangeCalls++;
        var r = await inner.GetRangeAsync(request, offset, destination, cdn, priority, ct);
        Record(r);
        return r;
    }

    public Task<ObservationResult> ObserveAsync(ObservationRequest request, CancellationToken ct)
    {
        lock (gate) observations[request.Kind] = observations.GetValueOrDefault(request.Kind) + 1;
        return inner.ObserveAsync(request, ct);
    }

    public Task<ObservationResult> RecordAsync(Observation observation, CancellationToken cancellationToken) => inner.RecordAsync(observation, cancellationToken);
    public Task<long> CountPresentAsync(IReadOnlyList<ObjectKey> keys, CancellationToken ct) => inner.CountPresentAsync(keys, ct);
    public Task<IReadOnlyList<bool>> HasKeysAsync(IReadOnlyList<ObjectKey> keys, CancellationToken ct) => inner.HasKeysAsync(keys, ct);
    public Task<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken ct) => inner.ReadHistoryAsync(query, ct);
}
