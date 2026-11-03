using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace VibeMiner.Objects;

// Narrow, metadata-only Unix traversal. Native layouts/constants are guarded to their reviewed 64-bit ABIs:
// Darwin SDK sys/{stat,dirent,fcntl}.h; Linux UAPI asm-generic/{stat,fcntl}.h and arch/{x86,arm64}/include/uapi/asm headers.
// Every child lookup is descriptor-relative and does not follow its final component. Directory opens additionally reject
// links atomically. No regular file is opened. Unknown OS/architecture fails closed rather than using path-based recursion.
internal static class LibraryFootprintNative
{
    internal readonly record struct Metadata(long Device, long Inode, int Mode, long Length)
    {
        public bool IsDirectory => (Mode & 0xf000) == 0x4000;
        public bool IsRegular => (Mode & 0xf000) == 0x8000;
        public bool IsLink => (Mode & 0xf000) == 0xa000;
        public bool SameIdentity(Metadata other) => Device == other.Device && Inode == other.Inode;
    }
    internal static bool Supported => BitConverter.IsLittleEndian && (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) &&
        RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64;
    private static bool DarwinX64 => OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64;
    private static int DirectoryFlags => DirectoryOpenFlags(OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux, RuntimeInformation.ProcessArchitecture);
    internal static int DirectoryOpenFlags(OSPlatform platform, Architecture architecture)
    {
        if (architecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException("The native directory ABI is unsupported.");
        if (platform == OSPlatform.OSX) return 0x01000100 | 0x00100000;
        // ARM64 overrides the generic O_DIRECTORY/O_NOFOLLOW bits; the x64 values instead select O_DIRECT/O_LARGEFILE there.
        if (platform == OSPlatform.Linux) return architecture == Architecture.Arm64
            ? (1 << 14) | (1 << 15) | (1 << 19)
            : (1 << 16) | (1 << 17) | (1 << 19);
        throw new PlatformNotSupportedException("The native directory ABI is unsupported.");
    }
    private static int NoFollow => OperatingSystem.IsMacOS() ? 0x20 : 0x100;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    [DllImport("libc", SetLastError = true, EntryPoint = "open")]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", SetLastError = true, EntryPoint = "openat")]
    private static extern int OpenAt(int parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int fd);
    [DllImport("libc", SetLastError = true, EntryPoint = "fstatat")]
    private static extern int StatAt(int parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [Out] byte[] status, int flags);
    [DllImport("libc", SetLastError = true, EntryPoint = "fstatat$INODE64")]
    private static extern int StatAtDarwinX64(int parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [Out] byte[] status, int flags);
    [DllImport("libc", SetLastError = true, EntryPoint = "fstat")]
    private static extern int Stat(int fd, [Out] byte[] status);
    [DllImport("libc", SetLastError = true, EntryPoint = "fstat$INODE64")]
    private static extern int StatDarwinX64(int fd, [Out] byte[] status);
    [DllImport("libc", SetLastError = true, EntryPoint = "fdopendir")]
    private static extern IntPtr OpenStream(int fd);
    [DllImport("libc", EntryPoint = "closedir")]
    private static extern int CloseStream(IntPtr stream);
    [DllImport("libc", SetLastError = true, EntryPoint = "readdir")]
    private static extern IntPtr ReadEntry(IntPtr stream);
    [DllImport("libc", SetLastError = true, EntryPoint = "readdir$INODE64")]
    private static extern IntPtr ReadEntryDarwinX64(IntPtr stream);
    [DllImport("libc", EntryPoint = "__error")]
    private static extern IntPtr DarwinError();
    [DllImport("libc", EntryPoint = "__errno_location")]
    private static extern IntPtr LinuxError();

    internal static LibraryFootprintProblemCode ErrorCode(int error, bool required = false) => error switch
    {
        2 or 3 => required ? LibraryFootprintProblemCode.MissingRequiredPath : LibraryFootprintProblemCode.Disappeared,
        40 or 62 => LibraryFootprintProblemCode.Link,
        _ => LibraryFootprintProblemCode.Unreadable
    };
    private static IOException Error() => new("Library metadata could not be read.", Marshal.GetLastPInvokeError());
    internal static int ErrorNumber(IOException error) => error.HResult < 0 ? error.HResult & 0xffff : error.HResult;
    private static Metadata Decode(byte[] bytes)
    {
        var modeOffset = OperatingSystem.IsMacOS() ? 4 : RuntimeInformation.ProcessArchitecture == Architecture.X64 ? 24 : 16;
        var mode = OperatingSystem.IsMacOS() ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(modeOffset)) : BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(modeOffset));
        return new(OperatingSystem.IsMacOS() ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadInt64LittleEndian(bytes),
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(8)), mode,
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(OperatingSystem.IsMacOS() ? 96 : 48)));
    }

    internal sealed class Directory : IDisposable
    {
        private int fd;
        private IntPtr stream;
        internal Metadata Identity { get; }
        private Directory(int fd)
        {
            this.fd = fd;
            var bytes = new byte[256];
            if ((DarwinX64 ? StatDarwinX64(fd, bytes) : Stat(fd, bytes)) != 0) throw Error();
            Identity = Decode(bytes);
            if (!Identity.IsDirectory) throw new IOException("Expected a directory.");
        }
        private static Directory Own(int fd)
        {
            if (fd < 0) throw Error();
            try { return new(fd); } catch { _ = Close(fd); throw; }
        }
        internal static Directory OpenRoot(string path, CancellationToken ct)
        {
            var current = Own(Open("/", DirectoryFlags));
            try
            {
                // A fully qualified normalized Unix path has at most MaxPathLength components; only two descriptors are live
                // while walking its ancestors. Unlike opening the full path, this cannot follow an ancestor symlink.
                foreach (var component in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
                {
                    ct.ThrowIfCancellationRequested();
                    var metadata = current.Get(component);
                    if (metadata.IsLink) throw new IOException("A root component is a link.", OperatingSystem.IsMacOS() ? 62 : 40);
                    var next = current.OpenChild(component);
                    if (!next.Identity.SameIdentity(metadata)) { next.Dispose(); throw new IOException("The root changed.", 116); }
                    current.Dispose(); current = next;
                }
                return current;
            }
            catch { current.Dispose(); throw; }
        }
        internal Metadata Get(string name)
        {
            var bytes = new byte[256];
            if ((DarwinX64 ? StatAtDarwinX64(fd, name, bytes, NoFollow) : StatAt(fd, name, bytes, NoFollow)) != 0) throw Error();
            return Decode(bytes);
        }
        internal Directory OpenChild(string name)
        {
            var child = OpenAt(fd, name, DirectoryFlags);
            if (child >= 0) return Own(child);
            var error = Error();
            if (ErrorNumber(error) is 20 or 40 or 62)
            {
                // O_DIRECTORY|O_NOFOLLOW may report ENOTDIR for a symlink (Darwin and Linux), rather than ELOOP.
                // Re-observe the entry without following it; a plain file must never be mislabeled as a link.
                var current = Get(name);
                if (current.IsLink) throw new IOException("The directory entry is a link.", OperatingSystem.IsMacOS() ? 62 : 40);
                if (!current.IsDirectory) throw new IOException("The directory entry changed.", 116);
            }
            throw error;
        }
        internal string? Next()
        {
            if (stream == IntPtr.Zero)
            {
                stream = OpenStream(fd);
                if (stream == IntPtr.Zero) throw Error();
            }
            // readdir's null return distinguishes end from error only when errno is cleared before the call.
            Marshal.WriteInt32(OperatingSystem.IsMacOS() ? DarwinError() : LinuxError(), 0);
            var entry = DarwinX64 ? ReadEntryDarwinX64(stream) : ReadEntry(stream);
            if (entry == IntPtr.Zero)
            {
                if (Marshal.GetLastPInvokeError() != 0) throw Error();
                return null;
            }
            var offset = OperatingSystem.IsMacOS() ? 21 : 19;
            var recordLength = (ushort)Marshal.ReadInt16(entry, 16);
            var maximum = Math.Min(OperatingSystem.IsMacOS() ? 1024 : 256, recordLength - offset);
            var length = 0;
            while (length < maximum && Marshal.ReadByte(entry, offset + length) != 0) length++;
            if (length == 0 || length == maximum) throw new IOException("A directory entry name is unsupported.", 84);
            var bytes = new byte[length]; Marshal.Copy(IntPtr.Add(entry, offset), bytes, 0, length);
            var name = Utf8.GetString(bytes);
            if (name.Contains('/')) throw new IOException("A directory entry name is unsupported.", 84);
            return name;
        }
        public void Dispose()
        {
            if (fd < 0) return;
            if (stream != IntPtr.Zero) { _ = CloseStream(stream); stream = IntPtr.Zero; }
            else _ = Close(fd);
            fd = -1;
        }
    }
}
