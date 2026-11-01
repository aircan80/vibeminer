using System.Buffers.Binary;
using System.Security.Cryptography;
using VibeMiner.Cdn;

namespace VibeMiner.Builds.Formats;

/// <summary>Which index layout a key names: an archive's <c>.index</c> or the CDN config's loose <c>file-index</c>.</summary>
internal enum ArchiveIndexKind
{
    /// <summary>EKey16, size4, offset4: 24-byte records, 170 per page.</summary>
    Archive,
    /// <summary>EKey16, size4, no offset: 20-byte records, 204 per page. Members are loose objects.</summary>
    FileIndex,
}

/// <summary>One index record. <see cref="Offset"/> is 0 for a file-index member.</summary>
internal readonly record struct ArchiveIndexEntry(CdnKey EKey, long EncodedSize, long Offset);

internal sealed record ArchiveIndexLimits(long MaxEntries = 10_000_000, int MaxPages = 100_000,
    long MaxIndexBytes = 512L << 20, int MaxTocBytes = 4 << 20);

/// <summary>
/// Reads a whole archive index or file-index, ported from <c>ArchiveIndexParser</c> (Protocols.Tact). The footer MD5 must be
/// <c>indexKey</c>; the footer checksum, widths, exact geometry, TOC hash, every page hash, strictly ascending nonzero keys,
/// TOC maximum keys, nonzero sizes and zero padding are all checked before any entry is returned. Group and prefix indexes
/// are Unsupported. Pure and bounded.
/// </summary>
internal static class ArchiveIndex
{
    public const int FooterBytes = 28;
    public const int PageBytes = 4096;
    private const int TocRowBytes = 24;

