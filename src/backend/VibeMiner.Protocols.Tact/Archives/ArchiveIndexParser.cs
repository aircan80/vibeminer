using System.Buffers.Binary;
using System.Security.Cryptography;
using VibeMiner.Domain;

namespace VibeMiner.Protocols.Tact.Archives;

public sealed record ArchiveIndexLimits(long MaxEntries = 10_000_000, int MaxPages = 100_000,
    long MaxIndexBytes = 512L << 20, int MaxTocBytes = 4 << 20);

/// <summary>Authenticated archive-only footer geometry; configuration size hints are not proof.</summary>
public sealed class ArchiveIndexFooter
{
    private readonly byte[] tocHash;
    internal ArchiveIndexFooter(string identity, long entries, int pages, long indexBytes, byte[] tocHash)
    { IndexIdentity = identity; EntryCount = entries; PageCount = pages; IndexBytes = indexBytes; this.tocHash = tocHash; }
    public string IndexIdentity { get; }
    public long EntryCount { get; }
    public int PageCount { get; }
    public long IndexBytes { get; }
    public long TocOffset => (long)PageCount * ArchiveIndexParser.PageBytes;
    public int TocBytes => PageCount * 24;
    internal ReadOnlySpan<byte> TocHash => tocHash;
}

public sealed class ArchiveIndexToc
{
    private readonly byte[] bytes;
    internal ArchiveIndexToc(ArchiveIndexFooter footer, byte[] bytes) { Footer = footer; this.bytes = bytes; }
    internal ArchiveIndexFooter Footer { get; }
    internal ReadOnlySpan<byte> Maximum(int page) => bytes.AsSpan(page * 16, 16);
    internal ReadOnlySpan<byte> PageHash(int page) => bytes.AsSpan(Footer.PageCount * 16 + page * 8, 8);

    /// <summary>Native boundaries are maximum keys: select the first maximum at or above target.</summary>
    public ArchiveIndexPage? FindPage(string encodingKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = ArchiveIndexParser.ParseKey(encodingKey);
        var lower = 0; var upper = Footer.PageCount;
        while (lower < upper)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var middle = lower + (upper - lower) / 2;
            if (Maximum(middle).SequenceCompareTo(target) < 0) lower = middle + 1;
            else upper = middle;
        }
        return lower == Footer.PageCount ? null : new(this, lower);
    }
}

/// <summary>A page reference cannot be supplied independently of its authenticated TOC.</summary>
public sealed class ArchiveIndexPage
{
    internal ArchiveIndexPage(ArchiveIndexToc owner, int ordinal) { Owner = owner; Ordinal = ordinal; }
    internal ArchiveIndexToc Owner { get; }
    public int Ordinal { get; }
    public long Offset => (long)Ordinal * ArchiveIndexParser.PageBytes;
    public int Length => ArchiveIndexParser.PageBytes;
}

public sealed record ArchiveIndexMatch(string IndexIdentity, string EncodingKey, long ArchiveOffset, long EncodedBytes)
{
    public long ArchiveEndExclusive => checked(ArchiveOffset + EncodedBytes);
}

/// <summary>
/// Original bounded archive-index authentication, continuing our Java parser at
/// 8f353d046d961108f7acc86bfe1381004467afd5 (protocol-tact/.../archive/ArchiveIndexParser.java).
/// Full EKey16, offset4, size4, checksum8 and 4 KiB pages only. Group/file/prefix layouts are
/// explicitly unsupported. No I/O, source selection or archive-byte integrity is implied.
/// </summary>
public static partial class ArchiveIndexParser
{
    public const int FooterBytes = 28;
    public const int PageBytes = 4096;
    private const int RecordBytes = 24;
    private const int EntriesPerPage = PageBytes / RecordBytes;

    public static ArchiveIndexFooter AuthenticateFooter(ReadOnlySpan<byte> footer, string expectedIndexIdentity,
        long observedIndexBytes, ArchiveIndexLimits limits, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(limits);
        cancellationToken.ThrowIfCancellationRequested();
        if (limits.MaxEntries < 0 || limits.MaxPages < 0 || limits.MaxIndexBytes < FooterBytes || limits.MaxTocBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(limits));
        var expected = ParseKey(expectedIndexIdentity);
        if (footer.Length != FooterBytes) throw Error("ARCHIVE_INDEX_INVALID", "An archive footer must contain exactly 28 bytes.");
        var owned = footer.ToArray();
        if (!CryptographicOperations.FixedTimeEquals(Digest(owned, cancellationToken), expected))
            throw Error("ARCHIVE_INDEX_INTEGRITY", "The archive-index footer does not match its filename identity.");
        var internalFooter = owned.AsSpan(8, 20).ToArray();
        internalFooter.AsSpan(12, 8).Clear();
        RequireHash(internalFooter, owned.AsSpan(20, 8), "Archive footer checksum mismatch.", cancellationToken);
        if (owned[8] != 1 || owned[9] != 0 || owned[10] != 0 || owned[11] != 4 ||
            owned[12] != 4 || owned[13] != 4 || owned[14] != 16 || owned[15] != 8)
            throw Error("ARCHIVE_INDEX_UNSUPPORTED", "Unsupported archive index version, flags, widths or checksum layout; group, file and prefix indexes are not admitted.");
        var entries = (long)BinaryPrimitives.ReadUInt32LittleEndian(owned.AsSpan(16, 4));
        var pages = (entries + EntriesPerPage - 1) / EntriesPerPage;
        var tocBytes = pages * 24;
        var total = pages * PageBytes + tocBytes + FooterBytes;
        if (entries > limits.MaxEntries || pages > limits.MaxPages || tocBytes > limits.MaxTocBytes ||
            tocBytes > int.MaxValue || total > limits.MaxIndexBytes)
            throw Error("ARCHIVE_INDEX_LIMIT", "Authenticated archive-index geometry exceeds the admitted bounds.");
        if (total != observedIndexBytes)
            throw Error("ARCHIVE_INDEX_INTEGRITY", "Observed archive-index length differs from its authenticated geometry.");
        cancellationToken.ThrowIfCancellationRequested();
        return new(Convert.ToHexStringLower(expected), entries, (int)pages, total, owned.AsSpan(0, 8).ToArray());
    }

