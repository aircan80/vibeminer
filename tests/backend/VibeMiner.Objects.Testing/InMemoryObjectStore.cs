using System.Collections.Concurrent;

namespace VibeMiner.Objects.Testing;

/// <summary>
/// An in-memory layer 1 for tests of the layers above (#153): the same verification (<see cref="ObjectVerification.Default"/>) and
/// the same answers as the real store, history log included, no files. ObjectStoreContractTests runs one behaviour suite against
/// both, so it can't drift. The members under "Test hooks" are outside the contract: counters, and faults the real store reports
/// the same way (a damaged copy reads Corrupt) or that a caller must survive (a store check that throws, bytes changed under a key).
/// </summary>
public sealed class InMemoryObjectStore : IObjectReader, IObjectWriter, IHistoryLog
{
    private sealed record Stored(byte[] Bytes, ObjectOrigin Origin, DateTimeOffset At);
    private readonly ConcurrentDictionary<ObjectKey, Stored> objects = new();
    private readonly ConcurrentDictionary<ObjectKey, byte[]> staged = new();
    private readonly ConcurrentDictionary<ObjectKey, HistoryEntry> history = new();
    private readonly ConcurrentDictionary<ObjectKey, byte> corrupt = new();
    private readonly List<HistoryObservation> appended = [];
    private readonly object writes = new();
    private readonly TimeProvider time;
    private int puts;

    public InMemoryObjectStore(TimeProvider? time = null) => this.time = time ?? TimeProvider.System;

    // Test hooks.

    /// <summary>Objects stored, history documents and records included.</summary>
    public int Count => objects.Count;
    /// <summary>PutAsync calls, AppendHistoryAsync's included; staging commits aren't Puts.</summary>
    public int Puts => Volatile.Read(ref puts);
    /// <summary>Observations AppendHistoryAsync stored (or found already stored), in call order, with their reply bytes.</summary>
    public IReadOnlyList<HistoryObservation> History { get { lock (appended) return appended.ToArray(); } }
    public ObjectOrigin? OriginOf(ObjectKey key) => objects.TryGetValue(key, out var s) ? s.Origin : null;
    /// <summary>The stored copy is damaged: reads answer Corrupt, as the real store does, until a Delete removes it.</summary>
    public void Corrupt(ObjectKey key) { if (!objects.ContainsKey(key)) throw new InvalidOperationException("Only a stored object can be damaged."); corrupt[key] = 0; }
    /// <summary>Replaces a stored object's bytes, same length, without any check: damage the store didn't catch, for callers that verify.</summary>
    public void Tamper(ObjectKey key, byte[] bytes)
    {
        lock (writes)
        {
            if (!objects.TryGetValue(key, out var s) || s.Bytes.Length != bytes.Length) throw new InvalidOperationException("Only a stored object can be tampered with, at its own length.");
            objects[key] = s with { Bytes = bytes.ToArray() };
        }
    }
    /// <summary>When set, runs before every HasAsync answer (it may throw, to fake a store failure or a cancellation there).</summary>
    public Func<ObjectKey, CancellationToken, ValueTask>? BeforeHas { get; set; }

    // IObjectReader.

    public async ValueTask<bool> HasAsync(ObjectKey key, CancellationToken ct = default)
    {
        if (BeforeHas is { } hook) await hook(key, ct);
        return objects.ContainsKey(key);
    }