    public static IReadOnlyList<ArchiveIndexEntry> Read(ReadOnlySpan<byte> index, CdnKey indexKey, ArchiveIndexKind kind,
        ArchiveIndexLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxEntries < 0 || limits.MaxPages < 0 || limits.MaxIndexBytes < FooterBytes || limits.MaxTocBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
        if (kind is not (ArchiveIndexKind.Archive or ArchiveIndexKind.FileIndex)) throw new ArgumentOutOfRangeException(nameof(kind));
        ct.ThrowIfCancellationRequested();
        var offsetWidth = kind == ArchiveIndexKind.Archive ? 4 : 0;
        var recordBytes = 16 + 4 + offsetWidth;
        var perPage = PageBytes / recordBytes;

        if (index.Length < FooterBytes) throw Corrupt("ARCHIVE_INDEX_INVALID", "An index is at least its 28-byte footer.");
        var footer = index[^FooterBytes..];
        Span<byte> digest = stackalloc byte[16];
        Span<byte> expected = stackalloc byte[16];
        indexKey.WriteTo(expected);
        MD5.HashData(footer, digest);
        if (!CryptographicOperations.FixedTimeEquals(digest, expected))
            throw Corrupt("ARCHIVE_INDEX_INTEGRITY", "The index footer does not match its key.");
        Span<byte> inner = stackalloc byte[20];
        footer[8..].CopyTo(inner); inner[12..].Clear();
        MD5.HashData(inner, digest);
        if (!digest[..8].SequenceEqual(footer[20..])) throw Corrupt("ARCHIVE_INDEX_INTEGRITY", "Index footer checksum mismatch.");
        if (footer[8] != 1 || footer[9] != 0 || footer[10] != 0 || footer[11] != 4 || footer[12] != offsetWidth ||
            footer[13] != 4 || footer[14] != 16 || footer[15] != 8)
            throw new BuildFormatException(BuildFailure.Unsupported, "ARCHIVE_INDEX_UNSUPPORTED",
                "Unsupported index version, flags, page size or field widths for this kind; group and prefix indexes are not read.");

        var entries = (long)BinaryPrimitives.ReadUInt32LittleEndian(footer[16..]);
        var pages = (entries + perPage - 1) / perPage;
        var tocBytes = pages * TocRowBytes;
        var total = pages * PageBytes + tocBytes + FooterBytes;
        if (entries > limits.MaxEntries || pages > limits.MaxPages || tocBytes > limits.MaxTocBytes || total > limits.MaxIndexBytes)
            throw Corrupt("ARCHIVE_INDEX_LIMIT", "The index geometry exceeds the admitted bounds.");
        if (total != index.Length) throw Corrupt("ARCHIVE_INDEX_INTEGRITY", "The index length differs from its footer's geometry.");

        var pageCount = (int)pages;
        var toc = index.Slice(pageCount * PageBytes, (int)tocBytes);
        if (!Hash8(toc, digest, ct).SequenceEqual(footer[..8])) throw Corrupt("ARCHIVE_INDEX_INTEGRITY", "Index TOC hash mismatch.");
        for (var p = 0; p < pageCount; p++)
        {
            if ((p & 255) == 0) ct.ThrowIfCancellationRequested();
            var maximum = toc.Slice(p * 16, 16);
            if (maximum.IndexOfAnyExcept((byte)0) < 0 || p > 0 && toc.Slice((p - 1) * 16, 16).SequenceCompareTo(maximum) >= 0)
                throw Corrupt("ARCHIVE_INDEX_INVALID", "Index maximum keys are zero, duplicate or unordered.");
        }

        var result = new ArchiveIndexEntry[entries];
        var row = 0;
        for (var p = 0; p < pageCount; p++)
        {
            ct.ThrowIfCancellationRequested();
            var page = index.Slice(p * PageBytes, PageBytes);
            if (!Hash8(page, digest, ct).SequenceEqual(toc.Slice(pageCount * 16 + p * 8, 8)))
                throw Corrupt("ARCHIVE_INDEX_INTEGRITY", $"Index page {p} hash mismatch.");
            var count = (int)Math.Min(perPage, entries - (long)p * perPage);
            ReadOnlySpan<byte> previous = p == 0 ? [] : toc.Slice((p - 1) * 16, 16);
            for (var r = 0; r < count; r++)
            {
                var record = page.Slice(r * recordBytes, recordBytes);
                var key = record[..16];
                if (key.IndexOfAnyExcept((byte)0) < 0 || !previous.IsEmpty && previous.SequenceCompareTo(key) >= 0)
                    throw Corrupt("ARCHIVE_INDEX_INVALID", "Index entries are zero, duplicate, unordered or outside their page's key interval.");
                var size = (long)BinaryPrimitives.ReadUInt32BigEndian(record[16..]);
                if (size == 0) throw Corrupt("ARCHIVE_INDEX_INVALID", "An index entry has zero encoded size.");
                var offset = offsetWidth == 0 ? 0L : BinaryPrimitives.ReadUInt32BigEndian(record[20..]);
                result[row++] = new(new CdnKey(key), size, offset);
                previous = key;
            }
            if (!previous.SequenceEqual(toc.Slice(p * 16, 16)))
                throw Corrupt("ARCHIVE_INDEX_INVALID", "An index page's last key differs from its maximum key.");
            if (page[(count * recordBytes)..].IndexOfAnyExcept((byte)0) >= 0)
                throw Corrupt("ARCHIVE_INDEX_INVALID", "Index page padding is nonzero.");
        }
        ct.ThrowIfCancellationRequested();
        return result;
    }

    private static ReadOnlySpan<byte> Hash8(ReadOnlySpan<byte> bytes, Span<byte> digest, CancellationToken ct)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        for (var at = 0; at < bytes.Length; at += 1 << 16)
        { ct.ThrowIfCancellationRequested(); md5.AppendData(bytes.Slice(at, Math.Min(1 << 16, bytes.Length - at))); }
        md5.GetHashAndReset(digest);
        return digest[..8];
    }

    private static BuildFormatException Corrupt(string code, string message) => new(BuildFailure.Corrupt, code, message);
}
