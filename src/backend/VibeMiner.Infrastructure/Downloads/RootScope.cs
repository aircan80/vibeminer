using System.Buffers.Binary;
using System.Collections;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

/// <summary>
/// The encoded objects of ROOT-listed files that no download-manifest member covers (D066), and the ROOT content
/// keys that cannot be resolved to an exact encoded object. A file is covered when any EKey of its CKey is a manifest
/// member. An uncovered file is represented by the first EKey ENCODING lists for it, so one stored object counts for
/// that file, as each manifest object counts once.
/// </summary>
internal sealed record RootScope(IReadOnlyList<LedgerValue> Objects, long UnresolvableContent, long DistinctContent);

internal static class RootScopes
{
    /// <summary>
    /// Working memory is fixed-size arrays: one 16-byte key per ROOT entry (at most <see cref="CompactAt"/>), one 16-byte
    /// key per distinct file, which later holds the uncovered files' EKeys in place, and one 8-byte size per uncovered file.
    /// Files with several EKeys add 24 bytes per EKey, at most <see cref="MaxMultipleKeys"/>. That is at most about 300 MiB;
    /// retail 12.1.5 (3,261,808 entries, 2,881,684 distinct files, 215 uncovered, none with several EKeys) needs 94 MiB.
    /// </summary>
    internal const int MaxDistinctContent = 6_000_000;
    private const int CompactAt = 8_000_000;
    internal const int MaxMultipleKeys = 1_000_000;

    internal static async Task<RootScope> ComputeAsync(Stream rootDecoded, long rootBytes, Stream encodingDecoded, long encodingBytes,
        Func<CancellationToken, Task<DownloadLedgerOrderedCursor>> manifest, Func<string, Task> progress, CancellationToken token)
    {
        await progress("Reading the build file list");
        var roots = new RootKeys();
        await RootInventoryReader.ReadAsync(rootDecoded, new(MaxDecodedBytes: rootBytes), roots, token);
        var (keys, distinct) = roots.Finish();
        await progress("Matching the file list with encoding metadata");
        var scan = new EncodingScan(keys, distinct, manifest, token);
        await EncodingInventoryReader.ReadAsync(encodingDecoded, new(MaxDecodedBytes: encodingBytes), scan, token);
        return await scan.FinishAsync();
    }

    internal static UInt128 Key(string hex)
    {
        Span<byte> raw = stackalloc byte[16];
        if (hex.Length != 32 || Convert.FromHexString(hex, raw, out _, out var written) != System.Buffers.OperationStatus.Done || written != 16)
            throw new DiscoveryException("ROOT_SCOPE_INVALID", "A file-list content key is not a full 16-byte key.");
        return BinaryPrimitives.ReadUInt128BigEndian(raw);
    }
    private static LedgerValue Value(UInt128 key, long bytes) => new((ulong)(key >> 64), (ulong)key, bytes);

