using System.Buffers.Binary;
using System.Security.Cryptography;
using VibeMiner.Builds.Formats;
using VibeMiner.Cdn;

namespace VibeMiner.Builds.Tests.Formats;

/// <summary>Writes synthetic archive indexes and file-indexes. Original metadata only; no downloaded index bytes.</summary>
internal static class ArchiveIndexWriter
{
    public const int Page = 4096, Footer = 28;

    public static CdnKey Key(uint value)
    { var bytes = new byte[16]; BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), value); return new(bytes); }

    public static int RecordBytes(ArchiveIndexKind kind) => kind == ArchiveIndexKind.Archive ? 24 : 20;
    public static int PerPage(ArchiveIndexKind kind) => Page / RecordBytes(kind);

    /// <summary>Rows go in the given order (pass them sorted for a valid index). Offsets are ignored for a file-index.</summary>
    public static (byte[] Bytes, CdnKey Key) Write(IReadOnlyList<(CdnKey EKey, uint Size, uint Offset)> rows, ArchiveIndexKind kind)
    {
        var record = RecordBytes(kind); var perPage = PerPage(kind);
        var pages = (rows.Count + perPage - 1) / perPage;
        var bytes = new byte[pages * Page + pages * 24 + Footer];
        for (var i = 0; i < rows.Count; i++)
        {
            var at = i / perPage * Page + i % perPage * record;
            rows[i].EKey.WriteTo(bytes.AsSpan(at));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at + 16), rows[i].Size);
            if (kind == ArchiveIndexKind.Archive) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(at + 20), rows[i].Offset);
        }
        for (var p = 0; p < pages; p++) rows[Math.Min(rows.Count, (p + 1) * perPage) - 1].EKey.WriteTo(bytes.AsSpan(pages * Page + p * 16));
        var footer = bytes.AsSpan(bytes.Length - Footer);
        footer[8] = 1; footer[11] = 4; footer[12] = (byte)(record - 20); footer[13] = 4; footer[14] = 16; footer[15] = 8;
        BinaryPrimitives.WriteUInt32LittleEndian(footer[16..], (uint)rows.Count);
        return (bytes, Seal(bytes, pages));
    }

    /// <summary>Simple index: keys 2, 4, ... with size 7 and offset 0xfffffff0.</summary>
    public static (byte[] Bytes, CdnKey Key) Even(int count, ArchiveIndexKind kind = ArchiveIndexKind.Archive) =>
        Write(Enumerable.Range(1, count).Select(i => (Key((uint)(i * 2)), 7u, 0xfffffff0u)).ToArray(), kind);

    public static int Pages(byte[] bytes) => (bytes.Length - Footer) / (Page + 24);
    public static Span<byte> TocOf(byte[] bytes) => bytes.AsSpan(Pages(bytes) * Page, Pages(bytes) * 24);
    public static Span<byte> PageOf(byte[] bytes, int page) => bytes.AsSpan(page * Page, Page);
    public static Span<byte> FooterOf(byte[] bytes) => bytes.AsSpan(bytes.Length - Footer);

    /// <summary>Rewrites page hashes, the TOC hash and the footer checksum after a test changes bytes; returns the new key.</summary>
    public static CdnKey Seal(byte[] bytes) => Seal(bytes, Pages(bytes));

    /// <summary>Rewrites only the footer checksum; returns the new key.</summary>
    public static CdnKey SealFooter(byte[] bytes)
    {
        var footer = FooterOf(bytes);
        footer[20..].Clear();
        MD5.HashData(footer[8..]).AsSpan(0, 8).CopyTo(footer[20..]);
        return new(MD5.HashData(footer));
    }

    private static CdnKey Seal(byte[] bytes, int pages)
    {
        var toc = bytes.AsSpan(pages * Page, pages * 24);
        for (var p = 0; p < pages; p++) MD5.HashData(bytes.AsSpan(p * Page, Page)).AsSpan(0, 8).CopyTo(toc[(pages * 16 + p * 8)..]);
        MD5.HashData(toc).AsSpan(0, 8).CopyTo(FooterOf(bytes));
        return SealFooter(bytes);
    }
}
