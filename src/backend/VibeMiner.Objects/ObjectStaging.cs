using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace VibeMiner.Objects;

/// <summary>
/// One large encoded object (up to 512 MiB) arriving in pieces (D124). Each BLTE chunk is checked as soon as its last byte arrives;
/// a bad chunk is cut off and refused. Disposing keeps the staged file: <see cref="ObjectStore.StageAsync"/> on the same key resumes
/// from the last proven chunk. Commit re-reads the file, verifies it again in full and appends it; only then is it visible.
/// </summary>
internal sealed class ObjectStaging : IObjectStaging
{
    private readonly ObjectStore store;
    private readonly LibraryClaim? claim;
    private SafeFileHandle? handle;
    private BlteVerifier verifier;
    private bool broken, finished;

    internal ObjectStaging(ObjectStore store, ObjectKey key, long declared, ObjectOrigin origin, string path, SafeFileHandle handle, BlteVerifier verifier, long length, LibraryClaim? claim)
    {
        this.claim = claim;
        this.store = store; Key = key; DeclaredLength = declared; Origin = origin; Path = path; this.handle = handle; this.verifier = verifier; Length = length;
    }

    public ObjectKey Key { get; }
    public long DeclaredLength { get; }
    public ObjectOrigin Origin { get; }
    internal string Path { get; }
    /// <summary>Bytes staged so far. After a resume, exactly the bytes already proven by chunk checks: fetch from here.</summary>
    public long Length { get; private set; }

