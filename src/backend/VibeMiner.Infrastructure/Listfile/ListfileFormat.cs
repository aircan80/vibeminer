using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using VibeMiner.Domain;

namespace VibeMiner.Infrastructure;

/// <summary>
/// Reads wow-listfile's CSV incrementally (D105): one <c>FileDataID;name</c> per line, LF or CRLF, FileDataIDs strictly
/// ascending. Bytes are untrusted: a line is at most <see cref="MaxLineBytes"/>, a name 1..<see cref="MaxNameBytes"/>
/// bytes of valid UTF-8 with no control characters, a FileDataID a canonical decimal up to 4294967295, and there are at
/// most <see cref="MaxNames"/> names. Anything else refuses the whole file. Blank lines are skipped. Bidirectional
/// formatting characters (U+061C, U+200E, U+200F, U+202A–U+202E, U+2066–U+2069) are removed from names, so a name can't
/// reorder what is shown around it. Output goes to two
/// in-memory buffers the caller drains after each chunk, so memory is bounded by the chunk size.
/// </summary>
public sealed class ListfileCsvParser
{
    public const int MaxNameBytes = 1024;
    public const int MaxLineBytes = 10 + 1 + MaxNameBytes + 1;
    public const int MaxNames = 8_000_000;
    private static readonly UTF8Encoding Strict = new(false, true);
    private readonly byte[] carry = new byte[MaxLineBytes];
    private int carried;
    private long previous = -1;
    private uint nameEnd;
    private long line;
    private bool completed;

    /// <summary>Entries (u32 FileDataID, u32 end of its name in the name blob), little-endian, since the last drain.</summary>
    public MemoryStream Entries { get; } = new();
    /// <summary>Name bytes since the last drain.</summary>
    public MemoryStream Names { get; } = new();
    public long Count { get; private set; }
    public long NameBytes => nameEnd;

    public void Feed(ReadOnlySpan<byte> chunk)
    {
        if (completed) throw new InvalidOperationException("The parser is complete.");
        while (chunk.Length > 0)
        {
            var newline = chunk.IndexOf((byte)'\n');
            if (newline < 0)
            {
                if (carried + chunk.Length > MaxLineBytes) throw Invalid($"Line {line + 1} is longer than {MaxLineBytes} bytes.");
                chunk.CopyTo(carry.AsSpan(carried));
                carried += chunk.Length;
                return;
            }
            if (carried > 0)
            {
                if (carried + newline > MaxLineBytes) throw Invalid($"Line {line + 1} is longer than {MaxLineBytes} bytes.");
                chunk[..newline].CopyTo(carry.AsSpan(carried));
                var length = carried + newline;
                carried = 0;
                Line(carry.AsSpan(0, length));
            }
            else
            {
                if (newline > MaxLineBytes) throw Invalid($"Line {line + 1} is longer than {MaxLineBytes} bytes.");
                Line(chunk[..newline]);
            }
            chunk = chunk[(newline + 1)..];
        }
    }

    /// <summary>The last line may end without a newline. An empty file, or one with no names, is refused.</summary>
    public void Complete()
    {
        if (completed) return;
        if (carried > 0) { var length = carried; carried = 0; Line(carry.AsSpan(0, length)); }
        completed = true;
        if (Count == 0) throw Invalid("The file has no names.");
    }

