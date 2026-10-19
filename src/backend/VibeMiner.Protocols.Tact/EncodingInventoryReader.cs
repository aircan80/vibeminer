using System.Buffers.Binary;
using System.Security.Cryptography;
using VibeMiner.Domain;

namespace VibeMiner.Protocols.Tact;

/// <summary>Original full EN-v1 enumeration. Rows remain provisional through source proof and EOF.</summary>
public static class EncodingInventoryReader
{
    public static async Task<EncodingCompletion> ReadAsync(Stream decoded, EncodingLimits limits,
        IEncodingConsumer consumer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(decoded);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(consumer);
        ValidateLimits(limits);
        cancellationToken.ThrowIfCancellationRequested();
        var reader = new AsyncInventoryReader(decoded, limits.MaxDecodedBytes, "EN", cancellationToken);
        var header = ParseHeader(await reader.BytesAsync(22), limits);
        await consumer.HeaderAsync(header, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var specifications = ParseSpecifications(await reader.BytesAsync((int)header.SpecificationBytes), limits, cancellationToken);
        for (var i = 0; i < specifications.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await consumer.SpecificationAsync(i, specifications[i], cancellationToken);
        }
        var state = new State();
        await ReadTableAsync(reader, header, EncodingTable.Content, specifications.Count, limits, consumer, state, cancellationToken);
        await ReadTableAsync(reader, header, EncodingTable.Encoding, specifications.Count, limits, consumer, state, cancellationToken);
        var trailer = new System.Text.StringBuilder();
        while (await reader.TryByteAsync() is { } next)
        {
            if (trailer.Length == limits.MaxTrailerBytes) throw Error("EN_LIMIT", "EN trailer exceeds its byte limit.");
            if (next is < 32 or > 126) throw Error("EN_UNSUPPORTED", "EN trailer is not printable ASCII.");
            trailer.Append((char)next);
        }
        await consumer.TrailerAsync(trailer.ToString(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return new(header.ContentPageCount, header.EncodingPageCount, state.ContentRecords, state.EncodingRecords,
            state.Candidates, specifications.Count, trailer.Length, reader.ConsumedBytes);
    }

    private sealed class State
    {
        public long ContentRecords, EncodingRecords, Candidates;
    }

    private static EncodingHeader ParseHeader(byte[] bytes, EncodingLimits limits)
    {
        if (bytes[0] != 'E' || bytes[1] != 'N') throw Error("EN_INVALID", "EN signature is invalid.");
        if (bytes[2] != 1 || bytes[3] != 16 || bytes[4] != 16 || bytes[17] != 0)
            throw Error("EN_UNSUPPORTED", "EN layout is unsupported.");
        var contentPageBytes = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(5)) * 1024;
        var encodingPageBytes = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(7)) * 1024;
        long contentPages = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(9));
        long encodingPages = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(13));
        long specBytes = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(18));
        if (contentPageBytes == 0 || encodingPageBytes == 0) throw Error("EN_INVALID", "EN page size is zero.");
        if (contentPageBytes > limits.MaxPageBytes || encodingPageBytes > limits.MaxPageBytes ||
            contentPages + encodingPages > limits.MaxPages || contentPages * 32 > limits.MaxIndexBytes ||
            encodingPages * 32 > limits.MaxIndexBytes || specBytes > limits.MaxSpecificationBytes)
            throw Error("EN_LIMIT", "EN declared page, index or specification limits are exceeded.");
        var minimum = 22 + specBytes + contentPages * (32 + (long)contentPageBytes) + encodingPages * (32 + (long)encodingPageBytes);
        if (minimum > limits.MaxDecodedBytes || contentPages + encodingPages > limits.MaxRecords || contentPages > limits.MaxCandidateReferences)
            throw Error("EN_LIMIT", "EN declared minimum work exceeds its limits.");
        return new(1, 16, 16, contentPageBytes, encodingPageBytes, contentPages, encodingPages, specBytes, minimum);
    }

    private static List<string> ParseSpecifications(byte[] bytes, EncodingLimits limits, CancellationToken cancellationToken)
    {
        var specs = new List<string>();
        var start = 0;
        for (var at = 0; at < bytes.Length; at++)
        {
            if ((at & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (bytes[at] == 0)
            {
                Add(System.Text.Encoding.ASCII.GetString(bytes, start, at - start));
                start = at + 1;
            }
            else if (bytes[at] is < 32 or > 126) throw Error("EN_UNSUPPORTED", "EN specification text is not printable ASCII.");
        }
        if (bytes.Length != 0)
        {
            if (start != bytes.Length) throw Error("EN_INVALID", "EN specification table is unterminated.");
            Add("");
        }
        return specs;
        void Add(string value)
        {
            if (specs.Count == limits.MaxSpecificationStrings) throw Error("EN_LIMIT", "EN specification count exceeds its limit.");
            specs.Add(value);
        }
    }

    private static async Task ReadTableAsync(AsyncInventoryReader reader, EncodingHeader header, EncodingTable table,
        int specCount, EncodingLimits limits, IEncodingConsumer consumer, State state, CancellationToken cancellationToken)
    {
        var count = (int)(table == EncodingTable.Content ? header.ContentPageCount : header.EncodingPageCount);
        var pageBytes = table == EncodingTable.Content ? header.ContentPageBytes : header.EncodingPageBytes;
        var index = await reader.BytesAsync(checked(count * 32));
        string? previousFirst = null;
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var first = Key(index, i * 32);
            if (IsZeroKey(index, i * 32) || previousFirst is not null && string.CompareOrdinal(first, previousFirst) <= 0)
                throw Error("EN_INVALID", "EN index keys are zero or not strictly ordered.");
            previousFirst = first;
        }
        string? previous = null;
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (state.ContentRecords + state.EncodingRecords == limits.MaxRecords)
                throw Error("EN_LIMIT", "EN aggregate record limit is exhausted.");
            var page = await reader.BytesAsync(pageBytes);
            if (!CryptographicOperations.FixedTimeEquals(MD5.HashData(page), index.AsSpan(ordinal * 32 + 16, 16)))
                throw Error("EN_INTEGRITY", "EN page MD5 does not match its index.");
            var first = Key(index, ordinal * 32);
            var nextFirst = ordinal + 1 < count ? Key(index, (ordinal + 1) * 32) : null;
            var records = 0;
            var at = 0;
            while (at < page.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (table == EncodingTable.Content)
                {
                    var candidates = page[at];
                    if (candidates == 0) { RequireZeroPadding(page, at, cancellationToken); break; }
                    if (candidates > limits.MaxKeysPerContent) throw Error("EN_LIMIT", "EN row has too many encoding candidates.");
                    var width = 22 + 16 * candidates;
                    if (width > page.Length - at) throw Error("EN_INVALID", "EN content record crosses its page.");
                    var key = Key(page, at + 6);
                    ValidateKey(page, at + 6, key, records == 0 ? first : null, previous, nextFirst);
                    CheckRecordLimit(++records, limits, state);
                    if (candidates > limits.MaxCandidateReferences - state.Candidates)
                        throw Error("EN_LIMIT", "EN aggregate candidate-reference limit is exceeded.");
                    var keys = new string[candidates];
                    for (var n = 0; n < candidates; n++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (IsZeroKey(page, at + 22 + n * 16)) throw Error("EN_INVALID", "EN encoding candidate is zero.");
                        keys[n] = Key(page, at + 22 + n * 16);
                    }
                    var entry = new EncodingContentEntry(key, U40(page, at + 1), Array.AsReadOnly(keys));
                    await consumer.ContentAsync(ordinal, state.ContentRecords, entry, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    state.ContentRecords++;
                    state.Candidates += candidates;
                    previous = key;
                    at += width;
                }
                else
                {
                    if (page.Length - at < 16) { RequireZeroPadding(page, at, cancellationToken); break; }
                    if (IsZeroKey(page, at))
                    {
                        var rest = at + 16;
                        if (page.Length - rest >= 4 && BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(rest)) == uint.MaxValue)
                        {
                            if (ordinal != count - 1) throw Error("EN_INVALID", "EN final marker appears before its last page.");
                            rest += 4;
                        }
                        RequireZeroPadding(page, rest, cancellationToken);
                        break;
                    }
                    if (page.Length - at < 25) throw Error("EN_INVALID", "EN encoding record crosses its page.");
                    var key = Key(page, at);
                    ValidateKey(page, at, key, records == 0 ? first : null, previous, nextFirst);
                    CheckRecordLimit(++records, limits, state);
                    var spec = BinaryPrimitives.ReadUInt32BigEndian(page.AsSpan(at + 16));
                    if (spec >= specCount) throw Error("EN_INVALID", "EN encoding row refers to a missing specification.");
                    await consumer.EncodingAsync(ordinal, state.EncodingRecords, new(key, U40(page, at + 20), spec), cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    state.EncodingRecords++;
                    previous = key;
                    at += 25;
                }
            }
            if (records == 0) throw Error("EN_INVALID", "EN indexed page contains no records.");
            await consumer.PageCompletedAsync(table, ordinal, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    private static void CheckRecordLimit(int perPage, EncodingLimits limits, State state)
    {
        if (perPage > limits.MaxRecordsPerPage || state.ContentRecords + state.EncodingRecords >= limits.MaxRecords)
            throw Error("EN_LIMIT", "EN page or aggregate record limit is exceeded.");
    }
    private static void ValidateKey(byte[] bytes, int at, string key, string? first, string? previous, string? next)
    {
        if (IsZeroKey(bytes, at) || first is not null && key != first ||
            previous is not null && string.CompareOrdinal(previous, key) >= 0 ||
            next is not null && string.CompareOrdinal(key, next) >= 0)
            throw Error("EN_INVALID", "EN row key differs from its ordered index interval.");
    }
    private static void RequireZeroPadding(byte[] page, int at, CancellationToken cancellationToken)
    {
        for (; at < page.Length; at++)
        {
            if ((at & 8191) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (page[at] != 0) throw Error("EN_INVALID", "EN page padding is nonzero.");
        }
    }
    private static string Key(byte[] bytes, int at) => Convert.ToHexStringLower(bytes.AsSpan(at, 16));
    private static bool IsZeroKey(byte[] bytes, int at) => bytes.AsSpan(at, 16).IndexOfAnyExcept((byte)0) < 0;
    private static long U40(byte[] bytes, int at) => ((long)bytes[at] << 32) | BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at + 1));
    private static void ValidateLimits(EncodingLimits limits)
    {
        if (limits.MaxDecodedBytes < 22 || limits.MaxRecords < 0 || limits.MaxCandidateReferences < 0 ||
            limits.MaxPages < 0 || limits.MaxIndexBytes < 0 || limits.MaxPageBytes < 1024 ||
            limits.MaxRecordsPerPage < 1 || limits.MaxKeysPerContent is < 1 or > 255 ||
            limits.MaxSpecificationStrings < 0 || limits.MaxSpecificationBytes < 0 || limits.MaxTrailerBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "Invalid EN enumeration limits.");
    }
    private static DiscoveryException Error(string code, string message) => new(code, message);
}
