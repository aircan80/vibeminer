using VibeMiner.Builds.Formats;
using VibeMiner.Cdn;
using Xunit;
using static VibeMiner.Builds.Tests.Formats.ArchiveIndexWriter;

namespace VibeMiner.Builds.Tests.Formats;

public sealed class ArchiveLocationsTests
{
    private static readonly CdnKey A = Key(0xa000_0001), B = Key(0xb000_0001), C = Key(0xc000_0001);

    private static ArchiveLocations Build(IReadOnlyList<IndexedArchive> archives, IReadOnlyList<ArchiveIndexEntry>? loose = null,
        ArchiveLocationLimits? limits = null, CancellationToken ct = default) => ArchiveLocations.Build(archives, loose, limits ?? new(), ct);

    private static IndexedArchive Archive(CdnKey archive, params (uint Key, long Size, long Offset)[] rows) =>
        new(archive, rows.Select(r => new ArchiveIndexEntry(Key(r.Key), r.Size, r.Offset)).ToArray());

    private static void Corrupt(string code, Action action)
    {
        var e = Assert.Throws<BuildFormatException>(action);
        Assert.Equal(code, e.Code); Assert.Equal(BuildFailure.Corrupt, e.Failure);
    }

    [Fact]
    public void FindsArchiveRangesAndLooseMembers()
    {
        var map = Build([Archive(A, (1, 10, 0), (3, 30, 10)), Archive(B, (2, 20, 0))], [new(Key(9), 90, 0)]);
        Assert.Equal(2, map.ArchiveCount); Assert.Equal(3, map.EntryCount); Assert.Equal(1, map.LooseCount);
        Assert.True(map.TryFind(Key(3), out var location));
        Assert.Equal(new ArchiveLocation(A, 10, 30), location);
        Assert.True(map.TryFind(Key(2), out location));
        Assert.Equal(new ArchiveLocation(B, 0, 20), location);
        Assert.False(map.TryFind(Key(9), out _));
        Assert.False(map.TryFind(Key(4), out _));
        Assert.True(map.TryFindLoose(Key(9), out var size));
        Assert.Equal(90, size);
        Assert.False(map.TryFindLoose(Key(1), out _));
    }

    [Fact]
    public void EarliestArchiveInConfigOrderWinsThenLowestOffset()
    {
        var map = Build([Archive(C, (5, 50, 900)), Archive(A, (5, 50, 1), (6, 60, 70), (6, 60, 7)), Archive(B, (6, 60, 0))]);
        Assert.Equal(2, map.EntryCount);
        Assert.True(map.TryFind(Key(5), out var location));
        Assert.Equal(new ArchiveLocation(C, 900, 50), location);
        Assert.True(map.TryFind(Key(6), out location));
        Assert.Equal(new ArchiveLocation(A, 7, 60), location);
    }

    [Fact]
    public void AnEKeyInBothAnArchiveAndTheFileIndexWithOneSizeIsBoth()
    {
        var map = Build([Archive(A, (1, 10, 0))], [new(Key(1), 10, 0), new(Key(1), 10, 0)]);
        Assert.Equal(1, map.LooseCount);
        Assert.True(map.TryFind(Key(1), out _));
        Assert.True(map.TryFindLoose(Key(1), out var size));
        Assert.Equal(10, size);
    }

    [Fact]
    public void TwoSizesForOneEKeyAnywhereAreASizeConflict()
    {
        Corrupt("ARCHIVE_INDEX_SIZE_CONFLICT", () => Build([Archive(A, (1, 10, 0)), Archive(B, (1, 11, 0))]));
        Corrupt("ARCHIVE_INDEX_SIZE_CONFLICT", () => Build([Archive(A, (1, 10, 0), (1, 11, 50))]));
        Corrupt("ARCHIVE_INDEX_SIZE_CONFLICT", () => Build([Archive(A, (1, 10, 0))], [new(Key(1), 11, 0)]));
        Corrupt("ARCHIVE_INDEX_SIZE_CONFLICT", () => Build([], [new(Key(2), 5, 0), new(Key(2), 6, 0)]));
    }

