using VibeMiner.Builds;
using VibeMiner.Files;
using VibeMiner.Files.Names;
using VibeMiner.ObjectAccess;

namespace VibeMiner.Cli.Browse;

/// <summary>One build and locale the Files views read.</summary>
internal sealed record FilesTarget(BuildRef Build, string Locale);

/// <summary>
/// A hold on the session's files for one target. <see cref="Files"/> is set only when the open's <see cref="Result"/> is Open,
/// and stays usable until this lease is disposed, even when another target has replaced it in the cache. Dispose it when the
/// load ends (completed, failed or canceled); disposing twice is harmless.
/// </summary>
internal sealed class FilesLease : IAsyncDisposable
{
    private readonly FilesCache owner;
    private readonly FilesCache.Shared shared;
    private readonly FilesCache.Entry? entry;
    private int disposed;

    internal FilesLease(FilesCache owner, FilesCache.Shared shared, FilesCache.Entry? entry, FilesOpenResult result)
    {
        this.owner = owner; this.shared = shared; this.entry = entry; Result = result;
    }

    public FilesTarget Target => new(Result.Build, Result.Locale);
    /// <summary>How the open ended: state, code and message as layer 4 gave them.</summary>
    public FilesOpenResult Result { get; }
    public IBuildFiles? Files => Result.Files;
    /// <summary>The session's community names (loaded once per file system). Browse only reads their status; it never updates them.</summary>
    public IFileNames Names => shared.Files.Names;

    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref disposed, 1) != 0 ? ValueTask.CompletedTask : owner.ReleaseAsync(shared, entry);
}

/// <summary>
/// The session's layer 4: one read-only store handle and one <see cref="IFileSystem"/> over it (so the community names load once),
/// and the most recent opened target's <see cref="IBuildFiles"/>. Opening another target replaces it; the old handle is disposed
/// once no lease holds it. Failed opens are not kept, so the next open tries again. A missing store throws the store's
/// OBJECT_STORE_MISSING, as every view's open does, and nothing is kept. Opens run one at a time, each under its own token;
/// leases may be held and released from any thread. The read-only store sees what a writer commits after it opened
/// (each lookup is a new index read), so a Download all in the same session needs no reopen; <see cref="Invalidate"/> forces one.
/// </summary>
internal sealed class FilesCache(Func<CancellationToken, Task<IStoreHandle>> openStore, Func<IObjectSource, IFileSystem> createFileSystem) : IAsyncDisposable
{
    /// <summary>The store handle and the file system over it, with a count of the cache, entries and leases holding them.</summary>
    internal sealed class Shared(IStoreHandle store, IFileSystem files)
    {
        public IStoreHandle Store { get; } = store;
        public IFileSystem Files { get; } = files;
        public int Holds = 1;
    }

    /// <summary>One opened target. Holds the shared part while it lives.</summary>
    internal sealed class Entry(Shared shared, FilesOpenResult result, bool cached)
    {
        public Shared Shared { get; } = shared;
        public FilesOpenResult Result { get; } = result;
        public FilesTarget Target { get; } = new(result.Build, result.Locale);
        public int Holds = cached ? 1 : 0;
    }

    private readonly Lock gate = new();
    private readonly SemaphoreSlim opening = new(1, 1);
    private Shared? shared;
    private Entry? current;
    private bool disposed;