    public static ArchiveIndexToc AuthenticateToc(ArchiveIndexFooter footer, ReadOnlySpan<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(footer);
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length != footer.TocBytes) throw Error("ARCHIVE_INDEX_INVALID", "Archive table-of-contents length mismatch.");
        var owned = bytes.ToArray();
        RequireHash(owned, footer.TocHash, "Archive table-of-contents checksum mismatch.", cancellationToken);
        for (var page = 0; page < footer.PageCount; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var maximum = owned.AsSpan(page * 16, 16);
            if (maximum.IndexOfAnyExcept((byte)0) < 0 || page > 0 && owned.AsSpan((page - 1) * 16, 16).SequenceCompareTo(maximum) >= 0)
                throw Error("ARCHIVE_INDEX_INVALID", "Archive maximum-key boundaries are zero, duplicate or unordered.");
        }
        return new(footer, owned);
    }

    public static ArchiveIndexMatch? Lookup(ArchiveIndexToc toc, ArchiveIndexPage page, ReadOnlySpan<byte> bytes,
        string encodingKey, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(toc);
        ArgumentNullException.ThrowIfNull(page);
        cancellationToken.ThrowIfCancellationRequested();
        var target = ParseKey(encodingKey);
        if (!ReferenceEquals(page.Owner, toc)) throw Error("ARCHIVE_INDEX_INVALID", "Archive page belongs to a different authenticated table of contents.");
        if (bytes.Length != PageBytes) throw Error("ARCHIVE_INDEX_INVALID", "An archive page must contain exactly 4096 bytes.");
        if (target.AsSpan().SequenceCompareTo(toc.Maximum(page.Ordinal)) > 0 ||
            page.Ordinal > 0 && target.AsSpan().SequenceCompareTo(toc.Maximum(page.Ordinal - 1)) <= 0)
            throw Error("ARCHIVE_INDEX_INVALID", "The requested key does not belong to this archive page interval.");
        var owned = bytes.ToArray();
        RequireHash(owned, toc.PageHash(page.Ordinal), "Archive padded-page checksum mismatch.", cancellationToken);
        var count = (int)Math.Min(EntriesPerPage, toc.Footer.EntryCount - (long)page.Ordinal * EntriesPerPage);
        ReadOnlySpan<byte> previous = page.Ordinal == 0 ? [] : toc.Maximum(page.Ordinal - 1);
        ArchiveIndexMatch? found = null;
        for (var record = 0; record < count; record++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var at = record * RecordBytes;
            var key = owned.AsSpan(at, 16);
            if (key.IndexOfAnyExcept((byte)0) < 0 || !previous.IsEmpty && previous.SequenceCompareTo(key) >= 0)
                throw Error("ARCHIVE_INDEX_INVALID", "Archive entries are zero, duplicate, unordered or outside their preceding maximum boundary.");
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(owned.AsSpan(at + 16, 4));
            var offset = (long)BinaryPrimitives.ReadUInt32BigEndian(owned.AsSpan(at + 20, 4));
            if (size == 0) throw Error("ARCHIVE_INDEX_INVALID", "An archive entry has zero encoded length.");
            if (key.SequenceEqual(target)) found = new(toc.Footer.IndexIdentity, Convert.ToHexStringLower(target), offset, size);
            previous = key;
        }
        if (!previous.SequenceEqual(toc.Maximum(page.Ordinal)))
            throw Error("ARCHIVE_INDEX_INVALID", "The archive page's last key differs from its authenticated maximum boundary.");
        for (var at = count * RecordBytes; at < PageBytes; at++)
        {
            if ((at & 511) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (owned[at] != 0) throw Error("ARCHIVE_INDEX_INVALID", "Archive page padding is nonzero.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return found;
    }

    internal static byte[] ParseKey(string key)
    {
        if (key is null || key.Length != 32 || key.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("A complete 16-byte hexadecimal key is required.", nameof(key));
        return Convert.FromHexString(key);
    }

    private static void RequireHash(ReadOnlySpan<byte> bytes, ReadOnlySpan<byte> expected, string message, CancellationToken token)
    {
        if (!CryptographicOperations.FixedTimeEquals(Digest(bytes, token).AsSpan(0, 8), expected))
            throw Error("ARCHIVE_INDEX_INTEGRITY", message);
    }
    private static byte[] Digest(ReadOnlySpan<byte> bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        for (var at = 0; at < bytes.Length; at += 8192)
        { token.ThrowIfCancellationRequested(); hash.AppendData(bytes.Slice(at, Math.Min(8192, bytes.Length - at))); }
        token.ThrowIfCancellationRequested();
        return hash.GetHashAndReset();
    }
    private static DiscoveryException Error(string code, string message) => new(code, message);
}
