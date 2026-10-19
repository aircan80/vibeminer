using System.Buffers.Binary;
using System.Security.Cryptography;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact.Archives;
using Xunit;

namespace VibeMiner.Backend.Tests.Protocols;

// Authored fixtures continue our Java ArchiveIndexParserTest at checkpoint
// 8f353d046d961108f7acc86bfe1381004467afd5. No downloaded index/archive bytes.
public sealed class ArchiveIndexParserTests
{
    [Fact]
    public void AuthenticatesMaximumBoundariesAndReturnsUnsignedArchiveRange()
    {
        var fixture = Build(171);
        var footer = Footer(fixture); var toc = ArchiveIndexParser.AuthenticateToc(footer, fixture.Toc);
        Assert.Equal(171, footer.EntryCount); Assert.Equal(2, footer.PageCount);
        Assert.Equal(8192, footer.TocOffset); Assert.Equal(48, footer.TocBytes);
        Assert.Equal(fixture.IndexBytes, footer.IndexBytes);
        Assert.Equal(0, toc.FindPage(Key(340))!.Ordinal);
        var page = toc.FindPage(Key(342).ToUpperInvariant())!;
        Assert.Equal(1, page.Ordinal); Assert.Equal(4096, page.Offset); Assert.Equal(4096, page.Length);
        var result = ArchiveIndexParser.Lookup(toc, page, fixture.Pages[1], Key(342).ToUpperInvariant())!;
        Assert.Equal(fixture.Identity, result.IndexIdentity); Assert.Equal(Key(342), result.EncodingKey);
        Assert.Equal(4294967280L, result.ArchiveOffset); Assert.Equal(7, result.EncodedBytes);
        Assert.Equal(4294967287L, result.ArchiveEndExclusive);
        Assert.Null(toc.FindPage(Key(343)));
        Assert.Null(ArchiveIndexParser.Lookup(toc, toc.FindPage(Key(3))!, fixture.Pages[0], Key(3)));
        Assert.Null(ArchiveIndexParser.Lookup(toc, toc.FindPage(Key(341))!, fixture.Pages[1], Key(341)));
    }

    [Fact]
    public void EmptyIndexesAuthenticateWithoutInventingAPage()
    {
        var fixture = Build(0); var footer = Footer(fixture);
        Assert.Equal(0, footer.EntryCount); Assert.Equal(0, footer.TocBytes); Assert.Equal(28, footer.IndexBytes);
        Assert.Null(ArchiveIndexParser.AuthenticateToc(footer, fixture.Toc).FindPage(Key(1)));
    }

    [Fact]
    public void FilenameInternalFooterTocAndPaddedPageProofsAreIndependent()
    {
        var fixture = Build(1);
        Code("ARCHIVE_INDEX_INTEGRITY", () => ArchiveIndexParser.AuthenticateFooter(fixture.Footer, Key(1), fixture.IndexBytes, new()));
        fixture.Footer[20] ^= 1;
        Code("ARCHIVE_INDEX_INTEGRITY", () => Footer(fixture)); // Filename MD5 is recomputed by fixture.Identity.
        fixture = Build(1); var footer = Footer(fixture);
        fixture.Toc[0] ^= 1;
        Code("ARCHIVE_INDEX_INTEGRITY", () => ArchiveIndexParser.AuthenticateToc(footer, fixture.Toc));
        fixture = Build(1); var toc = Toc(fixture);
        fixture.Pages[0][100] = 1;
        Code("ARCHIVE_INDEX_INTEGRITY", () => ArchiveIndexParser.Lookup(toc, toc.FindPage(Key(2))!, fixture.Pages[0], Key(2)));
    }

