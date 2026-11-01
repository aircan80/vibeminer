using System.Security.Cryptography;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Win32.SafeHandles;

namespace VibeMiner.Objects;

public sealed partial class ObjectStore
{
    private abstract class Request
    {
        public readonly TaskCompletionSource Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Outstanding;
        public bool Processed;
        public void TryFinish() { if (Processed && Outstanding == 0) Done.TrySetResult(); }
    }
    private sealed record Prepared(ObjectKey Key, ReadOnlyMemory<byte> Bytes, byte[] Sha256, byte[] Table, ObjectOrigin Origin, ObjectKey? Document);
    private sealed class PutRequest(Prepared?[] items, PutResult[] results, bool commitNow, CancellationToken ct) : Request
    {
        public readonly Prepared?[] Items = items; public readonly PutResult[] Results = results; public readonly bool CommitNow = commitNow; public readonly CancellationToken Token = ct;
    }
    private sealed class DeleteRequest(ObjectKey[] keys, DeleteResult[] results) : Request
    {
        public readonly ObjectKey[] Keys = keys; public readonly DeleteResult[] Results = results;
    }
    private sealed class StagedRequest(ObjectKey key, string path, long length, ObjectOrigin origin) : Request
    {
        public readonly ObjectKey Key = key; public readonly string Path = path; public readonly long Length = length; public readonly ObjectOrigin Origin = origin;
        public PutResult? Result;
    }
    private sealed class MaintenanceRequest(Func<Task<object?>> work) : Request
    {
        public readonly Func<Task<object?>> Work = work; public object? Value;
    }
    private sealed class Pending
    {
        public ObjectKey Key; public long Offset, Length; public byte[] Sha256 = []; public bool Tombstone; public ObjectOrigin Origin;
        public IndexRow? Removed; public HistoryRecordFormat.Parsed? History; public Request? Owner;
    }

    private FileStream? writerLock;
    private SqliteConnection? wconn;
    private SqliteCommand? wlookup;
    private SafeFileHandle? active;
    private int activeSeq, nextPackSeq = 1;
    private long activeLength, committedLength;
    private ulong nextSeq = 1;
    private readonly List<Pending> pending = [];
    private readonly Dictionary<ObjectKey, bool> pendingKeys = [];
    private long pendingBytes, firstPendingMs;
    private Channel<Request>? requests;
    private Task? loop;
    private ObjectStoreException? fault;
    private readonly SemaphoreSlim verifyGate = new(Math.Max(1, Environment.ProcessorCount / 2));
    private readonly List<string> problems = [];
    private Request? current;
    private StoreTotals totals;
    private volatile bool killed;

    // ---------------------------------------------------------------- open and recovery

