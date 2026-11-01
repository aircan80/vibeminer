using System.Text;

namespace VibeMiner.Files.Names;

/// <summary>Receives one validated listfile entry: its FileDataID and its name's UTF-8 bytes with direction marks removed. The span is
/// only valid during the call.</summary>
internal delegate void ListfileEntrySink(uint fileDataId, ReadOnlySpan<byte> name);

/// <summary>
/// Reads wow-listfile's CSV (D105) in chunks of any size; a line may straddle chunks. One <c>FileDataID;name</c> per
/// line, LF or CRLF, FileDataIDs strictly ascending canonical decimals up to 4294967295. A line is at most
/// <see cref="MaxLineBytes"/>, a name 1..<see cref="MaxNameBytes"/> bytes of strict UTF-8 with no control characters (C0, DEL or C1),
/// and there are at most <see cref="MaxNames"/> names in at most <see cref="MaxBlobBytes"/> of name bytes. Blank lines
/// are skipped; the last line may lack its newline. Bidirectional formatting characters (U+061C, U+200E, U+200F,
/// U+202A–U+202E, U+2066–U+2069) are removed from names. Anything else refuses the whole file with
/// <see cref="ListfileFormatException"/>. Same rules as the legacy <c>ListfileCsvParser</c>.
/// Nothing is kept (#186): each entry goes to the <see cref="ListfileEntrySink"/> as its line is parsed, and <see cref="Finish"/>
/// ends the file. After a refusal the parser is failed: every later <see cref="Feed"/> or <see cref="Finish"/> throws
/// <see cref="InvalidOperationException"/>.
/// The name-bytes cap is 512 MiB because D105 caps the downloaded asset at 512 MiB, and the names are never longer
/// than the file.
/// </summary>
internal sealed class ListfileParser
{
    public const int MaxNameBytes = 1024;
    public const int MaxLineBytes = 10 + 1 + MaxNameBytes + 1;
    public const int MaxNames = 8_000_000;
    public const int MaxBlobBytes = 512 << 20;
    private static readonly UTF8Encoding Strict = new(false, true);
    private readonly int maxNames, maxBlob;
    private readonly byte[] carry = new byte[MaxLineBytes];
    private int carried;
    private long previous = -1;
    private long line;
    private long blobLength;
    private bool failed;
    private readonly ListfileEntrySink sink;
    private bool finished;

    /// <summary>Every entry goes to <paramref name="sink"/>. Smaller caps for tests.</summary>
    internal ListfileParser(ListfileEntrySink sink, int maxNames = MaxNames, int maxBlob = MaxBlobBytes)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxNames, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxNames, MaxNames);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBlob, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxBlob, MaxBlobBytes);
        this.sink = sink;
        this.maxNames = maxNames;
        this.maxBlob = maxBlob;
    }

    public long Count { get; private set; }

    public void Feed(ReadOnlySpan<byte> chunk)
    {
        Usable();
        if (finished) throw new InvalidOperationException("The parser is complete.");
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

    /// <summary>The end of the file: parses a last line without a newline (to the sink) and returns the name count. An empty file,
    /// or one with no names, is refused. Calling it again returns the same count.</summary>
    public long Finish()
    {
        Usable();
        if (finished) return Count;
        try
        {
            if (carried > 0) { var length = carried; carried = 0; Line(carry.AsSpan(0, length)); }
            if (Count == 0) throw Invalid("The file has no names.");
        }
        catch (ListfileFormatException) { Fail(); throw; }
        finished = true;
        return Count;
    }

    private void Usable()
    {
        if (failed) throw new InvalidOperationException("The parser refused its input.");
    }

    private void Fail()
    {
        failed = true;
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
        if (blobLength + length > maxBlob) throw Invalid("The names are larger than VibeMiner allows.");
        blobLength += length;
        previous = id;
        Count++;
        sink((uint)id, kept[..length]);
    }

    private static ListfileFormatException Invalid(string detail) => new(detail);
}
