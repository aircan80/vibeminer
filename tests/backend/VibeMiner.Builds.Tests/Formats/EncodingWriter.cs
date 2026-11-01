using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using VibeMiner.Cdn;

namespace VibeMiner.Builds.Tests.Formats;

/// <summary>Builds synthetic EN v1 ENCODING files: raw pages for format tests, or whole files from (CKey, EKey) rows.</summary>
internal static class EncodingWriter
{
    public const int PageBytes = 1024;
    public static readonly byte[] DefaultSpecs = Encoding.ASCII.GetBytes("b:*=z\0b:*=n\0");

    /// <summary>Header, ESpec table, both indices (first key + page MD5) and pages, then the optional trailer.</summary>
    public static byte[] Build(byte[][] contentPages, byte[][] encodedPages, byte[]? specs = null, byte[]? trailer = null)
    {
        specs ??= DefaultSpecs;
        using var output = new MemoryStream();
        output.Write("EN"u8); output.Write([1, 16, 16]);
        U16(output, PageBytes / 1024); U16(output, PageBytes / 1024);
        U32(output, (uint)contentPages.Length); U32(output, (uint)encodedPages.Length);
        output.WriteByte(0); U32(output, (uint)specs.Length); output.Write(specs);
        foreach (var page in contentPages) { output.Write(page.AsSpan(6, 16)); output.Write(MD5.HashData(page)); }
        foreach (var page in contentPages) output.Write(page);
        foreach (var page in encodedPages) { output.Write(page.AsSpan(0, 16)); output.Write(MD5.HashData(page)); }
        foreach (var page in encodedPages) output.Write(page);
        if (trailer is not null) output.Write(trailer);
        return output.ToArray();
    }

    /// <summary>A whole file with one EKey per CKey, rows sorted and packed into 1 KiB pages, all on ESpec 0.</summary>
    public static byte[] FromFiles(IEnumerable<(CdnKey CKey, long DecodedSize, CdnKey EKey, long EncodedSize)> files)
    {
        var rows = files.ToList();
        var contents = rows.OrderBy(r => r.CKey).Select(r => Content(r.CKey, r.DecodedSize, r.EKey));
        var encodeds = rows.DistinctBy(r => r.EKey).OrderBy(r => r.EKey).Select(r => Encoded(r.EKey, r.EncodedSize, 0));
        return Build(Pack(contents), Pack(encodeds), Encoding.ASCII.GetBytes("n\0"));
    }

    public static byte[] Page(params byte[][] records)
    {
        var page = new byte[PageBytes]; var offset = 0;
        foreach (var record in records) { record.CopyTo(page, offset); offset += record.Length; }
        return page;
    }

    public static byte[] Content(uint key, long size, params uint[] eKeys) => Content(Key(key), size, eKeys.Select(Key).ToArray());

    public static byte[] Content(CdnKey key, long size, params CdnKey[] eKeys)
    {
        using var row = new MemoryStream(); row.WriteByte((byte)eKeys.Length); U40(row, size); row.Write(key.ToBytes());
        foreach (var eKey in eKeys) row.Write(eKey.ToBytes());
        return row.ToArray();
    }

    public static byte[] Encoded(uint key, long size, uint spec = 0) => Encoded(Key(key), size, spec);

    public static byte[] Encoded(CdnKey key, long size, uint spec)
    {
        using var row = new MemoryStream(); row.Write(key.ToBytes()); U32(row, spec); U40(row, size); return row.ToArray();
    }

    public static byte[] KeyBytes(uint value) { var bytes = new byte[16]; BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), value); return bytes; }
    public static CdnKey Key(uint value) => new(KeyBytes(value));

    private static byte[][] Pack(IEnumerable<byte[]> records)
    {
        var pages = new List<byte[]>();
        var current = new List<byte[]>(); var used = 0;
        foreach (var record in records)
        {
            if (used + record.Length > PageBytes) { pages.Add(Page([.. current])); current.Clear(); used = 0; }
            current.Add(record); used += record.Length;
        }
        if (current.Count > 0) pages.Add(Page([.. current]));
        return [.. pages];
    }

    private static void U16(Stream stream, int value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)value); stream.Write(b); }
    private static void U32(Stream stream, uint value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, value); stream.Write(b); }
    private static void U40(Stream stream, long value) { stream.WriteByte((byte)(value >> 32)); U32(stream, (uint)value); }
}