    private async Task OpenWriterAsync(CancellationToken ct)
    {
        System.IO.Directory.CreateDirectory(packsDirectory);
        System.IO.Directory.CreateDirectory(stagingDirectory);
        try { writerLock = new FileStream(Path.Combine(directory, LockFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException e) { throw new ObjectStoreException(ObjectStoreCodes.Busy, "Another VibeMiner process is writing this object store.", e); }

        // Refuse anything newer before writing a byte.
        int? version = null; var unreadable = false;
        try { version = ObjectIndex.ReadVersion(indexPath); }
        catch (Exception e) when (e is SqliteException or OverflowException or InvalidCastException or FormatException) { unreadable = true; problems.Add("the object index can't be opened; rebuilt from the packs"); }
        // A flipped user_version on a format 1 index is damage, rebuilt like any other; anything else above 1 is a newer index.
        var damagedVersion = version is { } v && ObjectIndex.IsDamagedVersion(indexPath, v);
        if (damagedVersion) { unreadable = true; problems.Add($"the index's schema version reads {version}; rebuilt from the packs"); }
        else if (version > ObjectIndex.SchemaVersion) throw NewerFormat($"index schema {version}");
        if (version == ObjectIndex.SchemaVersion || damagedVersion) CheckIndexKind();  // an index never committed (user_version 0) records no kind
        var disk = DiskPacks().OrderBy(p => p.seq).ToList();
        var valid = new List<(int seq, string path)>();
        foreach (var (seq, path) in disk)
        {
            var check = CheckPackHeader(path, seq, null, Kind);
            if (check == PackFormat.HeaderCheck.Newer) throw NewerFormat($"pack {seq}");
            if (check == PackFormat.HeaderCheck.Valid) valid.Add((seq, path));
        }
        nextPackSeq = disk.Count == 0 ? 1 : disk[^1].seq + 1;
        var packStore = OnePackStore(valid);
        // A damaged index (D126) is rebuilt from the packs, never trusted. Every open runs the cheap check (schema, store and pack
        // rows with their CRCs); the full one (integrity_check, totals over every row) only when the last writer didn't close cleanly.
        var fullCheck = false;
        if (!unreadable && version == ObjectIndex.SchemaVersion)
        {
            var (indexStore, problem, clean, _) = ObjectIndex.Validate(indexPath, full: false, ct);
            // The lock is held, so nothing else commits: the full check always settles here.
            if (problem is null && !clean) { fullCheck = true; (indexStore, problem, _, _) = ObjectIndex.Validate(indexPath, full: true, ct); }
            if (problem is not null) { unreadable = true; problems.Add($"{problem}; rebuilt from the packs"); }
            else if (packStore is not null && !packStore.AsSpan().SequenceEqual(indexStore)) { unreadable = true; problems.Add("the index names another store than its packs; rebuilt from the packs"); }
        }

        var rebuilt = false;
        if (unreadable || version is null or < 1)
        {
            if (valid.Count == 0)
            {
                foreach (var f in new[] { indexPath, indexPath + "-wal", indexPath + "-shm" }) File.Delete(f);
                foreach (var (seq, path) in disk)
                {
                    if (new FileInfo(path).Length <= PackFormat.PackHeaderBytes) File.Delete(path);
                    else problems.Add($"pack {seq} has no readable header; left alone and unused");
                }
                storeId = RandomNumberGenerator.GetBytes(16);
                using var c = ObjectIndex.Open(indexPath, readOnly: false);
                ObjectIndex.Create(c, storeId, NowMs, 1, Kind);
            }
            else
            {
                await RebuildIndexAsync(ct);
                rebuilt = true;
            }
        }
        if (!rebuilt)
        {
            await RefuseNewerTailAsync(ct);
            foreach (var leftover in new[] { RebuildPath, RebuildPath + "-wal", RebuildPath + "-shm" }) File.Delete(leftover);  // an open-time rebuild cut short
        }
        try
        {
            OpenWriterConnection();
            storeId = ReadStoreId(wconn!);
            // Unset the clean-close marker first, durably: from here on a crash leaves it unset, so the next open runs the full check.
            SetCleanClose(false);
            await LoadAndRecoverAsync(disk, ct);
        }
        catch (Exception e) when (!rebuilt && valid.Count > 0 && e is SqliteException or InvalidCastException or InvalidOperationException or FormatException or OverflowException)
        {
            // Damage the checks didn't see shows up in use: start again from the packs, once.
            problems.Add($"the object index failed while opening ({e.Message}); rebuilt from the packs");
            wlookup?.Dispose(); wlookup = null; wconn?.Dispose(); wconn = null; active?.Dispose(); active = null;
            pending.Clear(); pendingKeys.Clear(); pendingBytes = 0;
            SqliteConnection.ClearAllPools();
            await RebuildIndexAsync(ct);
            rebuilt = true;
            OpenWriterConnection();
            storeId = ReadStoreId(wconn!);
            SetCleanClose(false);
            await LoadAndRecoverAsync(disk, ct);
        }
        CleanStaging();
        readers = new IndexReaders(indexPath, options.ReadConnections);
        requests = Channel.CreateBounded<Request>(new BoundedChannelOptions(64) { SingleReader = true });
        loop = Task.Run(RunAsync, CancellationToken.None);
        OpenReport = OpenReport with { IndexRebuilt = rebuilt, FullIndexCheck = fullCheck, Problems = problems.ToArray() };
    }

    private string RebuildPath => indexPath + ".rebuild";

    /// <summary>The one store id every readable pack carries, or null with no packs; packs from two stores are refused.</summary>
    private static byte[]? OnePackStore(List<(int seq, string path)> valid)
    {
        byte[]? id = null;
        foreach (var (seq, path) in valid)
        {
            var packId = ReadPackStoreId(path);
            if (id is null) id = packId;
            else if (!packId.AsSpan().SequenceEqual(id)) throw new ObjectStoreException(ObjectStoreCodes.ForeignPack, $"Pack {seq} belongs to another object store.");
        }
        return id;
    }

    /// <summary>A newer writer's entries past the last commit are refused before anything is changed, on a read-only connection.</summary>
    private async Task RefuseNewerTailAsync(CancellationToken ct)
    {
        var active = new List<(int seq, long committed)>();
        using (var c = ObjectIndex.Open(indexPath, readOnly: true))
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT seq, committed_length FROM packs WHERE state = $s";
            cmd.Parameters.AddWithValue("$s", (int)PackState.Active);
            using var r = cmd.ExecuteReader();
            while (r.Read()) active.Add((r.GetInt32(0), r.GetInt64(1)));
        }
        foreach (var (seq, committed) in active)
        {
            var path = PackPath(seq);
            if (!File.Exists(path) || new FileInfo(path).Length <= committed) continue;
            using var h = PackFiles.OpenRead(path);
            var walker = new PackWalker(h, seq, committed, RandomAccess.GetLength(h), Kind);
            WalkStatus status;
            while ((status = await walker.NextAsync(ct)) == WalkStatus.Entry) { }
            if (status == WalkStatus.Newer) throw NewerFormat($"entry at {walker.Offset} in pack {seq}");
        }
    }

    private void OpenWriterConnection()
    {
        wconn = ObjectIndex.Open(indexPath, readOnly: false);
        wlookup = ObjectIndex.LookupCommand(wconn);
        LoadStoreRow();
    }

    private void LoadStoreRow()
    {
        using var cmd = wconn!.CreateCommand();
        cmd.CommandText = "SELECT next_seq, object_count, object_digest, history_count, history_digest FROM store WHERE id = 1";
        using var r = cmd.ExecuteReader();
        if (!r.Read()) throw new InvalidOperationException("The object index has no store row.");
        nextSeq = (ulong)r.GetInt64(0);
        totals = new(r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4));
    }

