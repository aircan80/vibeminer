using System.Text;

namespace VibeMiner.Files.Names;

/// <summary>
/// Reads wow-listfile's CSV (D105) in chunks of any size; a line may straddle chunks. One <c>FileDataID;name</c> per
/// line, LF or CRLF, FileDataIDs strictly ascending canonical decimals up to 4294967295. A line is at most
/// <see cref="MaxLineBytes"/>, a name 1..<see cref="MaxNameBytes"/> bytes of strict UTF-8 with no control characters (C0, DEL or C1),
/// and there are at most <see cref="MaxNames"/> names in at most <see cref="MaxBlobBytes"/> of name bytes. Blank lines
/// are skipped; the last line may lack its newline. Bidirectional formatting characters (U+061C, U+200E, U+200F,
/// U+202A–U+202E, U+2066–U+2069) are removed from names. Anything else refuses the whole file with
/// <see cref="ListfileFormatException"/>. Same rules as the legacy <c>ListfileCsvParser</c>.
/// After a refusal the parser is failed: every later <see cref="Feed"/> or <see cref="Complete"/> throws
/// <see cref="InvalidOperationException"/>, and its storage is released.
/// The name-bytes cap is 512 MiB because D105 caps the downloaded asset at 512 MiB, and the names are never longer
/// than the file. Storage starts near the expected file size when one is given and otherwise grows geometrically,
/// never past the caps, so memory is bounded by them.
/// </summary>
internal sealed class ListfileParser
{
    public const int MaxNameBytes = 1024;
    public const int MaxLineBytes = 10 + 1 + MaxNameBytes + 1;
    public const int MaxNames = 8_000_000;
    public const int MaxBlobBytes = 512 << 20;
    private const int InitialEntries = 1024;
    private const int InitialBlob = 64 << 10;
    private static readonly UTF8Encoding Strict = new(false, true);
    private readonly int maxNames, maxBlob;
    private readonly byte[] carry = new byte[MaxLineBytes];
    private int carried;
    private long previous = -1;
    private long line;
    private uint[] ids;
    private int[] ends;
    private byte[] blob;
    private int blobLength;
    private ListfileIndex? index;
    private bool failed;
    private readonly ListfileIndex.NameHasher? hasher;

    /// <summary><paramref name="expectedBytes"/> is the CSV's size when known (the asset size), used only to size the
    /// starting storage; a wrong hint costs memory or copies, never correctness.</summary>
    public ListfileParser(long expectedBytes = 0) : this(MaxNames, MaxBlobBytes, null, expectedBytes) { }

    /// <summary>Smaller caps and a replacement name hash, for tests.</summary>
    internal ListfileParser(int maxNames, int maxBlob, ListfileIndex.NameHasher? hasher = null, long expectedBytes = 0)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxNames, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxNames, MaxNames);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBlob, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBlob, MaxBlobBytes);
        this.maxNames = maxNames;
        this.maxBlob = maxBlob;
        this.hasher = hasher;
        // About 40 bytes a line in the real file; the blob is a little smaller than the file.
        var expected = Math.Max(expectedBytes, 0);
        var entries = (int)Math.Min(Math.Max(expected / 40, InitialEntries), maxNames);
        ids = new uint[entries];
        ends = new int[entries];
        blob = new byte[(int)Math.Min(Math.Max(expected, InitialBlob), maxBlob)];
    }

    public long Count { get; private set; }

    public void Feed(ReadOnlySpan<byte> chunk)
    {
        Usable();
        if (index is not null) throw new InvalidOperationException("The parser is complete.");
        try { FeedLines(chunk); }
        catch (ListfileFormatException) { Fail(); throw; }
    }

    private void FeedLines(ReadOnlySpan<byte> chunk)
    {
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

    /// <summary>Parses a last line without a newline and returns the index. An empty file, or one with no names, is
    /// refused. Calling it again returns the same index. Arrays with more than an eighth unused are trimmed.</summary>
    public ListfileIndex Complete()
    {
        Usable();
        if (index is not null) return index;
        try
        {
            if (carried > 0) { var length = carried; carried = 0; Line(carry.AsSpan(0, length)); }
            if (Count == 0) throw Invalid("The file has no names.");
        }
        catch (ListfileFormatException) { Fail(); throw; }
        var count = (int)Count;
        if (Wasteful(ids.Length, count)) { Array.Resize(ref ids, count); Array.Resize(ref ends, count); }
        if (Wasteful(blob.Length, blobLength)) Array.Resize(ref blob, blobLength);
        index = new ListfileIndex(ids, ends, blob, count, hasher);
        ids = []; ends = []; blob = [];
        return index;
    }

    private static bool Wasteful(int capacity, int used) => capacity - used > capacity / 8;

    private void Usable()
    {
        if (failed) throw new InvalidOperationException("The parser refused its input.");
    }

    private void Fail()
    {
        failed = true;
        ids = []; ends = []; blob = [];
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
        for (var i = 0; i < name.Length; i++)
        {
            // C0, DEL, and C1 (U+0080..U+009F, UTF-8 C2 80..C2 9F, NEL among them).
            if (name[i] is < 0x20 or 0x7f || name[i] == 0xC2 && i + 1 < name.Length && name[i + 1] is >= 0x80 and <= 0x9F)
                throw Invalid($"Line {line} has a control character in its name.");
        }
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
        if (Count == maxNames) throw Invalid($"The file has more than {maxNames:N0} names.");
        if ((long)blobLength + length > maxBlob) throw Invalid("The names are larger than VibeMiner allows.");
        EnsureBlob(blobLength + length);
        kept[..length].CopyTo(blob.AsSpan(blobLength));
        blobLength += length;
        EnsureEntries((int)Count + 1);
        previous = id;
        ids[Count] = (uint)id;
        ends[Count] = blobLength;
        Count++;
    }

    private void EnsureEntries(int needed)
    {
        if (needed <= ids.Length) return;
        var size = (int)Math.Min((long)ids.Length * 2, maxNames);
        Array.Resize(ref ids, size);
        Array.Resize(ref ends, size);
    }

    private void EnsureBlob(int needed)
    {
        if (needed <= blob.Length) return;
        var size = (int)Math.Min(Math.Max((long)blob.Length * 2, needed), maxBlob);
        Array.Resize(ref blob, size);
    }

    private static ListfileFormatException Invalid(string detail) => new(detail);
}
