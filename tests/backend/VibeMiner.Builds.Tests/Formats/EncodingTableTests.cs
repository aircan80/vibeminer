using System.Buffers.Binary;
using VibeMiner.Builds.Formats;
using VibeMiner.Cdn;
using Xunit;
using static VibeMiner.Builds.Tests.Formats.EncodingWriter;

namespace VibeMiner.Builds.Tests.Formats;

public sealed class EncodingTableTests
{
    private static readonly int Specs = DefaultSpecs.Length;

    private static EncodingTable Open(byte[] source, EncodingLimits? limits = null) =>
        EncodingTable.Open(source, limits ?? new(), CancellationToken.None);

    private static void AssertCode(string code, byte[] source, EncodingLimits? limits = null, BuildFailure failure = BuildFailure.Corrupt)
    {
        var error = Assert.Throws<BuildFormatException>(() => Open(source, limits));
        Assert.Equal(code, error.Code);
        Assert.Equal(failure, error.Failure);
    }

    [Fact]
    public void ReadsBothTablesCandidatesInOrderWideSizesAndSpecIndices()
    {
        var table = Open(Build(
            [Page(Content(1, (1L << 32) + 7, 11, 12)), Page(Content(2, 8, 13))],
            [Page(Encoded(11, (1L << 32) + 99, 0), Encoded(12, 12, 1)), Page(Encoded(13, 13, 0))],
            trailer: "opaque metadata"u8.ToArray()));
        Assert.Equal(2, table.ContentCount);
        Assert.Equal(3, table.EncodedCount);
        Assert.True(table.TryFindContent(Key(1), out var first));
        Assert.Equal(Key(1), first.CKey);
        Assert.Equal((1L << 32) + 7, first.DecodedSize);
        Assert.Equal([Key(11), Key(12)], first.EKeys);
        Assert.True(table.TryFindContent(Key(2), out var second));
        Assert.Equal([Key(13)], second.EKeys);
        Assert.True(table.TryFindEncoded(Key(11), out var wide));
        Assert.Equal(new EncodingEncoded(Key(11), (1L << 32) + 99, 0), wide);
        Assert.True(table.TryFindEncoded(Key(12), out var spec));
        Assert.Equal(1, spec.SpecIndex);
        Assert.True(table.TryFindEncoded(Key(13), out var last));
        Assert.Equal(13, last.EncodedSize);
    }

    [Fact]
    public void AdmitsEmptyTablesWithNoSpecificationsOrTrailer()
    {
        var table = Open(Build([], [], specs: []));
        Assert.Equal(0, table.ContentCount);
        Assert.Equal(0, table.EncodedCount);
        Assert.False(table.TryFindContent(Key(1), out _));
        Assert.False(table.TryFindEncoded(Key(1), out _));
    }

    [Fact]
    public void AbsenceBeforeBetweenAndAfterPageIntervalsIsFalse()
    {
        var table = Open(Build([Page(Content(2, 7, 11)), Page(Content(5, 9, 15))],
            [Page(Encoded(11, 17)), Page(Encoded(15, 19))]));
        foreach (var key in new uint[] { 1, 3, 6, 0 }) Assert.False(table.TryFindContent(Key(key), out _));
        foreach (var key in new uint[] { 10, 13, 16, 0 }) Assert.False(table.TryFindEncoded(Key(key), out _));
        Assert.True(table.TryFindContent(Key(5), out var found));
        Assert.Equal(9, found.DecodedSize);
    }

    [Fact]
    public void FindsRowsOnPageBoundariesFromAWriterBuiltFile()
    {
        var files = Enumerable.Range(1, 200).Select(i => (Key((uint)i * 3), (long)i, Key((uint)i * 3 + 1), (long)i + 1000)).ToList();
        var table = Open(FromFiles(files));
        Assert.Equal(200, table.ContentCount);
        Assert.Equal(200, table.EncodedCount);
        foreach (var (cKey, size, eKey, encodedSize) in files)
        {
            Assert.True(table.TryFindContent(cKey, out var c));
            Assert.Equal(size, c.DecodedSize);
            Assert.Equal([eKey], c.EKeys);
            Assert.True(table.TryFindEncoded(eKey, out var e));
            Assert.Equal(encodedSize, e.EncodedSize);
            Assert.False(table.TryFindContent(eKey, out _));
        }
    }

    [Fact]
    public void WorksOverASliceOfLargerMemory()
    {
        var source = Build([Page(Content(2, 7, 11))], [Page(Encoded(11, 17))]);
        byte[] padded = [0xaa, 0xbb, .. source, 0xcc];
        var table = EncodingTable.Open(padded.AsMemory(2, source.Length), new(), CancellationToken.None);
        Assert.True(table.TryFindContent(Key(2), out var c));
        Assert.Equal(7, c.DecodedSize);
        Assert.True(table.TryFindEncoded(Key(11), out var e));
        Assert.Equal(17, e.EncodedSize);
    }

