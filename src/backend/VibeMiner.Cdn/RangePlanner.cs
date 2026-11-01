namespace VibeMiner.Cdn;

/// <summary>Where an archive index puts an object: the archive's key, the offset and the encoded size.</summary>
public readonly record struct ArchiveLocation(CdnKey Archive, long Offset, long Size);

/// <summary>
/// One object to fetch. <see cref="Size"/> is the size its source declared (an EN row), when known. <see cref="Locations"/> are
/// the index entries that list it; none means it is loose. Index membership alone decides ranged or loose, never size.
/// </summary>
public sealed record PlanObject(CdnKey EKey, long? Size, IReadOnlyList<ArchiveLocation> Locations);

/// <summary>One object inside a merged range: where its bytes start in the range reply and how many there are.</summary>
public readonly record struct PlannedMember(CdnKey EKey, long OffsetInRange, long Size);

/// <summary>One single-range request over one archive. Gap bytes between members are fetched and dropped.</summary>
public sealed record PlannedRange(CdnKey Archive, long Offset, long Length, IReadOnlyList<PlannedMember> Members)
{
    public long MemberBytes => Members.Sum(m => m.Size);
    public long GapBytes => Length - MemberBytes;
}

/// <summary>An object no index lists: fetched whole from <c>data/</c>, with its declared size when known.</summary>
public readonly record struct PlannedLoose(CdnKey EKey, long? Size);

/// <summary>An object the plan refuses: <c>SIZE_CONFLICT</c>, <c>RANGE_OVERLAP</c> or <c>LOCATION_INVALID</c>. It fails alone.</summary>
public sealed record PlanRejection(CdnKey EKey, string Code, string Message);

public sealed record RangePlan(IReadOnlyList<PlannedRange> Ranges, IReadOnlyList<PlannedLoose> Loose, IReadOnlyList<PlanRejection> Rejected);

/// <summary>
/// Merge rule: a range keeps taking the next object of its archive while the gap is at most <see cref="GapBytes"/>, the range is
/// still under <see cref="TargetBytes"/> and the result stays within <see cref="CapBytes"/>. An object larger than the cap is a range on its own.
/// </summary>
public sealed record RangePlanOptions
{
    public long TargetBytes { get; init; } = 8L << 20;
    public long CapBytes { get; init; } = 16L << 20;
    /// <summary>1 MiB (D133): measured faster than 256 KiB on Retail (median 19.1 vs 15.7 MiB/s useful) for about 10 points more gap bytes.</summary>
    public long GapBytes { get; init; } = 1L << 20;

    internal void Validate()
    {
        if (TargetBytes < 1 || CapBytes < TargetBytes || GapBytes < 0 || GapBytes > CapBytes)
            throw new ArgumentException("Range options need 0 < target <= cap and 0 <= gap <= cap.");
    }
}

/// <summary>Pure planning of merged byte ranges over archives. No I/O, no state.</summary>
public static class RangePlanner
{
    public const string SizeConflict = "SIZE_CONFLICT", Overlap = "RANGE_OVERLAP", LocationInvalid = "LOCATION_INVALID";

