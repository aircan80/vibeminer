using System.Collections.Concurrent;
using VibeMiner.Objects;

namespace VibeMiner.Primitives.Tests;

/// <summary>
/// An in-memory layer 1 for tests of the layers above (#153): the same verification (<see cref="ObjectVerification.Default"/>) and
/// the same answers as the real store, no files. <see cref="ObjectStoreContractTests"/> runs one behaviour suite against both, so
/// it can't drift.
/// </summary>
public sealed class InMemoryObjectStore : IObjectReader, IObjectWriter
{
    private sealed record Stored(byte[] Bytes, ObjectOrigin Origin, DateTimeOffset At);
    private readonly ConcurrentDictionary<ObjectKey, Stored> objects = new();
    private readonly ConcurrentDictionary<ObjectKey, byte[]> staged = new();
    private readonly object writes = new();
    private readonly TimeProvider time;

    public InMemoryObjectStore(TimeProvider? time = null) => this.time = time ?? TimeProvider.System;

    public ValueTask<bool> HasAsync(ObjectKey key, CancellationToken ct = default) => ValueTask.FromResult(objects.ContainsKey(key));

    public ValueTask<ObjectRead> ReadAsync(ObjectKey key, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!objects.TryGetValue(key, out var s)) return ValueTask.FromResult(new ObjectRead(ReadState.Absent));
        if (s.Bytes.Length > 64 << 20) throw new ObjectStoreException(ObjectStoreCodes.Limit, "Whole reads are at most 64 MiB; read larger objects by range.");
        return ValueTask.FromResult(new ObjectRead(ReadState.Present, Info(key, s), s.Bytes.ToArray()));
    }

    public ValueTask<RangeRead> ReadRangeAsync(ObjectKey key, long offset, Memory<byte> destination, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (destination.Length > 64 << 20) throw new ObjectStoreException(ObjectStoreCodes.Limit, "A range read is at most 64 MiB.");
        if (!objects.TryGetValue(key, out var s)) return ValueTask.FromResult(new RangeRead(ReadState.Absent));
        if (offset + destination.Length > s.Bytes.Length) throw new ArgumentOutOfRangeException(nameof(offset), "The range runs past the end of the object.");
        s.Bytes.AsSpan((int)offset, destination.Length).CopyTo(destination.Span);
        return ValueTask.FromResult(new RangeRead(ReadState.Present, Info(key, s)));
    }

    public Task<IReadOnlyList<PutResult>> PutAsync(IReadOnlyList<PutObject> batch, bool commitNow = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
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
                : objects.TryRemove(k, out _) ? new DeleteResult(k, DeleteOutcome.Deleted) : new DeleteResult(k, DeleteOutcome.Absent)).ToArray());
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

    private static ObjectKey DocumentOf(ReadOnlySpan<byte> record) => new(ObjectKeyType.HistoryDocument, record.Slice(12, 16));
    private static ObjectInfo Info(ObjectKey key, Stored s) => new(key, s.Bytes.Length, s.Origin, s.At);

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