    /// <summary>Opens (or reuses) the target. The caller disposes the lease. Throws what the store open throws, and on cancellation.</summary>
    public async Task<FilesLease> OpenAsync(FilesTarget target, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (gate) ObjectDisposedException.ThrowIf(disposed, this);
        await opening.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The open holds the shared part from here, so an Invalidate meanwhile can't close the store under it.
            Shared? have;
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (current is { } reuse && reuse.Target == target) return Lease(reuse.Shared, reuse, reuse.Result);
                have = shared;
                if (have is not null) have.Holds++;
            }
            have ??= await OpenSharedAsync(ct).ConfigureAwait(false);
            FilesOpenResult result;
            try { result = await have.Files.ForAsync(target.Build, target.Locale, ct).ConfigureAwait(false); }
            catch { await ReleaseAsync(have, null).ConfigureAwait(false); throw; }
            Entry? retired = null;
            FilesLease lease;
            lock (gate)
            {
                if (result.Files is null) lease = Lease(have, null, result);
                else
                {
                    // Kept only when the cache still uses this store and isn't disposed; otherwise only the lease holds it.
                    var keep = !disposed && ReferenceEquals(shared, have);
                    var entry = new Entry(have, result, keep);
                    have.Holds++;
                    if (keep) { retired = current; current = entry; }
                    lease = Lease(have, entry, result);
                }
            }
            // The open's own hold, then the replaced entry's cache hold. The lease is the caller's whatever closing the old
            // handle (or a store an Invalidate dropped meanwhile) throws: nothing more can be done with a failed close.
            try { await ReleaseAsync(have, null).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException) { }
            try { if (retired is not null) await ReleaseEntryAsync(retired).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException) { }
            return lease;
        }
        finally { opening.Release(); }
    }

    /// <summary>Drops the cached handle, file system and store: the next open starts over. Leases already given keep theirs.</summary>
    public ValueTask InvalidateAsync() => DropAsync(dispose: false);

    /// <summary>Closes the cached handle, file system and store, each once no lease holds it. Later opens throw.</summary>
    public ValueTask DisposeAsync() => DropAsync(dispose: true);

    private async ValueTask DropAsync(bool dispose)
    {
        Entry? entry; Shared? store;
        lock (gate)
        {
            if (disposed) return;
            disposed = dispose;
            entry = current; store = shared;
            current = null; shared = null;
        }
        if (entry is not null) await ReleaseEntryAsync(entry).ConfigureAwait(false);
        if (store is not null) await ReleaseAsync(store, null).ConfigureAwait(false);
    }

    private async Task<Shared> OpenSharedAsync(CancellationToken ct)
    {
        var store = await openStore(ct).ConfigureAwait(false);
        Shared made;
        try { made = new Shared(store, createFileSystem(store.Objects)); }
        catch { await store.DisposeAsync().ConfigureAwait(false); throw; }
        lock (gate)
        {
            // The cache's hold and the calling open's.
            if (!disposed) { shared = made; made.Holds = 2; return made; }
        }
        await CloseAsync(made).ConfigureAwait(false);
        throw new ObjectDisposedException(nameof(FilesCache));
    }

    /// <summary>Called under the lock: one more hold on the shared part and on the entry, if any.</summary>
    private FilesLease Lease(Shared on, Entry? entry, FilesOpenResult result)
    {
        on.Holds++;
        if (entry is not null) entry.Holds++;
        return new FilesLease(this, on, entry, result);
    }

    internal async ValueTask ReleaseAsync(Shared on, Entry? entry)
    {
        if (entry is not null) await ReleaseEntryAsync(entry).ConfigureAwait(false);
        bool last;
        lock (gate) last = --on.Holds == 0;
        if (last) await CloseAsync(on).ConfigureAwait(false);
    }

    private async ValueTask ReleaseEntryAsync(Entry entry)
    {
        bool last;
        lock (gate) last = --entry.Holds == 0;
        if (!last) return;
        try { if (entry.Result.Files is { } files) await files.DisposeAsync().ConfigureAwait(false); }
        finally { await ReleaseAsync(entry.Shared, null).ConfigureAwait(false); }
    }

    private static async ValueTask CloseAsync(Shared on)
    {
        try
        {
            if (on.Files is IAsyncDisposable a) await a.DisposeAsync().ConfigureAwait(false);
            else if (on.Files is IDisposable d) d.Dispose();
        }
        finally { await on.Store.DisposeAsync().ConfigureAwait(false); }
    }
}
