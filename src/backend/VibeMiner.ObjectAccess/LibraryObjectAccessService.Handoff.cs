using VibeMiner.Objects;

namespace VibeMiner.ObjectAccess;

public sealed partial class LibraryObjectAccessService
{
    private LibraryObjectOwner? HandoffOwner(Flight flight, long length)
    {
        lock (gate)
        {
            var live = flight.Consumers.Where(c => !c.Token.IsCancellationRequested).ToArray();
            if (live.Length == 0) throw new OperationCanceledException(new CancellationToken(true));
            return live.FirstOrDefault(c => (c.Request.DeclaredLength is null || c.Request.DeclaredLength == length) &&
                (c.Range is null || c.Range.Value.Fits(length)))?.Owner;
        }
    }
    private static async Task<IObjectStaging> StageOwnedAsync(LibraryObjectOwner owner, ObjectKey key, long length, ObjectOrigin origin, CancellationToken ct)
    {
        try { return await owner.StageAsync(key, length, origin, ct).ConfigureAwait(false); }
        catch (InvalidOperationException e) { throw new ObjectStoreException(ObjectStoreCodes.Busy, "Another staging producer holds this key.", e); }
        catch (ArgumentException e) { throw new ObjectStoreException(ObjectRefusalCodes.KeyTypeUnsupported, e.Message, e); }
    }
    private sealed class HandoffWriter(LibraryObjectAccessService service, Flight flight) : IObjectWriter
    {
        public Task<IReadOnlyList<PutResult>> PutAsync(IReadOnlyList<PutObject> batch, bool commitNow = false, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (batch.Count == 0) return Task.FromResult<IReadOnlyList<PutResult>>([]);
            if (batch.Count > service.limits.MaxBatch || batch.Any(p => p.Key != flight.Request.Key)) throw new ArgumentException("A publication handoff contains only its captured key.", nameof(batch));
            var owner = service.HandoffOwner(flight, batch[0].Bytes.Length);
            if (owner is null) return Task.FromResult<IReadOnlyList<PutResult>>(batch.Select(p => new PutResult(p.Key, PutOutcome.Refused,
                ObjectRefusalCodes.SizeMismatch, "No live submitted consumer accepts this payload length.")).ToArray());
            // This local immutable capability is the accepted handoff. A later departure cannot rewrite the committed owner.
            return owner.PutAsync(batch, commitNow, ct);
        }
        public async Task<IObjectStaging> StageAsync(ObjectKey key, long declaredLength, ObjectOrigin origin = ObjectOrigin.Cdn, CancellationToken ct = default)
        {
            var owner = service.HandoffOwner(flight, declaredLength) ?? throw new ObjectStoreException(ObjectRefusalCodes.SizeMismatch, "No live consumer accepts this staged length.");
            var capturedOrigin = flight.StagingOpened ? flight.StagingOrigin : origin;
            var staging = await StageOwnedAsync(owner, key, declaredLength, capturedOrigin, ct).ConfigureAwait(false);
            var effectiveOrigin = staging.Length == 0 ? origin : flight.StagingOpened ? flight.StagingOrigin : ObjectOrigin.Unknown;
            if (capturedOrigin != effectiveOrigin)
            {
                await staging.DisposeAsync().ConfigureAwait(false);
                staging = await StageOwnedAsync(owner, key, declaredLength, effectiveOrigin, ct).ConfigureAwait(false);
            }
            flight.StagingOpened = true; flight.StagingOrigin = effectiveOrigin;
            return new HandoffStaging(service, flight, owner, staging, effectiveOrigin);
        }
        public Task<IReadOnlyList<DeleteResult>> DeleteAsync(IReadOnlyList<ObjectKey> keys, CancellationToken ct = default) =>
            throw new ObjectStoreException(ObjectStoreCodes.LibraryOperationUnsupported, "Library acquisition never deletes authoritative library entries.");
    }
    private sealed class HandoffStaging(LibraryObjectAccessService service, Flight flight, LibraryObjectOwner owner,
        IObjectStaging staging, ObjectOrigin origin) : IObjectStaging
    {
        private LibraryObjectOwner captured = owner;
        private IObjectStaging inner = staging;
        public ObjectKey Key => inner.Key;
        public long DeclaredLength => inner.DeclaredLength;
        public long Length => inner.Length;
        public Task AppendAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default) => inner.AppendAsync(bytes, ct);
        public async Task<PutResult> CommitAsync(CancellationToken ct = default)
        {
            for (var retargets = 0; ; retargets++)
            {
                ct.ThrowIfCancellationRequested();
                var live = service.HandoffOwner(flight, DeclaredLength);
                if (live is null) throw new ObjectStoreException(ObjectRefusalCodes.SizeMismatch, "No live consumer accepts this staged length.");
                if (ReferenceEquals(live, captured)) return await inner.CommitAsync(ct).ConfigureAwait(false);
                if (retargets >= service.limits.MaxStageRetargets)
                    throw new ObjectStoreException(ObjectStoreCodes.Limit, "The live-owner staging handoff exhausted its bounded retarget budget; proven staging is retained.");
                var key = Key; var length = DeclaredLength;
                await inner.DisposeAsync().ConfigureAwait(false); // Release A; only bytes proven again may become B's staging.
                inner = await StageOwnedAsync(live, key, length, origin, ct).ConfigureAwait(false); captured = live;
                if (inner.Length != length) throw new ObjectStoreException(ObjectStoreCodes.StagingRejected,
                    "The owner handoff could not re-prove the complete staged file; no payload or claim was published.");
            }
        }
        public ValueTask AbandonAsync() => inner.AbandonAsync();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