    /// <summary>Appends the next bytes. A failed chunk check truncates to the last proven chunk and throws; resume with a new StageAsync.</summary>
    public async Task AppendAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(handle is null || finished, this);
        if (broken) throw new ObjectStoreException(ObjectStoreCodes.StagingRejected, "This staging was refused; start it again to resume from the proven bytes.");
        if (Length + bytes.Length > DeclaredLength) throw new ArgumentException("More bytes than the declared length.", nameof(bytes));
        await RandomAccess.WriteAsync(handle, bytes, Length, ct);
        store.At(StorePoint.StagingAppended);
        try { verifier.Append(bytes.Span); Length += bytes.Length; }
        catch (VerificationFailure f)
        {
            broken = true;
            Length = verifier.VerifiedPrefix;
            RandomAccess.SetLength(handle, Length);
            throw new ObjectStoreException(ObjectStoreCodes.StagingRejected, $"Staged bytes for {Key} were refused ({f.Code}): {f.Message}");
        }
    }

    /// <summary>Verifies the complete object again, appends and commits it, and removes the staged file. Refused bytes are removed too.</summary>
    public async Task<PutResult> CommitAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(handle is null || finished, this);
        if (broken || Length != DeclaredLength) throw new InvalidOperationException("Only a complete, unrefused staging can be committed.");
        ct.ThrowIfCancellationRequested();
        try { verifier.Complete(); }
        catch (VerificationFailure f) { await AbandonAsync(); return new(Key, PutOutcome.Refused, f.Code, f.Message); }
        handle.Dispose(); handle = null;
        try
        {
            var result = await store.CommitStagedAsync(Key, Path, DeclaredLength, Origin, claim, ct);
            if (result.Outcome != PutOutcome.Canceled) store.DeleteStagingFile(this);
            return result;
        }
        finally { finished = true; store.ReleaseStaging(this); verifier.Dispose(); }
    }

    /// <summary>Removes this active staging's unpublished bytes. A finished handle never touches a path another staging may have resumed.</summary>
    public ValueTask AbandonAsync()
    {
        if (finished || handle is null) return ValueTask.CompletedTask;
        handle.Dispose(); handle = null;
        try { store.DeleteStagingFile(this); }
        finally
        {
            finished = true;
            store.ReleaseStaging(this);
            verifier.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>Closes the staged file and keeps it for a later resume.</summary>
    public ValueTask DisposeAsync()
    {
        handle?.Dispose(); handle = null;
        if (!finished) { finished = true; store.ReleaseStaging(this); }
        verifier.Dispose();
        return ValueTask.CompletedTask;
    }
}

public sealed partial class ObjectStore
{
    private readonly ConcurrentDictionary<ObjectKey, ObjectStaging> stagings = new();
    private long stagingBytes;
    private int stagingOpens;
    private TaskCompletionSource? stagingOpensDrained;

    /// <summary>
    /// Starts or resumes staging an encoded object larger than a Put can hold. At most <see cref="ObjectStoreOptions.MaxStagings"/>
    /// at once and <see cref="ObjectStoreOptions.MaxStagingBytes"/> declared in all; one staging per key.
    /// </summary>
    public async Task<IObjectStaging> StageAsync(ObjectKey key, long declaredLength, ObjectOrigin origin = ObjectOrigin.Cdn, CancellationToken ct = default)
    {
        EnsureWriter();
        if (Kind == ObjectStoreKind.Library) throw LibraryUnsupported();
        return await StageOwnedAsync(null, key, declaredLength, origin, ct);
    }

    internal async Task<IObjectStaging> StageOwnedAsync(LibraryClaim? claim, ObjectKey key, long declaredLength, ObjectOrigin origin, CancellationToken ct)
    {
        EnsureWriter();
        ct.ThrowIfCancellationRequested();
        if (Kind == ObjectStoreKind.Library && claim is null) throw LibraryUnsupported();
        if (Kind == ObjectStoreKind.Generated) throw new ArgumentException("A generated store has no staging; Put objects whole.", nameof(key));
        if (!Enum.IsDefined(origin)) throw new ArgumentOutOfRangeException(nameof(origin));
        if (key.Type != ObjectKeyType.Encoded) throw new ArgumentException("Only encoded objects are staged; Put the others whole.", nameof(key));
        if (ObjectFormats.Refusal(key, declaredLength) is { } refusal) throw new ArgumentException($"Can't stage this object ({refusal}).", nameof(key));
        lock (stagings)
        {
            EnsureWriter();
            if (stagings.ContainsKey(key)) throw new InvalidOperationException($"{key} is already being staged.");
            if (stagings.Count >= options.MaxStagings || stagingBytes + declaredLength > options.MaxStagingBytes)
                throw new ObjectStoreException(ObjectStoreCodes.Limit, "The staging budget is in use; finish or dispose another staging first.");
            stagingBytes += declaredLength;
            stagingOpens++;
            stagings[key] = null!;  // reserved
        }
        try
        {
            var path = Path.Combine(stagingDirectory, $"{(byte)key.Type:x2}{key.Namespace:x2}{key.KeyHex}.part");
            var h = File.OpenHandle(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, FileOptions.Asynchronous);
            BlteVerifier? verifier = null;
            try
            {
                var length = RandomAccess.GetLength(h);
                // Trust chunk-proven bytes, or a complete library handoff reverified against its whole key below.
                // Other trailing bytes may be unsynced garbage; partial headerless data has no resume proof.
                verifier = new BlteVerifier(key, declaredLength);
                long keep = 0;
                if (length > 0 && length <= declaredLength)
                {
                    await FeedAsync(h, verifier, length, ct);
                    keep = verifier.VerifiedPrefix;
                    if (Kind == ObjectStoreKind.Library && length == declaredLength)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            verifier.Complete();
                            keep = length;
                        }
                        catch (VerificationFailure) { /* retain only the independently proven chunks, if any */ }
                    }
                    verifier.Dispose();
                    // Complete consumes the headerless digest. Rebuild it for normal append/commit validation.
                    verifier = new BlteVerifier(key, declaredLength);
                    await FeedAsync(h, verifier, keep, ct);
                }
                if (keep != length) RandomAccess.SetLength(h, keep);
                var staging = new ObjectStaging(this, key, declaredLength, origin, path, h, verifier, keep, claim);
                lock (stagings) { EnsureWriter(); stagings[key] = staging; }
                verifier = null; // transferred to the returned staging
                return staging;
            }
            catch { verifier?.Dispose(); h.Dispose(); throw; }
        }
        catch { lock (stagings) { stagings.TryRemove(key, out _); stagingBytes -= declaredLength; } throw; }
        finally
        {
            lock (stagings)
            {
                stagingOpens--;
                if (stagingOpens == 0) stagingOpensDrained?.TrySetResult();
            }
        }
    }

    private Task DrainStagingOpensAsync()
    {
        lock (stagings)
        {
            if (stagingOpens == 0) return Task.CompletedTask;
            stagingOpensDrained ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            return stagingOpensDrained.Task;
        }
    }

    /// <summary>Feeds the first <paramref name="upTo"/> staged bytes to a verifier; stops quietly at the first failed check.</summary>
    private async Task FeedAsync(SafeFileHandle h, BlteVerifier verifier, long upTo, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        try
        {
            for (long at = 0; at < upTo;)
            {
                ct.ThrowIfCancellationRequested();
                if (Kind == ObjectStoreKind.Library) At(StorePoint.StagingResumeRead);
                ct.ThrowIfCancellationRequested();
                var n = (int)Math.Min(1 << 20, upTo - at);
                if (await PackFiles.ReadFullyAsync(h, buffer.AsMemory(0, n), at, ct) != n) return;
                try { verifier.Append(buffer.AsSpan(0, n)); } catch (VerificationFailure) { return; }
                at += n;
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    internal void DeleteStagingFile(ObjectStaging staging)
    {
        lock (stagings)
        {
            // Close can release a staging while its caller's commit continuation is still queued. A new store/session may
            // already have resumed this path: only the instance holding this store's current reservation may remove it.
            if (stagings.TryGetValue(staging.Key, out var current) && ReferenceEquals(current, staging)) File.Delete(staging.Path);
        }
    }

    internal void ReleaseStaging(ObjectStaging staging)
    {
        lock (stagings) { if (stagings.TryRemove(new KeyValuePair<ObjectKey, ObjectStaging>(staging.Key, staging))) stagingBytes -= staging.DeclaredLength; }
    }

    /// <summary>Copies a staged file into the pack in one pass that verifies the key type, hashes and builds the block table.</summary>
    private async Task ProcessStagedAsync(StagedRequest r)
    {
        if (r.Token.IsCancellationRequested) { r.Result = new(r.Key, PutOutcome.Canceled); return; }
        if (Present(r.Key))
        {
            if (r.Claim is not null)
            {
                // A different request may only have appended this key. Publish that value first, then durably attach this owner.
                if (pending.Count > 0) await CommitAsync();
                At(StorePoint.StagedBeforePublish);
                if (r.Token.IsCancellationRequested) { r.Result = new(r.Key, PutOutcome.Canceled); return; }
                Durably(tx => LibraryOwnership.Attach(wconn!, tx, r.Claim, r.Key));
            }
            r.Result = new(r.Key, PutOutcome.AlreadyPresent);
            return;
        }
        var size = PackFormat.EntryBytes(r.Length);
        await EnsureRoomAsync(size);
        var offset = activeLength;
        var payloadAt = offset + PackFormat.EntryHeaderBytes;
        var table = new byte[PackFormat.TableBytes(r.Length)];
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var verifier = ObjectVerifier.For(r.Key, r.Length);
        var buffer = ArrayPool<byte>.Shared.Rent(1 << 20);
        byte[] digest;
        try
        {
            r.Token.ThrowIfCancellationRequested();
            At(StorePoint.EntryStarted);
            SafeFileHandle source;
            try { source = File.OpenHandle(r.Path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous | FileOptions.SequentialScan); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new VerificationFailure(ObjectStoreCodes.StagingRejected, $"The staged file can't be read: {e.Message}"); }
            using var sourceLease = source;
            if (RandomAccess.GetLength(source) != r.Length) throw new VerificationFailure(ObjectRefusalCodes.SizeMismatch, "The staged file changed length.");
            for (long at = 0; at < r.Length;)
            {
                r.Token.ThrowIfCancellationRequested();
                var n = (int)Math.Min(1 << 20, r.Length - at);
                int got;
                try { got = await PackFiles.ReadFullyAsync(source, buffer.AsMemory(0, n), at, r.Token); }
                catch (IOException e) { throw new VerificationFailure(ObjectStoreCodes.StagingRejected, $"The staged file can't be read: {e.Message}"); }
                if (got != n) throw new VerificationFailure(ObjectRefusalCodes.SizeMismatch, "The staged file is short.");
                var span = buffer.AsSpan(0, n);
                verifier.Append(span);
                sha.AppendData(span);
                for (var b = 0; b < n; b += PackFormat.BlockBytes)
                    BinaryPrimitives.WriteUInt32LittleEndian(table.AsSpan((int)((at + b) >> PackFormat.BlockShift) * 4), PackFormat.Crc32C(span.Slice(b, Math.Min(PackFormat.BlockBytes, n - b))));
                await RandomAccess.WriteAsync(active!, buffer.AsMemory(0, n), payloadAt + at, r.Token);
                if (at == 0) At(StorePoint.EntryHalfWritten);
                at += n;
            }
            r.Token.ThrowIfCancellationRequested();
            verifier.Complete();
            digest = sha.GetHashAndReset();
            var header = new byte[PackFormat.EntryHeaderBytes];
            PackFormat.WriteEntryHeader(header, new EntryHeader(r.Key, r.Length, digest, NowMs, r.Origin, nextSeq, false));
            await RandomAccess.WriteAsync(active!, table, payloadAt + r.Length, r.Token);
            await RandomAccess.WriteAsync(active!, header, offset, r.Token);
            At(StorePoint.EntryWritten);
            if (r.Claim is not null) At(StorePoint.StagedBeforePublish);
            r.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (r.Claim is not null && r.Token.IsCancellationRequested)
        {
            // activeLength still names this copy's start, including any earlier pending entries. Never cut back to committedLength.
            RandomAccess.SetLength(active!, offset);
            r.Result = new(r.Key, PutOutcome.Canceled);
            return;
        }
        catch (VerificationFailure f)
        {
            RandomAccess.SetLength(active!, offset);
            try { File.Delete(r.Path); } catch (IOException) { /* an unreadable staged file may not delete either; a later open clears old ones */ }
            r.Result = new(r.Key, PutOutcome.Refused, f.Code, f.Message);
            return;
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        // The publication boundary: from this point cancellation cannot describe a committed value as rolled back.
        nextSeq++;
        activeLength += size;
        AddPending(new Pending { Key = r.Key, Offset = offset, Length = r.Length, Sha256 = digest, Owner = r, Claim = r.Claim }, size);
        r.Result = new(r.Key, PutOutcome.Stored);
        await CommitAsync();
    }
}
