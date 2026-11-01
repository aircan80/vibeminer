using System.Buffers.Binary;
using System.Text;
using VibeMiner.Builds.Formats;
using Xunit;
using static VibeMiner.Builds.Tests.Formats.RootWriter;

namespace VibeMiner.Builds.Tests.Formats;

public sealed class RootTableTests
{
    private static RootTable Parse(byte[] data, RootLimits? limits = null) => RootTable.Parse(data, limits ?? new(), CancellationToken.None);

    private static void Refused(byte[] data, BuildFailure failure, string code, RootLimits? limits = null)
    {
        var error = Assert.Throws<BuildFormatException>(() => Parse(data, limits));
        Assert.Equal((failure, code), (error.Failure, error.Code));
    }

    [Theory]
    [InlineData(1, 20)]
    [InlineData(1, 24)]
    [InlineData(2, 24)]
    public void KeepsNamedAndUnnamedVariantsAndUnsignedIdsInFileOrder(int version, int headerBytes)
    {
        var data = Build(version, headerBytes,
            new RootBlock([9, 3], 0x80000001, 0x40000002, version == 2 ? 0x20u : 0u, 0, [0, ulong.MaxValue]),
            new RootBlock([9, uint.MaxValue - 10], 0x80000002, NoNames));
        var root = Parse(data);

        Assert.Equal(version, root.Version);
        Assert.Equal(4, root.EntryCount);
        Assert.Equal(3, root.FileCount);
        Assert.Equal(2, root.BlockCount);
        Assert.Equal(0x80000003u, root.LocaleUnion);
        var nine = root.Find(9);
        Assert.Equal([0x80000001u, 0x80000002u], nine.Select(e => e.LocaleFlags));
        Assert.Equal(Key(0x11), nine[0].CKey);
        Assert.Equal(Key(0x11), nine[1].CKey);
        Assert.Equal(0x40000002ul | (version == 2 ? 0x20ul : 0ul), nine[0].ContentFlags);
        Assert.Equal(NoNames, nine[1].ContentFlags);
        RootRawFlags? raw = version == 2 ? new(0x40000002, 0x20, 0) : null;
        Assert.Equal(raw, nine[0].Raw);
        Assert.Equal(version == 2 ? new RootRawFlags(NoNames, 0, 0) : null, nine[1].Raw);
        Assert.Equal(new RootEntry(13, Key(0x22), 0x80000001, nine[0].ContentFlags, raw), Assert.Single(root.Find(13)));
        Assert.Equal(Key(0x22), Assert.Single(root.Find(uint.MaxValue)).CKey);
        Assert.Empty(root.Find(10));
        Assert.True(root.TryFindByNameHash(0, out var id));
        Assert.Equal(9u, id);
        Assert.True(root.TryFindByNameHash(ulong.MaxValue, out id));
        Assert.Equal(13u, id);
        Assert.False(root.TryFindByNameHash(1, out _));
        Assert.Equal(0, root.NameHashConflicts);
    }

    [Fact]
    public void VersionTwoDerivesContentFlagsAndNoNamesFromTheOr()
    {
        var root = Parse(Build(2, 24, new RootBlock([4], 1, 0x80000000, NoNames | 0x40, 0xff)));
        var row = Assert.Single(root.Find(4));
        Assert.Equal(0x80000000ul | NoNames | 0x40ul | (0xfful << 17), row.ContentFlags);
        Assert.Equal(new RootRawFlags(0x80000000, NoNames | 0x40, 0xff), row.Raw);
        Assert.False(root.TryFindByNameHash(0, out _));
    }

    [Fact]
    public void VersionTwoKeepsRawPartsPerBlockWhenTheOrIsEqual()
    {
        var root = Parse(Build(2, 24, new RootBlock([4], 2, 0x1, 0x80, 0, Names: [1]), new RootBlock([4], 2, 0x81, 0, 0, Names: [2])));
        var rows = root.Find(4);
        Assert.Equal([0x81ul, 0x81ul], rows.Select(e => e.ContentFlags));
        Assert.Equal([new RootRawFlags(0x1, 0x80, 0), new RootRawFlags(0x81, 0, 0)], rows.Select(e => e.Raw!.Value));
        Assert.All(Parse(Build(1, 20, new RootBlock([4], 2, 0x81, Names: [1]))).Find(4), e => Assert.Null(e.Raw));
    }

