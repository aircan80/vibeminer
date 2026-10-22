using System.Collections;
using System.Globalization;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Protocols.Tact;

namespace VibeMiner.Infrastructure;

/// <summary>
/// The in-memory resolution behind one file catalog (D071). Every distinct ROOT and install content key, sorted, with the
/// encoded object Files shows for it: the lexicographically first EKey ENCODING lists for that content that has an encoded
/// size (the one on-demand file acquisition fetches), its encoded size and the decoded size. A content key ENCODING does
/// not list, or whose EKeys all lack a size, stays unresolved and is never given a size.
/// <para>
/// Working memory is fixed-size arrays: 16 bytes per membership while ROOT is read (at most <see cref="MaxMemberships"/>)
/// plus 4 per ROOT membership for the FileDataID statistics, released after the first pass; then 16 + 16 + 8 + 8 + 4
/// bytes per distinct content key, and 24 per EKey of content with several EKeys (at most <see cref="MaxMultipleKeys"/>).
/// Retail 12.1.5.70448 (3,262,082 ROOT and install memberships, 2,881,834 distinct contents) needs about 150 MiB of
/// arrays at their peak, while ENCODING is merged; the membership key array is kept at its first size, not copied smaller.
/// </para>
/// </summary>
internal sealed class FileCatalogIndex
{
    internal const int MaxMemberships = 8_000_000;
    internal const int MaxMultipleKeys = 1_000_000;
    internal const int MaxVariants = 65_536;

    private UInt128[] keys = [];
    private int count;
    private UInt128[] encodingKeys = [];
    private long[] encoded = [], decoded = [];

    internal long RootMembers { get; private set; }
    internal long RootFiles { get; private set; }
    internal IReadOnlyList<InstallRow> Install { get; private set; } = [];
    internal long InstallFiles { get; private set; }
    internal IReadOnlyList<(uint Locale, uint Flags, long Members)> Variants { get; private set; } = [];
    /// <summary>Every sized EKey of a content that has several, in ascending key order; empty on most builds.</summary>
    internal IReadOnlyList<(int Content, UInt128 Key, long Size)> Alternatives { get; private set; } = [];
    internal IEnumerable<(string Key, long Bytes)> AlternativeKeys => Alternatives.Select(item => (Hex(item.Key), item.Size));

    /// <summary>
    /// The legacy catalog's rule (#20): an already stored EKey of a content is preferred to the lexicographically first one;
    /// among stored ones the lexicographically first. There is no download-manifest expectation here to prefer before it.
    /// </summary>
    internal void PreferStored(IReadOnlySet<string> stored)
    {
        var chosen = new HashSet<int>();
        foreach (var (content, key, size) in Alternatives)
            if (!chosen.Contains(content) && stored.Contains(Hex(key))) { encodingKeys[content] = key; encoded[content] = size; chosen.Add(content); }
    }
    internal int DistinctContent => count;

    internal sealed record InstallRow(long Ordinal, InstallEntry Entry);

    internal static async Task<FileCatalogIndex> BuildAsync(Stream rootDecoded, long rootBytes, IReadOnlyList<InstallRow> install,
        Stream encodingDecoded, long encodingBytes, CancellationToken token)
    {
        var index = new FileCatalogIndex { Install = install };
        var roots = new RootPass(install.Count, token);
        await RootInventoryReader.ReadAsync(rootDecoded, new(MaxDecodedBytes: rootBytes), roots, token);
        roots.Finish(index);
        var scan = new EncodingPass(index, token);
        await EncodingInventoryReader.ReadAsync(encodingDecoded, new(MaxDecodedBytes: encodingBytes), scan, token);
        scan.Finish();
        return index;
    }

    /// <summary>Install entries in manifest order with their file groups counted; small and bounded by the install reader.</summary>
    internal sealed class InstallPass(CancellationToken token) : IInstallConsumer
    {
        internal List<InstallRow> Rows { get; } = [];
        public ValueTask TagAsync(int ordinal, InstallTag tag, CancellationToken cancellationToken) => ValueTask.CompletedTask;
        public ValueTask EntryAsync(long ordinal, InstallEntry entry, CancellationToken cancellationToken)
        {
            token.ThrowIfCancellationRequested();
            if (Rows.Count >= MaxMemberships) throw Limit();
            Rows.Add(new(ordinal, entry)); return ValueTask.CompletedTask;
        }
    }