    [Theory]
    [InlineData(8, 2)]
    [InlineData(9, 1)]
    [InlineData(10, 1)]
    [InlineData(11, 8)]
    [InlineData(12, 0)]
    [InlineData(12, 6)]
    [InlineData(13, 8)]
    [InlineData(14, 9)]
    [InlineData(15, 16)]
    public void UnsupportedVersionFlagsAndFileGroupPrefixWidthsAreNotGuessed(int at, byte value)
    {
        var fixture = Build(1); fixture.Footer[at] = value; SealFooter(fixture);
        Code("ARCHIVE_INDEX_UNSUPPORTED", () => Footer(fixture));
    }

    [Fact]
    public void AuthenticatedCountAndGeometryGovernRangesInsteadOfAdvertisedHints()
    {
        var fixture = Build(7060); var footer = Footer(fixture);
        Assert.Equal(42, footer.PageCount); Assert.Equal(1008, footer.TocBytes); Assert.Equal(173068, footer.IndexBytes);
        Code("ARCHIVE_INDEX_INTEGRITY", () => ArchiveIndexParser.AuthenticateFooter(fixture.Footer, fixture.Identity, 135988, new()));
        Code("ARCHIVE_INDEX_INTEGRITY", () => ArchiveIndexParser.AuthenticateFooter(fixture.Footer, fixture.Identity, fixture.IndexBytes + 1, new()));
        Code("ARCHIVE_INDEX_LIMIT", () => ArchiveIndexParser.AuthenticateFooter(fixture.Footer, fixture.Identity, fixture.IndexBytes, new(MaxEntries: 7000)));
        Code("ARCHIVE_INDEX_LIMIT", () => ArchiveIndexParser.AuthenticateFooter(fixture.Footer, fixture.Identity, fixture.IndexBytes, new(MaxPages: 41)));
        Code("ARCHIVE_INDEX_LIMIT", () => ArchiveIndexParser.AuthenticateFooter(fixture.Footer, fixture.Identity, fixture.IndexBytes, new(MaxTocBytes: 1007)));
        Code("ARCHIVE_INDEX_LIMIT", () => ArchiveIndexParser.AuthenticateFooter(fixture.Footer, fixture.Identity, fixture.IndexBytes, new(MaxIndexBytes: 173067)));
        BinaryPrimitives.WriteUInt32LittleEndian(fixture.Footer.AsSpan(16), uint.MaxValue); SealFooter(fixture);
        Code("ARCHIVE_INDEX_LIMIT", () => Footer(fixture));
    }

    [Fact]
    public void ExactLengthsAndLimitPolicyAreCheckedBeforeParsing()
    {
        var fixture = Build(1);
        foreach (var size in new[] { 0, 27, 29 })
            Code("ARCHIVE_INDEX_INVALID", () => ArchiveIndexParser.AuthenticateFooter(new byte[size], fixture.Identity, fixture.IndexBytes, new()));
        foreach (var limits in new ArchiveIndexLimits[] { new(MaxEntries: -1), new(MaxPages: -1), new(MaxIndexBytes: 27), new(MaxTocBytes: -1) })
            Assert.Throws<ArgumentOutOfRangeException>(() => ArchiveIndexParser.AuthenticateFooter(fixture.Footer, fixture.Identity, fixture.IndexBytes, limits));
        var footer = Footer(fixture);
        Code("ARCHIVE_INDEX_INVALID", () => ArchiveIndexParser.AuthenticateToc(footer, new byte[23]));
        Code("ARCHIVE_INDEX_INVALID", () => ArchiveIndexParser.AuthenticateToc(footer, new byte[25]));
        var toc = Toc(fixture); var page = toc.FindPage(Key(2))!;
        foreach (var length in new[] { 0, 4095, 4097 })
            Code("ARCHIVE_INDEX_INVALID", () => ArchiveIndexParser.Lookup(toc, page, new byte[length], Key(2)));
        Assert.Throws<ArgumentException>(() => toc.FindPage("abc"));
        Assert.Throws<ArgumentException>(() => toc.FindPage(new string('x', 32)));
        Assert.Throws<ArgumentException>(() => toc.FindPage(new string('1', 18)));
    }

