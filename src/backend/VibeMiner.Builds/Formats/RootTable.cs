using System.Buffers.Binary;
using VibeMiner.Cdn;

namespace VibeMiner.Builds.Formats;

/// <summary>Bounds for one decoded ROOT. Defaults match the old reader; checked before anything is allocated.</summary>
public sealed record RootLimits(
    long MaxDecodedBytes = 512L << 20,
    long MaxRecords = 50_000_000,
    int MaxBlocks = 1_000_000,
    int MaxEntriesPerBlock = 2_000_000);

/// <summary>
/// One ROOT membership. <see cref="ContentFlags"/> is the derived value: v1 the block's flags, v2
/// <c>first | second | (third &lt;&lt; 17)</c>. The same FileDataID can have several memberships.
/// </summary>
/// <remarks><see cref="Raw"/> keeps the v2 parts the OR loses; null for v1.</remarks>
public readonly record struct RootEntry(uint FileDataId, CdnKey CKey, uint LocaleFlags, ulong ContentFlags, RootRawFlags? Raw = null);

/// <summary>The three raw content-flag parts of a v2 block header, as read.</summary>
public readonly record struct RootRawFlags(uint First, uint Second, byte Third);

/// <summary>
/// A WoW ROOT (TSFM v1, v2) indexed in memory by FileDataID and name hash. Every membership is kept, in file order per
/// FileDataID; nothing is filtered by locale or content flags. See <c>docs/formats/root.md</c>.
/// </summary>
public sealed class RootTable
{
    public const ulong NoNames = 0x10000000;
    private const int CancelEvery = 65_536;

    private readonly Membership[] entries;
    private readonly uint[] blockLocales;
    private readonly ulong[] blockFlags;
    private readonly RootRawFlags[]? blockRaw;
    private readonly ulong[] nameHashes;
    private readonly uint[] nameIds;

    private RootTable(int version, Membership[] entries, uint[] blockLocales, ulong[] blockFlags, RootRawFlags[]? blockRaw,
        ulong[] nameHashes, uint[] nameIds, int fileCount, uint localeUnion, int nameHashConflicts)
    {
        Version = version; this.entries = entries; this.blockLocales = blockLocales; this.blockFlags = blockFlags;
        this.blockRaw = blockRaw;
        this.nameHashes = nameHashes; this.nameIds = nameIds;
        FileCount = fileCount; LocaleUnion = localeUnion; NameHashConflicts = nameHashConflicts;
    }

    /// <summary>The TSFM version, 1 or 2.</summary>
    public int Version { get; }
    /// <summary>Memberships (header total), not unique files.</summary>
    public long EntryCount => entries.Length;
    /// <summary>Distinct FileDataIDs.</summary>
    public int FileCount { get; }
    public int BlockCount => blockLocales.Length;
    /// <summary>OR of every block's locale flags. It doesn't prove which locales a build supports.</summary>
    public uint LocaleUnion { get; }
    /// <summary>Name hashes seen with more than one FileDataID. The first FileDataID in file order is the one kept.</summary>
    public int NameHashConflicts { get; }

