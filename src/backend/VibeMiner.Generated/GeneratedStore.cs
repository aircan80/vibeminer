using VibeMiner.Objects;

namespace VibeMiner.Generated;

/// <summary>
/// Bounds and timing passed to a standalone GeneratedStore's layer 1 pack store. Borrowed facades retain the shared library's settings;
/// only the internal bounded commit-splitting and observation hooks apply to borrowed calls.
/// </summary>
public sealed record GeneratedStoreOptions
{
    /// <summary>
    /// A pack seals at this size and only sealed packs compact. Smaller than the object store's 2 GiB: generated data is rewritten
    /// often (a listfile download stages about 150 MB and deletes it, #186), so packs must seal for that space to come back.
    /// </summary>
    public const long DefaultMaxPackBytes = 256L << 20;
    public long MaxPackBytes { get; init; } = DefaultMaxPackBytes;
    public int CommitObjects { get; init; } = 128;
    public long CommitBytes { get; init; } = 16L << 20;
    public TimeSpan CommitDelay { get; init; } = TimeSpan.FromMilliseconds(250);
    public TimeProvider Time { get; init; } = TimeProvider.System;
    /// <summary>Tests only: the most bytes one layer 1 Put takes (layer 1's bound); a larger batch goes in several commits.</summary>
    internal long MaxCommitBytes { get; init; } = 256L << 20;
    /// <summary>Tests only: called after each layer 1 commit of a Put, with the commit's object count.</summary>
    internal Action<int>? AfterCommit { get; init; }
}

/// <summary>What the store holds as of the last commit: live packs, objects, the packs' committed bytes and the part live objects use.</summary>
public sealed record GeneratedStoreStats(int Packs, long Objects, long PackBytes, long LiveBytes, DateTimeOffset CreatedAt);

/// <summary>A compaction: packs rewritten, their bytes before and after, objects moved, and packs skipped for holding a corrupt object.</summary>
public sealed record GeneratedCompactReport(int PacksCompacted, long BytesBefore, long BytesAfter, long Moved, long SkippedCorrupt);

/// <summary>
/// The real generated store (D210): a standalone layer 1 pack store of kind Generated in its own folder (<see cref="DefaultFolderName"/> in the data
/// folder), or an explicit borrowed view of a mixed library. Every key is ObjectKeyType.Generated; reads are checked as layer 1 checks
/// them, and a Put returns once its batch is committed.
/// </summary>
public sealed class GeneratedStore : IGeneratedStore, IAsyncDisposable
{
    /// <summary>The store's folder name inside the data folder.</summary>
    public const string DefaultFolderName = "generated-v1";
    private readonly ObjectStore? store;
    private readonly IObjectReader reader;
    private readonly IObjectWriter writer;
    private readonly GeneratedStoreOptions options;
    private readonly BorrowedLifetime? borrowed;

    private GeneratedStore(ObjectStore store, GeneratedStoreOptions options)
    {
        this.store = store; reader = store; writer = store; this.options = options; Folder = store.Folder;
    }

    private GeneratedStore(LibraryObjectOwner owner, GeneratedStoreOptions options)
    {
        reader = owner; writer = owner; this.options = options; Folder = owner.Folder; borrowed = new();
    }

    public string Folder { get; }