    [Fact]
    public void AuthenticatedTocRejectsZeroDuplicateAndDescendingMaximumKeys()
    {
        foreach (var kind in new[] { "zero", "duplicate", "descending" })
        {
            var fixture = Build(171);
            if (kind == "zero") fixture.Toc.AsSpan(0, 16).Clear();
            if (kind == "duplicate") fixture.Toc.AsSpan(0, 16).CopyTo(fixture.Toc.AsSpan(16));
            if (kind == "descending") KeyBytes(1).CopyTo(fixture.Toc, 16);
            Seal(fixture);
            Code("ARCHIVE_INDEX_INVALID", () => Toc(fixture));
        }
    }

    [Fact]
    public void AuthenticatedPageRejectsDirtyPaddingZeroSizeDuplicateAndDescendingKeys()
    {
        foreach (var kind in new[] { "padding", "zero-size", "zero-key", "duplicate", "descending" })
        {
            var fixture = Build(2);
            if (kind == "padding") fixture.Pages[0][100] = 1;
            if (kind == "zero-size") fixture.Pages[0].AsSpan(16, 4).Clear();
            if (kind == "zero-key") fixture.Pages[0].AsSpan(0, 16).Clear();
            if (kind == "duplicate") fixture.Pages[0].AsSpan(0, 16).CopyTo(fixture.Pages[0].AsSpan(24));
            if (kind == "descending") KeyBytes(1).CopyTo(fixture.Pages[0], 24);
            Seal(fixture); var toc = Toc(fixture);
            Code("ARCHIVE_INDEX_INVALID", () => ArchiveIndexParser.Lookup(toc, toc.FindPage(Key(2))!, fixture.Pages[0], Key(2)));
        }
    }

    [Fact]
    public void RejectsWrongLastMaximumCrossPageOrderingAndForeignBindingsEvenWithValidHashes()
    {
        var fixture = Build(171); KeyBytes(344).CopyTo(fixture.Toc, 16); Seal(fixture);
        var toc = Toc(fixture);
        Code("ARCHIVE_INDEX_INVALID", () => ArchiveIndexParser.Lookup(toc, toc.FindPage(Key(342))!, fixture.Pages[1], Key(342)));
        fixture = Build(171); KeyBytes(338).CopyTo(fixture.Pages[1], 0); Seal(fixture); toc = Toc(fixture);
        Code("ARCHIVE_INDEX_INVALID", () => ArchiveIndexParser.Lookup(toc, toc.FindPage(Key(342))!, fixture.Pages[1], Key(342)));
        fixture = Build(171); toc = Toc(fixture); var other = Toc(fixture);
        Code("ARCHIVE_INDEX_INVALID", () => ArchiveIndexParser.Lookup(toc, other.FindPage(Key(2))!, fixture.Pages[0], Key(2)));
        Code("ARCHIVE_INDEX_INVALID", () => ArchiveIndexParser.Lookup(toc, toc.FindPage(Key(2))!, fixture.Pages[0], Key(342)));
        Code("ARCHIVE_INDEX_INVALID", () => ArchiveIndexParser.Lookup(toc, toc.FindPage(Key(342))!, fixture.Pages[1], Key(340)));
    }

    [Fact]
    public void BoundProofsOwnBytesAndMaximumUnsignedEndIsRepresentable()
    {
        var fixture = Build(1);
        BinaryPrimitives.WriteUInt32BigEndian(fixture.Pages[0].AsSpan(16), uint.MaxValue);
        BinaryPrimitives.WriteUInt32BigEndian(fixture.Pages[0].AsSpan(20), uint.MaxValue); Seal(fixture);
        var footer = Footer(fixture); fixture.Footer.AsSpan().Clear();
        var toc = ArchiveIndexParser.AuthenticateToc(footer, fixture.Toc); fixture.Toc.AsSpan().Clear();
        var result = ArchiveIndexParser.Lookup(toc, toc.FindPage(Key(2))!, fixture.Pages[0], Key(2))!;
        Assert.Equal(8589934590L, result.ArchiveEndExclusive);
        Assert.Equal(4294967295L, result.EncodedBytes);
        Assert.Equal(4294967295L, result.ArchiveOffset);
    }