    private void Line(ReadOnlySpan<byte> text)
    {
        line++;
        if (text.Length > 0 && text[^1] == (byte)'\r') text = text[..^1];
        if (text.Length == 0) return;
        var separator = text.IndexOf((byte)';');
        if (separator < 1 || separator > 10) throw Invalid($"Line {line} doesn’t start with a FileDataID and a semicolon.");
        var digits = text[..separator];
        if (digits[0] == (byte)'0' && digits.Length > 1) throw Invalid($"Line {line} has a FileDataID with a leading zero.");
        long id = 0;
        foreach (var digit in digits)
        {
            if (digit is < (byte)'0' or > (byte)'9') throw Invalid($"Line {line} doesn’t start with a FileDataID and a semicolon.");
            id = id * 10 + (digit - '0');
        }
        if (id > uint.MaxValue) throw Invalid($"Line {line} has a FileDataID above 4294967295.");
        if (id <= previous) throw Invalid($"Line {line} isn’t in FileDataID order (FileDataID {id} after {previous}).");
        var name = text[(separator + 1)..];
        if (name.Length is < 1 or > MaxNameBytes) throw Invalid($"Line {line} has a name of {name.Length} bytes; names are 1 to {MaxNameBytes}.");
        foreach (var value in name)
            if (value is < 0x20 or 0x7f) throw Invalid($"Line {line} has a control character in its name.");
        try { _ = Strict.GetCharCount(name); }
        catch (DecoderFallbackException) { throw Invalid($"Line {line} has a name that isn’t valid UTF-8."); }
        Span<byte> kept = stackalloc byte[MaxNameBytes];
        var length = 0;
        for (var at = 0; at < name.Length;)
        {
            var bidi = at + 1 < name.Length && name[at] == 0xD8 && name[at + 1] == 0x9C ? 2
                : at + 2 < name.Length && name[at] == 0xE2 && (name[at + 1] == 0x80 && name[at + 2] is 0x8E or 0x8F or >= 0xAA and <= 0xAE
                    || name[at + 1] == 0x81 && name[at + 2] is >= 0xA6 and <= 0xA9) ? 3 : 0;
            if (bidi > 0) { at += bidi; continue; }
            kept[length++] = name[at++];
        }
        if (length == 0) throw Invalid($"Line {line} has a name made only of direction marks.");
        if (length != name.Length) name = kept[..length].ToArray();
        if (Count == MaxNames) throw Invalid($"The file has more than {MaxNames:N0} names.");
        // A name blob past 4 GiB can't be indexed with 32-bit ends; the asset bound keeps it far below.
        if ((ulong)nameEnd + (ulong)name.Length > uint.MaxValue) throw Invalid("The names are larger than VibeMiner allows.");
        previous = id;
        nameEnd += (uint)name.Length;
        Span<byte> entry = stackalloc byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(entry, (uint)id);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], nameEnd);
        Entries.Write(entry);
        Names.Write(name);
        Count++;
    }

    private static DiscoveryException Invalid(string detail) => CommunityListfile.Invalid(detail);
}

/// <summary>
/// The verified local index of one listfile release (D105), <c>names-&lt;tag&gt;.vlf</c>, little-endian:
/// a 32-byte header (<c>VMLF</c>, version 1, count, block size, blob offset, blob length), then count entries of
/// (u32 FileDataID, u32 end of its name in the blob) in ascending FileDataID order, then the UTF-8 name blob.
/// Opening hashes the whole file against its record and checks every entry; only then are names served. One sparse
/// FileDataID per block is kept in memory; a lookup reads one block of entries and the name bytes.
/// </summary>
public sealed class ListfileIndex : IDisposable
{
    public const int HeaderBytes = 32;
    public const int EntryBytes = 8;
    public const int DefaultBlock = 256;
    private static readonly byte[] Magic = "VMLF"u8.ToArray();
    private static readonly UTF8Encoding Strict = new(false, true);
    private readonly SafeFileHandle handle;
    private readonly uint[] sparse;
    private readonly int block;
    private readonly long count, blobOffset;
    private readonly object gate = new();
    private int leases;
    private bool retired, closed;

    private ListfileIndex(SafeFileHandle handle, uint[] sparse, int block, long count, long blobOffset, CommunityListfileRelease release)
    {
        this.handle = handle; this.sparse = sparse; this.block = block; this.count = count; this.blobOffset = blobOffset; Release = release;
    }

    public CommunityListfileRelease Release { get; }
    public long Count => count;

