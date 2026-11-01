using System.Buffers.Binary;
using VibeMiner.Builds.Formats;
using VibeMiner.Cdn;
using Xunit;
using static VibeMiner.Builds.Tests.Formats.ArchiveIndexWriter;

namespace VibeMiner.Builds.Tests.Formats;

// Ported from ArchiveIndexParserTests and ArchiveIndexEnumerationTests (Backend.Tests), adapted to the whole-index reader.
public sealed class ArchiveIndexTests
{
    private static IReadOnlyList<ArchiveIndexEntry> Read(byte[] bytes, CdnKey key, ArchiveIndexKind kind = ArchiveIndexKind.Archive,
        ArchiveIndexLimits? limits = null, CancellationToken ct = default) => ArchiveIndex.Read(bytes, key, kind, limits ?? new(), ct);

    private static void Code(string code, BuildFailure failure, Action action)
    {
        var e = Assert.Throws<BuildFormatException>(action);
        Assert.Equal(code, e.Code); Assert.Equal(failure, e.Failure);
    }
    private static ArchiveIndexKind KindOf(bool fileIndex) => fileIndex ? ArchiveIndexKind.FileIndex : ArchiveIndexKind.Archive;
    private static void Corrupt(string code, Action action) => Code(code, BuildFailure.Corrupt, action);

    [Fact]
    public void ReadsEveryEntryAcrossMaximumBoundariesWithUnsignedFields()
    {
        var (bytes, key) = Even(171);
        Assert.Equal(2 * 4096 + 48 + 28, bytes.Length);
        var rows = Read(bytes, key);
        Assert.Equal(171, rows.Count);
        Assert.Equal(new ArchiveIndexEntry(Key(2), 7, 4294967280L), rows[0]);
        Assert.Equal(new ArchiveIndexEntry(Key(342), 7, 4294967280L), rows[^1]);
        Assert.Equal(Key(340), rows[169].EKey);
    }

