using System.Buffers.Binary;

namespace VibeMiner.Protocols.Tact.Archives;

/// <summary>Compact native metadata. The hexadecimal key is allocated only when requested.</summary>
public readonly record struct ArchiveIndexEntry(ulong KeyHigh, ulong KeyLow, uint EncodedSize, uint Offset)
{
    public string EncodingKey => $"{KeyHigh:x16}{KeyLow:x16}";
    public long EncodedBytes => EncodedSize;
    public long ArchiveOffset => Offset;
}

public static partial class ArchiveIndexParser
{
    /// <summary>Returns a page bound to the authenticated TOC; there is no page for an empty index.</summary>
    public static ArchiveIndexPage GetPage(ArchiveIndexToc toc, int ordinal, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toc);
        cancellationToken.ThrowIfCancellationRequested();
        if (ordinal < 0 || ordinal >= toc.Footer.PageCount) throw new ArgumentOutOfRangeException(nameof(ordinal));
        return new(toc, ordinal);
    }

    /// <summary>
    /// Authenticates all native page bytes, entries, bounds and padding before returning at most 170
    /// compact entries. It shares the selected-lookup validator; no provisional row escapes on failure.
    /// </summary>
    public static IReadOnlyList<ArchiveIndexEntry> EnumeratePage(ArchiveIndexToc toc, ArchiveIndexPage page,
        ReadOnlySpan<byte> bytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toc);
        ArgumentNullException.ThrowIfNull(page);
        cancellationToken.ThrowIfCancellationRequested();
        if (!ReferenceEquals(page.Owner, toc)) throw Error("ARCHIVE_INDEX_INVALID", "Archive page belongs to a different authenticated table of contents.");
        if (bytes.Length != PageBytes) throw Error("ARCHIVE_INDEX_INVALID", "An archive page must contain exactly 4096 bytes.");
        // Own the bounded input so validation and extraction see the same bytes.
        var owned = bytes.ToArray();
        _ = Lookup(toc, page, owned, Convert.ToHexStringLower(toc.Maximum(page.Ordinal)), cancellationToken);
        var count = (int)Math.Min(EntriesPerPage, toc.Footer.EntryCount - (long)page.Ordinal * EntriesPerPage);
        var entries = new ArchiveIndexEntry[count];
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = owned.AsSpan(i * RecordBytes, RecordBytes);
            entries[i] = new(BinaryPrimitives.ReadUInt64BigEndian(row), BinaryPrimitives.ReadUInt64BigEndian(row[8..]),
                BinaryPrimitives.ReadUInt32BigEndian(row[16..]), BinaryPrimitives.ReadUInt32BigEndian(row[20..]));
        }
        return entries;
    }
}