    [Fact]
    public void VariantsAcrossManyBlocksComeBackInFileOrder()
    {
        var root = Parse(Build(1, 20,
            new RootBlock(Deltas(5, 7), 0x2, NoNames, Keys: [Key(1), Key(2)]),
            new RootBlock(Deltas(1, 5), 0x200, NoNames, Keys: [Key(3), Key(4)]),
            new RootBlock(Deltas(5), 0x80, 0x1, Names: [77], Keys: [Key(5)])));
        Assert.Equal([Key(1), Key(4), Key(5)], root.Find(5).Select(e => e.CKey));
        Assert.Equal([0x2u, 0x200u, 0x80u], root.Find(5).Select(e => e.LocaleFlags));
        Assert.Equal(3, root.FileCount);
        Assert.True(root.TryFindByNameHash(77, out var id));
        Assert.Equal(5u, id);
    }

    [Fact]
    public void ANameHashOnTwoFileDataIdsKeepsTheFirstAndCountsTheConflict()
    {
        var root = Parse(Build(1, 20,
            new RootBlock(Deltas(8, 9), 0x2, 0, Names: [100, 200]),
            new RootBlock(Deltas(8, 30), 0x200, 0, Names: [100, 200]),
            new RootBlock(Deltas(31, 32), 0x80, 0, Names: [200, 300])));
        Assert.Equal(1, root.NameHashConflicts);
        Assert.True(root.TryFindByNameHash(100, out var id));
        Assert.Equal(8u, id);
        Assert.True(root.TryFindByNameHash(200, out id));
        Assert.Equal(9u, id);
        Assert.True(root.TryFindByNameHash(300, out id));
        Assert.Equal(32u, id);
    }

    [Theory]
    [InlineData(1, 20)]
    [InlineData(1, 24)]
    [InlineData(2, 24)]
    public void EveryTruncationIsInvalid(int version, int headerBytes)
    {
        var data = Build(version, headerBytes, new RootBlock([5, 0], 1, 0, Names: [8, 9]), new RootBlock([7], 2, NoNames));
        for (var length = 0; length < data.Length; length++)
            Refused(data[..length], BuildFailure.Corrupt, "ROOT_INVALID");
    }

    [Fact]
    public void EmptyRootsAndEmptyBlocksHaveHonestCounts()
    {
        var root = Parse(Build(1, 20), new(MaxRecords: 0, MaxBlocks: 0, MaxEntriesPerBlock: 0));
        Assert.Equal((0L, 0, 0, 0u), (root.EntryCount, root.FileCount, root.BlockCount, root.LocaleUnion));
        root = Parse(Build(1, 20, new RootBlock([], 4, NoNames)));
        Assert.Equal((0L, 1, 4u), (root.EntryCount, root.BlockCount, root.LocaleUnion));
        Assert.Empty(root.Find(0));
    }

    [Fact]
    public void UnsignedDeltaOverflowIsInvalid() =>
        Refused(Build(1, 20, new RootBlock([uint.MaxValue, 0], 1, NoNames)), BuildFailure.Corrupt, "ROOT_INVALID");

    [Fact]
    public void CountAndNamedFramingMismatchesAreInvalid()
    {
        var valid = Build(1, 20, new RootBlock([1], 1, 0, Names: [3]));
        foreach (var (offset, value) in new[] { (12, 0u), (12, 2u), (16, 0u), (16, 2u) })
        {
            var bytes = (byte[])valid.Clone();
            Put32(bytes, offset, value);
            Refused(bytes, BuildFailure.Corrupt, "ROOT_INVALID");
        }
        var unnamed = Build(1, 20, new RootBlock([1], 1, NoNames));
        Put32(unnamed, 16, 1);
        Refused(unnamed, BuildFailure.Corrupt, "ROOT_INVALID");
        Refused([.. valid, 0], BuildFailure.Corrupt, "ROOT_INVALID");
    }