    /// <summary>Every membership of one FileDataID in file order; empty when the complete ROOT has none.</summary>
    public IReadOnlyList<RootEntry> Find(uint fileDataId)
    {
        int lo = 0, hi = entries.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (entries[mid].Id < fileDataId) lo = mid + 1; else hi = mid;
        }
        var end = lo;
        while (end < entries.Length && entries[end].Id == fileDataId) end++;
        if (end == lo) return [];
        var found = new RootEntry[end - lo];
        for (var i = lo; i < end; i++)
        {
            var e = entries[i];
            found[i - lo] = new(e.Id, e.Key, blockLocales[e.Block], blockFlags[e.Block], blockRaw?[e.Block]);
        }
        return found;
    }

    /// <summary>
    /// Distinct FileDataIDs from <paramref name="fromId"/> up, ascending, that have a membership whose locale flags share a
    /// bit with <paramref name="localeMask"/>. Lazy; walks the table in place.
    /// </summary>
    internal IEnumerable<uint> FileDataIds(uint localeMask, uint fromId)
    {
        int lo = 0, hi = entries.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (entries[mid].Id < fromId) lo = mid + 1; else hi = mid;
        }
        for (var i = lo; i < entries.Length;)
        {
            var id = entries[i].Id;
            var listed = false;
            for (; i < entries.Length && entries[i].Id == id; i++)
                listed |= (blockLocales[entries[i].Block] & localeMask) != 0;
            if (listed) yield return id;
        }
    }

    /// <summary>The FileDataID a name hash belongs to, for memberships in blocks that carry name hashes.</summary>
    public bool TryFindByNameHash(ulong nameHash, out uint fileDataId)
    {
        var at = Array.BinarySearch(nameHashes, nameHash);
        fileDataId = at >= 0 ? nameIds[at] : 0;
        return at >= 0;
    }

    /// <summary>
    /// Parses decoded ROOT bytes. Throws <see cref="BuildFormatException"/>: Unsupported <c>ROOT_UNSUPPORTED</c> for a ROOT
    /// without TSFM or an unknown version or header, Unsupported <c>ROOT_UNSUPPORTED_LAYOUT</c> for the pre-10.1.7 TSFM
    /// header, Corrupt <c>ROOT_INVALID</c> for bad bytes, Corrupt <c>ROOT_LIMIT</c> past a limit.
    /// </summary>
    public static RootTable Parse(ReadOnlyMemory<byte> decoded, RootLimits limits, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxDecodedBytes < 20 || limits.MaxRecords < 0 || limits.MaxBlocks < 0 || limits.MaxEntriesPerBlock < 0)
            throw new ArgumentOutOfRangeException(nameof(limits), "ROOT limits must be nonnegative and allow a header.");
        ct.ThrowIfCancellationRequested();
        var r = new Cursor(decoded.Span);
        if (decoded.Length > limits.MaxDecodedBytes) throw r.Fail(BuildFailure.Corrupt, "ROOT_LIMIT", "ROOT decoded byte limit exceeded.");
        if (!r.Take(4).SequenceEqual("TSFM"u8))
            throw r.Fail(BuildFailure.Unsupported, "ROOT_UNSUPPORTED", "ROOT has no TSFM signature; this layout is unsupported.");
        if (IsShortTsfmHeader(decoded.Span))
            throw r.Fail(BuildFailure.Unsupported, "ROOT_UNSUPPORTED_LAYOUT",
                "ROOT uses the pre-10.1.7 TSFM header (no header size or version), which is unsupported.");
        var headerBytes = r.U32();
        var version = r.U32();
        var total = r.U32();
        var named = r.U32();
        if (version is not (1 or 2) || headerBytes is not (20 or 24) || version == 2 && headerBytes != 24)
            throw r.Fail(BuildFailure.Unsupported, "ROOT_UNSUPPORTED", "ROOT version/header layout is unsupported.");
        if (headerBytes == 24 && r.U32() != 0)
            throw r.Fail(BuildFailure.Unsupported, "ROOT_UNSUPPORTED", "ROOT header extension is unsupported.");
        if (total > limits.MaxRecords) throw r.Fail(BuildFailure.Corrupt, "ROOT_LIMIT", "ROOT declared membership count exceeds the limit.");
        if (named > total) throw r.Fail(BuildFailure.Corrupt, "ROOT_INVALID", "ROOT named membership count exceeds its total.");

        // Every membership takes at least 20 bytes, so a header claim never sizes an allocation past the input.
        var capacity = (int)Math.Min(total, r.Remaining / 20);
        var all = new Membership[capacity];
        var names = new NameRow[(int)Math.Min(named, r.Remaining / 28)];
        var locales = new List<uint>();
        var flagsByBlock = new List<ulong>();
        var rawByBlock = new List<RootRawFlags>();
        long observed = 0, observedNamed = 0;
        uint localeUnion = 0;
        while (r.Remaining > 0)
        {
            ct.ThrowIfCancellationRequested();
            var block = locales.Count;
            if (block >= limits.MaxBlocks) throw r.Fail(BuildFailure.Corrupt, "ROOT_LIMIT", "ROOT block count exceeds the limit.");
            var count = r.U32();
            uint locale, first, second = 0;
            byte third = 0;
            if (version == 1) { first = r.U32(); locale = r.U32(); }
            else { locale = r.U32(); first = r.U32(); second = r.U32(); third = r.U8(); }
            r.Context = $"version={version}, block={block}, rawFlags={first}/{second}/{third}";
            if (count > limits.MaxEntriesPerBlock || count > limits.MaxRecords - observed)
                throw r.Fail(BuildFailure.Corrupt, "ROOT_LIMIT", "ROOT block membership count exceeds the limit.");
            if (count > total - observed)
                throw r.Fail(BuildFailure.Corrupt, "ROOT_INVALID", "ROOT block membership count exceeds the declared total.");
            var flags = first | second | ((ulong)third << 17);
            var hasNames = (flags & NoNames) == 0;
            var framing = count * (hasNames ? 28L : 20L);
            if (framing > limits.MaxDecodedBytes - r.Position)
                throw r.Fail(BuildFailure.Corrupt, "ROOT_LIMIT", "ROOT declared framing exceeds the decoded byte limit.");
            if (hasNames && count > named - observedNamed)
                throw r.Fail(BuildFailure.Corrupt, "ROOT_INVALID", "ROOT named block exceeds the declared named count.");
            if (framing > r.Remaining) throw r.Fail(BuildFailure.Corrupt, "ROOT_INVALID", "ROOT stream is truncated.");

            var n = (int)count;
            var start = (int)observed;
            var deltas = r.Take(n * 4);
            var keys = r.Take(n * 16);
            var hashes = hasNames ? r.Take(n * 8) : default;
            ulong next = 0;
            for (var i = 0; i < n; i++)
            {
                if (i % CancelEvery == CancelEvery - 1) ct.ThrowIfCancellationRequested();
                var id = next + BinaryPrimitives.ReadUInt32LittleEndian(deltas[(i * 4)..]);
                if (id > uint.MaxValue)
                    throw r.Fail(BuildFailure.Corrupt, "ROOT_INVALID", "ROOT FileDataID delta overflows unsigned32.");
                next = id + 1;
                all[start + i] = new((uint)id, block, new CdnKey(keys.Slice(i * 16, 16)));
                if (hasNames)
                    names[observedNamed + i] = new(BinaryPrimitives.ReadUInt64LittleEndian(hashes[(i * 8)..]), (uint)id, (uint)(start + i));
            }
            observed += count;
            if (hasNames) observedNamed += count;
            locales.Add(locale);
            flagsByBlock.Add(flags);
            if (version == 2) rawByBlock.Add(new(first, second, third));
            localeUnion |= locale;
        }
        if (observed != total || observedNamed != named)
            throw r.Fail(BuildFailure.Corrupt, "ROOT_INVALID", "ROOT declared and observed membership counts disagree.");

        ct.ThrowIfCancellationRequested();
        Array.Sort(all);
        ct.ThrowIfCancellationRequested();
        var fileCount = 0;
        for (var i = 0; i < all.Length; i++)
            if (i == 0 || all[i].Id != all[i - 1].Id) fileCount++;

        Array.Sort(names);
        ct.ThrowIfCancellationRequested();
        var unique = 0;
        for (var i = 0; i < names.Length; i++)
            if (i == 0 || names[i].Hash != names[i - 1].Hash) unique++;
        var nameHashes = new ulong[unique];
        var nameIds = new uint[unique];
        int conflicts = 0, at = -1;
        var conflicted = false;
        for (var i = 0; i < names.Length; i++)
        {
            if (at < 0 || names[i].Hash != nameHashes[at])
            {
                at++;
                nameHashes[at] = names[i].Hash;
                nameIds[at] = names[i].Id;
                conflicted = false;
            }
            else if (names[i].Id != nameIds[at] && !conflicted)
            {
                conflicts++;
                conflicted = true;
            }
        }
        return new((int)version, all, [.. locales], [.. flagsByBlock], version == 2 ? [.. rawByBlock] : null, nameHashes, nameIds, fileCount, localeUnion, conflicts);
    }

    // 8.2 to 10.1.5: TSFM, total count, named count, then v1 blocks at offset 12. Recognised only when the second u32 is
    // not a known header size and the first block's framing fits the counts and the input.
    private static bool IsShortTsfmHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 24) return false;
        var total = BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        var named = BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]);
        var firstCount = BinaryPrimitives.ReadUInt32LittleEndian(bytes[12..]);
        return total is not (20 or 24) && named <= total && firstCount >= 1 && firstCount <= total
            && 24 + (long)firstCount * 20 <= bytes.Length;
    }

    /// <summary>24 bytes per membership; the block index carries its locale and content flags.</summary>
    private readonly record struct Membership(uint Id, int Block, CdnKey Key) : IComparable<Membership>
    {
        // Within one block IDs strictly increase, so (Id, Block) is unique and orders variants as the file does.
        public int CompareTo(Membership other) => Id != other.Id ? Id.CompareTo(other.Id) : Block.CompareTo(other.Block);
    }

    private readonly record struct NameRow(ulong Hash, uint Id, uint Ordinal) : IComparable<NameRow>
    {
        public int CompareTo(NameRow other) => Hash != other.Hash ? Hash.CompareTo(other.Hash) : Ordinal.CompareTo(other.Ordinal);
    }

    private ref struct Cursor(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> bytes = bytes;
        public int Position { get; private set; }
        public string Context { get; set; } = "";
        public readonly int Remaining => bytes.Length - Position;

        public ReadOnlySpan<byte> Take(int length)
        {
            if (length > Remaining) throw Fail(BuildFailure.Corrupt, "ROOT_INVALID", "ROOT stream is truncated.");
            var slice = bytes.Slice(Position, length);
            Position += length;
            return slice;
        }

        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
        public byte U8() => Take(1)[0];

        public readonly BuildFormatException Fail(BuildFailure failure, string code, string message) =>
            new(failure, code, $"{message} [decodedOffset={Position}{(Context.Length == 0 ? "" : ", " + Context)}]");
    }
}
