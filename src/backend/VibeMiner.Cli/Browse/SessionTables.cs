using VibeMiner.Objects;

namespace VibeMiner.Cli.Browse;

/// <summary>
/// The one tables composition a browse session keeps (#180): opened on the first <c>:tables</c> load and reused by every later
/// one, so layer 3's opened builds and each build's TACT keys survive key presses; closed when browse exits. It holds a lease
/// on the session's <see cref="SharedWriter"/>, so <c>r</c> and <c>a</c> share that handle instead of finding the store busy.
/// The cost: from the first <c>:tables</c> load until browse exits, browse holds the writer lock. When another process holds
/// the lock at that first load, the composition is read-only (whatever isn't stored answers NeedsNetwork) until browse restarts.
/// </summary>
internal sealed class SessionTables(BrowseSession session, Func<IStoreHandle, string, bool, CancellationToken, Task<TablesLease>> compose) : IAsyncDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private TablesLease? open;
    private bool disposed;

    /// <summary>Whether the composition is open, and over the writer. Null before the first load.</summary>
    public bool? Writer { get; private set; }

    /// <summary>
    /// <see cref="BrowseSession.OpenTables"/>: a lease on the session's composition, opening it on first use. Disposing the
    /// lease closes nothing; the session does that.
    /// </summary>
    public async Task<TablesLease> LeaseAsync(BrowseSession _, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            open ??= await OpenAsync(ct).ConfigureAwait(false);
            return new TablesLease(open.Tables);
        }
        finally { gate.Release(); }
    }

    private async Task<TablesLease> OpenAsync(CancellationToken ct)
    {
        IStoreHandle handle;
        try { handle = await session.OpenWriterAsync(ct, create: false).ConfigureAwait(false); }
        catch (ObjectStoreException e) when (e.Code == ObjectStoreCodes.Busy) { handle = await session.OpenReadAsync(ct).ConfigureAwait(false); }
        try
        {
            var lease = await compose(handle, session.Folder.Path, false, ct).ConfigureAwait(false);
            Writer = handle.Writer;
            return lease;
        }
        catch
        {
            await handle.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            if (open is not null) await open.DisposeAsync().ConfigureAwait(false);
            open = null;
        }
        finally { gate.Release(); }
    }
}
