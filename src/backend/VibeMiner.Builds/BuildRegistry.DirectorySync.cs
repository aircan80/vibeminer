using System.Runtime.InteropServices;

namespace VibeMiner.Builds;

/// <summary>Directory-name durability using the same platform boundary as the pack store, without exposing another layer's internals.</summary>
internal static class RegistryDirectorySync
{
    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", SetLastError = true, EntryPoint = "fsync")]
    private static extern int FSync(int fd);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int fd);

    internal static void Sync(string directory)
    {
        // Windows journals names; Unix requires an explicit directory fsync after creating a file or child directory.
        if (OperatingSystem.IsWindows()) return;
        var fd = Open(directory, 0);
        if (fd < 0) throw new IOException($"Could not open the registry directory for synchronization (errno {Marshal.GetLastPInvokeError()}).");
        try
        {
            if (FSync(fd) != 0) throw new IOException($"Could not synchronize the registry directory (errno {Marshal.GetLastPInvokeError()}).");
        }
        finally { _ = Close(fd); }
    }
}