    [Fact]
    public void MaximumUnsignedSizeAndOffsetAreRepresentable()
    {
        var (bytes, key) = Write([(Key(1), uint.MaxValue, uint.MaxValue)], ArchiveIndexKind.Archive);
        Assert.Equal(new ArchiveIndexEntry(Key(1), 4294967295L, 4294967295L), Read(bytes, key).Single());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyIndexIsJustItsFooter(bool fileIndex)
    {
        var kind = KindOf(fileIndex);
        var (bytes, key) = Write([], kind);
        Assert.Equal(28, bytes.Length);
        Assert.Empty(Read(bytes, key, kind));
    }

    [Fact]
    public void FileIndexHas20ByteRecords204PerPageAndNoOffsets()
    {
        var rows = Enumerable.Range(1, 205).Select(i => (Key((uint)i), (uint)(i + 10), 99u)).ToArray();
        var (bytes, key) = Write(rows, ArchiveIndexKind.FileIndex);
        Assert.Equal(2 * 4096 + 48 + 28, bytes.Length);
        var read = Read(bytes, key, ArchiveIndexKind.FileIndex);
        Assert.Equal(205, read.Count);
        Assert.Equal(new ArchiveIndexEntry(Key(204), 214, 0), read[203]);
        Assert.Equal(new ArchiveIndexEntry(Key(205), 215, 0), read[204]);
        Assert.Equal(Key(204).ToBytes(), TocOf(bytes)[..16].ToArray());
    }

    [Fact]
    public void KindMustMatchTheFooterOffsetWidth()
    {
        var (archive, archiveKey) = Even(3);
        Code("ARCHIVE_INDEX_UNSUPPORTED", BuildFailure.Unsupported, () => Read(archive, archiveKey, ArchiveIndexKind.FileIndex));
        var (file, fileKey) = Even(3, ArchiveIndexKind.FileIndex);
        Code("ARCHIVE_INDEX_UNSUPPORTED", BuildFailure.Unsupported, () => Read(file, fileKey, ArchiveIndexKind.Archive));
    }

    [Fact]
    public void KeyFooterChecksumTocAndPageProofsAreIndependent()
    {
        var (bytes, key) = Even(1);
        Corrupt("ARCHIVE_INDEX_INTEGRITY", () => Read(bytes, Key(1)));
        FooterOf(bytes)[20] ^= 1;
        Corrupt("ARCHIVE_INDEX_INTEGRITY", () => Read(bytes, new CdnKey(System.Security.Cryptography.MD5.HashData(FooterOf(bytes)))));
        (bytes, key) = Even(1);
        TocOf(bytes)[0] ^= 1; key = SealFooter(bytes);
        Corrupt("ARCHIVE_INDEX_INTEGRITY", () => Read(bytes, key));
        (bytes, key) = Even(1);
        PageOf(bytes, 0)[100] = 1;
        Corrupt("ARCHIVE_INDEX_INTEGRITY", () => Read(bytes, key));
    }

    [Theory]
    [InlineData(false, 8, 2)]
    [InlineData(false, 9, 1)]
    [InlineData(false, 10, 1)]
    [InlineData(false, 11, 8)]
    [InlineData(false, 12, 0)]
    [InlineData(false, 12, 6)]
    [InlineData(false, 13, 8)]
    [InlineData(false, 14, 9)]
    [InlineData(false, 15, 16)]
    [InlineData(true, 12, 4)]
    [InlineData(true, 12, 6)]
    [InlineData(true, 14, 9)]
    public void UnsupportedVersionFlagsAndGroupOrPrefixWidthsAreNotGuessed(bool fileIndex, int at, byte value)
    {
        var kind = KindOf(fileIndex);
        var (bytes, _) = Even(1, kind);
        FooterOf(bytes)[at] = value;
        var key = SealFooter(bytes);
        Code("ARCHIVE_INDEX_UNSUPPORTED", BuildFailure.Unsupported, () => Read(bytes, key, kind));
    }

    [Fact]
    public void FooterGeometryGovernsLengthAndLimits()
    {
        var (bytes, key) = Even(7060);
        Assert.Equal(173068, bytes.Length);
        Assert.Equal(7060, Read(bytes, key).Count);
        Corrupt("ARCHIVE_INDEX_INTEGRITY", () => ArchiveIndex.Read(bytes.AsSpan(4096), key, ArchiveIndexKind.Archive, new(), default));
        Corrupt("ARCHIVE_INDEX_INTEGRITY", () => Read([0, .. bytes], key));
        Corrupt("ARCHIVE_INDEX_LIMIT", () => Read(bytes, key, limits: new(MaxEntries: 7000)));
        Corrupt("ARCHIVE_INDEX_LIMIT", () => Read(bytes, key, limits: new(MaxPages: 41)));
        Corrupt("ARCHIVE_INDEX_LIMIT", () => Read(bytes, key, limits: new(MaxTocBytes: 1007)));
        Corrupt("ARCHIVE_INDEX_LIMIT", () => Read(bytes, key, limits: new(MaxIndexBytes: 173067)));
        BinaryPrimitives.WriteUInt32LittleEndian(FooterOf(bytes)[16..], uint.MaxValue);
        key = SealFooter(bytes);
        Corrupt("ARCHIVE_INDEX_LIMIT", () => Read(bytes, key));
    }

    [Fact]
    public void ShortInputAndBadLimitPolicyAreRefusedBeforeParsing()
    {
        var (bytes, key) = Even(1);
        foreach (var size in new[] { 0, 27 }) Corrupt("ARCHIVE_INDEX_INVALID", () => Read(new byte[size], key));
        foreach (var limits in new ArchiveIndexLimits[] { new(MaxEntries: -1), new(MaxPages: -1), new(MaxIndexBytes: 27), new(MaxTocBytes: -1) })
            Assert.Throws<ArgumentOutOfRangeException>(() => Read(bytes, key, limits: limits));
        Assert.Throws<ArgumentOutOfRangeException>(() => Read(bytes, key, (ArchiveIndexKind)7));
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("duplicate")]
    [InlineData("descending")]
    public void TocRejectsZeroDuplicateAndDescendingMaximumKeys(string kind)
    {
        var (bytes, _) = Even(171);
        var toc = TocOf(bytes);
        if (kind == "zero") toc[..16].Clear();
        if (kind == "duplicate") toc[..16].CopyTo(toc[16..]);
        if (kind == "descending") Key(1).WriteTo(toc[16..]);
        var key = Seal(bytes);
        Corrupt("ARCHIVE_INDEX_INVALID", () => Read(bytes, key));
    }

    [Theory]
    [InlineData(false, "padding")]
    [InlineData(false, "zero-size")]
    [InlineData(false, "zero-key")]
    [InlineData(false, "duplicate")]
    [InlineData(false, "descending")]
    [InlineData(true, "padding")]
    [InlineData(true, "zero-size")]
    [InlineData(true, "duplicate")]
    [InlineData(true, "descending")]
    public void PagesRejectDirtyPaddingZeroSizeDuplicateAndDescendingKeys(bool fileIndex, string kind)
    {
        var index = KindOf(fileIndex);
        var (bytes, _) = Even(2, index);
        var page = PageOf(bytes, 0); var second = RecordBytes(index);
        if (kind == "padding") page[^1] = 1;
        if (kind == "zero-size") page.Slice(16, 4).Clear();
        if (kind == "zero-key") page[..16].Clear();
        if (kind == "duplicate") page[..16].CopyTo(page[second..]);
        if (kind == "descending") Key(1).WriteTo(page[second..]);
        var key = Seal(bytes);
        Corrupt("ARCHIVE_INDEX_INVALID", () => Read(bytes, key, index));
    }

    [Fact]
    public void LastKeyMustEqualItsMaximumAndPagesMustFollowThePreviousMaximum()
    {
        var (bytes, _) = Even(171);
        Key(344).WriteTo(TocOf(bytes)[16..]);
        var key = Seal(bytes);
        Corrupt("ARCHIVE_INDEX_INVALID", () => Read(bytes, key));
        (bytes, _) = Even(171);
        Key(338).WriteTo(PageOf(bytes, 1));
        key = Seal(bytes);
        Corrupt("ARCHIVE_INDEX_INVALID", () => Read(bytes, key));
    }

    [Fact]
    public void CancellationIsObserved()
    {
        var (bytes, key) = Even(171);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => Read(bytes, key, ct: stop.Token));
    }
}