    /// <summary>The resolved encoded object of a content key, or nulls when it is unresolved. Unknown keys are refused.</summary>
    internal (string? EncodingKey, long? EncodedBytes, long? DecodedBytes) Resolve(string contentKey)
    {
        var at = Array.BinarySearch(keys, 0, count, RootScopes.Key(contentKey));
        if (at < 0) throw new DiscoveryException("FILE_CATALOG_CHANGED", "The build file list changed between its two reads.");
        long? size = decoded[at] < 0 ? null : decoded[at];
        return encoded[at] < 0 ? (null, null, size) : (Hex(encodingKeys[at]), encoded[at], size);
    }
    private static string Hex(UInt128 key)
    {
        Span<byte> raw = stackalloc byte[16];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt128BigEndian(raw, key);
        return Convert.ToHexStringLower(raw);
    }
    private static DiscoveryException Limit() =>
        new("FILE_CATALOG_LIMIT", $"This build lists more than {MaxMemberships:N0} files, more than this version can prepare.");

    /// <summary>First pass over ROOT: one key per membership, the FileDataID statistics and the variant counts.</summary>
    private sealed class RootPass(int installCount, CancellationToken token) : IRootConsumer
    {
        private UInt128[]? keys;
        private uint[]? files;
        private int count;
        private readonly Dictionary<(uint, uint), long> variants = [];
        public ValueTask HeaderAsync(RootHeader header, CancellationToken cancellationToken)
        {
            if (header.MembershipCount < 0 || header.MembershipCount + installCount > MaxMemberships) throw Limit();
            keys = new UInt128[header.MembershipCount + installCount];
            files = new uint[header.MembershipCount];
            return ValueTask.CompletedTask;
        }
        public ValueTask EntryAsync(long blockOrdinal, long entryOrdinal, RootEntry entry, CancellationToken cancellationToken)
        {
            token.ThrowIfCancellationRequested();
            if (keys is null || files is null) throw new DiscoveryException("ROOT_INVALID", "A file-list entry came before its header.");
            if (count == files.Length) throw new DiscoveryException("ROOT_INVALID", "The build file list has more entries than it declares.");
            keys[count] = RootScopes.Key(entry.ContentKey); files[count] = entry.FileDataId; count++;
            var pair = (entry.LocaleFlags, entry.ContentFlags);
            if (variants.TryGetValue(pair, out var members)) variants[pair] = members + 1;
            else if (variants.Count >= MaxVariants)
                throw new DiscoveryException("FILE_CATALOG_LIMIT", $"This build's file list has more than {MaxVariants:N0} locale and content-flag variants.");
            else variants[pair] = 1;
            return ValueTask.CompletedTask;
        }
        internal void Finish(FileCatalogIndex index)
        {
            if (keys is null || files is null) throw new DiscoveryException("ROOT_INVALID", "The build file list has no header.");
            if (count != files.Length) throw new DiscoveryException("ROOT_INVALID", "The build file list has fewer entries than it declares.");
            Array.Sort(files);
            long distinct = 0; var run = 0;
            for (var i = 0; i < files.Length; i++)
            {
                run = i > 0 && files[i] == files[i - 1] ? run + 1 : 1;
                if (run == 1) distinct++;
                if (run > DiscoveryCatalog.MaxFileMemberships)
                    throw new DiscoveryException("INVENTORY_LIMIT", $"A ROOT FileDataID has more than {DiscoveryCatalog.MaxFileMemberships:N0} memberships, the bound Files reads per file.");
            }
            files = null;
            var names = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var row in index.Install)
            {
                token.ThrowIfCancellationRequested();
                keys[count++] = RootScopes.Key(row.Entry.ContentKey);
                names[row.Entry.Name] = names.GetValueOrDefault(row.Entry.Name) + 1;
                if (names[row.Entry.Name] > DiscoveryCatalog.MaxFileMemberships)
                    throw new DiscoveryException("INVENTORY_LIMIT", $"An install name has more than {DiscoveryCatalog.MaxFileMemberships:N0} memberships, the bound Files reads per file.");
            }
            Array.Sort(keys, 0, count); var unique = 0;
            for (var i = 0; i < count; i++) if (i == 0 || keys[i] != keys[unique - 1]) keys[unique++] = keys[i];
            index.keys = keys; index.count = unique;
            index.RootMembers = count - index.Install.Count; index.RootFiles = distinct; index.InstallFiles = names.Count;
            index.Variants = [.. variants.Select(pair => (pair.Key.Item1, pair.Key.Item2, pair.Value)).OrderBy(pair => pair.Item1).ThenBy(pair => pair.Item2)];
            index.encodingKeys = new UInt128[unique];
            index.encoded = new long[unique]; Array.Fill(index.encoded, -1L);
            index.decoded = new long[unique]; Array.Fill(index.decoded, -1L);
        }
    }

    /// <summary>
    /// Content entries give each listed content its decoded size and EKeys; once the encoded-object table starts, every
    /// content entry has been seen, so the chosen EKeys are merged with that EKey-ordered table for their sizes.
    /// </summary>
    private sealed class EncodingPass(FileCatalogIndex index, CancellationToken token) : IEncodingConsumer
    {
        private readonly BitArray several = new(index.count);
        private readonly List<(UInt128 Key, int Content)> multiple = [];
        private long[]? multipleSizes;
        private int[]? order;
        private int orderCount, singleAt, multipleAt;

        public ValueTask ContentAsync(int pageOrdinal, long ordinal, EncodingContentEntry entry, CancellationToken cancellationToken)
        {
            token.ThrowIfCancellationRequested();
            if (order is not null) throw new DiscoveryException("ROOT_SCOPE_INVALID", "Encoding content followed its encoded-object table.");
            var at = Array.BinarySearch(index.keys, 0, index.count, RootScopes.Key(entry.ContentKey));
            if (at < 0 || index.decoded[at] >= 0 || entry.EncodingKeys.Count == 0) return ValueTask.CompletedTask;
            index.decoded[at] = entry.DecodedBytes;
            if (entry.EncodingKeys.Count == 1) { index.encodingKeys[at] = RootScopes.Key(entry.EncodingKeys[0]); orderCount++; return ValueTask.CompletedTask; }
            if (multiple.Count + entry.EncodingKeys.Count > MaxMultipleKeys)
                throw new DiscoveryException("FILE_CATALOG_LIMIT", "This build has more multi-object files than this version can prepare.");
            several[at] = true;
            foreach (var key in entry.EncodingKeys) multiple.Add((RootScopes.Key(key), at));
            return ValueTask.CompletedTask;
        }

        public ValueTask EncodingAsync(int pageOrdinal, long ordinal, EncodingEncodedEntry entry, CancellationToken cancellationToken)
        {
            token.ThrowIfCancellationRequested();
            if (order is null) Prepare();
            var key = RootScopes.Key(entry.EncodingKey);
            // Both sequences ascend, so each is one forward merge with the table.
            while (singleAt < orderCount && index.encodingKeys[order![singleAt]] < key) singleAt++;
            while (singleAt < orderCount && index.encodingKeys[order![singleAt]] == key) index.encoded[order[singleAt++]] = entry.EncodedBytes;
            while (multipleAt < multiple.Count && multiple[multipleAt].Key < key) multipleAt++;
            while (multipleAt < multiple.Count && multiple[multipleAt].Key == key) multipleSizes![multipleAt++] = entry.EncodedBytes;
            return ValueTask.CompletedTask;
        }

        private void Prepare()
        {
            order = new int[orderCount]; var next = 0;
            for (var i = 0; i < index.count; i++) if (index.decoded[i] >= 0 && !several[i]) order[next++] = i;
            if (next != orderCount) throw new DiscoveryException("ROOT_SCOPE_INVALID", "Encoding content was counted inconsistently.");
            var keys = index.encodingKeys;
            Array.Sort(order, (a, b) => keys[a].CompareTo(keys[b]));
            multiple.Sort((a, b) => a.Key != b.Key ? a.Key.CompareTo(b.Key) : a.Content.CompareTo(b.Content));
            multipleSizes = new long[multiple.Count]; Array.Fill(multipleSizes, -1L);
        }

        internal void Finish()
        {
            if (order is null) Prepare();
            // In ascending EKey order, the first sized EKey met for a content is its lexicographically first sized one.
            var sized = new Dictionary<int, int>();
            for (var i = 0; i < multiple.Count; i++)
            {
                var (key, content) = multiple[i];
                if (multipleSizes![i] < 0) continue;
                sized[content] = sized.GetValueOrDefault(content) + 1;
                if (index.encoded[content] < 0) { index.encodingKeys[content] = key; index.encoded[content] = multipleSizes[i]; }
            }
            // Contents with several sized EKeys keep them, in ascending key order, so a stored one can be preferred.
            var alternatives = new List<(int Content, UInt128 Key, long Size)>();
            for (var i = 0; i < multiple.Count; i++)
                if (multipleSizes![i] >= 0 && sized.GetValueOrDefault(multiple[i].Content) > 1) alternatives.Add((multiple[i].Content, multiple[i].Key, multipleSizes[i]));
            index.Alternatives = alternatives;
            order = null; multiple.Clear(); multiple.TrimExcess();
        }
    }

    internal static string RootRaw(long block, long entry, RootRawContentFlags flags) => string.Create(CultureInfo.InvariantCulture,
        $"{{\"blockOrdinal\":{block},\"entryOrdinal\":{entry},\"RawContentFlags\":{{\"First\":{flags.First},\"Second\":{flags.Second},\"Third\":{flags.Third}}}}}");
}