    private async Task LoadAndRecoverAsync(List<(int seq, string path)> disk, CancellationToken ct)
    {
        var rows = new Dictionary<int, (PackState state, long committed)>();
        using (var cmd = wconn!.CreateCommand())
        {
            cmd.CommandText = "SELECT seq, state, committed_length FROM packs";
            using var r = cmd.ExecuteReader();
            while (r.Read()) rows[r.GetInt32(0)] = ((PackState)r.GetInt32(1), r.GetInt64(2));
        }
        if (rows.Count > 0) nextPackSeq = Math.Max(nextPackSeq, rows.Keys.Max() + 1);
        // Unfinished compaction targets and retired packs go first; their rows go after their files.
        foreach (var (seq, (state, _)) in rows.Where(r => r.Value.state is PackState.Compacting or PackState.Retired).ToList())
        {
            File.Delete(PackPath(seq)); DirectorySync.Sync(packsDirectory);
            ExecWriter("DELETE FROM packs WHERE seq = $s", ("$s", seq));
            rows.Remove(seq);
        }
        foreach (var (seq, path) in disk)
        {
            if (rows.ContainsKey(seq) || !File.Exists(path)) continue;
            // A pack is listed before anything is appended to it, so an unlisted file holds at most its own header.
            if (new FileInfo(path).Length <= PackFormat.PackHeaderBytes) { File.Delete(path); continue; }
            problems.Add($"pack {seq} is not listed in the index; left alone and unused");
        }
        foreach (var (seq, (state, committed)) in rows)
        {
            var path = PackPath(seq);
            if (!File.Exists(path)) { problems.Add($"pack {seq} is missing; its objects read as lost"); if (state == PackState.Active) SealRow(seq); continue; }
            var length = new FileInfo(path).Length;
            if (state == PackState.Sealed)
            {
                if (length < committed) problems.Add($"pack {seq} is shorter than its committed length; objects past its end read as corrupt");
                else if (length > committed) problems.Add($"sealed pack {seq} has {length - committed} bytes past its committed length; left alone");
                continue;
            }
            if (length < committed) { problems.Add($"pack {seq} is shorter than its committed length; sealed, objects past its end read as corrupt"); SealRow(seq); continue; }
            activeSeq = seq; activeLength = committedLength = committed;
            active = PackFiles.OpenWrite(path, FileMode.Open);
            if (length > committed) await RecoverTailAsync(length, ct);
        }
    }

    /// <summary>
    /// Entries past the last commit: each is checked again (header, SHA-256, blocks and its key type) and indexed in store order,
    /// keeping the first copy of a key. An entry whose header is sound but whose bytes fail is skipped, not trusted; the pack ends
    /// after the last sound entry, so only bytes that nothing valid follows (a torn append) are cut off.
    /// </summary>
    private async Task RecoverTailAsync(long length, CancellationToken ct)
    {
        using var read = PackFiles.OpenRead(PackPath(activeSeq));
        var walker = new PackWalker(read, activeSeq, committedLength, length, Kind);
        var recovered = 0; var end = committedLength;
        var damaged = new List<string>();
        // Keys whose first copy in the tail recovery refused. Scan doesn't run key checks, so a tombstone after the tail makes it
        // agree: it removes the refused copy, and a sound later copy is appended again after it.
        var refused = new List<ObjectKey>();
        void Refuse(ObjectKey key) { if (!Present(key) && !refused.Contains(key)) refused.Add(key); }
        while (true)
        {
            var status = await walker.NextAsync(ct);
            if (status == WalkStatus.Newer) throw NewerFormat($"entry at {walker.Offset} in pack {activeSeq}");
            if (status != WalkStatus.Entry) break;
            var e = walker.Current;
            ObjectKey? document = null;
            var (check, reason) = await EntryIO.CheckPayloadAsync(read, walker.CurrentOffset, e, keyType: true, null, ct, d => document = d);
            if (check == PayloadCheck.Corrupt) { damaged.Add($"{e.Key} at {walker.CurrentOffset} ({reason})"); continue; }
            // A sound entry after damaged ones: the damage is in the middle (bad media), so nothing before here is cut off.
            foreach (var d in damaged) problems.Add($"pack {activeSeq}: entry {d} is damaged; kept in the pack, not indexed");
            damaged.Clear();
            end = walker.Offset;
            nextSeq = Math.Max(nextSeq, e.Sequence + 1);
            if (check == PayloadCheck.TypeFailed) { problems.Add($"entry {e.Key} at {walker.CurrentOffset} in pack {activeSeq} fails its key check ({reason}); not indexed"); Refuse(e.Key); continue; }
            if (document is { } doc && !Present(doc)) { problems.Add($"history record {e.Key} names a document the store doesn't have; not indexed"); Refuse(e.Key); continue; }
            if (e.Tombstone)
            {
                // Every tombstone is kept by compaction, so each one counts as kept bytes, whether it removes a row or not.
                IndexRow? removed = null;
                if (pendingKeys.TryGetValue(e.Key, out var inTail))
                {
                    if (inTail)
                    {
                        var copy = pending.FindLastIndex(p => p.Key == e.Key && !p.Tombstone);
                        pendingBytes -= PackFormat.EntryBytes(pending[copy].Length); pending.RemoveAt(copy);
                    }
                }
                else removed = LookupWriter(e.Key);
                AddPending(new Pending { Key = e.Key, Offset = walker.CurrentOffset, Tombstone = true, Removed = removed, Sha256 = e.Sha256 }, PackFormat.EntryHeaderBytes);
                recovered++; continue;
            }
            if (Present(e.Key)) continue;  // a later copy of a key the store has: the first copy wins
            var history = e.Key.Type == ObjectKeyType.HistoryRecord ? await EntryIO.ReadHistoryAsync(read, walker.CurrentOffset, e, ct) : null;
            AddPending(new Pending { Key = e.Key, Offset = walker.CurrentOffset, Length = e.Length, Sha256 = e.Sha256, History = history, Origin = e.Origin }, e.EntryBytes);
            recovered++;
        }
        var truncated = length - end;
        if (truncated > 0)
        {
            RandomAccess.SetLength(active!, end); RandomAccess.FlushToDisk(active!);
            problems.Add($"pack {activeSeq}: {truncated} bytes past the last whole entry were never committed; truncated");
        }
        At(StorePoint.RecoveryTruncated);
        activeLength = end;
        foreach (var key in refused)
        {
            var copy = pending.FindIndex(p => p.Key == key && !p.Tombstone);
            Pending? sound = null;
            if (copy >= 0) { sound = pending[copy]; pendingBytes -= PackFormat.EntryBytes(sound.Length); pending.RemoveAt(copy); pendingKeys.Remove(key); }
            await AppendTombstoneAsync(key, null, null);
            if (sound is null) continue;
            // Bounded like a Put: a larger sound copy stays behind the tombstone, so the key reads Absent and is fetched again.
            if (sound.Length > ObjectFormats.MaxInMemoryBytes) { problems.Add($"{key}: a sound copy over 64 MiB followed a refused one; left out, fetch it again"); continue; }
            var bytes = new byte[sound.Length];
            if (await PackFiles.ReadFullyAsync(read, bytes, sound.Offset + PackFormat.EntryHeaderBytes, ct) != bytes.Length)
            { problems.Add($"{key}: its sound copy couldn't be read again; left out, fetch it again"); continue; }
            await AppendAsync(new Prepared(key, bytes, sound.Sha256, PackFormat.BlockTable(bytes), sound.Origin, null), null, sound.History);
        }
        await CommitAsync();
        OpenReport = OpenReport with { RecoveredEntries = recovered, TruncatedBytes = truncated };
    }