    /// <param name="archiveOrder">The CDN config's archive list. It orders ranges and picks one location for an EKey listed in more than
    /// one archive (the earliest archive, then the lowest offset). Archives it doesn't list come after, in key order.</param>
    public static RangePlan Plan(IEnumerable<PlanObject> objects, IReadOnlyList<CdnKey>? archiveOrder = null, RangePlanOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(objects);
        options ??= new(); options.Validate();
        var rank = new Dictionary<CdnKey, int>();
        if (archiveOrder is not null) for (var i = 0; i < archiveOrder.Count; i++) rank.TryAdd(archiveOrder[i], i);
        int Rank(CdnKey archive) => rank.TryGetValue(archive, out var r) ? r : int.MaxValue;

        // Merge repeated EKeys first, keeping the first-seen order for loose objects.
        var order = new List<CdnKey>();
        var sizes = new Dictionary<CdnKey, HashSet<long>>();
        var locations = new Dictionary<CdnKey, HashSet<ArchiveLocation>>();
        var rejected = new Dictionary<CdnKey, PlanRejection>();
        foreach (var o in objects)
        {
            ArgumentNullException.ThrowIfNull(o);
            if (!sizes.TryGetValue(o.EKey, out var s)) { order.Add(o.EKey); sizes[o.EKey] = s = []; locations[o.EKey] = []; }
            if (o.Size is { } size) { if (size < 1) Reject(o.EKey, LocationInvalid, "A declared size is not positive."); else s.Add(size); }
            foreach (var l in o.Locations)
            {
                if (l.Offset < 0 || l.Size < 1 || l.Offset > long.MaxValue - l.Size) { Reject(o.EKey, LocationInvalid, "An index entry has an invalid offset or size."); continue; }
                locations[o.EKey].Add(l); s.Add(l.Size);
            }
        }
        void Reject(CdnKey key, string code, string message) => rejected.TryAdd(key, new(key, code, message));

        var loose = new List<PlannedLoose>();
        var chosen = new List<(ArchiveLocation Location, CdnKey EKey)>();
        foreach (var key in order)
        {
            if (rejected.ContainsKey(key)) continue;
            if (sizes[key].Count > 1) { Reject(key, SizeConflict, $"Sources declare {sizes[key].Count} different sizes for one EKey."); continue; }
            var size = sizes[key].Count == 1 ? sizes[key].First() : (long?)null;
            if (locations[key].Count == 0) { loose.Add(new(key, size)); continue; }
            var pick = locations[key].OrderBy(l => Rank(l.Archive)).ThenBy(l => l.Archive).ThenBy(l => l.Offset).First();
            chosen.Add((pick, key));
        }

        var ranges = new List<PlannedRange>();
        foreach (var archive in chosen.GroupBy(c => c.Location.Archive).OrderBy(g => Rank(g.Key)).ThenBy(g => g.Key))
        {
            var members = archive.OrderBy(c => c.Location.Offset).ThenBy(c => c.EKey).ToArray();
            // Any two members whose spans intersect are both refused: an index that overlaps itself proves neither.
            var bad = new bool[members.Length];
            var reach = -1; long reachEnd = long.MinValue;
            for (var i = 0; i < members.Length; i++)
            {
                var l = members[i].Location;
                if (l.Offset < reachEnd) { bad[i] = true; bad[reach] = true; }
                if (l.Offset + l.Size > reachEnd) { reachEnd = l.Offset + l.Size; reach = i; }
            }
            for (var i = 0; i < members.Length; i++)
                if (bad[i]) Reject(members[i].EKey, Overlap, "Its archive span overlaps another object's.");
            var kept = members.Where((_, i) => !bad[i]).ToArray();

            var start = 0;
            while (start < kept.Length)
            {
                var begin = kept[start].Location.Offset;
                var end = begin + kept[start].Location.Size;
                var next = start + 1;
                while (next < kept.Length)
                {
                    var l = kept[next].Location;
                    if (l.Offset - end > options.GapBytes || end - begin >= options.TargetBytes || l.Offset + l.Size - begin > options.CapBytes) break;
                    end = l.Offset + l.Size; next++;
                }
                ranges.Add(new(archive.Key, begin, end - begin,
                    kept[start..next].Select(m => new PlannedMember(m.EKey, m.Location.Offset - begin, m.Location.Size)).ToArray()));
                start = next;
            }
        }
        return new(ranges, loose, rejected.Values.ToArray());
    }

    /// <summary>Cuts a range reply back into its members' bytes. The reply must be exactly the range's length.</summary>
    public static IReadOnlyList<(CdnKey EKey, ReadOnlyMemory<byte> Bytes)> Split(PlannedRange range, ReadOnlyMemory<byte> reply)
    {
        ArgumentNullException.ThrowIfNull(range);
        if (reply.Length != range.Length) throw new ArgumentException("The reply is not exactly the range's length.", nameof(reply));
        var result = new (CdnKey, ReadOnlyMemory<byte>)[range.Members.Count];
        for (var i = 0; i < result.Length; i++)
        {
            var m = range.Members[i];
            result[i] = (m.EKey, reply.Slice(checked((int)m.OffsetInRange), checked((int)m.Size)));
        }
        return result;
    }
}