    [Fact]
    public void UnknownSignatureVersionHeaderAndExtensionAreUnsupported()
    {
        var bytes = Build(2, 24);
        foreach (var (offset, value) in new[] { (0, 0u), (8, 3u), (8, 0u), (4, 28u), (4, 20u), (20, 1u) })
        {
            var changed = (byte[])bytes.Clone();
            Put32(changed, offset, value);
            Refused(changed, BuildFailure.Unsupported, "ROOT_UNSUPPORTED");
        }
    }

    [Fact]
    public void LegacyRootWithoutTsfmIsUnsupported()
    {
        // Pre-8.2 layout: blocks straight away (count, content flags, locale flags), no TSFM header.
        var legacy = new byte[12 + 4 + 16 + 8];
        Put32(legacy, 0, 1);
        Put32(legacy, 8, 2);
        Refused(legacy, BuildFailure.Unsupported, "ROOT_UNSUPPORTED");
    }

    private static byte[] ShortHeaderRoot(uint total, uint named)
    {
        // 8.2 to 10.1.5: TSFM, total, named, then v1 blocks (count, content flags, locale flags).
        var bytes = new byte[12 + (12 + 28) + (12 + 20)];
        "TSFM"u8.CopyTo(bytes);
        Put32(bytes, 4, total);
        Put32(bytes, 8, named);
        Put32(bytes, 12, 1);
        Put32(bytes, 20, 2);
        Put32(bytes, 52, 1);
        Put32(bytes, 56, (uint)NoNames);
        Put32(bytes, 60, 2);
        return bytes;
    }

    [Fact]
    public void PreTenOneSevenTsfmHeaderIsRefusedAsItsOwnLayout()
    {
        var error = Assert.Throws<BuildFormatException>(() => Parse(ShortHeaderRoot(2, 1)));
        Assert.Equal((BuildFailure.Unsupported, "ROOT_UNSUPPORTED_LAYOUT"), (error.Failure, error.Code));
        Assert.Contains("pre-10.1.7", error.Message);
    }

    [Theory]
    [InlineData(2u, 3u)]
    [InlineData(20u, 3u)]
    [InlineData(24u, 3u)]
    public void ImplausibleShortHeaderCountsStayRootUnsupported(uint total, uint named) =>
        Refused(ShortHeaderRoot(total, named), BuildFailure.Unsupported, "ROOT_UNSUPPORTED");

    [Fact]
    public void ShortHeaderWhoseFirstBlockDoesNotFitStaysRootUnsupported()
    {
        var bytes = ShortHeaderRoot(2, 1);
        Put32(bytes, 12, 3);
        Refused(bytes, BuildFailure.Unsupported, "ROOT_UNSUPPORTED");
        Put32(bytes, 12, 0);
        Refused(bytes, BuildFailure.Unsupported, "ROOT_UNSUPPORTED");
        bytes = ShortHeaderRoot(10, 1);
        Put32(bytes, 12, 4);
        Refused(bytes, BuildFailure.Unsupported, "ROOT_UNSUPPORTED");
    }

    [Fact]
    public void LimitsRefuseWithRootLimit()
    {
        var data = Build(1, 20, new RootBlock([1, 2], 1, 0, Names: [3, 4]));
        foreach (var limit in new RootLimits[] { new(MaxDecodedBytes: data.Length - 1), new(MaxDecodedBytes: 20),
            new(MaxRecords: 1), new(MaxBlocks: 0), new(MaxEntriesPerBlock: 1) })
            Refused(data, BuildFailure.Corrupt, "ROOT_LIMIT", limit);
        var huge = Build(1, 20, new RootBlock([], 1, NoNames));
        Put32(huge, 12, uint.MaxValue);
        Put32(huge, 20, uint.MaxValue);
        Refused(huge, BuildFailure.Corrupt, "ROOT_LIMIT", new(MaxRecords: uint.MaxValue, MaxEntriesPerBlock: int.MaxValue));
    }