    private void CleanStaging()
    {
        var cutoff = options.Time.GetUtcNow() - options.StagingLifetime;
        foreach (var f in System.IO.Directory.EnumerateFiles(stagingDirectory, "*.part"))
            if (File.GetLastWriteTimeUtc(f) < cutoff.UtcDateTime) File.Delete(f);
    }

    // ---------------------------------------------------------------- the public writes

    /// <summary>
    /// Verifies each object against its key type, then appends and commits the batch (D122, D123). Returns once every stored object
    /// is committed, which is at most the commit delay later unless <paramref name="commitNow"/>. Cancellation stops objects not yet
    /// appended; an appended object is committed regardless, and its result still says Stored.
    /// </summary>
    public async Task<IReadOnlyList<PutResult>> PutAsync(IReadOnlyList<PutObject> batch, bool commitNow = false, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        EnsureWriter();
        if (batch.Count > MaxBatchObjects || batch.Sum(o => (long)o.Bytes.Length) > MaxBatchBytes)
            throw new ObjectStoreException(ObjectStoreCodes.Limit, "A Put batch is at most 4,096 objects and 256 MiB.");
        var results = new PutResult[batch.Count];
        var items = new Prepared?[batch.Count];
        var documents = new HashSet<ObjectKey>();
        await verifyGate.WaitAsync(ct);
        try
        {
            for (var i = 0; i < batch.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var o = batch[i];
                if (!Enum.IsDefined(o.Origin)) throw new ArgumentOutOfRangeException(nameof(batch), "Unknown object origin.");
                // The same instance a client can call first (IObjectVerifier), so the two can never disagree.
                if (ObjectVerification.For(Kind).Check(o.Key, o.Bytes.Span, o.DeclaredLength, out var document) is { } refusal)
                { results[i] = new(o.Key, PutOutcome.Refused, refusal.Code, refusal.Message); continue; }
                items[i] = new Prepared(o.Key, o.Bytes, SHA256.HashData(o.Bytes.Span), PackFormat.BlockTable(o.Bytes.Span), o.Origin, document);
            }
        }
        finally { verifyGate.Release(); }
        if (items.All(p => p is null)) return results;
        var request = new PutRequest(items, results, commitNow, ct);
        await Enqueue(request, ct);
        await request.Done.Task;
        return results;
    }

    /// <summary>Stores one version-service observation: its reply document and its dated record, in one batch. A generated store refuses both.</summary>
    public Task<IReadOnlyList<PutResult>> AppendHistoryAsync(HistoryObservation observation, CancellationToken ct = default) =>
        PutAsync(HistoryRecordFormat.Objects(observation), commitNow: true, ct);

    /// <summary>Appends a tombstone for each key and removes it from the index in one commit. History entries can't be deleted.</summary>
    public async Task<IReadOnlyList<DeleteResult>> DeleteAsync(IReadOnlyList<ObjectKey> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        EnsureWriter();
        if (keys.Count > MaxBatchObjects) throw new ObjectStoreException(ObjectStoreCodes.Limit, "A Delete batch is at most 4,096 keys.");
        var request = new DeleteRequest(keys.ToArray(), new DeleteResult[keys.Count]);
        await Enqueue(request, ct);
        await request.Done.Task;
        return request.Results;
    }

    internal async Task<PutResult> CommitStagedAsync(ObjectKey key, string path, long length, ObjectOrigin origin, CancellationToken ct)
    {
        EnsureWriter();
        var request = new StagedRequest(key, path, length, origin);
        await Enqueue(request, ct);
        await request.Done.Task;
        return request.Result!;
    }

    private async Task<T> MaintainAsync<T>(Func<Task<T>> work, CancellationToken ct)
    {
        EnsureWriter();
        var request = new MaintenanceRequest(async () => await work());
        await Enqueue(request, ct);
        await request.Done.Task;
        return (T)request.Value!;
    }

    private async Task Enqueue(Request request, CancellationToken ct)
    {
        try { await requests!.Writer.WriteAsync(request, ct); }
        catch (ChannelClosedException) { throw fault ?? new ObjectStoreException(ObjectStoreCodes.Closed, "The object store is closed."); }
    }

    private void EnsureWriter()
    {
        ThrowIfClosed();
        if (readOnly) throw new ObjectStoreException(ObjectStoreCodes.ReadOnly, "This object store is open read-only.");
        if (fault is not null) throw fault;
    }

