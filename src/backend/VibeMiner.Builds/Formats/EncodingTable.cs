using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using VibeMiner.Cdn;

namespace VibeMiner.Builds.Formats;

/// <summary>Bounds for one ENCODING file. Header claims are checked against these before anything is sized from them.</summary>
internal sealed record EncodingLimits(
    long MaxDecodedBytes = 1L << 30,
    long MaxRecords = 50_000_000,
    long MaxCandidateReferences = 100_000_000,
    int MaxPages = 1_000_000,
    int MaxIndexBytes = 32 << 20,
    int MaxPageBytes = 64 << 10,
    int MaxRecordsPerPage = 65_536,
    int MaxKeysPerContent = 255,
    int MaxSpecificationStrings = 1_000_000,
    int MaxSpecificationBytes = 32 << 20,
    int MaxTrailerBytes = 1 << 20);

/// <summary>One content row: the CKey, its decoded size and every EKey that encodes it, in file order.</summary>
internal sealed record EncodingContent(CdnKey CKey, long DecodedSize, IReadOnlyList<CdnKey> EKeys);

/// <summary>One encoded row: the EKey, its encoded (BLTE) size and its index into the ESpec string table.</summary>
internal readonly record struct EncodingEncoded(CdnKey EKey, long EncodedSize, int SpecIndex);

/// <summary>
/// An EN v1 ENCODING file over decoded bytes held in memory. <see cref="Open"/> validates the whole file once (magic, layout,
/// page MD5s, key order, padding, ESpec table, trailer) and keeps only the page indices. Lookups binary-search an index,
/// copy one page, check its MD5 again and scan it; they share no mutable state, so any number may run at once.
/// </summary>
internal sealed class EncodingTable
{
    private const int HeaderBytes = 22, IndexEntryBytes = 32, EncodedRecordBytes = 25;
    private readonly ReadOnlyMemory<byte> data;
    private readonly Directory content, encoded;

    private EncodingTable(ReadOnlyMemory<byte> data, Directory content, Directory encoded, long contentCount, long encodedCount)
    {
        this.data = data; this.content = content; this.encoded = encoded;
        ContentCount = contentCount; EncodedCount = encodedCount;
    }

    /// <summary>Number of content (CKey) rows.</summary>
    public long ContentCount { get; }
    /// <summary>Number of encoded (EKey) rows.</summary>
    public long EncodedCount { get; }

    /// <summary>Validates <paramref name="decoded"/> in full. The caller keeps the memory alive and unchanged while the table is used.</summary>
    public static EncodingTable Open(ReadOnlyMemory<byte> decoded, EncodingLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(limits);
        ValidateLimits(limits);
        ct.ThrowIfCancellationRequested();
        var bytes = decoded.Span;
        if (bytes.Length > limits.MaxDecodedBytes) throw Limit("EN exceeds its decoded-byte limit.");
        if (bytes.Length < HeaderBytes) throw Invalid("EN is truncated.");
        if (bytes[0] != 'E' || bytes[1] != 'N') throw Invalid("EN signature is invalid.");
        if (bytes[2] != 1 || bytes[3] != 16 || bytes[4] != 16 || bytes[17] != 0)
            throw new BuildFormatException(BuildFailure.Unsupported, "EN_UNSUPPORTED", "EN layout is unsupported.");
        var contentPageBytes = BinaryPrimitives.ReadUInt16BigEndian(bytes[5..]) * 1024;
        var encodedPageBytes = BinaryPrimitives.ReadUInt16BigEndian(bytes[7..]) * 1024;
        long contentPages = BinaryPrimitives.ReadUInt32BigEndian(bytes[9..]);
        long encodedPages = BinaryPrimitives.ReadUInt32BigEndian(bytes[13..]);
        long specBytes = BinaryPrimitives.ReadUInt32BigEndian(bytes[18..]);
        if (contentPageBytes == 0 || encodedPageBytes == 0) throw Invalid("EN page size is zero.");
        if (contentPageBytes > limits.MaxPageBytes || encodedPageBytes > limits.MaxPageBytes ||
            contentPages + encodedPages > limits.MaxPages || contentPages * IndexEntryBytes > limits.MaxIndexBytes ||
            encodedPages * IndexEntryBytes > limits.MaxIndexBytes || specBytes > limits.MaxSpecificationBytes)
            throw Limit("EN declared page, index or specification limits are exceeded.");
        var minimum = HeaderBytes + specBytes + contentPages * (IndexEntryBytes + (long)contentPageBytes) +
            encodedPages * (IndexEntryBytes + (long)encodedPageBytes);
        if (minimum > limits.MaxDecodedBytes || contentPages + encodedPages > limits.MaxRecords ||
            contentPages > limits.MaxCandidateReferences)
            throw Limit("EN declared minimum work exceeds its limits.");
        if (minimum > bytes.Length) throw Invalid("EN is truncated.");

        var at = HeaderBytes;
        var specCount = CountSpecifications(bytes.Slice(at, (int)specBytes), limits, ct);
        at += (int)specBytes;
        var counts = new Counts();
        var contentDirectory = ReadTable(bytes, ref at, (int)contentPages, contentPageBytes, true, specCount, limits, counts, ct);
        var encodedDirectory = ReadTable(bytes, ref at, (int)encodedPages, encodedPageBytes, false, specCount, limits, counts, ct);
        var trailer = bytes[at..];
        for (var i = 0; i < trailer.Length; i++)
        {
            if (i == limits.MaxTrailerBytes) throw Limit("EN trailer exceeds its byte limit.");
            if (trailer[i] is < 32 or > 126)
                throw new BuildFormatException(BuildFailure.Unsupported, "EN_UNSUPPORTED", "EN trailer is not printable ASCII.");
        }
        ct.ThrowIfCancellationRequested();
        return new(decoded, contentDirectory, encodedDirectory, counts.Content, counts.Encoded);
    }

