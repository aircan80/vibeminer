using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VibeMiner.Objects;

/// <summary>fsync of a directory, so a created or renamed file's name survives power loss. Windows journals names itself.</summary>
internal static partial class DirectorySync
{
    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", SetLastError = true, EntryPoint = "fsync")]
    private static extern int FSync(int fd);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int fd);

    public static void Sync(string directory)
    {
        if (OperatingSystem.IsWindows()) return;
        var fd = Open(directory, 0);
        if (fd < 0) throw new IOException($"Could not open {directory} to sync it (errno {Marshal.GetLastPInvokeError()}).");
        try { if (FSync(fd) != 0) throw new IOException($"Could not sync {directory} (errno {Marshal.GetLastPInvokeError()})."); }
        finally { _ = Close(fd); }
    }
}

internal static class PackFiles
{
    public static string Name(int seq) => $"pack-{seq:D6}.vmp";
    public static bool TryParse(string fileName, out int seq)
    {
        seq = 0;
        return fileName.Length == 15 && fileName.StartsWith("pack-", StringComparison.Ordinal) && fileName.EndsWith(".vmp", StringComparison.Ordinal) &&
            int.TryParse(fileName.AsSpan(5, 6), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out seq) && seq > 0;
    }
    /// <summary>Readers share ReadWrite|Delete so the writer can append and compaction can delete while they read (Windows too).</summary>
    public static SafeFileHandle OpenRead(string path) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous | FileOptions.RandomAccess);
    public static SafeFileHandle OpenWrite(string path, FileMode mode) =>
        File.OpenHandle(path, mode, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, FileOptions.Asynchronous);

    public static async ValueTask<int> ReadFullyAsync(SafeFileHandle h, Memory<byte> buffer, long offset, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await RandomAccess.ReadAsync(h, buffer[total..], offset + total, ct);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    public static bool IsDiskFull(IOException e) => (e.HResult & 0xFFFF) is 28 or 112 or 39;
}

/// <summary>Bounded cache of read handles to packs, leased per read so eviction and retirement never close a handle in use.</summary>
internal sealed class PackReaders(string packsDirectory, int capacity) : IDisposable
{
    private sealed class Entry(SafeFileHandle handle) { public SafeFileHandle Handle = handle; public int Leases; public bool Dropped; public long Used; }
    private readonly Dictionary<int, Entry> open = [];
    private long clock;
    private bool disposed;

    public readonly struct Lease(PackReaders owner, object entry, SafeFileHandle handle) : IDisposable
    {
        public SafeFileHandle Handle { get; } = handle;
        public void Dispose() => owner?.Release((Entry)entry);
    }

    /// <summary>A lease on the pack, or null when its file doesn't exist.</summary>
    public Lease? Acquire(int seq)
    {
        lock (open)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!open.TryGetValue(seq, out var e))
            {
                SafeFileHandle h;
                try { h = PackFiles.OpenRead(Path.Combine(packsDirectory, PackFiles.Name(seq))); }
                catch (FileNotFoundException) { return null; }
                catch (DirectoryNotFoundException) { return null; }
                e = new Entry(h); open[seq] = e;
            }
            // Lease first, then evict: the entry being acquired is never the victim (QA #153: it was, and its handle closed).
            e.Leases++; e.Used = ++clock;
            while (open.Count > capacity && Evict()) { }
            return new Lease(this, e, e.Handle);
        }
    }

    /// <summary>Closes the least recently used handle no read holds; false when every open handle is leased (the cache runs over until one is released).</summary>
    private bool Evict()
    {
        var victim = open.Where(p => p.Value.Leases == 0).OrderBy(p => p.Value.Used).Select(p => (int?)p.Key).FirstOrDefault();
        if (victim is not { } v) return false;
        open[v].Handle.Dispose(); open.Remove(v);
        return true;
    }

    /// <summary>Forget the pack's handle (compaction retired it); a read in flight finishes on its own lease.</summary>
    public void Invalidate(int seq)
    {
        lock (open)
        {
            if (!open.Remove(seq, out var e)) return;
            if (e.Leases == 0) e.Handle.Dispose(); else e.Dropped = true;
        }
    }

    private void Release(Entry e)
    {
        lock (open) { if (--e.Leases == 0 && (e.Dropped || disposed)) e.Handle.Dispose(); }
    }

    public void Dispose()
    {
        lock (open)
        {
            disposed = true;
            foreach (var e in open.Values) if (e.Leases == 0) e.Handle.Dispose(); else e.Dropped = true;
            open.Clear();
        }
    }
}
