using VibeMiner.Cdn;

namespace VibeMiner.Builds.Formats;

/// <summary>One archive of a CDN config, in config order, with the entries read from its index.</summary>
internal sealed record IndexedArchive(CdnKey Archive, IReadOnlyList<ArchiveIndexEntry> Entries);

internal sealed record ArchiveLocationLimits(int MaxArchives = 100_000, long MaxEntries = 16_000_000, long MaxLooseEntries = 4_000_000);

/// <summary>
/// Where each EKey of a build lives: one sorted, compact map built from a CDN config's archive indexes and its optional
/// file-index. D133: an EKey in several archives gets the earliest archive in CDN config order, then the lowest offset; the
/// same EKey with two sizes anywhere (archives or file-index) is Corrupt <c>ARCHIVE_INDEX_SIZE_CONFLICT</c>.
/// </summary>
internal sealed class ArchiveLocations
{
    private const int SortRun = 1 << 16;
    private readonly CdnKey[] archives;
    private readonly ArraySegment<Row> rows;
    private readonly ArraySegment<Loose> loose;

    private ArchiveLocations(CdnKey[] archives, ArraySegment<Row> rows, ArraySegment<Loose> loose)
    { this.archives = archives; this.rows = rows; this.loose = loose; }

    /// <summary>Archives the CDN config listed.</summary>
    public int ArchiveCount => archives.Length;
    /// <summary>Distinct EKeys found in at least one archive.</summary>
    public long EntryCount => rows.Count;
    /// <summary>Distinct EKeys in the file-index.</summary>
    public long LooseCount => loose.Count;

    public static ArchiveLocations Build(IReadOnlyList<IndexedArchive> archives, IReadOnlyList<ArchiveIndexEntry>? fileIndex,
        ArchiveLocationLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(archives);
        ArgumentNullException.ThrowIfNull(limits);
        if (limits.MaxArchives < 0 || limits.MaxEntries < 0 || limits.MaxLooseEntries < 0 || limits.MaxEntries > Array.MaxLength ||
            limits.MaxLooseEntries > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(limits));
        ct.ThrowIfCancellationRequested();
        if (archives.Count > limits.MaxArchives) throw Corrupt("ARCHIVE_INDEX_LIMIT", "The CDN config lists more archives than admitted.");
        long total = 0;
        foreach (var archive in archives)
        {
            ArgumentNullException.ThrowIfNull(archive);
            ArgumentNullException.ThrowIfNull(archive.Entries);
            total += archive.Entries.Count;
            if (total > limits.MaxEntries) throw Corrupt("ARCHIVE_INDEX_LIMIT", "The archive indexes hold more entries than admitted.");
        }
        if (fileIndex is not null && fileIndex.Count > limits.MaxLooseEntries)
            throw Corrupt("ARCHIVE_INDEX_LIMIT", "The file-index holds more entries than admitted.");

        var keys = new CdnKey[archives.Count];
        var all = new Row[total];
        var at = 0;
        for (var a = 0; a < archives.Count; a++)
        {
            ct.ThrowIfCancellationRequested();
            keys[a] = archives[a].Archive;
            foreach (var e in archives[a].Entries)
            {
                if ((at & 4095) == 0) ct.ThrowIfCancellationRequested();
                if (e.Offset is < 0 or > uint.MaxValue) throw new ArgumentException("An archive offset is 4 bytes.", nameof(archives));
                all[at++] = new(e.EKey, a, (uint)e.Offset, Size(e, nameof(archives)));
            }
        }
        Sort(all, ct);
        var rows = Collapse(all, ct);

        var looseRows = new Loose[fileIndex?.Count ?? 0];
        for (var i = 0; i < looseRows.Length; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            looseRows[i] = new(fileIndex![i].EKey, Size(fileIndex[i], nameof(fileIndex)));
        }
        Sort(looseRows, ct);
        var loose = Collapse(looseRows, ct);
        CrossCheck(rows, loose, ct);
        ct.ThrowIfCancellationRequested();
        return new(keys, rows, loose);
    }

    /// <summary>The archive range holding <paramref name="eKey"/>, chosen by the D133 rule.</summary>
    public bool TryFind(CdnKey eKey, out ArchiveLocation location)
    {
        var i = Search(rows.AsSpan(), eKey);
        if (i < 0) { location = default; return false; }
        var row = rows[i];
        location = new(archives[row.Archive], row.Offset, row.Size);
        return true;
    }

