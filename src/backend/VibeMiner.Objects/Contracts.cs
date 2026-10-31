namespace VibeMiner.Objects;

/// <summary>Reading layer 1: the store, the read-only second store and <see cref="LayeredObjectReader"/> all offer this and no more.</summary>
public interface IObjectReader
{
    /// <summary>Whether a committed entry exists for the key. Index only; no bytes are read.</summary>
    ValueTask<bool> HasAsync(ObjectKey key, CancellationToken ct = default);
    /// <summary>A whole object, at most 64 MiB. Never returns bytes that fail a check: those read Corrupt.</summary>
    ValueTask<ObjectRead> ReadAsync(ObjectKey key, CancellationToken ct = default);
    /// <summary><paramref name="destination"/>.Length bytes from <paramref name="offset"/>, reading and checking only the blocks under the range.</summary>
    ValueTask<RangeRead> ReadRangeAsync(ObjectKey key, long offset, Memory<byte> destination, CancellationToken ct = default);
}

/// <summary>Writing layer 1. Every object is checked by <see cref="ObjectVerification.Default"/> before it can be stored.</summary>
public interface IObjectWriter
{
    /// <summary>Verifies and stores a batch; per object Stored, AlreadyPresent, Refused (with the same code <see cref="IObjectVerifier.Check"/> gives) or Canceled.</summary>
    Task<IReadOnlyList<PutResult>> PutAsync(IReadOnlyList<PutObject> batch, bool commitNow = false, CancellationToken ct = default);
    /// <summary>Removes objects; history entries can't be deleted.</summary>
    Task<IReadOnlyList<DeleteResult>> DeleteAsync(IReadOnlyList<ObjectKey> keys, CancellationToken ct = default);
    /// <summary>Starts or resumes one encoded object too large for a Put (up to 512 MiB), checked chunk by chunk as it arrives.</summary>
    Task<IObjectStaging> StageAsync(ObjectKey key, long declaredLength, ObjectOrigin origin = ObjectOrigin.Cdn, CancellationToken ct = default);
}

/// <summary>One large object arriving in pieces. Disposing keeps the proven bytes for a later resume; Abandon removes them.</summary>
public interface IObjectStaging : IAsyncDisposable
{
    ObjectKey Key { get; }
    long DeclaredLength { get; }
    /// <summary>Bytes staged so far; after a resume, exactly the bytes chunk checks prove again. Fetch from here.</summary>
    long Length { get; }
    /// <summary>Appends the next bytes; a failed chunk check cuts back to the proven bytes and throws OBJECT_STAGING_REJECTED.</summary>
    Task AppendAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default);
    /// <summary>Verifies the whole object again, stores it and removes the staged bytes.</summary>
    Task<PutResult> CommitAsync(CancellationToken ct = default);
    ValueTask AbandonAsync();
}

/// <summary>Reading the history log of raw version-service replies, oldest first.</summary>
public interface IHistoryReader
{
    ValueTask<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken ct = default);
}

/// <summary>The history log: append-only, survives reset.</summary>
public interface IHistoryLog : IHistoryReader
{
    /// <summary>Stores one observation: the reply document (once however often it's seen) and its dated record.</summary>
    Task<IReadOnlyList<PutResult>> AppendHistoryAsync(HistoryObservation observation, CancellationToken ct = default);
}

/// <summary>Maintenance of the one writer's store.</summary>
public interface IObjectStoreMaintenance
{
    /// <summary>Rebuilds the index from the packs alone.</summary>
    Task<ScanReport> ScanAsync(IProgress<ScanProgress>? progress = null, CancellationToken ct = default);
    /// <summary>Scrubs every indexed object (plus its key type's own check with <paramref name="full"/>).</summary>
    Task<VerifyReport> VerifyAsync(bool full = false, IProgress<ScanProgress>? progress = null, CancellationToken ct = default);
    /// <summary>Rewrites sealed packs with dead space.</summary>
    Task<CompactReport> CompactAsync(CompactOptions? compact = null, CancellationToken ct = default);
}

/// <summary>Why bytes can't be stored under a key: one of <see cref="ObjectRefusalCodes"/>, and what failed.</summary>
public sealed record ObjectRefusal(string Code, string Message);

/// <summary>
/// The per-key-type check layer 1 runs before anything reaches a pack (D122). <see cref="ObjectVerification.Default"/> is the
/// one implementation; the store's Put and staging use that same instance, so a client checking first gets exactly Put's answer.
/// </summary>
public interface IObjectVerifier
{
    /// <summary>
    /// Checks a whole object held in memory: null when Put would store it, else the refusal Put would give, the 64 MiB whole-object
    /// bound included. Put also refuses a history record whose document isn't stored yet; that depends on the store, not the bytes.
    /// </summary>
    ObjectRefusal? Check(ObjectKey key, ReadOnlySpan<byte> bytes, long declaredLength);
    /// <summary>Starts a check of an object that arrives in pieces (up to the key type's limit, 512 MiB for encoded objects).</summary>
    IObjectCheck Begin(ObjectKey key, long declaredLength);
}