    /// <summary>The sized uncovered objects in ascending key order, read from the scan's arrays without copying them.</summary>
    private sealed class UncoveredObjects(UInt128[] keys, long[] sizes, int count) : IReadOnlyList<LedgerValue>
    {
        public LedgerValue this[int index] => (uint)index < (uint)count ? Value(keys[index], sizes[index]) : throw new ArgumentOutOfRangeException(nameof(index));
        public int Count => count;
        public IEnumerator<LedgerValue> GetEnumerator() { for (var i = 0; i < count; i++) yield return this[i]; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// One array sized from the declared entry count (capped at <see cref="CompactAt"/>), so it is never grown or copied.
    /// A larger list is sorted and deduplicated in place whenever the array fills.
    /// </summary>
    private sealed class RootKeys : IRootConsumer
    {
        private UInt128[]? keys;
        private int count;
        public ValueTask HeaderAsync(RootHeader header, CancellationToken token)
        {
            keys = new UInt128[(int)Math.Min(header.MembershipCount, CompactAt)];
            return ValueTask.CompletedTask;
        }
        public ValueTask EntryAsync(long blockOrdinal, long entryOrdinal, RootEntry entry, CancellationToken token)
        {
            var slots = keys ?? throw new DiscoveryException("ROOT_SCOPE_INVALID", "A file-list entry came before its header.");
            if (count == slots.Length)
            {
                if (slots.Length < CompactAt) throw new DiscoveryException("ROOT_SCOPE_INVALID", "The build file list has more entries than it declares.");
                Compact();
            }
            slots[count++] = Key(entry.ContentKey);
            return ValueTask.CompletedTask;
        }
        private void Compact()
        {
            var slots = keys!;
            Array.Sort(slots, 0, count); var unique = 0;
            for (var i = 0; i < count; i++) if (i == 0 || slots[i] != slots[unique - 1]) slots[unique++] = slots[i];
            count = unique;
            if (count > MaxDistinctContent)
                throw new DiscoveryException("ROOT_SCOPE_LIMIT", "The build file list has more distinct files than this version can count.");
        }
        internal (UInt128[] Keys, int Count) Finish()
        {
            if (keys is null) throw new DiscoveryException("ROOT_SCOPE_INVALID", "The build file list has no header.");
            Compact(); return (keys, count);
        }
    }

    /// <summary>
    /// Content entries mark resolved files and collect their EKeys. When the encoding table starts, every content entry
    /// has been seen, so the EKeys are merged once with the sorted manifest and the uncovered ones are looked up by size.
    /// </summary>
    private sealed class EncodingScan(UInt128[] roots, int files, Func<CancellationToken, Task<DownloadLedgerOrderedCursor>> manifest, CancellationToken token) : IEncodingConsumer
    {
        private readonly BitArray resolved = new(files);
        // At most one single EKey per distinct file, so this never grows. After the merge it holds the uncovered EKeys.
        private readonly UInt128[] keys = new UInt128[files];
        private int singleCount, uncoveredCount;
        private readonly List<(UInt128 Key, int File, int Ordinal)> multiple = [];
        // The exact encoded size of each uncovered EKey, -1 until the encoded-object table names it.
        private long[]? sizes;
        private long resolvedCount;

        public ValueTask ContentAsync(int pageOrdinal, long ordinal, EncodingContentEntry entry, CancellationToken cancellationToken)
        {
            if (sizes is not null) throw new DiscoveryException("ROOT_SCOPE_INVALID", "Encoding content followed its encoded-object table.");
            var file = Array.BinarySearch(roots, 0, files, Key(entry.ContentKey));
            if (file < 0 || resolved[file] || entry.EncodingKeys.Count == 0) return ValueTask.CompletedTask;
            resolved[file] = true; resolvedCount++;
            if (entry.EncodingKeys.Count == 1) { keys[singleCount++] = Key(entry.EncodingKeys[0]); return ValueTask.CompletedTask; }
            if (multiple.Count + entry.EncodingKeys.Count > MaxMultipleKeys)
                throw new DiscoveryException("ROOT_SCOPE_LIMIT", "The build file list has more multi-object files than this version can count.");
            for (var i = 0; i < entry.EncodingKeys.Count; i++) multiple.Add((Key(entry.EncodingKeys[i]), file, i));
            return ValueTask.CompletedTask;
        }

        public async ValueTask EncodingAsync(int pageOrdinal, long ordinal, EncodingEncodedEntry entry, CancellationToken cancellationToken)
        {
            if (sizes is null) await UncoveredAsync();
            var at = Array.BinarySearch(keys, 0, uncoveredCount, Key(entry.EncodingKey));
            if (at >= 0) sizes![at] = entry.EncodedBytes;
        }

        internal async Task<RootScope> FinishAsync()
        {
            if (sizes is null) await UncoveredAsync();
            // A resolved file whose EKey has no encoded size cannot be fetched or verified either; drop it in place.
            var kept = 0;
            for (var i = 0; i < uncoveredCount; i++)
                if (sizes![i] >= 0) { keys[kept] = keys[i]; sizes[kept] = sizes[i]; kept++; }
            var unsized = uncoveredCount - kept;
            return new(new UncoveredObjects(keys, sizes!, kept), files - resolvedCount + unsized, files);
        }

        /// <summary>
        /// The first EKey of every resolved file with no manifest member among its EKeys, written into the front of the
        /// single-EKey array: each file adds at most one, never more than were read, so the array is reused as is.
        /// </summary>
        private async Task UncoveredAsync()
        {
            Array.Sort(keys, 0, singleCount);
            var count = 0;
            // Writes trail the reads, so compacting into the array being merged is safe.
            await foreach (var (key, member) in MergeAsync(new ArraySegment<UInt128>(keys, 0, singleCount))) if (!member) keys[count++] = key;
            if (multiple.Count > 0)
            {
                multiple.Sort((a, b) => a.Key.CompareTo(b.Key));
                var covered = new HashSet<int>();
                var index = 0;
                await foreach (var (_, member) in MergeAsync(multiple.Select(item => item.Key)))
                { if (member) covered.Add(multiple[index].File); index++; }
                foreach (var item in multiple) if (item.Ordinal == 0 && !covered.Contains(item.File)) keys[count++] = item.Key;
                multiple.Clear(); multiple.TrimExcess();
            }
            Array.Sort(keys, 0, count);
            var unique = 0;
            for (var i = 0; i < count; i++) if (i == 0 || keys[i] != keys[unique - 1]) keys[unique++] = keys[i];
            uncoveredCount = unique;
            sizes = new long[unique];
            Array.Fill(sizes, -1L);
        }

        /// <summary>One sequential pass over the sorted manifest for an ascending key sequence.</summary>
        private async IAsyncEnumerable<(UInt128 Key, bool Member)> MergeAsync(IEnumerable<UInt128> ascending)
        {
            await using var cursor = await manifest(token);
            var more = await cursor.NextAsync(token); UInt128 current = default;
            if (more) current = Join(cursor.Current);
            foreach (var key in ascending)
            {
                token.ThrowIfCancellationRequested();
                while (more && current < key) { more = await cursor.NextAsync(token); if (more) current = Join(cursor.Current); }
                yield return (key, more && current == key);
            }
        }
        private static UInt128 Join(LedgerValue value) => new(value.High, value.Low);
    }
}