    [Fact]
    public void AHeaderClaimingMillionsOfEntriesOverTinyInputIsInvalidNotAllocated()
    {
        var data = Build(1, 20, new RootBlock([1], 1, 0, Names: [3]));
        Put32(data, 12, 40_000_000);
        Put32(data, 16, 40_000_000);
        Refused(data, BuildFailure.Corrupt, "ROOT_INVALID");
    }

    [Fact]
    public void ExactByteLimitAllowsEofButNotOneMoreByte()
    {
        var data = Build(1, 20, new RootBlock([4], 1, NoNames));
        Assert.Equal(1, Parse(data, new(MaxDecodedBytes: data.Length)).EntryCount);
        Refused([.. data, 0], BuildFailure.Corrupt, "ROOT_LIMIT", new(MaxDecodedBytes: data.Length));
    }

    [Fact]
    public void RejectsInvalidLimitsAndHonoursCancellation()
    {
        foreach (var limit in new RootLimits[] { new(MaxDecodedBytes: 19), new(MaxRecords: -1), new(MaxBlocks: -1), new(MaxEntriesPerBlock: -1) })
            Assert.Throws<ArgumentOutOfRangeException>(() => RootTable.Parse(Build(1, 20), limit, CancellationToken.None));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => RootTable.Parse(Build(1, 20), new(), canceled.Token));
    }

    [Fact]
    public void LargeBlockIndexesEveryEntry()
    {
        const int entries = 120_000;
        var root = Parse(Build(2, 24, new RootBlock(new uint[entries], 0x1234, NoNames)));
        Assert.Equal(entries, root.EntryCount);
        Assert.Equal(entries, root.FileCount);
        Assert.Equal(0x1234u, Assert.Single(root.Find(entries - 1)).LocaleFlags);
        Assert.Empty(root.Find(entries));
    }

    // lookup3.c driver5 (Bob Jenkins, public domain): hashlittle2 outputs as (c, b) for seeds (c, b).
    [Theory]
    [InlineData("", 0u, 0u, 0xdeadbeefu, 0xdeadbeefu)]
    [InlineData("", 0u, 0xdeadbeefu, 0xbd5b7ddeu, 0xdeadbeefu)]
    [InlineData("", 0xdeadbeefu, 0xdeadbeefu, 0x9c093ccdu, 0xbd5b7ddeu)]
    [InlineData("Four score and seven years ago", 0u, 0u, 0x17770551u, 0xce7226e6u)]
    [InlineData("Four score and seven years ago", 0u, 1u, 0xe3607caeu, 0xbd371de4u)]
    [InlineData("Four score and seven years ago", 1u, 0u, 0xcd628161u, 0x6cbea4b3u)]
    public void HashLittle2MatchesLookup3TestVectors(string text, uint seedC, uint seedB, uint c, uint b)
    {
        uint pc = seedC, pb = seedB;
        RootNameHash.HashLittle2(Encoding.ASCII.GetBytes(text), ref pc, ref pb);
        Assert.Equal((c, b), (pc, pb));
    }

    [Fact]
    public void NameHashUppercasesUsesBackslashesAndPutsPcHigh()
    {
        Assert.Equal(0xdeadbeefdeadbeeful, RootNameHash.Of(""));
        Assert.Equal(0x17770551ce7226e6ul, NameOfRaw("Four score and seven years ago"));
        Assert.Equal(NameOfRaw("FOUR SCORE AND SEVEN YEARS AGO"), RootNameHash.Of("four score and seven years ago"));
        Assert.Equal(RootNameHash.Of("WORLD\\MAPS\\X.WDT"), RootNameHash.Of("world/maps/x.wdt"));
        Assert.Equal(NameOfRaw("WORLD\\MAPS\\X.WDT"), RootNameHash.Of("World/Maps\\x.wdt"));
        Assert.NotEqual(NameOfRaw("WORLD/MAPS/X.WDT"), RootNameHash.Of("world/maps/x.wdt"));
    }

    private static ulong NameOfRaw(string text)
    {
        uint pc = 0, pb = 0;
        RootNameHash.HashLittle2(Encoding.ASCII.GetBytes(text), ref pc, ref pb);
        return (ulong)pc << 32 | pb;
    }

    private static void Put32(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at, 4), value);
}
