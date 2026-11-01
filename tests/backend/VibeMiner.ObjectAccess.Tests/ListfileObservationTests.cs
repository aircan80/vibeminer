using VibeMiner.Cdn;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.ObjectAccess.Tests;

public sealed class ListfileObservationTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string Tag = "202611010042";
    private static readonly byte[] ReleaseJson = "{\"tag_name\":\"202611010042\"}"u8.ToArray();
    private static ObservationRequest Release => new(ObservationKind.ListfileRelease, "wowlistfile", "latest");
    private static ObservationRequest Part(int index) => new(ObservationKind.ListfilePart, Tag, ObservationRequest.ListfilePartRegion(index));

    [Theory]
    [InlineData(0, "p0")]
    [InlineData(9, "p9")]
    [InlineData(10, "p10")]
    [InlineData(63, "p63")]
    public void APartRegionIsTheIndexWithoutLeadingZeros(int index, string region)
    {
        Assert.Equal(region, ObservationRequest.ListfilePartRegion(index));
        Assert.Equal(index, ObservationRequest.ListfilePartIndex(region));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(64)]
    public void APartIndexOutsideTheRangeHasNoRegion(int index) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ObservationRequest.ListfilePartRegion(index));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("p")]
    [InlineData("p00")]
    [InlineData("p01")]
    [InlineData("p64")]
    [InlineData("p99")]
    [InlineData("p100")]
    [InlineData("P1")]
    [InlineData("p-1")]
    [InlineData("p+1")]
    [InlineData("p１")]
    [InlineData(" p1")]
    [InlineData("q1")]
    [InlineData("latest")]
    public void OnlyAStrictPartRegionHasAnIndex(string? region) => Assert.Null(ObservationRequest.ListfilePartIndex(region));

    [Fact]
    public void TheWellFormedShapesHaveNoProblem()
    {
        Assert.Null(Release.Problem());
        Assert.Null(Part(0).Problem());
        Assert.Null(Part(63).Problem());
        Assert.Null(new ObservationRequest(ObservationKind.ListfilePart, "a_b-1", "p7").Problem());
        Assert.Null(new ObservationRequest(ObservationKind.ListfilePart, new string('9', 64), "p7").Problem());
        Assert.Equal(("wowlistfile", "latest"), (ObservationRequest.ListfileProduct, ObservationRequest.ListfileReleaseRegion));
    }

    [Theory]
    [InlineData(ObservationKind.ListfileRelease, "wowlistfile", "us", null)]
    [InlineData(ObservationKind.ListfileRelease, "WowListfile", "latest", null)]
    [InlineData(ObservationKind.ListfileRelease, "", "latest", null)]
    [InlineData(ObservationKind.ListfileRelease, "wowlistfile", "latest", "")]
    [InlineData(ObservationKind.ListfilePart, "202611010042", "latest", null)]
    [InlineData(ObservationKind.ListfilePart, "202611010042", "p01", null)]
    [InlineData(ObservationKind.ListfilePart, "202611010042", "p64", null)]
    [InlineData(ObservationKind.ListfilePart, "v1.0", "p0", null)]
    [InlineData(ObservationKind.ListfilePart, "../x", "p0", null)]
    [InlineData(ObservationKind.ListfilePart, "", "p0", null)]
    [InlineData(ObservationKind.ListfilePart, "a", "p0", "Map")]
    public void AWrongShapeHasAProblem(ObservationKind kind, string product, string region, string? name) =>
        Assert.NotNull(new ObservationRequest(kind, product, region, name).Problem());

    [Fact]
    public void ATagLongerThan64IsAProblem() =>
        Assert.NotNull(new ObservationRequest(ObservationKind.ListfilePart, new string('9', 65), "p0").Problem());

    [Fact]
    public async Task TheFakeRecordsAReleaseAndItsPartsWithTheirGitHubUrls()
    {
        var fake = new InMemoryObjectSource()
            .AddReply("wowlistfile", "latest", ObservationKind.ListfileRelease, ReleaseJson)
            .AddReply(Tag, "p0", ObservationKind.ListfilePart, "1;a.blp\n"u8.ToArray())
            .AddReply(Tag, "p1", ObservationKind.ListfilePart, "2;b.blp\n"u8.ToArray());

        var release = await fake.ObserveAsync(Release, None);
        Assert.Equal((ObjectState.Present, true), (release.State, release.Recorded));
        Assert.Equal("https://api.github.com/repos/wowdev/wow-listfile/releases/latest", release.Observation!.Url.AbsoluteUri);
        Assert.Equal(ReleaseJson, release.Observation.Bytes);

        var part = await fake.ObserveAsync(Part(1), None);
        Assert.Equal($"https://github.com/wowdev/wow-listfile/releases/download/{Tag}/community-listfile-withcapitals.csv", part.Observation!.Url.AbsoluteUri);
        // A part past the end answers as the real client reports GitHub's 416.
        var past = await fake.ObserveAsync(Part(2), None);
        Assert.Equal((ObjectState.Unavailable, "CDN_HTTP_STATUS", false), (past.State, past.Code, past.Recorded));

        var r = Assert.Single(await fake.ReadHistoryAsync(new(Kind: HistoryKind.ListfileRelease), None));
        Assert.Equal(("wowlistfile", "latest", release.Observation.Url.AbsoluteUri), (r.Product, r.Region, r.Url));
        var p = Assert.Single(await fake.ReadHistoryAsync(new(Product: Tag, Kind: HistoryKind.ListfilePart), None));
        Assert.Equal(("p1", part.Observation.Url.AbsoluteUri), (p.Region, p.Url));
        Assert.Equal("2;b.blp\n"u8.ToArray(), (await fake.GetKeyAsync(new(p.Document), null, FetchPriority.Interactive, None)).Bytes);
    }

    [Fact]
    public async Task TheFakeRefusesAWrongShapeAndRecordsNothing()
    {
        var fake = new InMemoryObjectSource().AddReply(Tag, "p01", ObservationKind.ListfilePart, "x"u8.ToArray());
        var r = await fake.ObserveAsync(new(ObservationKind.ListfilePart, Tag, "p01"), None);
        Assert.Equal((ObjectState.Refused, "OBSERVATION_REQUEST_INVALID", false), (r.State, r.Code, r.Recorded));
        Assert.Empty(await fake.ReadHistoryAsync(new(), None));
    }

    [Fact]
    public async Task TheRealSourceRefusesAWrongShapeBeforeLookingForASource()
    {
        var s = new Scenario();
        var bad = await s.Source.ObserveAsync(new(ObservationKind.ListfilePart, Tag, "p64"), None);
        Assert.Equal((ObjectState.Refused, "OBSERVATION_REQUEST_INVALID"), (bad.State, bad.Code));
        Assert.Empty(s.Listfile.Fetched);
        // The wired source is asked for a well-formed request; with nothing scripted it answers a dated 404.
        var good = await s.Source.ObserveAsync(Release, None);
        Assert.Equal((ObjectState.NotFound, "CDN_NOT_FOUND", s.Listfile.At), (good.State, good.Code, good.ObservedAt!.Value));
        Assert.Equal(["release"], s.Listfile.Fetched);
        Assert.Empty(s.Store.History);
    }

    private static string AssetUrl => $"https://github.com/wowdev/wow-listfile/releases/download/{Tag}/community-listfile-withcapitals.csv";
    private const int PartBytes = 8 * 1024 * 1024;

    [Fact]
    public void TheForwardersMatchTheClientsAddresses()
    {
        Assert.Equal("https://api.github.com/repos/wowdev/wow-listfile/releases/latest", WowListfileObservations.ReleaseUrl);
        Assert.Equal(AssetUrl, WowListfileObservations.AssetUrl(Tag));
        Assert.Equal("community-listfile-withcapitals.csv", WowListfileObservations.AssetName);
        Assert.Equal(WowListfileClient.PartBytes, ObservationRequest.ListfilePartBytes);
        Assert.Equal(WowListfileClient.MaxParts, ObservationRequest.ListfileMaxParts);
    }

    [Fact]
    public async Task TheAdapterServesReleasesAndPartsWithTheReplysUrlAndTime()
    {
        var client = new Scenario.ListfileClient().Release(ReleaseJson).Part(Tag, 2, "x"u8.ToArray(), 2L * PartBytes + 1);
        var adapter = new WowListfileObservations(client);
        Assert.Equal([ObservationKind.ListfileRelease, ObservationKind.ListfilePart], adapter.Kinds);
        var r = await adapter.FetchAsync(Release, None);
        Assert.Equal((ObservationKind.ListfileRelease, "wowlistfile", "latest", WowListfileObservations.ReleaseUrl, client.At),
            (r.Kind, r.Product, r.Region, r.Url.AbsoluteUri, r.ObservedAt));
        Assert.Equal(ReleaseJson, r.Bytes);
        var p = await adapter.FetchAsync(Part(2), None);
        Assert.Equal((ObservationKind.ListfilePart, Tag, "p2", AssetUrl, client.At), (p.Kind, p.Product, p.Region, p.Url.AbsoluteUri, p.ObservedAt));
        Assert.Equal("x"u8.ToArray(), p.Bytes);
        Assert.Equal(["release", $"{Tag}/2"], client.Fetched);
    }

    [Theory]
    [InlineData(1L)]
    [InlineData(0L)]
    [InlineData(PartBytes - 1L)]
    public async Task APartAtTheWrongOffsetIsAProtocolFailure(long offset)
    {
        var client = new Scenario.ListfileClient().Part(Tag, 1, "x"u8.ToArray(), 2L * PartBytes, offset);
        var e = await Assert.ThrowsAsync<CdnException>(() => new WowListfileObservations(client).FetchAsync(Part(1), None));
        Assert.Equal(CdnFailure.Protocol, e.Failure);

        var s = new Scenario();
        s.Listfile.Part(Tag, 1, "x"u8.ToArray(), 2L * PartBytes, offset);
        var r = await s.Source.ObserveAsync(Part(1), None);
        Assert.Equal((ObjectState.Refused, false), (r.State, r.Recorded));
        Assert.Equal("CDN_PROTOCOL", r.Code);
        Assert.Empty(s.Store.History);
    }

    // Part 1 of a file of `total` bytes, `length` bytes long.
    [Theory]
    [InlineData(PartBytes + 1L, 0)]                    // one byte short
    [InlineData(PartBytes + 1L, 2)]                    // one byte long
    [InlineData(2L * PartBytes, PartBytes - 1)]        // a full part, one short
    [InlineData(2L * PartBytes, PartBytes + 1)]        // a full part, one long
    [InlineData((long)PartBytes, 0)]                   // the file ends at the part's offset
    [InlineData(PartBytes - 1L, 1)]                    // the file ends before the part's offset
    [InlineData(64L * PartBytes + 1, PartBytes)]       // a file over 512 MiB
    [InlineData(long.MaxValue, PartBytes)]
    public async Task APartOfTheWrongLengthOrFileSizeIsAProtocolFailure(long total, int length)
    {
        var client = new Scenario.ListfileClient().Part(Tag, 1, new byte[length], total);
        var e = await Assert.ThrowsAsync<CdnException>(() => new WowListfileObservations(client).FetchAsync(Part(1), None));
        Assert.Equal(CdnFailure.Protocol, e.Failure);
    }

    [Theory]
    [InlineData(PartBytes + 1L, 1)]
    [InlineData(2L * PartBytes, PartBytes)]
    [InlineData(64L * PartBytes, PartBytes)]
    public async Task APartOfTheRightLengthIsServed(long total, int length)
    {
        var client = new Scenario.ListfileClient().Part(Tag, 1, new byte[length], total);
        Assert.Equal(length, (await new WowListfileObservations(client).FetchAsync(Part(1), None)).Bytes.Length);
    }

    [Theory]
    [InlineData("https://github.com/wowdev/wow-listfile/releases/download/202611010041/community-listfile-withcapitals.csv")]
    [InlineData("https://github.com/wowdev/wow-listfile/releases/download/202611010042/community-listfile.csv")]
    [InlineData("https://release-assets.githubusercontent.com/wowdev/wow-listfile/releases/download/202611010042/community-listfile-withcapitals.csv")]
    [InlineData("https://github.com/wowdev/wow-listfile/releases/download/202611010042/community-listfile-withcapitals.csv?sig=1")]
    [InlineData("http://github.com/wowdev/wow-listfile/releases/download/202611010042/community-listfile-withcapitals.csv")]
    public async Task APartFromAnotherAddressIsAProtocolFailure(string url)
    {
        var client = new Scenario.ListfileClient().Part(Tag, 0, "x"u8.ToArray(), 1, url: new Uri(url));
        var e = await Assert.ThrowsAsync<CdnException>(() => new WowListfileObservations(client).FetchAsync(Part(0), None));
        Assert.Equal(CdnFailure.Protocol, e.Failure);
        Assert.Equal(AssetUrl, e.Attempts[^1].Url.AbsoluteUri);
    }

    [Fact]
    public async Task TheFakeClientRefusesABadTagOrIndexLikeTheRealOne()
    {
        var client = new Scenario.ListfileClient();
        var tag = client.FetchPartAsync("V1.0", 0, None);
        var low = client.FetchPartAsync(Tag, -1, None);
        var high = client.FetchPartAsync(Tag, WowListfileClient.MaxParts, None);
        Assert.True(tag.IsFaulted && low.IsFaulted && high.IsFaulted);
        await Assert.ThrowsAsync<ArgumentException>(() => tag);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => low);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => high);
        Assert.Empty(client.Fetched);
    }

    [Theory]
    [InlineData(ObservationKind.Definition, "0123abcd", "wowdbdefs", "Map")]
    [InlineData(ObservationKind.Versions, "wow", "us", null)]
    [InlineData(ObservationKind.ListfileRelease, "wowlistfile", "us", null)]
    [InlineData(ObservationKind.ListfileRelease, "wowlistfile", "latest", "Map")]
    [InlineData(ObservationKind.ListfilePart, Tag, "p64", null)]
    [InlineData(ObservationKind.ListfilePart, Tag, "p01", null)]
    [InlineData(ObservationKind.ListfilePart, "../x", "p0", null)]
    [InlineData(ObservationKind.ListfilePart, Tag, "latest", null)]
    public async Task TheAdapterRefusesAWrongShapeWithoutAskingTheClient(ObservationKind kind, string product, string region, string? name)
    {
        var client = new Scenario.ListfileClient().Release(ReleaseJson).Part(Tag, 0, "x"u8.ToArray(), 1);
        await Assert.ThrowsAsync<ArgumentException>(() => new WowListfileObservations(client).FetchAsync(new(kind, product, region, name), None));
        Assert.Empty(client.Fetched);
    }

    [Fact]
    public async Task TheRealSourceRecordsAReleaseAndItsPartsInTheHistoryLog()
    {
        var s = new Scenario();
        var first = new byte[PartBytes];
        "1;a.blp\n"u8.CopyTo(first);
        s.Listfile.Release(ReleaseJson).Part(Tag, 0, first, PartBytes + 8).Part(Tag, 1, "2;b.blp\n"u8.ToArray(), PartBytes + 8);
        var release = await s.Source.ObserveAsync(Release, None);
        var p0 = await s.Source.ObserveAsync(Part(0), None);
        var p1 = await s.Source.ObserveAsync(Part(1), None);
        Assert.All([release, p0, p1], r => Assert.Equal((ObjectState.Present, true), (r.State, r.Recorded)));

        var r = Assert.Single(s.Store.History, h => h.Kind == HistoryKind.ListfileRelease);
        Assert.Equal(("wowlistfile", "latest", WowListfileObservations.ReleaseUrl, s.Listfile.At), (r.Product, r.Region, r.Url, r.ObservedAt));
        Assert.Equal(ReleaseJson, r.Reply.ToArray());
        var parts = s.Store.History.Where(h => h.Kind == HistoryKind.ListfilePart).OrderBy(h => h.Region).ToArray();
        Assert.Equal([(Tag, "p0", AssetUrl), (Tag, "p1", AssetUrl)], parts.Select(h => (h.Product, h.Region, h.Url)));
        Assert.Equal("2;b.blp\n"u8.ToArray(), parts[1].Reply.ToArray());

        var entry = Assert.Single(await s.Source.ReadHistoryAsync(new(Product: Tag, Region: "p1", Kind: HistoryKind.ListfilePart), None));
        var local = new LocalObjectSource(s.Store, s.Store);
        Assert.Equal("2;b.blp\n"u8.ToArray(), (await local.GetKeyAsync(new(entry.Document), null, FetchPriority.Interactive, None)).Bytes);
    }

    [Fact]
    public async Task ClientFailuresKeepTheirCdnMapping()
    {
        var s = new Scenario();
        s.Listfile.FailRelease(CdnFailure.Offline).FailPart(Tag, 0, CdnFailure.HttpStatus).FailPart(Tag, 1, CdnFailure.TooLarge);
        var offline = await s.Source.ObserveAsync(Release, None);
        Assert.Equal((ObjectState.Unavailable, "CDN_OFFLINE", s.Listfile.At), (offline.State, offline.Code, offline.ObservedAt!.Value));
        var status = await s.Source.ObserveAsync(Part(0), None);
        Assert.Equal((ObjectState.Unavailable, "CDN_HTTP_STATUS"), (status.State, status.Code));
        var big = await s.Source.ObserveAsync(Part(1), None);
        Assert.Equal((ObjectState.Refused, "CDN_TOO_LARGE"), (big.State, big.Code));
        var past = await s.Source.ObserveAsync(Part(2), None);  // not scripted: past the end, as a 416
        Assert.Equal((ObjectState.Unavailable, "CDN_HTTP_STATUS", s.Listfile.At), (past.State, past.Code, past.ObservedAt!.Value));
        Assert.Empty(s.Store.History);
    }

    [Fact]
    public async Task ARefusedShapeNeverReachesTheClient()
    {
        var s = new Scenario();
        s.Listfile.Release(ReleaseJson).Part(Tag, 0, "x"u8.ToArray(), 1);
        foreach (var bad in new ObservationRequest[]
                 {
                     new(ObservationKind.ListfileRelease, "wowlistfile", "p0"), new(ObservationKind.ListfileRelease, "wowlistfile", "latest", "Map"),
                     new(ObservationKind.ListfilePart, Tag, "p00"), new(ObservationKind.ListfilePart, "Tag", "p0"),
                 })
            Assert.Equal("OBSERVATION_REQUEST_INVALID", (await s.Source.ObserveAsync(bad, None)).Code);
        Assert.Empty(s.Listfile.Fetched);
        Assert.Empty(s.Store.History);
    }

    [Theory]
    [InlineData(0, new int[0])]
    [InlineData(1, new[] { 1 })]
    [InlineData(PartBytes, new[] { PartBytes })]
    [InlineData(PartBytes + 1, new[] { PartBytes, 1 })]
    [InlineData(2 * PartBytes, new[] { PartBytes, PartBytes })]
    [InlineData(2 * PartBytes + 5, new[] { PartBytes, PartBytes, 5 })]
    public async Task AddListfileSplitsTheAssetIntoParts(int length, int[] sizes)
    {
        var asset = new byte[length];
        for (var i = 0; i < length; i++) asset[i] = (byte)(i * 31 + (i >> 20));
        var fake = new InMemoryObjectSource().AddListfile(Tag, ReleaseJson, asset);

        var release = await fake.ObserveAsync(Release, None);
        Assert.Equal((ObjectState.Present, WowListfileObservations.ReleaseUrl), (release.State, release.Observation!.Url.AbsoluteUri));
        Assert.Equal(ReleaseJson, release.Observation.Bytes);
        var joined = new List<byte>();
        for (var i = 0; i < sizes.Length; i++)
        {
            var part = await fake.ObserveAsync(Part(i), None);
            Assert.Equal((ObjectState.Present, AssetUrl, sizes[i]), (part.State, part.Observation!.Url.AbsoluteUri, part.Observation.Bytes.Length));
            joined.AddRange(part.Observation.Bytes);
        }
        Assert.Equal(asset, joined.ToArray());
        var past = await fake.ObserveAsync(Part(sizes.Length), None);
        Assert.Equal((ObjectState.Unavailable, "CDN_HTTP_STATUS"), (past.State, past.Code));
        Assert.Equal(sizes.Length, (await fake.ReadHistoryAsync(new(Product: Tag, Kind: HistoryKind.ListfilePart), None)).Count);
    }

    [Fact]
    public void AddListfileRefusesAnAssetOver512MiB() =>
        Assert.Throws<ArgumentException>(() => new InMemoryObjectSource().AddListfile(Tag, ReleaseJson, new byte[64 * PartBytes + 1]));
}
