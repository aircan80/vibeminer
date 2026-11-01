using System.Buffers;
using System.Text;

namespace VibeMiner.Formats.Generated;

/// <summary>
/// Row encoding rows-v1: the record bytes of one <see cref="Db2Row"/> (its id is the record id). A flags byte (1 copy, 2 has copy
/// source, 4 has text issues; other bits 0), the copy source as a varint when flagged, the value count as a varint, each value as a
/// varint UTF-8 length and its bytes, then when flagged the text issues: a varint count, each a varint column index, then code and
/// raw hex as length-prefixed UTF-8. Varints are unsigned LEB128. A null issue list and an empty one read back as they were written.
/// </summary>
internal static class RowCodec
{
    private const byte Copy = 1, HasSource = 2, HasIssues = 4;
    private static readonly UTF8Encoding Strict = new(false, true);
    private static readonly string[] Ascii = [.. Enumerable.Range(0, 0x80).Select(c => ((char)c).ToString())];
    private static readonly string[] Decimals = [.. Enumerable.Range(0, 1000).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture))];

    /// <summary>Encodes into <paramref name="output"/> (cleared first). A string that isn't valid UTF-16 throws <see cref="ArgumentException"/>.</summary>
    public static void Encode(Db2Row row, ArrayBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(row.Values, nameof(row));
        output.ResetWrittenCount();
        var flags = (byte)((row.IsCopy ? Copy : 0) | (row.CopySourceId is not null ? HasSource : 0) | (row.TextIssues is not null ? HasIssues : 0));
        output.GetSpan(1)[0] = flags; output.Advance(1);
        if (row.CopySourceId is { } source) WriteVar(output, source);
        WriteVar(output, (ulong)row.Values.Count);
        foreach (var value in row.Values) WriteString(output, value ?? throw new ArgumentException("A row value is null.", nameof(row)));
        if (row.TextIssues is { } issues)
        {
            WriteVar(output, (ulong)issues.Count);
            foreach (var issue in issues)
            {
                if (issue is null) throw new ArgumentException("A text issue is null.", nameof(row));
                ArgumentOutOfRangeException.ThrowIfNegative(issue.ColumnIndex, nameof(row));
                WriteVar(output, (ulong)issue.ColumnIndex);
                WriteString(output, issue.Code ?? throw new ArgumentException("A text issue code is null.", nameof(row)));
                WriteString(output, issue.RawBytesHex ?? throw new ArgumentException("A text issue's raw hex is null.", nameof(row)));
            }
        }
    }

    /// <summary>Decodes one record; anything out of shape throws <see cref="VibeMiner.Generated.GeneratedStoreException"/> (Corrupt).</summary>
    public static Db2Row Decode(uint id, ReadOnlySpan<byte> bytes)
    {
        var at = 0;
        if (bytes.Length == 0) throw TableJson.Corrupt("A row record is empty.");
        var flags = bytes[at++];
        if ((flags & ~(Copy | HasSource | HasIssues)) != 0) throw TableJson.Corrupt("A row record has unknown flags.");
        uint? source = (flags & HasSource) != 0 ? (uint)ReadVar(bytes, ref at, uint.MaxValue) : null;
        var count = Count(bytes, ref at);
        var values = new string[count];
        for (var i = 0; i < count; i++) values[i] = ReadString(bytes, ref at);
        List<Db2TextIssue>? issues = null;
        if ((flags & HasIssues) != 0)
        {
            var n = Count(bytes, ref at);
            issues = new List<Db2TextIssue>(n);
            for (var i = 0; i < n; i++)
            {
                var column = (int)ReadVar(bytes, ref at, int.MaxValue);
                issues.Add(new Db2TextIssue(column, ReadString(bytes, ref at), ReadString(bytes, ref at)));
            }
        }
        if (at != bytes.Length) throw TableJson.Corrupt("A row record has bytes after its end.");
        return new Db2Row(id, (flags & Copy) != 0, source, values, issues);
    }

    private static void WriteVar(ArrayBufferWriter<byte> output, ulong value)
    {
        var span = output.GetSpan(10);
        var n = 0;
        while (value >= 0x80) { span[n++] = (byte)(value | 0x80); value >>= 7; }
        span[n++] = (byte)value;
        output.Advance(n);
    }

    private static void WriteString(ArrayBufferWriter<byte> output, string value)
    {
        int length;
        try { length = Strict.GetByteCount(value); }
        catch (EncoderFallbackException e) { throw new ArgumentException("A row string isn't valid UTF-16.", e); }
        WriteVar(output, (ulong)length);
        output.Advance(Strict.GetBytes(value, output.GetSpan(Math.Max(length, 1))));
    }

    private static ulong ReadVar(ReadOnlySpan<byte> bytes, ref int at, ulong max)
    {
        ulong value = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (at >= bytes.Length) throw TableJson.Corrupt("A row record ends inside a number.");
            var b = bytes[at++];
            if (shift == 63 && b > 1) break;
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value <= max ? value : throw TableJson.Corrupt("A row record has an oversized number.");
        }
        throw TableJson.Corrupt("A row record has an oversized number.");
    }

    // Every value and issue takes at least one byte, so a count over the bytes left is out of shape.
    private static int Count(ReadOnlySpan<byte> bytes, ref int at)
    {
        var count = (int)ReadVar(bytes, ref at, int.MaxValue);
        return count <= bytes.Length - at ? count : throw TableJson.Corrupt("A row record's count is over its bytes.");
    }

    /// <summary>The value of 2-3 ASCII digits without a leading zero, else -1.</summary>
    private static int SmallDecimal(ReadOnlySpan<byte> digits)
    {
        if (digits.Length < 2 || digits[0] == (byte)'0') return -1;
        var n = 0;
        foreach (var d in digits)
        {
            if (d is < (byte)'0' or > (byte)'9') return -1;
            n = n * 10 + d - '0';
        }
        return n;
    }

    private static string ReadString(ReadOnlySpan<byte> bytes, ref int at)
    {
        var length = (int)ReadVar(bytes, ref at, int.MaxValue);
        if (length > bytes.Length - at) throw TableJson.Corrupt("A row string runs past the record's end.");
        // "", one-character ASCII strings and the decimals "10"-"999" (small ints, the commonest values) come from one shared
        // instance each; the values are equal.
        if (length == 0) return "";
        if (length == 1 && bytes[at] < 0x80) return Ascii[bytes[at++]];
        if (length <= 3 && SmallDecimal(bytes.Slice(at, length)) is var n && n >= 0) { at += length; return Decimals[n]; }
        string value;
        try { value = Strict.GetString(bytes.Slice(at, length)); }
        catch (DecoderFallbackException e) { throw TableJson.Corrupt("A row string isn't valid UTF-8.", e); }
        at += length;
        return value;
    }
}