    /// <summary>Finds the content row for <paramref name="cKey"/>; false when the file has none.</summary>
    public bool TryFindContent(CdnKey cKey, [MaybeNullWhen(false)] out EncodingContent entry)
    {
        entry = null;
        var ordinal = content.PageFor(cKey);
        if (ordinal < 0) return false;
        var rented = ArrayPool<byte>.Shared.Rent(content.PageBytes);
        try
        {
            var page = ReadPage(content, ordinal, rented);
            for (var at = 0; at < page.Length;)
            {
                var count = page[at];
                if (count == 0) return false;
                var width = 22 + 16 * count;
                if (width > page.Length - at) return false;
                var comparison = new CdnKey(page.Slice(at + 6, 16)).CompareTo(cKey);
                if (comparison > 0) return false;
                if (comparison == 0)
                {
                    var keys = new CdnKey[count];
                    for (var n = 0; n < count; n++) keys[n] = new(page.Slice(at + 22 + n * 16, 16));
                    entry = new(cKey, U40(page[(at + 1)..]), Array.AsReadOnly(keys));
                    return true;
                }
                at += width;
            }
            return false;
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    /// <summary>Finds the encoded row for <paramref name="eKey"/>; false when the file has none.</summary>
    public bool TryFindEncoded(CdnKey eKey, out EncodingEncoded entry)
    {
        entry = default;
        var ordinal = encoded.PageFor(eKey);
        if (ordinal < 0) return false;
        var rented = ArrayPool<byte>.Shared.Rent(encoded.PageBytes);
        try
        {
            var page = ReadPage(encoded, ordinal, rented);
            for (var at = 0; page.Length - at >= EncodedRecordBytes; at += EncodedRecordBytes)
            {
                var key = page.Slice(at, 16);
                if (key.IndexOfAnyExcept((byte)0) < 0) return false;
                var comparison = new CdnKey(key).CompareTo(eKey);
                if (comparison > 0) return false;
                if (comparison == 0)
                {
                    entry = new(eKey, U40(page[(at + 20)..]), (int)BinaryPrimitives.ReadUInt32BigEndian(page[(at + 16)..]));
                    return true;
                }
            }
            return false;
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    /// <summary>
    /// Every encoded row in key order (the order <see cref="Open"/> validated), reading one page at a time and checking its
    /// MD5 again. Lazy; each enumeration is independent.
    /// </summary>
    internal IEnumerable<EncodingEncoded> EncodedRows()
    {
        var page = new byte[encoded.PageBytes];
        for (var ordinal = 0; ordinal < encoded.Firsts.Length; ordinal++)
        {
            ReadPage(encoded, ordinal, page);
            for (var at = 0; page.Length - at >= EncodedRecordBytes && !IsZero(page.AsSpan(at, 16)); at += EncodedRecordBytes)
                yield return EncodedAt(page, at);
        }
    }

    private static EncodingEncoded EncodedAt(byte[] page, int at) =>
        new(new CdnKey(page.AsSpan(at, 16)), U40(page.AsSpan(at + 20)), (int)BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(at + 16)));

    private ReadOnlySpan<byte> ReadPage(Directory directory, int ordinal, byte[] buffer)
    {
        var page = buffer.AsSpan(0, directory.PageBytes);
        data.Span.Slice(directory.PagesOffset + ordinal * directory.PageBytes, directory.PageBytes).CopyTo(page);
        Span<byte> hash = stackalloc byte[16];
        MD5.HashData(page, hash);
        if (!CryptographicOperations.FixedTimeEquals(hash, directory.Hashes.AsSpan(ordinal * 16, 16)))
            throw new BuildFormatException(BuildFailure.Corrupt, "EN_INTEGRITY", "EN page differs from the one validated at open.");
        return page;
    }

    private static int CountSpecifications(ReadOnlySpan<byte> bytes, EncodingLimits limits, CancellationToken ct)
    {
        var count = 0;
        var start = 0;
        for (var at = 0; at < bytes.Length; at++)
        {
            if ((at & 8191) == 0) ct.ThrowIfCancellationRequested();
            if (bytes[at] == 0) { Add(); start = at + 1; }
            else if (bytes[at] is < 32 or > 126)
                throw new BuildFormatException(BuildFailure.Unsupported, "EN_UNSUPPORTED", "EN specification text is not printable ASCII.");
        }
        if (start != bytes.Length) throw Invalid("EN specification table is unterminated.");
        return count;
        void Add()
        {
            if (count == limits.MaxSpecificationStrings) throw Limit("EN specification count exceeds its limit.");
            count++;
        }
    }

    private static Directory ReadTable(ReadOnlySpan<byte> bytes, ref int at, int count, int pageBytes, bool isContent,
        int specCount, EncodingLimits limits, Counts counts, CancellationToken ct)
    {
        var index = bytes.Slice(at, count * IndexEntryBytes);
        at += index.Length;
        var firsts = new CdnKey[count];
        var hashes = new byte[count * 16];
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var entry = index.Slice(i * IndexEntryBytes, IndexEntryBytes);
            if (IsZero(entry[..16])) throw Invalid("EN index keys are zero or not strictly ordered.");
            firsts[i] = new(entry[..16]);
            if (i > 0 && firsts[i] <= firsts[i - 1]) throw Invalid("EN index keys are zero or not strictly ordered.");
            entry[16..].CopyTo(hashes.AsSpan(i * 16));
        }
        var pagesOffset = at;
        CdnKey? previous = null;
        Span<byte> hash = stackalloc byte[16];
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            ct.ThrowIfCancellationRequested();
            if (counts.Content + counts.Encoded == limits.MaxRecords) throw Limit("EN aggregate record limit is exhausted.");
            var page = bytes.Slice(at, pageBytes);
            at += pageBytes;
            MD5.HashData(page, hash);
            if (!CryptographicOperations.FixedTimeEquals(hash, hashes.AsSpan(ordinal * 16, 16)))
                throw new BuildFormatException(BuildFailure.Corrupt, "EN_INTEGRITY", "EN page MD5 does not match its index.");
            CdnKey? next = ordinal + 1 < count ? firsts[ordinal + 1] : null;
            var records = 0;
            var offset = 0;
            while (offset < page.Length)
            {
                if ((records & 1023) == 0) ct.ThrowIfCancellationRequested();
                if (isContent)
                {
                    var candidates = page[offset];
                    if (candidates == 0) { RequireZero(page[offset..]); break; }
                    if (candidates > limits.MaxKeysPerContent) throw Limit("EN row has too many encoding candidates.");
                    var width = 22 + 16 * candidates;
                    if (width > page.Length - offset) throw Invalid("EN content record crosses its page.");
                    var key = KeyInInterval(page.Slice(offset + 6, 16), records == 0 ? firsts[ordinal] : null, previous, next);
                    CheckRecordLimit(++records, limits, counts);
                    if (candidates > limits.MaxCandidateReferences - counts.Candidates)
                        throw Limit("EN aggregate candidate-reference limit is exceeded.");
                    for (var n = 0; n < candidates; n++)
                        if (IsZero(page.Slice(offset + 22 + n * 16, 16))) throw Invalid("EN encoding candidate is zero.");
                    counts.Content++;
                    counts.Candidates += candidates;
                    previous = key;
                    offset += width;
                }
                else
                {
                    if (page.Length - offset < 16) { RequireZero(page[offset..]); break; }
                    if (IsZero(page.Slice(offset, 16)))
                    {
                        var rest = offset + 16;
                        if (page.Length - rest >= 4 && BinaryPrimitives.ReadUInt32BigEndian(page[rest..]) == uint.MaxValue)
                        {
                            if (ordinal != count - 1) throw Invalid("EN final marker appears before its last page.");
                            rest += 4;
                        }
                        RequireZero(page[rest..]);
                        break;
                    }
                    if (page.Length - offset < EncodedRecordBytes) throw Invalid("EN encoding record crosses its page.");
                    var key = KeyInInterval(page.Slice(offset, 16), records == 0 ? firsts[ordinal] : null, previous, next);
                    CheckRecordLimit(++records, limits, counts);
                    if (BinaryPrimitives.ReadUInt32BigEndian(page[(offset + 16)..]) >= (uint)specCount)
                        throw Invalid("EN encoding row refers to a missing specification.");
                    counts.Encoded++;
                    previous = key;
                    offset += EncodedRecordBytes;
                }
            }
            if (records == 0) throw Invalid("EN indexed page contains no records.");
        }
        return new(firsts, hashes, pageBytes, pagesOffset);
    }

    private static void CheckRecordLimit(int perPage, EncodingLimits limits, Counts counts)
    {
        if (perPage > limits.MaxRecordsPerPage || counts.Content + counts.Encoded >= limits.MaxRecords)
            throw Limit("EN page or aggregate record limit is exceeded.");
    }

    private static CdnKey KeyInInterval(ReadOnlySpan<byte> bytes, CdnKey? first, CdnKey? previous, CdnKey? next)
    {
        if (IsZero(bytes)) throw Invalid("EN row key is zero.");
        var key = new CdnKey(bytes);
        if (first is { } f && key != f || previous is { } p && p >= key || next is { } n && key >= n)
            throw Invalid("EN row key differs from its ordered index interval.");
        return key;
    }

    private static void RequireZero(ReadOnlySpan<byte> bytes)
    {
        if (!IsZero(bytes)) throw Invalid("EN page padding is nonzero.");
    }

    private static bool IsZero(ReadOnlySpan<byte> bytes) => bytes.IndexOfAnyExcept((byte)0) < 0;
    private static long U40(ReadOnlySpan<byte> bytes) => ((long)bytes[0] << 32) | BinaryPrimitives.ReadUInt32BigEndian(bytes[1..]);

    private static void ValidateLimits(EncodingLimits limits)
    {
        if (limits.MaxDecodedBytes < HeaderBytes || limits.MaxRecords < 0 || limits.MaxCandidateReferences < 0 ||
            limits.MaxPages < 0 || limits.MaxIndexBytes < 0 || limits.MaxPageBytes < 1024 ||
            limits.MaxRecordsPerPage < 1 || limits.MaxKeysPerContent is < 1 or > 255 ||
            limits.MaxSpecificationStrings < 0 || limits.MaxSpecificationBytes < 0 || limits.MaxTrailerBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "Invalid EN limits.");
    }

    private static BuildFormatException Invalid(string message) => new(BuildFailure.Corrupt, "EN_INVALID", message);
    private static BuildFormatException Limit(string message) => new(BuildFailure.Corrupt, "EN_LIMIT", message);

    private sealed class Counts { public long Content, Encoded, Candidates; }

    private sealed record Directory(CdnKey[] Firsts, byte[] Hashes, int PageBytes, int PagesOffset)
    {
        /// <summary>The last page whose first key is at or below <paramref name="key"/>, or -1.</summary>
        public int PageFor(CdnKey key)
        {
            int low = 0, high = Firsts.Length;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (Firsts[middle] <= key) low = middle + 1; else high = middle;
            }
            return low - 1;
        }
    }
}