    [Fact]
    public void RejectsEveryMissingByteOfRequiredFraming()
    {
        var full = Build([Page(Content(1, 7, 11))], [Page(Encoded(11, 9, 0))]);
        for (var length = 0; length < full.Length; length++) AssertCode("EN_INVALID", full[..length]);
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 9)]
    [InlineData(4, 9)]
    [InlineData(17, 1)]
    public void RejectsUnsupportedLayouts(int offset, byte value)
    {
        var source = Build([], []); source[offset] = value;
        AssertCode("EN_UNSUPPORTED", source, failure: BuildFailure.Unsupported);
    }

    [Fact]
    public void RejectsBadMagicAndZeroPageGeometry()
    {
        var source = Build([], []); source[0] = (byte)'X';
        AssertCode("EN_INVALID", source);
        source = Build([], []); source[5] = source[6] = 0;
        AssertCode("EN_INVALID", source);
    }

    [Fact]
    public void ChecksEveryPageMd5AtOpenIncludingUnqueriedPages()
    {
        var source = Build([Page(Content(1, 7, 11))], []);
        source[22 + Specs + 32 + 100] ^= 1;
        AssertCode("EN_INTEGRITY", source);
        var valid = Build([Page(Content(2, 7, 11))], [Page(Encoded(11, 17)), Page(Encoded(15, 19))]);
        var lastPage = valid.ToArray(); lastPage[^1] ^= 1;
        AssertCode("EN_INTEGRITY", lastPage);
    }

    [Fact]
    public void LookupRechecksThePageMd5AgainstTheIndexKeptAtOpen()
    {
        var source = Build([Page(Content(2, 7, 11))], [Page(Encoded(11, 17))]);
        var table = Open(source);
        source[22 + Specs + 32 + 100] ^= 1;
        var error = Assert.Throws<BuildFormatException>(() => table.TryFindContent(Key(2), out _));
        Assert.Equal(("EN_INTEGRITY", BuildFailure.Corrupt), (error.Code, error.Failure));
        Assert.True(table.TryFindEncoded(Key(11), out _));
        source[22 + Specs + 32 + 100] ^= 1;
        Assert.True(table.TryFindContent(Key(2), out _));
    }

    [Fact]
    public void RejectsDuplicateUnorderedAndUnboundIndexKeys()
    {
        AssertCode("EN_INVALID", Build([Page(Content(1, 1, 11)), Page(Content(1, 2, 12))], []));
        AssertCode("EN_INVALID", Build([Page(Content(2, 1, 11)), Page(Content(1, 2, 12))], []));
        AssertCode("EN_INVALID", Build([Page(Content(5, 1, 11)), Page(Content(2, 1, 12))], []));
        var source = Build([Page(Content(1, 1, 11))], []);
        KeyBytes(2).CopyTo(source, 22 + Specs);
        AssertCode("EN_INVALID", source);
        Array.Clear(source, 22 + Specs, 16);
        AssertCode("EN_INVALID", source);
    }

    [Fact]
    public void RejectsRowsCrossingIndexIntervalsAndDuplicateRows()
    {
        AssertCode("EN_INVALID", Build([Page(Content(1, 1, 11), Content(3, 3, 13)), Page(Content(2, 2, 12))], []));
        AssertCode("EN_INVALID", Build([Page(Content(1, 1, 11), Content(1, 2, 12))], []));
        AssertCode("EN_INVALID", Build([], [Page(Encoded(11, 1, 0), Encoded(11, 2, 0))]));
    }

    [Fact]
    public void RejectsCrossPageRowsEmptyPagesZeroCandidatesAndNonzeroPadding()
    {
        var crossed = Page(Content(1, 1, 11)); crossed[38] = 255;
        AssertCode("EN_INVALID", Build([crossed], []));
        var padding = Page(Content(1, 1, 11)); padding[^1] = 1;
        AssertCode("EN_INVALID", Build([padding], []));
        AssertCode("EN_INVALID", Build([Page(Content(1, 1, 0))], []));
        var empty = Build([new byte[PageBytes]], []);
        KeyBytes(1).CopyTo(empty, 22 + Specs);
        AssertCode("EN_INVALID", empty);
    }

    [Fact]
    public void ValidatesSpecificationFramingCountAndReferences()
    {
        AssertCode("EN_INVALID", Build([], [], specs: "unterminated"u8.ToArray()));
        AssertCode("EN_UNSUPPORTED", Build([], [], specs: [0xff, 0]), failure: BuildFailure.Unsupported);
        AssertCode("EN_INVALID", Build([], [Page(Encoded(11, 1, 99))]));
        AssertCode("EN_INVALID", Build([], [Page(Encoded(11, 1, 0))], specs: []));
        AssertCode("EN_LIMIT", Build([], []), new(MaxSpecificationStrings: 1));
        Assert.Equal(0, Open(Build([], []), new(MaxSpecificationStrings: 2)).EncodedCount);
        AssertCode("EN_LIMIT", Build([], []), new(MaxSpecificationBytes: 1));
    }

    [Fact]
    public void SpecIndexEqualToTheSpecificationCountIsRefused()
    {
        Open(Build([], [Page(Encoded(11, 1, 1))]));
        AssertCode("EN_INVALID", Build([], [Page(Encoded(11, 1, 2))]));
        AssertCode("EN_INVALID", Build([], [Page(Encoded(11, 1, 1))], specs: "b:*=z\0"u8.ToArray()));
    }

    [Fact]
    public void AdmitsOnlyTheLastPagesEncodedFinalMarkerAndZeroPadding()
    {
        var marked = Page(Encoded(11, 1, 0));
        marked.AsSpan(25 + 16, 4).Fill(255);
        Assert.True(Open(Build([], [marked])).TryFindEncoded(Key(11), out _));
        AssertCode("EN_INVALID", Build([], [marked, Page(Encoded(12, 2, 0))]));
        marked[^1] = 1;
        AssertCode("EN_INVALID", Build([], [marked]));
    }

    [Fact]
    public void TrailerIsBoundedAndPrintable()
    {
        AssertCode("EN_UNSUPPORTED", Build([Page(Content(1, 1, 11))], [], trailer: [0]), failure: BuildFailure.Unsupported);
        var valid = Build([Page(Content(2, 7, 11))], [Page(Encoded(11, 17))]);
        AssertCode("EN_UNSUPPORTED", [.. valid, 0], failure: BuildFailure.Unsupported);
        AssertCode("EN_LIMIT", Build([], [], trailer: "xx"u8.ToArray()), new(MaxTrailerBytes: 1));
        Open(Build([], [], trailer: "x"u8.ToArray()), new(MaxTrailerBytes: 1));
    }

    [Fact]
    public void EnforcesHeaderWorkBoundsAndPerPageAggregateCandidateLimits()
    {
        var one = Build([Page(Content(1, 1, 11))], []);
        AssertCode("EN_LIMIT", one, new(MaxDecodedBytes: one.Length - 1));
        AssertCode("EN_LIMIT", one, new(MaxPages: 0));
        AssertCode("EN_LIMIT", one, new(MaxIndexBytes: 31));
        AssertCode("EN_LIMIT", one, new(MaxRecords: 0));
        var two = Build([Page(Content(1, 1, 11), Content(2, 2, 12, 13))], []);
        AssertCode("EN_LIMIT", two, new(MaxKeysPerContent: 1));
        AssertCode("EN_LIMIT", two, new(MaxRecordsPerPage: 1));
        AssertCode("EN_LIMIT", two, new(MaxCandidateReferences: 2));
        AssertCode("EN_LIMIT", two, new(MaxRecords: 1));
        var hugePages = Build([], []);
        BinaryPrimitives.WriteUInt32BigEndian(hugePages.AsSpan(9), uint.MaxValue);
        AssertCode("EN_LIMIT", hugePages);
    }

    [Fact]
    public void InvalidLimitsAndCancellationFailBeforeParsing()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Open([], new(MaxPageBytes: 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Open([], new(MaxKeysPerContent: 256)));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            EncodingTable.Open(Build([Page(Content(1, 1, 11))], []), new(), canceled.Token));
    }

    [Fact]
    public void ConcurrentLookupsAgreeWithSequentialOnes()
    {
        var files = Enumerable.Range(1, 2000).Select(i => (Key((uint)i * 2), (long)i, Key((uint)i * 2 + 1), (long)i * 7)).ToList();
        var table = Open(FromFiles(files));
        var failures = 0;
        Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 8 }, worker =>
        {
            for (var n = 0; n < files.Count; n++)
            {
                var (cKey, size, eKey, encodedSize) = files[(n * 31 + worker * 97) % files.Count];
                if (!table.TryFindContent(cKey, out var c) || c.DecodedSize != size || c.EKeys.Single() != eKey ||
                    !table.TryFindEncoded(eKey, out var e) || e.EncodedSize != encodedSize || table.TryFindEncoded(cKey, out _))
                    Interlocked.Increment(ref failures);
            }
        });
        Assert.Equal(0, failures);
    }

    [Fact]
    public void LookupAllocatesAtMostOnePage()
    {
        var table = Open(FromFiles(Enumerable.Range(1, 100).Select(i => (Key((uint)i * 2), (long)i, Key((uint)i * 2 + 1), (long)i))));
        table.TryFindEncoded(Key(3), out _);
        table.TryFindContent(Key(2), out _);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10; i++) table.TryFindEncoded(Key(3), out _);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before <= PageBytes);
        before = GC.GetAllocatedBytesForCurrentThread();
        Assert.True(table.TryFindContent(Key(2), out _));
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before <= PageBytes + 256);
    }
}