    [Fact]
    public void EmptyConfigHasNothing()
    {
        var map = Build([], null);
        Assert.Equal(0, map.ArchiveCount); Assert.Equal(0, map.EntryCount); Assert.Equal(0, map.LooseCount);
        Assert.False(map.TryFind(Key(1), out _));
        Assert.False(map.TryFindLoose(Key(1), out _));
    }

    [Fact]
    public void LimitsAndOutOfRangeEntriesAreRefused()
    {
        Corrupt("ARCHIVE_INDEX_LIMIT", () => Build([Archive(A), Archive(B)], limits: new(MaxArchives: 1)));
        Corrupt("ARCHIVE_INDEX_LIMIT", () => Build([Archive(A, (1, 1, 0)), Archive(B, (2, 1, 0))], limits: new(MaxEntries: 1)));
        Corrupt("ARCHIVE_INDEX_LIMIT", () => Build([], [new(Key(1), 1, 0), new(Key(2), 1, 0)], new(MaxLooseEntries: 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Build([], limits: new(MaxEntries: -1)));
        Assert.Throws<ArgumentException>(() => Build([Archive(A, (1, 0, 0))]));
        Assert.Throws<ArgumentException>(() => Build([Archive(A, (1, 1L << 32, 0))]));
        Assert.Throws<ArgumentException>(() => Build([Archive(A, (1, 1, -1))]));
        Assert.Throws<ArgumentException>(() => Build([], [new(Key(1), 0, 0)]));
    }

    [Fact]
    public void LargeInputsAcrossSortRunsMatchAReferenceMap()
    {
        var random = new Random(165);
        var archives = Enumerable.Range(0, 8).Select(a => Key((uint)(0xd000_0000 + a))).ToArray();
        var sizes = new Dictionary<uint, long>();
        var parts = archives.Select(archive => new IndexedArchive(archive, Enumerable.Range(0, 25_000).Select(_ =>
        {
            var key = (uint)random.Next(1, 120_000);
            if (!sizes.TryGetValue(key, out var size)) sizes[key] = size = random.Next(1, 1 << 20);
            return new ArchiveIndexEntry(Key(key), size, random.Next(0, int.MaxValue));
        }).ToArray())).ToArray();
        var expected = new Dictionary<CdnKey, ArchiveLocation>();
        foreach (var part in parts)
            foreach (var e in part.Entries)
            {
                if (!expected.TryGetValue(e.EKey, out var seen) || seen.Archive == part.Archive && e.Offset < seen.Offset)
                    expected[e.EKey] = new(part.Archive, e.Offset, e.EncodedSize);
            }

        var map = Build(parts);
        Assert.Equal(expected.Count, map.EntryCount);
        foreach (var (key, location) in expected)
        {
            Assert.True(map.TryFind(key, out var found));
            Assert.Equal(location, found);
        }
        Assert.False(map.TryFind(Key(200_000), out _));
    }

    [Fact]
    public void CancellationIsObserved()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Build([Archive(A, (1, 1, 0))], ct: stop.Token));
    }

    [Fact]
    public void ReadsIndexesIntoOneMap()
    {
        var (archiveBytes, archiveKey) = Write([(Key(1), 10, 0), (Key(2), 20, 10)], ArchiveIndexKind.Archive);
        var (fileBytes, fileKey) = Write([(Key(3), 30, 0)], ArchiveIndexKind.FileIndex);
        var map = Build([new(A, ArchiveIndex.Read(archiveBytes, archiveKey, ArchiveIndexKind.Archive, new(), default))],
            ArchiveIndex.Read(fileBytes, fileKey, ArchiveIndexKind.FileIndex, new(), default));
        Assert.True(map.TryFind(Key(2), out var location));
        Assert.Equal(new ArchiveLocation(A, 10, 20), location);
        Assert.True(map.TryFindLoose(Key(3), out var size));
        Assert.Equal(30, size);
    }
}
