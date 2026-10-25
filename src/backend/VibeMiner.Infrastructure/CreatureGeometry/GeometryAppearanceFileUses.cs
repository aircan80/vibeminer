namespace VibeMiner.Infrastructure;

/// <summary>Which VMA1 files are in use right now (D103): every open appearance stream, including a geometry read lease, and
/// every appearance publication from before its file is written until its row is committed or abandoned. Cleanup deletes a file
/// only after <see cref="TryBeginDelete"/>; a use of that file waits until the delete has finished, then finds the file or not.</summary>
public sealed class GeometryAppearanceFileUses
{
    private readonly object gate = new();
    private readonly Dictionary<string, int> uses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, TaskCompletionSource> deleting = new(StringComparer.Ordinal);

    internal async Task<IDisposable> UseAsync(string sha256, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(sha256);
        while (true)
        {
            Task wait;
            lock (gate)
            {
                if (!deleting.TryGetValue(sha256, out var pending))
                { uses[sha256] = uses.GetValueOrDefault(sha256) + 1; return new Use(this, sha256); }
                wait = pending.Task;
            }
            await wait.WaitAsync(token);
        }
    }

    /// <summary>False while the file is in use or another delete of it runs. On true the caller must call <see cref="EndDelete"/>.</summary>
    internal bool TryBeginDelete(string sha256)
    {
        lock (gate)
        {
            if (uses.ContainsKey(sha256) || deleting.ContainsKey(sha256)) return false;
            deleting.Add(sha256, new(TaskCreationOptions.RunContinuationsAsynchronously)); return true;
        }
    }

    internal void EndDelete(string sha256)
    {
        TaskCompletionSource? done;
        lock (gate) { if (deleting.Remove(sha256, out done) is false) return; }
        done!.TrySetResult();
    }

    internal int InUse(string sha256) { lock (gate) return uses.GetValueOrDefault(sha256); }

    /// <summary>Test seam: runs after a Prepare has written a VMA1 file, while it still holds the use and before its row commits.</summary>
    internal Func<string, Task>? AfterPublished { get; set; }

    private void Release(string sha256)
    {
        lock (gate) { if (uses[sha256] == 1) uses.Remove(sha256); else uses[sha256]--; }
    }

    private sealed class Use(GeometryAppearanceFileUses owner, string sha256) : IDisposable
    {
        private int released;
        public void Dispose() { if (Interlocked.Exchange(ref released, 1) == 0) owner.Release(sha256); }
    }
}

/// <summary>A read-only stream that holds a file use until it is disposed.</summary>
internal sealed class GeometryAppearanceUseStream(Stream inner, IDisposable use) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override int Read(Span<byte> buffer) => inner.Read(buffer);
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => inner.ReadAsync(buffer, offset, count, token);
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) try { inner.Dispose(); } finally { use.Dispose(); }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        try { await inner.DisposeAsync(); } finally { use.Dispose(); }
        await base.DisposeAsync();
    }
}