    [Fact]
    public void CancellationIsObservedAtEveryPublicBoundary()
    {
        var fixture = Build(171); var footer = Footer(fixture); var toc = Toc(fixture); var page = toc.FindPage(Key(342))!;
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); var token = canceled.Token;
        Assert.ThrowsAny<OperationCanceledException>(() => ArchiveIndexParser.AuthenticateFooter(fixture.Footer, fixture.Identity, fixture.IndexBytes, new(), token));
        Assert.ThrowsAny<OperationCanceledException>(() => ArchiveIndexParser.AuthenticateToc(footer, fixture.Toc, token));
        Assert.ThrowsAny<OperationCanceledException>(() => toc.FindPage(Key(342), token));
        Assert.ThrowsAny<OperationCanceledException>(() => ArchiveIndexParser.Lookup(toc, page, fixture.Pages[1], Key(342), token));
    }

    private sealed record Fixture(byte[] Footer, byte[] Toc, byte[][] Pages)
    {
        public string Identity => Convert.ToHexStringLower(MD5.HashData(Footer));
        public long IndexBytes => Pages.Length * 4120L + 28;
    }
    private static ArchiveIndexFooter Footer(Fixture fixture) => ArchiveIndexParser.AuthenticateFooter(fixture.Footer, fixture.Identity, fixture.IndexBytes, new());
    private static ArchiveIndexToc Toc(Fixture fixture) => ArchiveIndexParser.AuthenticateToc(Footer(fixture), fixture.Toc);
    private static Fixture Build(int count)
    {
        var pages = (count + 169) / 170;
        var data = Enumerable.Range(0, pages).Select(_ => new byte[4096]).ToArray();
        var toc = new byte[pages * 24]; var footer = new byte[28];
        for (var index = 0; index < count; index++)
        {
            var page = index / 170; var at = index % 170 * 24;
            KeyBytes((uint)((index + 1) * 2)).CopyTo(data[page], at);
            BinaryPrimitives.WriteUInt32BigEndian(data[page].AsSpan(at + 16), 7);
            BinaryPrimitives.WriteUInt32BigEndian(data[page].AsSpan(at + 20), 0xfffffff0);
        }
        for (var page = 0; page < pages; page++) KeyBytes((uint)(Math.Min(count, (page + 1) * 170) * 2)).CopyTo(toc, page * 16);
        footer[8] = 1; footer[11] = 4; footer[12] = 4; footer[13] = 4; footer[14] = 16; footer[15] = 8;
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(16), (uint)count);
        var result = new Fixture(footer, toc, data); Seal(result); return result;
    }
    private static void Seal(Fixture fixture)
    {
        for (var page = 0; page < fixture.Pages.Length; page++)
            MD5.HashData(fixture.Pages[page]).AsSpan(0, 8).CopyTo(fixture.Toc.AsSpan(fixture.Pages.Length * 16 + page * 8));
        MD5.HashData(fixture.Toc).AsSpan(0, 8).CopyTo(fixture.Footer);
        SealFooter(fixture);
    }
    private static void SealFooter(Fixture fixture)
    {
        fixture.Footer.AsSpan(20, 8).Clear();
        MD5.HashData(fixture.Footer.AsSpan(8, 20)).AsSpan(0, 8).CopyTo(fixture.Footer.AsSpan(20));
    }
    private static byte[] KeyBytes(uint key)
    { var bytes = new byte[16]; BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), key); return bytes; }
    private static string Key(uint key) => Convert.ToHexStringLower(KeyBytes(key));
    private static void Code(string code, Action action) => Assert.Equal(code, Assert.Throws<DiscoveryException>(action).Code);
}