    /// <summary>
    /// Borrows an immutable Derived owner of a mixed library. Reads attach that owner's membership before returning a hit.
    /// Disposal drains accepted calls and never closes the library; callers keep the library open until this facade is drained.
    /// Pack size, commit timing and other library settings remain those of the library's opener.
    /// Delete, compaction and statistics are unsupported; preserve the library and its authoritative ownership metadata.
    /// </summary>
    public static GeneratedStore Borrow(LibraryObjectOwner owner, GeneratedStoreOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.Role != LibraryObjectRole.Derived) throw new ArgumentException("Generated objects require a Derived library owner.", nameof(owner));
        return new(owner, options ?? new());
    }

    /// <summary>
    /// Opens (or creates) the store at <paramref name="folder"/> (an absolute path) as its one writer, recovering anything past the last
    /// commit. A folder holding another kind of store, or a newer format, throws <see cref="GeneratedStoreCodes.Unusable"/>; one another
    /// writer holds throws <see cref="GeneratedStoreCodes.Busy"/>.
    /// </summary>
    public static async Task<GeneratedStore> OpenAsync(string folder, GeneratedStoreOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        options ??= new();
        var objectOptions = new ObjectStoreOptions
        {
            Kind = ObjectStoreKind.Generated,
            MaxPackBytes = options.MaxPackBytes,
            CommitObjects = options.CommitObjects,
            CommitBytes = options.CommitBytes,
            CommitDelay = options.CommitDelay,
            Time = options.Time,
        };
        try { return new GeneratedStore(await ObjectStore.OpenAsync(folder, objectOptions, cancellationToken), options); }
        catch (ObjectStoreException e) { throw Map(e); }
    }

    public async ValueTask<bool> HasAsync(GeneratedKey key, CancellationToken cancellationToken = default)
    {
        using var call = borrowed?.Enter();
        cancellationToken.ThrowIfCancellationRequested();
        try { return await reader.HasAsync(ToObjectKey(key), cancellationToken); }
        catch (ObjectStoreException e) { throw Map(e, borrowed is not null); }
    }

    public async ValueTask<GeneratedRead> ReadAsync(GeneratedKey key, CancellationToken cancellationToken = default)
    {
        using var call = borrowed?.Enter();
        cancellationToken.ThrowIfCancellationRequested();
        ObjectRead read;
        try { read = await reader.ReadAsync(ToObjectKey(key), cancellationToken); }
        catch (ObjectStoreException e) { throw Map(e, borrowed is not null); }
        return read.State switch
        {
            ReadState.Present => new(GeneratedReadState.Present, read.Bytes!.Length, read.Bytes),
            ReadState.Absent => new(GeneratedReadState.Absent),
            _ => new(GeneratedReadState.Corrupt, Problem: Problem(read.State, read.Problem)),
        };
    }

    public async ValueTask<GeneratedRangeRead> ReadRangeAsync(GeneratedKey key, long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        using var call = borrowed?.Enter();
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        // No object is larger, so a longer range is past any object's end.
        if (destination.Length > GeneratedLimits.MaxObjectBytes) throw new ArgumentOutOfRangeException(nameof(destination), "The range is past the object's end.");
        cancellationToken.ThrowIfCancellationRequested();
        RangeRead read;
        try { read = await reader.ReadRangeAsync(ToObjectKey(key), offset, destination, cancellationToken); }
        catch (ObjectStoreException e) { throw Map(e, borrowed is not null); }
        return read.State switch
        {
            ReadState.Present => new(GeneratedReadState.Present, read.Info!.Length),
            ReadState.Absent => new(GeneratedReadState.Absent),
            _ => new(GeneratedReadState.Corrupt, Problem: Problem(read.State, read.Problem)),
        };
    }

    public async Task<IReadOnlyList<GeneratedPutOutcome>> PutAsync(IReadOnlyList<GeneratedPut> batch, CancellationToken cancellationToken = default)
    {
        using var call = borrowed?.Enter();
        ArgumentNullException.ThrowIfNull(batch);
        cancellationToken.ThrowIfCancellationRequested();
        if (batch.Count > GeneratedLimits.MaxBatch) throw new GeneratedStoreException(GeneratedStoreCodes.Limit, "Too many objects in one Put.");
        foreach (var put in batch)
        {
            ArgumentNullException.ThrowIfNull(put, nameof(batch));
            if (put.Bytes.Length is 0 or > GeneratedLimits.MaxObjectBytes) throw new GeneratedStoreException(GeneratedStoreCodes.Limit, "An object is empty or over 64 MiB.");
        }
        var outcomes = new GeneratedPutOutcome[batch.Count];
        for (var start = 0; start < batch.Count;)
        {
            // Layer 1 takes at most 256 MiB per Put; each commit holds at least one object, and every object fits alone.
            var end = start; long bytes = 0;
            while (end < batch.Count && (end == start || bytes + batch[end].Bytes.Length <= options.MaxCommitBytes)) bytes += batch[end++].Bytes.Length;
            var objects = new PutObject[end - start];
            for (var i = start; i < end; i++) objects[i - start] = new PutObject(ToObjectKey(batch[i].Key), batch[i].Bytes.Length, batch[i].Bytes, ObjectOrigin.Unknown);
            IReadOnlyList<PutResult> results;
            try { results = await writer.PutAsync(objects, commitNow: true, cancellationToken); }
            catch (ObjectStoreException e) { throw Map(e, borrowed is not null); }
            options.AfterCommit?.Invoke(results.Count);
            for (var i = 0; i < results.Count; i++)
                outcomes[start + i] = results[i].Outcome switch
                {
                    PutOutcome.Stored => GeneratedPutOutcome.Stored,
                    PutOutcome.AlreadyPresent => GeneratedPutOutcome.AlreadyPresent,
                    // Objects stored before the cancellation stay stored; a retry sees them AlreadyPresent.
                    PutOutcome.Canceled => throw new OperationCanceledException(cancellationToken),
                    _ => throw new GeneratedStoreException(GeneratedStoreCodes.Limit, $"The object store refused an object: {results[i].Message} ({results[i].Code})."),
                };
            start = end;
        }
        return outcomes;
    }

    public async Task DeleteAsync(IReadOnlyList<GeneratedKey> keys, CancellationToken cancellationToken = default)
    {
        using var call = borrowed?.Enter();
        ArgumentNullException.ThrowIfNull(keys);
        cancellationToken.ThrowIfCancellationRequested();
        if (borrowed is not null) throw LibraryUnsupported();
        for (var start = 0; start < keys.Count; start += GeneratedLimits.MaxBatch)
        {
            var part = keys.Skip(start).Take(GeneratedLimits.MaxBatch).Select(ToObjectKey).ToArray();
            try { await store!.DeleteAsync(part, cancellationToken); }
            catch (ObjectStoreException e) { throw Map(e, borrowed is not null); }
        }
    }

    /// <summary>What a standalone index holds as of the last commit. Borrowed facades refuse statistics until owner-scoped accounting exists.</summary>
    public async ValueTask<GeneratedStoreStats> StatsAsync(CancellationToken cancellationToken = default)
    {
        using var call = borrowed?.Enter();
        cancellationToken.ThrowIfCancellationRequested();
        if (borrowed is not null) throw LibraryUnsupported();
        try
        {
            var s = await store!.StatsAsync(cancellationToken);
            return new(s.Packs, s.Objects, s.PackBytes, s.LiveBytes, s.CreatedAt);
        }
        catch (ObjectStoreException e) { throw Map(e, borrowed is not null); }
    }

    /// <summary>Rewrites up to <paramref name="maxPacks"/> standalone sealed packs whose dead share is at least <paramref name="minDeadFraction"/>. Borrowed facades refuse compaction.</summary>
    public async Task<GeneratedCompactReport> CompactAsync(double minDeadFraction = 0.25, int maxPacks = 1, CancellationToken cancellationToken = default)
    {
        using var call = borrowed?.Enter();
        cancellationToken.ThrowIfCancellationRequested();
        if (borrowed is not null) throw LibraryUnsupported();
        try
        {
            var r = await store!.CompactAsync(new CompactOptions(minDeadFraction, maxPacks), cancellationToken);
            return new(r.PacksCompacted, r.BytesBefore, r.BytesAfter, r.Moved, r.SkippedCorrupt);
        }
        catch (ObjectStoreException e) { throw Map(e, borrowed is not null); }
    }

    public ValueTask DisposeAsync() => borrowed is null ? store!.DisposeAsync() : new(borrowed.CloseAsync());

    private static GeneratedStoreException LibraryUnsupported() => new(GeneratedStoreCodes.LibraryOperationUnsupported,
        "This operation is unsupported by a borrowed generated facade. Preserve the shared library and its authoritative ownership metadata.");

    // Admission is synchronous, before the first await. Closing fences new calls while accepted ones finish through their last commit.
    // Only one drain completion is retained; there is no per-call task, queue or worker.
    private sealed class BorrowedLifetime
    {
        private readonly object gate = new();
        private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool closing;
        private int active;

        public IDisposable Enter()
        {
            lock (gate)
            {
                if (closing) throw new GeneratedStoreException(GeneratedStoreCodes.Closed, "The borrowed generated facade is closed.");
                active++;
                return new Call(this);
            }
        }

        public Task CloseAsync()
        {
            lock (gate)
            {
                closing = true;
                if (active == 0) drained.TrySetResult();
                return drained.Task;
            }
        }

        private sealed class Call(BorrowedLifetime lifetime) : IDisposable
        {
            public void Dispose()
            {
                lock (lifetime.gate)
                {
                    lifetime.active--;
                    if (lifetime.closing && lifetime.active == 0) lifetime.drained.TrySetResult();
                }
            }
        }
    }

    private static ObjectKey ToObjectKey(GeneratedKey key)
    {
        Span<byte> bytes = stackalloc byte[GeneratedKey.Bytes];
        key.WriteTo(bytes);
        return new ObjectKey(ObjectKeyType.Generated, bytes);
    }

    private static string Problem(ReadState state, string? problem) =>
        state == ReadState.Lost ? $"The object's pack is gone: {problem}." : $"The object's bytes failed a check: {problem}.";

    private static GeneratedStoreException Map(ObjectStoreException e, bool borrowed = false) => new(e.Code switch
    {
        ObjectStoreCodes.DiskFull => GeneratedStoreCodes.DiskFull,
        ObjectStoreCodes.WriteFailed => GeneratedStoreCodes.WriteFailed,
        ObjectStoreCodes.Closed => GeneratedStoreCodes.Closed,
        ObjectStoreCodes.Limit => GeneratedStoreCodes.Limit,
        ObjectStoreCodes.Busy => GeneratedStoreCodes.Busy,
        ObjectStoreCodes.LibraryOperationUnsupported => GeneratedStoreCodes.LibraryOperationUnsupported,
        // NewerFormat, ForeignPack, KindMismatch, an index the writer couldn't rebuild, and anything else.
        _ => GeneratedStoreCodes.Unusable,
    }, borrowed ? $"{e.Message} Preserve the shared library and its authoritative ownership metadata." : e.Message, e);
}
