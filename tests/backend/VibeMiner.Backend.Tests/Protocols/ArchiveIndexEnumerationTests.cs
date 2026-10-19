using System.Buffers.Binary;
using System.Security.Cryptography;
using VibeMiner.Domain;
using VibeMiner.Protocols.Tact.Archives;
using Xunit;

namespace VibeMiner.Backend.Tests.Protocols;

public sealed class ArchiveIndexEnumerationTests
{
    [Fact]
    public void EnumeratesEveryAuthenticatedEntryAcrossMaximumBoundaries()
    {
        var fixture = AuthoredArchiveIndex.Build(Enumerable.Range(1, 171)
            .Select(i => (AuthoredArchiveIndex.Key((uint)i), (uint)(i + 1), uint.MaxValue)).ToArray());
        var footer = ArchiveIndexParser.AuthenticateFooter(fixture.Footer, fixture.Identity, fixture.Bytes.Length, new());
        var toc = ArchiveIndexParser.AuthenticateToc(footer, fixture.Toc);
        var rows = Enumerable.Range(0, footer.PageCount).SelectMany(i => ArchiveIndexParser.EnumeratePage(toc,
            ArchiveIndexParser.GetPage(toc, i), fixture.Bytes.AsSpan(i * 4096, 4096))).ToArray();
        Assert.Equal(171, rows.Length);
        Assert.Equal(AuthoredArchiveIndex.Key(1), rows[0].EncodingKey);
        Assert.Equal(AuthoredArchiveIndex.Key(171), rows[^1].EncodingKey);
        Assert.Equal(172L, rows[^1].EncodedBytes);
        Assert.Equal((long)uint.MaxValue, rows[^1].ArchiveOffset);
        Assert.Throws<ArgumentOutOfRangeException>(() => ArchiveIndexParser.GetPage(toc, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ArchiveIndexParser.GetPage(toc, 2));
    }

    [Fact]
    public void EnumerationAuthenticatesWholePageAndRejectsForeignProofAndCancellation()
    {
        var fixture = AuthoredArchiveIndex.Build([(AuthoredArchiveIndex.Key(1), 7u, 9u)]);
        var footer = ArchiveIndexParser.AuthenticateFooter(fixture.Footer, fixture.Identity, fixture.Bytes.Length, new());
        var toc = ArchiveIndexParser.AuthenticateToc(footer, fixture.Toc);
        var page = ArchiveIndexParser.GetPage(toc, 0);
        var other = ArchiveIndexParser.AuthenticateToc(footer, fixture.Toc);
        Assert.Equal("ARCHIVE_INDEX_INVALID", Assert.Throws<DiscoveryException>(() =>
            ArchiveIndexParser.EnumeratePage(other, page, fixture.Bytes.AsSpan(0, 4096))).Code);
        fixture.Bytes[4095] = 1;
        Assert.Equal("ARCHIVE_INDEX_INTEGRITY", Assert.Throws<DiscoveryException>(() =>
            ArchiveIndexParser.EnumeratePage(toc, page, fixture.Bytes.AsSpan(0, 4096))).Code);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => ArchiveIndexParser.GetPage(toc, 0, stop.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => ArchiveIndexParser.EnumeratePage(toc, page,
            fixture.Bytes.AsSpan(0, 4096), stop.Token));
    }

    [Fact]
    public void AuthenticatedMalformedPaddingIsRejectedAndEmptyIndexHasNoPage()
    {
        var fixture = AuthoredArchiveIndex.Build([(AuthoredArchiveIndex.Key(1), 7u, 9u)], dirtyPadding: true);
        var footer = ArchiveIndexParser.AuthenticateFooter(fixture.Footer, fixture.Identity, fixture.Bytes.Length, new());
        var toc = ArchiveIndexParser.AuthenticateToc(footer, fixture.Toc);
        Assert.Equal("ARCHIVE_INDEX_INVALID", Assert.Throws<DiscoveryException>(() =>
            ArchiveIndexParser.EnumeratePage(toc, ArchiveIndexParser.GetPage(toc, 0), fixture.Bytes.AsSpan(0, 4096))).Code);
        fixture = AuthoredArchiveIndex.Build([]);
        footer = ArchiveIndexParser.AuthenticateFooter(fixture.Footer, fixture.Identity, fixture.Bytes.Length, new());
        toc = ArchiveIndexParser.AuthenticateToc(footer, fixture.Toc);
        Assert.Throws<ArgumentOutOfRangeException>(() => ArchiveIndexParser.GetPage(toc, 0));
    }
}

// Original deterministic metadata only. No downloaded index or game payload bytes.
internal sealed record AuthoredArchiveIndex(byte[] Bytes, byte[] Footer, byte[] Toc, string Identity)
{
    internal static string Key(uint value) => $"{value:x32}";
    internal static AuthoredArchiveIndex Build((string Key, uint Size, uint Offset)[] rows, bool dirtyPadding = false)
    {
        var pages = (rows.Length + 169) / 170;
        var data = new byte[pages * 4096]; var toc = new byte[pages * 24]; var footer = new byte[28];
        for (var i = 0; i < rows.Length; i++)
        {
            var at = i / 170 * 4096 + i % 170 * 24;
            Convert.FromHexString(rows[i].Key).CopyTo(data, at);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(at + 16), rows[i].Size);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(at + 20), rows[i].Offset);
        }
        if (dirtyPadding && pages != 0) data[^1] = 1;
        for (var i = 0; i < pages; i++)
        {
            Convert.FromHexString(rows[Math.Min(rows.Length, (i + 1) * 170) - 1].Key).CopyTo(toc, i * 16);
            MD5.HashData(data.AsSpan(i * 4096, 4096)).AsSpan(0, 8).CopyTo(toc.AsSpan(pages * 16 + i * 8));
        }
        MD5.HashData(toc).AsSpan(0, 8).CopyTo(footer);
        footer[8] = 1; footer[11] = 4; footer[12] = 4; footer[13] = 4; footer[14] = 16; footer[15] = 8;
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(16), (uint)rows.Length);
        MD5.HashData(footer.AsSpan(8, 20)).AsSpan(0, 8).CopyTo(footer.AsSpan(20));
        return new([.. data, .. toc, .. footer], footer, toc, Convert.ToHexStringLower(MD5.HashData(footer)));
    }
}