    // ---------------------------------------------------------------- the writer loop

    private async Task RunAsync()
    {
        var reader = requests!.Reader;
        try
        {
            // Group commit (D123): a batch closes at 128 objects or 16 MiB, when its oldest entry has waited the commit delay, or as soon
            // as nothing else is queued. Producers wait on their commit, so waiting longer than that only adds latency; the batch
            // grows by itself while a pack sync is running.
            while (true)
            {
                if (pending.Count == 0 && !touches.IsEmpty && !await WaitAsync(reader, TimeSpan.FromSeconds(30)))
                {
                    try { await CommitAsync(); }
                    catch (Exception e) when (e is IOException or SqliteException or UnauthorizedAccessException) { touches.Clear(); Recover(e, null); }
                    continue;
                }
                if (!await reader.WaitToReadAsync()) break;
                while (reader.TryRead(out var request))
                {
                    current = request;
                    try
                    {
                        await ProcessAsync(request);
                        if (pending.Count > 0 && NowMs - firstPendingMs >= options.CommitDelay.TotalMilliseconds) await CommitAsync();
                    }
                    catch (SimulatedCrashException) { throw; }
                    catch (Exception e) when (e is IOException or SqliteException or UnauthorizedAccessException) { Recover(e, request); }
                    catch (Exception e) { request.Done.TrySetException(e); }
                    current = null;
                }
                if (pending.Count > 0)
                {
                    try { await CommitAsync(); }
                    catch (Exception e) when (e is IOException or SqliteException or UnauthorizedAccessException) { Recover(e, null); }
                }
            }
            if (!killed && (pending.Count > 0 || !touches.IsEmpty)) await CommitAsync();
        }
        catch (SimulatedCrashException) { Crash(); }
        catch (Exception e) when (e is IOException or SqliteException or UnauthorizedAccessException) { Recover(e, null); if (fault is null) Fail(Error(e)); }
        catch (Exception e)
        {
            // Anything else is a bug: stop writing and tell every caller, so none of them waits forever.
            var error = new ObjectStoreException(ObjectStoreCodes.WriteFailed, "The object store's writer stopped on an unexpected error.", e);
            var owners = pending.Select(p => p.Owner).Append(current).Where(o => o is not null).Distinct().ToList();
            pending.Clear(); pendingKeys.Clear();
            Fail(error);
            foreach (var o in owners) o!.Done.TrySetException(error);
        }
    }