    public ValueTask<ObjectRead> ReadAsync(ObjectKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!objects.TryGetValue(key, out var s)) return ValueTask.FromResult(new ObjectRead(ReadState.Absent));
        if (corrupt.ContainsKey(key)) return ValueTask.FromResult(new ObjectRead(ReadState.Corrupt, Problem: "Damaged copy (fake)."));
        if (s.Bytes.Length > 64 << 20) throw new ObjectStoreException(ObjectStoreCodes.Limit, "Whole reads are at most 64 MiB; read larger objects by range.");
        return ValueTask.FromResult(new ObjectRead(ReadState.Present, Info(key, s), s.Bytes.ToArray()));
    }

    public ValueTask<RangeRead> ReadRangeAsync(ObjectKey key, long offset, Memory<byte> destination, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (destination.Length > 64 << 20) throw new ObjectStoreException(ObjectStoreCodes.Limit, "A range read is at most 64 MiB.");
        if (!objects.TryGetValue(key, out var s)) return ValueTask.FromResult(new RangeRead(ReadState.Absent));
        if (corrupt.ContainsKey(key)) return ValueTask.FromResult(new RangeRead(ReadState.Corrupt));
        if (offset + destination.Length > s.Bytes.Length) throw new ArgumentOutOfRangeException(nameof(offset), "The range runs past the end of the object.");
        s.Bytes.AsSpan((int)offset, destination.Length).CopyTo(destination.Span);
        return ValueTask.FromResult(new RangeRead(ReadState.Present, Info(key, s)));
    }

    // IObjectWriter.

    public Task<IReadOnlyList<PutResult>> PutAsync(IReadOnlyList<PutObject> batch, bool commitNow = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        Interlocked.Increment(ref puts);
        ct.ThrowIfCancellationRequested();  // as the real store: canceled before it starts is a thrown cancellation, not a result
        if (batch.Count > 4096 || batch.Sum(o => (long)o.Bytes.Length) > 256L << 20)
            throw new ObjectStoreException(ObjectStoreCodes.Limit, "A Put batch is at most 4,096 objects and 256 MiB.");
        var results = new PutResult[batch.Count];
        lock (writes)
            for (var i = 0; i < batch.Count; i++)
            {
                var o = batch[i];
                if (!Enum.IsDefined(o.Origin)) throw new ArgumentOutOfRangeException(nameof(batch), "Unknown object origin.");
                if (ObjectVerification.Default.Check(o.Key, o.Bytes.Span, o.DeclaredLength) is { } refusal) { results[i] = new(o.Key, PutOutcome.Refused, refusal.Code, refusal.Message); continue; }
                if (objects.ContainsKey(o.Key)) { results[i] = new(o.Key, PutOutcome.AlreadyPresent); continue; }
                if (o.Key.Type == ObjectKeyType.HistoryRecord && !objects.ContainsKey(DocumentOf(o.Bytes.Span)))
                { results[i] = new(o.Key, PutOutcome.Refused, ObjectRefusalCodes.HistoryDocumentMissing, "Put the reply document before (or with) its record."); continue; }
                objects[o.Key] = new(o.Bytes.ToArray(), o.Origin, time.GetUtcNow());
                if (o.Key.Type == ObjectKeyType.HistoryRecord) history[o.Key] = EntryOf(o.Key, o.Bytes.Span);
                results[i] = new(o.Key, PutOutcome.Stored);
            }
        return Task.FromResult<IReadOnlyList<PutResult>>(results);
    }

    public Task<IReadOnlyList<DeleteResult>> DeleteAsync(IReadOnlyList<ObjectKey> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        lock (writes)
            return Task.FromResult<IReadOnlyList<DeleteResult>>(keys.Select(k =>
                k.Type is ObjectKeyType.HistoryDocument or ObjectKeyType.HistoryRecord ? new DeleteResult(k, DeleteOutcome.Refused, ObjectRefusalCodes.DeleteRefused)
                : objects.TryRemove(k, out _) ? Deleted(k) : new DeleteResult(k, DeleteOutcome.Absent)).ToArray());
        DeleteResult Deleted(ObjectKey k) { corrupt.TryRemove(k, out _); return new(k, DeleteOutcome.Deleted); }
    }

    public Task<IObjectStaging> StageAsync(ObjectKey key, long declaredLength, ObjectOrigin origin = ObjectOrigin.Cdn, CancellationToken ct = default)
    {
        if (key.Type != ObjectKeyType.Encoded) throw new ArgumentException("Only encoded objects are staged; Put the others whole.", nameof(key));
        if (ObjectFormats.Refusal(key, declaredLength) is { } refusal) throw new ArgumentException($"Can't stage this object ({refusal}).", nameof(key));
        // Resume: keep only what chunk checks prove again, as the real store does.
        var kept = staged.GetValueOrDefault(key) ?? [];
        using (var again = ObjectVerification.Default.Begin(key, declaredLength))
        {
            again.Append(kept);
            kept = kept.AsSpan(0, (int)again.ProvenLength).ToArray();
        }
        staged[key] = kept;
        return Task.FromResult<IObjectStaging>(new Staging(this, key, declaredLength, origin, kept));
    }

    // IHistoryLog.

    public async Task<IReadOnlyList<PutResult>> AppendHistoryAsync(HistoryObservation observation, CancellationToken ct = default)
    {
        var results = await PutAsync(HistoryRecordFormat.Objects(observation), commitNow: true, ct);
        if (results.All(r => r.Outcome != PutOutcome.Refused)) lock (appended) appended.Add(observation);
        return results;
    }

    /// <summary>As the real store: oldest first, then by record key, filtered, after the cursor, at most <see cref="HistoryQuery.Limit"/>.</summary>
    public ValueTask<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Limit is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(query), "A history page is 1 to 10,000 entries.");
        ct.ThrowIfCancellationRequested();
        // Unix milliseconds, as the store keeps them: a cursor's sub-millisecond part counts for nothing.
        static int Order(HistoryEntry a, HistoryEntry b) => a.ObservedAt.ToUnixTimeMilliseconds() != b.ObservedAt.ToUnixTimeMilliseconds()
            ? a.ObservedAt.ToUnixTimeMilliseconds().CompareTo(b.ObservedAt.ToUnixTimeMilliseconds())
            : a.Record.KeyBytes().AsSpan().SequenceCompareTo(b.Record.KeyBytes());
        var page = history.Values
            .Where(e => (query.Product is null || e.Product == query.Product) && (query.Region is null || e.Region == query.Region)
                && (query.Kind is null || e.Kind == query.Kind) && (query.After is null || Order(e, query.After) > 0))
            .ToList();
        page.Sort(Order);
        return ValueTask.FromResult<IReadOnlyList<HistoryEntry>>(page.Take(query.Limit).ToArray());
    }

    private static ObjectKey DocumentOf(ReadOnlySpan<byte> record) => new(ObjectKeyType.HistoryDocument, record.Slice(12, 16));
    private static ObjectInfo Info(ObjectKey key, Stored s) => new(key, s.Bytes.Length, s.Origin, s.At);

    /// <summary>A record Put has already verified, read with the store's own parser.</summary>
    private static HistoryEntry EntryOf(ObjectKey record, ReadOnlySpan<byte> bytes)
    {
        if (!HistoryRecordFormat.TryParse(bytes, out var p)) throw new InvalidOperationException("Put stored a history record it can't parse.");
        return new(p.Kind, p.Product, p.Region, DateTimeOffset.FromUnixTimeMilliseconds(p.ObservedMs), p.Url, p.Document, record);
    }

    private sealed class Staging : IObjectStaging
    {
        private readonly InMemoryObjectStore owner;
        private readonly ObjectOrigin origin;
        private readonly IObjectCheck check;
        private readonly MemoryStream bytes = new();
        private bool broken, finished;

        public Staging(InMemoryObjectStore owner, ObjectKey key, long declared, ObjectOrigin origin, byte[] resumed)
        {
            this.owner = owner; Key = key; DeclaredLength = declared; this.origin = origin;
            check = ObjectVerification.Default.Begin(key, declared);
            check.Append(resumed); bytes.Write(resumed);
        }

        public ObjectKey Key { get; }
        public long DeclaredLength { get; }
        public long Length => bytes.Length;

        public Task AppendAsync(ReadOnlyMemory<byte> next, CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(finished, this);
            if (broken) throw new ObjectStoreException(ObjectStoreCodes.StagingRejected, "This staging was refused; start it again to resume from the proven bytes.");
            if (Length + next.Length > DeclaredLength) throw new ArgumentException("More bytes than the declared length.", nameof(next));
            // As the real store: write, then check, and on a refusal cut back to what the checks proved.
            bytes.Write(next.Span);
            if (check.Append(next.Span) is { } refusal)
            {
                broken = true;
                bytes.SetLength(check.ProvenLength);
                owner.staged[Key] = bytes.ToArray();
                throw new ObjectStoreException(ObjectStoreCodes.StagingRejected, $"Staged bytes for {Key} were refused ({refusal.Code}): {refusal.Message}");
            }
            owner.staged[Key] = bytes.ToArray();
            return Task.CompletedTask;
        }

        public Task<PutResult> CommitAsync(CancellationToken ct = default)
        {
            ObjectDisposedException.ThrowIf(finished, this);
            if (broken || Length != DeclaredLength) throw new InvalidOperationException("Only a complete, unrefused staging can be committed.");
            finished = true;
            owner.staged.TryRemove(Key, out _);
            if (check.Complete() is { } refusal) return Task.FromResult(new PutResult(Key, PutOutcome.Refused, refusal.Code, refusal.Message));
            lock (owner.writes)
            {
                if (owner.objects.ContainsKey(Key)) return Task.FromResult(new PutResult(Key, PutOutcome.AlreadyPresent));
                owner.objects[Key] = new(bytes.ToArray(), origin, owner.time.GetUtcNow());
            }
            return Task.FromResult(new PutResult(Key, PutOutcome.Stored));
        }

        public ValueTask AbandonAsync() { finished = true; owner.staged.TryRemove(Key, out _); check.Dispose(); return ValueTask.CompletedTask; }
        public ValueTask DisposeAsync() { finished = true; check.Dispose(); return ValueTask.CompletedTask; }
    }
}