    /// <summary>Whether <paramref name="eKey"/> is a file-index member, and its encoded size.</summary>
    public bool TryFindLoose(CdnKey eKey, out long encodedSize)
    {
        var i = Search(loose.AsSpan(), eKey);
        encodedSize = i < 0 ? 0 : loose[i].Size;
        return i >= 0;
    }

    private static uint Size(ArchiveIndexEntry e, string parameter) =>
        e.EncodedSize is < 1 or > uint.MaxValue ? throw new ArgumentException("An encoded size is 1 to 2^32-1 bytes.", parameter) : (uint)e.EncodedSize;

    /// <summary>Drops duplicates in place (the first of each run is kept) and returns the kept prefix without copying.</summary>
    private static ArraySegment<T> Collapse<T>(T[] sorted, CancellationToken ct) where T : IKeyed
    {
        var kept = 0;
        for (var i = 0; i < sorted.Length; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            if (kept > 0 && sorted[kept - 1].Key == sorted[i].Key)
            {
                if (sorted[kept - 1].Size != sorted[i].Size) throw SizeConflict();
                continue;
            }
            sorted[kept++] = sorted[i];
        }
        return new(sorted, 0, kept);
    }

    private static void CrossCheck(ReadOnlySpan<Row> rows, ReadOnlySpan<Loose> loose, CancellationToken ct)
    {
        for (int r = 0, l = 0, step = 0; r < rows.Length && l < loose.Length; step++)
        {
            if ((step & 4095) == 0) ct.ThrowIfCancellationRequested();
            var c = rows[r].Key.CompareTo(loose[l].Key);
            if (c < 0) r++;
            else if (c > 0) l++;
            else if (rows[r].Size != loose[l].Size) throw SizeConflict();
            else { r++; l++; }
        }
    }

    private static int Search<T>(ReadOnlySpan<T> sorted, CdnKey key) where T : IKeyed
    {
        int lo = 0, hi = sorted.Length - 1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            var c = sorted[mid].Key.CompareTo(key);
            if (c == 0) return mid;
            if (c < 0) lo = mid + 1; else hi = mid - 1;
        }
        return -1;
    }

    /// <summary>Sorts runs with <see cref="Array.Sort{T}(T[], int, int)"/>, then merges them, checking cancellation throughout.</summary>
    private static void Sort<T>(T[] items, CancellationToken ct) where T : IComparable<T>
    {
        var n = items.Length;
        for (var at = 0; at < n; at += SortRun)
        { ct.ThrowIfCancellationRequested(); Array.Sort(items, at, Math.Min(SortRun, n - at)); }
        if (n <= SortRun) return;
        var source = items; var target = new T[n];
        for (long width = SortRun; width < n; width *= 2)
        {
            for (long lo = 0; lo < n; lo += 2 * width)
            {
                int mid = (int)Math.Min(lo + width, n), hi = (int)Math.Min(lo + 2 * width, n);
                int i = (int)lo, j = mid, o = (int)lo;
                while (o < hi)
                {
                    if ((o & 4095) == 0) ct.ThrowIfCancellationRequested();
                    target[o++] = j >= hi || i < mid && source[i].CompareTo(source[j]) <= 0 ? source[i++] : source[j++];
                }
            }
            (source, target) = (target, source);
        }
        if (!ReferenceEquals(source, items)) Array.Copy(source, items, n);
    }

    private static BuildFormatException SizeConflict() =>
        Corrupt("ARCHIVE_INDEX_SIZE_CONFLICT", "One EKey has two different encoded sizes across the build's indexes.");
    private static BuildFormatException Corrupt(string code, string message) => new(BuildFailure.Corrupt, code, message);

    private interface IKeyed { CdnKey Key { get; } uint Size { get; } }

    private readonly record struct Row(CdnKey Key, int Archive, uint Offset, uint Size) : IComparable<Row>, IKeyed
    {
        public int CompareTo(Row other)
        {
            var c = Key.CompareTo(other.Key);
            if (c == 0) c = Archive.CompareTo(other.Archive);
            return c != 0 ? c : Offset.CompareTo(other.Offset);
        }
    }

    private readonly record struct Loose(CdnKey Key, uint Size) : IComparable<Loose>, IKeyed
    {
        public int CompareTo(Loose other) { var c = Key.CompareTo(other.Key); return c != 0 ? c : Size.CompareTo(other.Size); }
    }
}
