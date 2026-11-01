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
    public List<(string Folder, bool Writer)> Opened { get; } = [];
    public int Open { get; private set; }

    public Task<IStoreHandle> OpenReadOnlyAsync(string folder, CancellationToken ct) => OpenAsync(folder, false, ReadOnlyFailure);
    public Task<IStoreHandle> OpenWriterAsync(string folder, CancellationToken ct) => OpenAsync(folder, true, WriterFailure ?? ReadOnlyFailure);

    private Task<IStoreHandle> OpenAsync(string folder, bool writer, string? failure)
    {
        Opened.Add((folder, writer));
        if (OpenThrows is not null) throw OpenThrows;
        if (failure is not null) throw new ObjectStoreException(failure, failure == ObjectStoreCodes.Missing ? $"No object store at {folder}." : "Scripted failure.");
        Open++;
        return Task.FromResult<IStoreHandle>(new Handle(this, writer));
    }

    private sealed class Handle(FakeStores owner, bool writer) : IStoreHandle
    {
        public bool Writer => writer;
        public IObjectSource Objects => owner.LaterHistoryPageThrows is { } failure ? new FailingHistory(owner.Source, failure) : owner.Source;
        public ValueTask<StoreStats> StatsAsync(CancellationToken ct) => ValueTask.FromResult(owner.Stats);
        public async Task<ScrubReport> ScrubAsync(IProgress<ScanProgress>? progress, CancellationToken ct)
        {
            if (owner.BeforeScrub is { } wait) await wait(ct);
            return owner.Scrub;
        }
        public ValueTask DisposeAsync() { owner.Open--; return ValueTask.CompletedTask; }
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
    public Task<long> CountPresentAsync(IReadOnlyList<ObjectKey> keys, CancellationToken ct) => inner.CountPresentAsync(keys, ct);
    public Task<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken ct) =>
        query.After is null ? inner.ReadHistoryAsync(query, ct) : Task.FromException<IReadOnlyList<HistoryEntry>>(failure);
}

internal sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset now = start;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan by) => now += by;
}
