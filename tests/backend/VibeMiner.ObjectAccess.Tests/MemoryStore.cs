using VibeMiner.Objects;

namespace VibeMiner.ObjectAccess.Tests;

/// <summary>
/// An in-memory store for these tests: the real verification on Put, recorded origins, a history log, and copies that can be
/// marked corrupt (the store's own fake has no damaged copies).
/// </summary>
internal sealed class MemoryStore : IObjectReader, IObjectWriter, IHistoryLog
{
    private readonly Lock gate = new();
    private readonly Dictionary<ObjectKey, (byte[] Bytes, ObjectOrigin Origin)> objects = [];
    private readonly HashSet<ObjectKey> corrupt = [];
    public List<HistoryObservation> History { get; } = [];
    public int Puts;

    public ObjectOrigin? OriginOf(ObjectKey key) { lock (gate) return objects.TryGetValue(key, out var o) ? o.Origin : null; }
    public int Count { get { lock (gate) return objects.Count; } }
    public void Corrupt(ObjectKey key) { lock (gate) corrupt.Add(key); }
    public void Add(PutObject o) { lock (gate) objects[o.Key] = (o.Bytes.ToArray(), o.Origin); }

    /// <summary>When set, runs before every HasAsync answer (it may throw, to fake a store failure or a cancellation there).</summary>
    public Func<ObjectKey, CancellationToken, ValueTask>? BeforeHas { get; set; }

    public async ValueTask<bool> HasAsync(ObjectKey key, CancellationToken ct = default)
    {
        if (BeforeHas is { } hook) await hook(key, ct);
        lock (gate) return objects.ContainsKey(key);
    }

    public ValueTask<ObjectRead> ReadAsync(ObjectKey key, CancellationToken ct = default)
    {
        lock (gate)
        {
            if (!objects.TryGetValue(key, out var o)) return ValueTask.FromResult(new ObjectRead(ReadState.Absent));
            if (corrupt.Contains(key)) return ValueTask.FromResult(new ObjectRead(ReadState.Corrupt, Problem: "flipped (fake)"));
            return ValueTask.FromResult(new ObjectRead(ReadState.Present, new ObjectInfo(key, o.Bytes.Length, o.Origin, DateTimeOffset.UnixEpoch), (byte[])o.Bytes.Clone()));
        }
    }

    public ValueTask<RangeRead> ReadRangeAsync(ObjectKey key, long offset, Memory<byte> destination, CancellationToken ct = default)
    {
        lock (gate)
        {
            if (!objects.TryGetValue(key, out var o)) return ValueTask.FromResult(new RangeRead(ReadState.Absent));
            if (corrupt.Contains(key)) return ValueTask.FromResult(new RangeRead(ReadState.Corrupt));
            o.Bytes.AsMemory((int)offset, destination.Length).CopyTo(destination);
            return ValueTask.FromResult(new RangeRead(ReadState.Present, new ObjectInfo(key, o.Bytes.Length, o.Origin, DateTimeOffset.UnixEpoch)));
        }
    }

    public Task<IReadOnlyList<PutResult>> PutAsync(IReadOnlyList<PutObject> batch, bool commitNow = false, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Puts);
        var results = new List<PutResult>();
        lock (gate)
            foreach (var o in batch)
            {
                if (objects.ContainsKey(o.Key)) { results.Add(new(o.Key, PutOutcome.AlreadyPresent)); continue; }
                if (ObjectVerification.Default.Check(o.Key, o.Bytes.Span, o.DeclaredLength) is { } refusal)
                { results.Add(new(o.Key, PutOutcome.Refused, refusal.Code, refusal.Message)); continue; }
                objects[o.Key] = (o.Bytes.ToArray(), o.Origin);
                results.Add(new(o.Key, PutOutcome.Stored));
            }
        return Task.FromResult<IReadOnlyList<PutResult>>(results);
    }

    public Task<IReadOnlyList<DeleteResult>> DeleteAsync(IReadOnlyList<ObjectKey> keys, CancellationToken ct = default)
    {
        lock (gate)
            return Task.FromResult<IReadOnlyList<DeleteResult>>(keys.Select(k =>
            { corrupt.Remove(k); return new DeleteResult(k, objects.Remove(k) ? DeleteOutcome.Deleted : DeleteOutcome.Absent); }).ToArray());
    }

    /// <summary>Staging without files or resume: bytes are held, checked by the real incremental check, and stored on commit.</summary>
    public Task<IObjectStaging> StageAsync(ObjectKey key, long declaredLength, ObjectOrigin origin = ObjectOrigin.Cdn, CancellationToken ct = default) =>
        Task.FromResult<IObjectStaging>(new Staging(this, key, declaredLength, origin));

    private sealed class Staging(MemoryStore owner, ObjectKey key, long declared, ObjectOrigin origin) : IObjectStaging
    {
        private readonly MemoryStream bytes = new();
        private readonly IObjectCheck check = ObjectVerification.Default.Begin(key, declared);
        public ObjectKey Key => key;
        public long DeclaredLength => declared;
        public long Length => bytes.Length;
        public Task AppendAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            if (check.Append(data.Span) is { } refusal) throw new ObjectStoreException(ObjectStoreCodes.StagingRejected, refusal.Message);
            bytes.Write(data.Span);
            return Task.CompletedTask;
        }
        public Task<PutResult> CommitAsync(CancellationToken ct = default)
        {
            if (check.Complete() is { } refusal) return Task.FromResult(new PutResult(key, PutOutcome.Refused, refusal.Code, refusal.Message));
            lock (owner.gate)
            {
                if (owner.objects.ContainsKey(key)) return Task.FromResult(new PutResult(key, PutOutcome.AlreadyPresent));
                owner.objects[key] = (bytes.ToArray(), origin);
            }
            return Task.FromResult(new PutResult(key, PutOutcome.Stored));
        }
        public ValueTask AbandonAsync() { bytes.SetLength(0); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { check.Dispose(); return ValueTask.CompletedTask; }
    }

    public Task<IReadOnlyList<PutResult>> AppendHistoryAsync(HistoryObservation observation, CancellationToken ct = default)
    {
        lock (gate) History.Add(observation);
        return PutAsync(HistoryRecordFormat.Objects(observation), true, ct);
    }

    public ValueTask<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken ct = default)
    {
        lock (gate)
            return ValueTask.FromResult<IReadOnlyList<HistoryEntry>>(History
                .Where(h => (query.Product is null || h.Product == query.Product) && (query.Region is null || h.Region == query.Region) && (query.Kind is null || h.Kind == query.Kind))
                .Select(h => { var o = HistoryRecordFormat.Objects(h); return new HistoryEntry(h.Kind, h.Product, h.Region, h.ObservedAt, h.Url, o[0].Key, o[1].Key); })
                .OrderBy(h => h.ObservedAt).Take(query.Limit).ToArray());
    }
}