/// <summary>An object checked as its bytes arrive, in order.</summary>
public interface IObjectCheck : IDisposable
{
    /// <summary>Feeds the next bytes. Returns the refusal as soon as the bytes can't match; once refused, it stays refused.</summary>
    ObjectRefusal? Append(ReadOnlySpan<byte> bytes);
    /// <summary>Ends the check after the last byte: null when the object is right.</summary>
    ObjectRefusal? Complete();
    /// <summary>Bytes from the start already proven (whole BLTE chunks); 0 when the type has no partial proof. After a refusal, the bytes proven before it.</summary>
    long ProvenLength { get; }
}

/// <summary>The default and only <see cref="IObjectVerifier"/>: the checks of D122, original code with no parser dependency.</summary>
public sealed class ObjectVerification : IObjectVerifier
{
    public static ObjectVerification Default { get; } = new();
    private ObjectVerification() { }

    public ObjectRefusal? Check(ObjectKey key, ReadOnlySpan<byte> bytes, long declaredLength) => Check(key, bytes, declaredLength, out _);

    /// <summary>As <see cref="Check(ObjectKey, ReadOnlySpan{byte}, long)"/>, also naming the document a history record needs.</summary>
    internal ObjectRefusal? Check(ObjectKey key, ReadOnlySpan<byte> bytes, long declaredLength, out ObjectKey? document)
    {
        document = null;
        if (ObjectFormats.Refusal(key, declaredLength) is { } code) return new(code, Explain(code));
        if (bytes.Length != declaredLength) return new(ObjectRefusalCodes.SizeMismatch, "The bytes are not the declared length.");
        if (declaredLength > ObjectFormats.MaxInMemoryBytes) return new(ObjectRefusalCodes.TooLarge, "Over 64 MiB: stage it instead.");
        try { ObjectVerifier.VerifyWhole(key, bytes, out document); return null; }
        catch (VerificationFailure f) { return new(f.Code, f.Message); }
    }

    public IObjectCheck Begin(ObjectKey key, long declaredLength)
    {
        if (ObjectFormats.Refusal(key, declaredLength) is { } code) return new Refused(new(code, Explain(code)));
        return new Incremental(ObjectVerifier.For(key, declaredLength));
    }

    private static string Explain(string code) => code switch
    {
        ObjectRefusalCodes.KeyTypeUnsupported => "This key type is not one format 1 stores.",
        ObjectRefusalCodes.NamespaceUnsupported => "Only namespace 0 is stored in format 1.",
        _ => "The declared length is zero or over this key type's limit.",
    };

    private sealed class Incremental(ObjectVerifier verifier) : IObjectCheck
    {
        private ObjectRefusal? refusal;
        public long ProvenLength => verifier.VerifiedPrefix;
        public ObjectRefusal? Append(ReadOnlySpan<byte> bytes)
        {
            if (refusal is not null) return refusal;
            try { verifier.Append(bytes); return null; }
            catch (VerificationFailure f) { return refusal = new(f.Code, f.Message); }
        }
        public ObjectRefusal? Complete()
        {
            if (refusal is not null) return refusal;
            try { verifier.Complete(); return null; }
            catch (VerificationFailure f) { return refusal = new(f.Code, f.Message); }
        }
        public void Dispose() => verifier.Dispose();
    }

    private sealed class Refused(ObjectRefusal refusal) : IObjectCheck
    {
        public long ProvenLength => 0;
        public ObjectRefusal? Append(ReadOnlySpan<byte> bytes) => refusal;
        public ObjectRefusal? Complete() => refusal;
        public void Dispose() { }
    }
}

/// <summary>
/// A store opened for reading only, such as the shared dev store (D130). It offers reads, the history log and a scrub, and
/// nothing that writes: there is no Put, Delete, Stage, Scan or Compact to call.
/// </summary>
public sealed class ReadOnlyObjectStore : IObjectReader, IHistoryReader, IAsyncDisposable
{
    private readonly ObjectStore inner;
    internal ReadOnlyObjectStore(ObjectStore inner) => this.inner = inner;
    public string Folder => inner.Folder;
    public ValueTask<bool> HasAsync(ObjectKey key, CancellationToken ct = default) => inner.HasAsync(key, ct);
    public ValueTask<ObjectRead> ReadAsync(ObjectKey key, CancellationToken ct = default) => inner.ReadAsync(key, ct);
    public ValueTask<RangeRead> ReadRangeAsync(ObjectKey key, long offset, Memory<byte> destination, CancellationToken ct = default) => inner.ReadRangeAsync(key, offset, destination, ct);
    public ValueTask<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken ct = default) => inner.ReadHistoryAsync(query, ct);
    /// <summary>Scrubs every indexed object, as the writer's Verify does; it reads only.</summary>
    public Task<VerifyReport> VerifyAsync(bool full = false, IProgress<ScanProgress>? progress = null, CancellationToken ct = default) => inner.VerifyAsync(full, progress, ct);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