    /// <summary>True when a request arrived within the timeout; false on timeout. A closed channel counts as arrived.</summary>
    private async Task<bool> WaitAsync(ChannelReader<Request> reader, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout, options.Time);
        try { await reader.WaitToReadAsync(cts.Token); return true; }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { return false; }
    }

    private async Task ProcessAsync(Request request)
    {
        switch (request)
        {
            case PutRequest put: await ProcessPutAsync(put); break;
            case DeleteRequest delete: await ProcessDeleteAsync(delete); break;
            case StagedRequest staged: await ProcessStagedAsync(staged); break;
            case MaintenanceRequest m:
                if (pending.Count > 0) await CommitAsync();
                m.Value = await m.Work();
                break;
        }
        request.Processed = true;
        request.TryFinish();
    }

    private async Task ProcessPutAsync(PutRequest put)
    {
        for (var i = 0; i < put.Items.Length; i++)
        {
            if (put.Items[i] is not { } p) continue;
            if (put.Token.IsCancellationRequested) { put.Results[i] = new(p.Key, PutOutcome.Canceled); continue; }
            if (Present(p.Key)) { put.Results[i] = new(p.Key, PutOutcome.AlreadyPresent); continue; }
            if (p.Document is { } doc && !Present(doc))
            { put.Results[i] = new(p.Key, PutOutcome.Refused, ObjectRefusalCodes.HistoryDocumentMissing, "Put the reply document before (or with) its record."); continue; }
            HistoryRecordFormat.Parsed? history = null;
            if (p.Key.Type == ObjectKeyType.HistoryRecord && HistoryRecordFormat.TryParse(p.Bytes.Span, out var parsed)) history = parsed;
            await AppendAsync(p, put, history);
            put.Results[i] = new(p.Key, PutOutcome.Stored);
            if (pending.Count >= options.CommitObjects || pendingBytes >= options.CommitBytes) await CommitAsync();
        }
        if (put.CommitNow && pending.Count > 0) await CommitAsync();
    }

    private async Task ProcessDeleteAsync(DeleteRequest delete)
    {
        for (var i = 0; i < delete.Keys.Length; i++)
        {
            var key = delete.Keys[i];
            if (key.Type is ObjectKeyType.HistoryDocument or ObjectKeyType.HistoryRecord)
            { delete.Results[i] = new(key, DeleteOutcome.Refused, ObjectRefusalCodes.DeleteRefused); continue; }
            IndexRow? row = null;
            if (pendingKeys.TryGetValue(key, out var present))
            {
                // Deleting an object appended in this same commit window: commit it first so the tombstone has a row to remove.
                if (present) { await CommitAsync(); row = LookupWriter(key); }
            }
            else row = LookupWriter(key);
            if (row is null) { delete.Results[i] = new(key, DeleteOutcome.Absent); continue; }
            await AppendTombstoneAsync(key, row, delete);
            delete.Results[i] = new(key, DeleteOutcome.Deleted);
        }
        if (pending.Count > 0) await CommitAsync();
    }

    /// <summary>Appends a tombstone; <paramref name="removed"/> is the row it takes out of the index, if any.</summary>
    private async Task AppendTombstoneAsync(ObjectKey key, IndexRow? removed, Request? owner)
    {
        await EnsureRoomAsync(PackFormat.EntryHeaderBytes);
        var header = new byte[PackFormat.EntryHeaderBytes];
        var e = new EntryHeader(key, 0, SHA256.HashData([]), NowMs, ObjectOrigin.Unknown, nextSeq++, true);
        PackFormat.WriteEntryHeader(header, e);
        var offset = activeLength;
        At(StorePoint.EntryStarted);
        await RandomAccess.WriteAsync(active!, header, offset);
        At(StorePoint.EntryWritten);
        activeLength += header.Length;
        AddPending(new Pending { Key = key, Offset = offset, Tombstone = true, Removed = removed, Sha256 = e.Sha256, Owner = owner }, header.Length);
    }

    private async Task AppendAsync(Prepared p, Request? owner, HistoryRecordFormat.Parsed? history)
    {
        var size = PackFormat.EntryBytes(p.Bytes.Length);
        await EnsureRoomAsync(size);
        var header = new byte[PackFormat.EntryHeaderBytes];
        PackFormat.WriteEntryHeader(header, new EntryHeader(p.Key, p.Bytes.Length, p.Sha256, NowMs, p.Origin, nextSeq++, false));
        var offset = activeLength;
        At(StorePoint.EntryStarted);
        if (options.Fault is null) await RandomAccess.WriteAsync(active!, [header, p.Bytes, p.Table], offset);
        else
        {
            var half = p.Bytes.Length / 2;
            await RandomAccess.WriteAsync(active!, [header, p.Bytes[..half]], offset);
            At(StorePoint.EntryHalfWritten);
            await RandomAccess.WriteAsync(active!, [p.Bytes[half..], p.Table], offset + header.Length + half);
        }
        At(StorePoint.EntryWritten);
        activeLength += size;
        AddPending(new Pending { Key = p.Key, Offset = offset, Length = p.Bytes.Length, Sha256 = p.Sha256, History = history, Owner = owner }, size);
    }

    private void AddPending(Pending p, long bytes)
    {
        if (pending.Count == 0) firstPendingMs = NowMs;
        pending.Add(p); pendingKeys[p.Key] = !p.Tombstone; pendingBytes += bytes;
        if (p.Owner is { } o) o.Outstanding++;
    }

    private bool Present(ObjectKey key) => pendingKeys.TryGetValue(key, out var present) ? present : LookupWriter(key) is not null;
    private IndexRow? LookupWriter(ObjectKey key) => ObjectIndex.Lookup(wlookup!, key.ToIndexKey());

    /// <summary>Starts a new pack when there is none or the entry wouldn't fit. Everything pending is committed first.</summary>
    private async Task EnsureRoomAsync(long size)
    {
        if (active is not null && (activeLength + size <= options.MaxPackBytes || activeLength == PackFormat.PackHeaderBytes)) return;
        if (pending.Count > 0) await CommitAsync();
        var seq = TakePackNumber();
        var path = PackPath(seq);
        var handle = PackFiles.OpenWrite(path, FileMode.CreateNew);
        try
        {
            var header = new byte[PackFormat.PackHeaderBytes];
            PackFormat.WritePackHeader(header, seq, storeId, NowMs, Kind);
            await RandomAccess.WriteAsync(handle, header, 0);
            At(StorePoint.PackFileWritten);
            RandomAccess.FlushToDisk(handle);
            DirectorySync.Sync(packsDirectory);
            // Durable (D123): a lost roll would leave the new pack unlisted and every commit after it unreachable.
            Durably(tx =>
            {
                if (active is not null) SetPackState(tx, activeSeq, PackState.Sealed);
                InsertPack(tx, seq, PackState.Active, PackFormat.PackHeaderBytes);
            });
            At(StorePoint.PackListed);
        }
        catch (SimulatedCrashException) { handle.Dispose(); throw; }
        catch { handle.Dispose(); throw; }
        active?.Dispose();
        active = handle; activeSeq = seq; activeLength = committedLength = PackFormat.PackHeaderBytes;
    }

    /// <summary>Group commit (D123): fsync the pack, then one index transaction for the whole batch. Nothing is visible before it.</summary>
    private Task CommitAsync()
    {
        if (pending.Count > 0)
        {
            At(StorePoint.BeforePackSync);
            RandomAccess.FlushToDisk(active!);
            At(StorePoint.AfterPackSync);
        }
        var next = totals;
        using (var tx = wconn!.BeginTransaction())
        {
            if (pending.Count > 0) next = WriteRows(tx);
            FlushTouches(tx);
            At(StorePoint.InsideIndexCommit);
            tx.Commit();
        }
        totals = next;
        At(StorePoint.AfterIndexCommit);
        committedLength = activeLength;
        var owners = pending.Select(p => p.Owner).Where(o => o is not null).ToList();
        pending.Clear(); pendingKeys.Clear(); pendingBytes = 0;
        foreach (var o in owners) { o!.Outstanding--; o.TryFinish(); }
        return Task.CompletedTask;
    }

    /// <summary>Writes the pending batch's rows and returns the store totals they leave; the caller adopts them once the transaction commits.</summary>
    private StoreTotals WriteRows(SqliteTransaction tx)
    {
        var t = totals;
        using var insert = Command(tx, "INSERT INTO objects(k, pack, offset, length, sha256) VALUES($k, $p, $o, $l, $s)", "$k", "$p", "$o", "$l", "$s");
        using var remove = Command(tx, "DELETE FROM objects WHERE k = $k", "$k");
        using var lookupTx = ObjectIndex.LookupCommand(wconn!, tx);
        using var live = Command(tx, "UPDATE packs SET live_bytes = live_bytes + $d WHERE seq = $p", "$d", "$p");
        using var history = Command(tx, "INSERT OR IGNORE INTO history VALUES($r, $t, $pr, $rg, $kd, $doc, $u)", "$r", "$t", "$pr", "$rg", "$kd", "$doc", "$u");
        long added = 0;
        foreach (var p in pending)
        {
            if (p.Tombstone)
            {
                // The row's own values leave the digest: read them in this transaction, whatever the caller thought was there.
                if (ObjectIndex.Lookup(lookupTx, p.Key.ToIndexKey()) is { } was)
                {
                    remove.Parameters[0].Value = p.Key.ToIndexKey(); remove.ExecuteNonQuery();
                    t = t with { Objects = t.Objects - 1, ObjectDigest = t.ObjectDigest ^ ObjectIndex.ObjectDigest(p.Key.ToIndexKey(), was.Pack, was.Offset, was.Length, was.Sha256) };
                }
                if (p.Key.Type == ObjectKeyType.HistoryRecord) t = DeleteHistoryRow(tx, p.Key, t);
                if (p.Removed is { } gone) { live.Parameters[0].Value = -PackFormat.EntryBytes(gone.Length); live.Parameters[1].Value = gone.Pack; live.ExecuteNonQuery(); }
                added += PackFormat.EntryHeaderBytes;  // compaction keeps tombstones, so they count as kept bytes
                continue;
            }
            insert.Parameters[0].Value = p.Key.ToIndexKey(); insert.Parameters[1].Value = activeSeq; insert.Parameters[2].Value = p.Offset;
            insert.Parameters[3].Value = p.Length; insert.Parameters[4].Value = p.Sha256; insert.ExecuteNonQuery();
            t = t with { Objects = t.Objects + 1, ObjectDigest = t.ObjectDigest ^ ObjectIndex.ObjectDigest(p.Key.ToIndexKey(), activeSeq, p.Offset, p.Length, p.Sha256) };
            added += PackFormat.EntryBytes(p.Length);
            if (p.History is { } h)
            {
                history.Parameters[0].Value = p.Key.KeyBytes(); history.Parameters[1].Value = h.ObservedMs; history.Parameters[2].Value = h.Product;
                history.Parameters[3].Value = h.Region; history.Parameters[4].Value = (int)h.Kind; history.Parameters[5].Value = h.Document.KeyBytes();
                history.Parameters[6].Value = h.Url;
                if (history.ExecuteNonQuery() == 1)
                    t = t with { History = t.History + 1, HistoryDigest = t.HistoryDigest ^ ObjectIndex.HistoryDigest(p.Key.KeyBytes(), h.ObservedMs, h.Product, h.Region, (long)h.Kind, h.Document.KeyBytes(), h.Url) };
            }
        }
        ExecWriter("UPDATE packs SET committed_length = $c, live_bytes = live_bytes + $d, row_check = vm_pack_check(seq, state, $c) WHERE seq = $s", tx, ("$c", activeLength), ("$d", added), ("$s", activeSeq));
        WriteStoreRow(tx, (long)nextSeq, t);
        return t;
    }

    /// <summary>Removes a history row and takes it out of the totals (values read first: the digest needs them).</summary>
    private StoreTotals DeleteHistoryRow(SqliteTransaction tx, ObjectKey record, StoreTotals t)
    {
        using (var cmd = wconn!.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT observed_at, product, region, kind, document, url FROM history WHERE record = $r";
            cmd.Parameters.AddWithValue("$r", record.KeyBytes());
            using var r = cmd.ExecuteReader();
            if (r.Read())
                t = t with { History = t.History - 1, HistoryDigest = t.HistoryDigest ^ ObjectIndex.HistoryDigest(record.KeyBytes(), r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt64(3), r.GetFieldValue<byte[]>(4), r.GetString(5)) };
        }
        ExecWriter("DELETE FROM history WHERE record = $r", tx, ("$r", record.KeyBytes()));
        return t;
    }

    private void FlushTouches(SqliteTransaction tx)
    {
        if (touches.IsEmpty) return;
        var day = options.Time.GetUtcNow().ToUnixTimeSeconds() / 86400;
        using var touch = Command(tx, "UPDATE objects SET last_read = $d WHERE k = $k AND (last_read IS NULL OR last_read < $d)", "$d", "$k");
        touch.Parameters[0].Value = day;
        foreach (var key in touches.Keys) { touches.TryRemove(key, out _); touch.Parameters[1].Value = key.ToIndexKey(); touch.ExecuteNonQuery(); }
    }

    private SqliteCommand Command(SqliteTransaction? tx, string sql, params string[] names)
    {
        var cmd = wconn!.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var n in names) cmd.Parameters.Add(new SqliteParameter { ParameterName = n });
        return cmd;
    }

    private void ExecWriter(string sql, params (string name, object value)[] values) => ExecWriter(sql, null, values);
    private void ExecWriter(string sql, SqliteTransaction? tx, params (string name, object value)[] values)
    {
        using var cmd = wconn!.CreateCommand(); cmd.Transaction = tx; cmd.CommandText = sql;
        foreach (var (n, v) in values) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// One index transaction with synchronous=FULL, so it and every commit before it in the WAL survive power loss. Ordinary
    /// batches commit with NORMAL: losing one only loses rows tail recovery finds again in the synced active pack. Anything whose
    /// loss recovery couldn't undo (listing or sealing a pack, switching a compaction) goes through here.
    /// </summary>
    private void Durably(Action<SqliteTransaction> work)
    {
        ObjectIndex.Exec(wconn!, "PRAGMA synchronous=FULL");
        try
        {
            using var tx = wconn!.BeginTransaction();
            work(tx);
            tx.Commit();
        }
        finally { ObjectIndex.Exec(wconn!, "PRAGMA synchronous=NORMAL"); }
    }

    /// <summary>The next pack number; file names hold six digits, so the store refuses to go past pack 999,999 (about 2 PB at 2 GiB).</summary>
    private int TakePackNumber()
    {
        if (nextPackSeq > 999_999) throw new ObjectStoreException(ObjectStoreCodes.Limit, "The store has used every pack number (999,999).");
        return nextPackSeq++;
    }

    private void SetCleanClose(bool clean) => Durably(tx => ExecWriter(
        "UPDATE store SET clean_close = $c, row_check = vm_store_check(store_id, format, created_at, next_seq, object_count, object_digest, history_count, history_digest, $c) WHERE id = 1",
        tx, ("$c", clean ? 1L : 0L)));

    private void SealRow(int seq) => SetPackState(null, seq, PackState.Sealed);

    // Pack and store rows always carry their row check (ObjectIndex.Validate). SET sees the old values, so the new ones go in by name.
    private void SetPackState(SqliteTransaction? tx, int seq, PackState state) =>
        ExecWriter("UPDATE packs SET state = $st, row_check = vm_pack_check(seq, $st, committed_length) WHERE seq = $s", tx, ("$st", (int)state), ("$s", seq));
    private void InsertPack(SqliteTransaction? tx, int seq, PackState state, long committed) =>
        ExecWriter("INSERT INTO packs VALUES($s, $st, $c, 0, vm_pack_check($s, $st, $c))", tx, ("$s", seq), ("$st", (int)state), ("$c", committed));
    private void WriteStoreRow(SqliteTransaction? tx, long next, StoreTotals t) =>
        ExecWriter("UPDATE store SET next_seq = $n, object_count = $oc, object_digest = $od, history_count = $hc, history_digest = $hd, " +
            "row_check = vm_store_check(store_id, format, created_at, $n, $oc, $od, $hc, $hd, clean_close) WHERE id = 1", tx,
            ("$n", next), ("$oc", t.Objects), ("$od", t.ObjectDigest), ("$hc", t.History), ("$hd", t.HistoryDigest));
    private string PackPath(int seq) => Path.Combine(packsDirectory, PackFiles.Name(seq));

    // ---------------------------------------------------------------- failure, crash and close

    private static ObjectStoreException Error(Exception e) => e is IOException io && PackFiles.IsDiskFull(io)
        ? new(ObjectStoreCodes.DiskFull, "The volume holding the object store is full; nothing past the last commit was kept.", e)
        : new(ObjectStoreCodes.WriteFailed, "Writing the object store failed; nothing past the last commit was kept.", e);

    /// <summary>
    /// A write or index error: everything not yet committed is dropped (its callers get the error) and the pack is cut back to its
    /// last commit. If that cut fails too, the store stops writing until it's reopened. Callers hear of the error only after the
    /// cut: a failed Put leaves the pack as it was at the last commit.
    /// </summary>
    private void Recover(Exception e, Request? current)
    {
        var error = Error(e);
        var owners = pending.Select(p => p.Owner).Append(current).Where(o => o is not null).Distinct().ToList();
        pending.Clear(); pendingKeys.Clear(); pendingBytes = 0;
        At(StorePoint.WriteErrorCutting);
        try { if (active is not null) { RandomAccess.SetLength(active, committedLength); RandomAccess.FlushToDisk(active); } activeLength = committedLength; }
        catch (Exception cut) when (cut is IOException or UnauthorizedAccessException) { Fail(error); }
        foreach (var o in owners) o!.Done.TrySetException(error);
    }

    private void Fail(ObjectStoreException error)
    {
        fault = error;
        requests?.Writer.TryComplete();
        while (requests is not null && requests.Reader.TryRead(out var r)) r.Done.TrySetException(error);
    }

    /// <summary>Tests only: stop as a kill would, from outside the writer (which must be idle).</summary>
    internal void Kill() => Crash();

    private void Crash()
    {
        // Drop every file handle and the writer lock first, as a kill would, and only then wake the waiters.
        killed = true;
        Volatile.Write(ref closed, 1);
        AbandonWriter();
        var error = new ObjectStoreException(ObjectStoreCodes.Closed, "The object store stopped (simulated crash).");
        var owners = pending.Select(p => p.Owner).Append(current).Where(o => o is not null).Distinct().ToList();
        pending.Clear(); pendingKeys.Clear();
        Fail(error);
        foreach (var o in owners) o!.Done.TrySetException(error);
    }

    private async Task CloseWriterAsync()
    {
        requests?.Writer.TryComplete();
        if (loop is not null) await loop;
        foreach (var s in stagings.Values.Where(v => v is not null).ToList()) await s.DisposeAsync();
        if (wconn is not null && fault is null && !killed)
        {
            // Clean close (D126): everything is committed; the marker is set durably, so the next open needs only the cheap check.
            try { SetCleanClose(true); }
            catch (SqliteException) { /* left unset: the next open runs the full check, which is only slower */ }
            try { ObjectIndex.Exec(wconn, "PRAGMA wal_checkpoint(TRUNCATE)"); }
            catch (SqliteException) { /* a reader in another process holds the WAL; the next writer checkpoints */ }
        }
        AbandonWriter();
    }

    private void AbandonWriter()
    {
        wlookup?.Dispose(); wlookup = null;
        wconn?.Dispose(); wconn = null;
        active?.Dispose(); active = null;
        writerLock?.Dispose(); writerLock = null;
    }
}