    public static byte[] Header(long count, int block, long blobLength)
    {
        var header = new byte[HeaderBytes];
        Magic.CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8), (uint)count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)block);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16), HeaderBytes + count * EntryBytes);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(24), blobLength);
        return header;
    }

    /// <summary>Opens and verifies an index: its SHA-256 and length must equal the record's, and its structure must be
    /// exact. Returns null for anything that doesn't verify. Reads the file once, sequentially, in 1 MiB pieces.</summary>
    public static async Task<ListfileIndex?> OpenAsync(string path, string sha256, long length, long names,
        CommunityListfileRelease release, CancellationToken token)
    {
        SafeFileHandle handle;
        try { handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.Asynchronous); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        var keep = false;
        try
        {
            if (RandomAccess.GetLength(handle) != length || length < HeaderBytes) return null;
            var header = new byte[HeaderBytes];
            if (await RandomAccess.ReadAsync(handle, header, 0, token) != HeaderBytes || !header.AsSpan(0, 4).SequenceEqual(Magic)
                || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != 1) return null;
            long count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            var block = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
            long blobOffset = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16)), blobLength = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(24));
            if (count != names || count is < 1 or > ListfileCsvParser.MaxNames || block is < 1 or > 65536
                || blobOffset != HeaderBytes + count * EntryBytes || blobLength < count || blobLength > uint.MaxValue || blobOffset + blobLength != length) return null;
            var sparse = new uint[(int)((count + block - 1) / block)];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1 << 20];
            long position = 0, entry = 0, previous = -1, end = 0;
            while (position < length)
            {
                var read = await FillAsync(handle, buffer.AsMemory(0, (int)Math.Min(buffer.Length, length - position)), position, token);
                if (read == 0) return null;
                hash.AppendData(buffer, 0, read);
                // Entries are 8 bytes at an 8-byte-aligned offset from 32, so a 1 MiB piece never splits one.
                for (var at = position < HeaderBytes ? HeaderBytes - position : 0; at + EntryBytes <= read && entry < count; at += EntryBytes, entry++)
                {
                    var span = buffer.AsSpan((int)at, EntryBytes);
                    long id = BinaryPrimitives.ReadUInt32LittleEndian(span), next = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                    if (id <= previous || next <= end || next > blobLength) return null;
                    if (entry % block == 0) sparse[entry / block] = (uint)id;
                    previous = id; end = next;
                }
                position += read;
            }
            if (entry != count || end != blobLength || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), sha256, StringComparison.Ordinal)) return null;
            keep = true;
            return new ListfileIndex(handle, sparse, block, count, blobOffset, release);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        finally { if (!keep) handle.Dispose(); }
    }

    // Reads until the buffer is full or the file ends, so every piece but the last is whole.
    private static async Task<int> FillAsync(SafeFileHandle handle, Memory<byte> buffer, long offset, CancellationToken token)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await RandomAccess.ReadAsync(handle, buffer[total..], offset + total, token);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    /// <summary>Takes a lease, or false once the index is retired. Every lease must be returned.</summary>
    public bool TryAcquire() { lock (gate) { if (retired) return false; leases++; return true; } }
    public void ReleaseLease() { lock (gate) { leases--; CloseIfDone(); } }
    /// <summary>No new leases; the file closes when the last one is returned.</summary>
    public void Dispose() { lock (gate) { retired = true; CloseIfDone(); } }
    private void CloseIfDone() { if (retired && leases == 0 && !closed) { closed = true; handle.Dispose(); } }

    /// <summary>The names of the FileDataIDs that have one. The caller holds a lease.</summary>
    public async Task<Dictionary<long, string>> LookupAsync(IReadOnlyList<long> ids, CancellationToken token)
    {
        var names = new Dictionary<long, string>();
        var entries = new byte[(block + 1) * EntryBytes];
        foreach (var id in ids.Distinct())
        {
            if (id is < 0 or > uint.MaxValue || sparse.Length == 0 || id < sparse[0]) continue;
            var lo = 0; var hi = sparse.Length - 1;
            while (lo < hi) { var mid = (lo + hi + 1) / 2; if (sparse[mid] <= id) lo = mid; else hi = mid - 1; }
            long first = (long)lo * block, last = Math.Min(count, first + block);
            // One entry before the block gives the start of its first name.
            var from = first == 0 ? 0 : first - 1;
            var bytes = (int)((last - from) * EntryBytes);
            if (await FillAsync(handle, entries.AsMemory(0, bytes), HeaderBytes + from * EntryBytes, token) != bytes) continue;
            int a = 0, b = (int)(last - first) - 1, offset = first == 0 ? 0 : EntryBytes;
            while (a <= b)
            {
                var mid = (a + b) / 2;
                var at = offset + mid * EntryBytes;
                var value = BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(at));
                if (value < id) a = mid + 1;
                else if (value > id) b = mid - 1;
                else
                {
                    var end = BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(at + 4));
                    var start = mid == 0 && first == 0 ? 0u : BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(at - 4));
                    var name = new byte[end - start];
                    if (await FillAsync(handle, name, blobOffset + start, token) != name.Length) break;
                    try { names[id] = Strict.GetString(name); } catch (DecoderFallbackException) { }
                    break;
                }
            }
        }
        return names;
    }
}
