using VibeMiner.Cdn;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.ObjectAccess.Tests;

/// <summary>Fetching listfile parts without recording them, and recording held parts and deltas later (#186).</summary>
public sealed class ListfileRecordingTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string Tag = "202611010042";
    private const int PartBytes = 8 * 1024 * 1024;
    private const int DeltaBytes = 16 * 1024 * 1024;
    private static readonly DateTimeOffset At = new(2026, 11, 1, 13, 0, 0, TimeSpan.Zero);
    private static string AssetUrl(string tag = Tag) => $"https://github.com/wowdev/wow-listfile/releases/download/{tag}/community-listfile-withcapitals.csv";
    private static ObservationRequest Part(int index, bool record = true) =>
        new(ObservationKind.ListfilePart, Tag, ObservationRequest.ListfilePartRegion(index), Record: record);
    private static Observation HeldPart(int index, byte[] bytes, string tag = Tag) =>
        new(ObservationKind.ListfilePart, tag, ObservationRequest.ListfilePartRegion(index), new Uri(AssetUrl(tag)), At, bytes);
    private static Observation Delta(byte[] bytes, string tag = Tag) =>
        new(ObservationKind.ListfileDelta, tag, "delta", new Uri(AssetUrl(tag)), At, bytes);

    [Fact]
    public void TheDeltaKindsAndRegionMatchLayerOne()
    {
        Assert.Equal(8, (int)ObservationKind.ListfileDelta);
        Assert.Equal((byte)HistoryKind.ListfileDelta, (byte)ObservationKind.ListfileDelta);
        Assert.Equal("delta", ObservationRequest.ListfileDeltaRegion);
        Assert.Equal(DeltaBytes, ObservationRequest.ListfileMaxDeltaBytes);
        Assert.True(new ObservationRequest(ObservationKind.Versions, "wow", "us").Record);
    }

    [Fact]
    public void ADeltaIsNeverAFetchableRequest()
    {
        Assert.NotNull(new ObservationRequest(ObservationKind.ListfileDelta, Tag, "delta").Problem());
        Assert.NotNull(new ObservationRequest(ObservationKind.ListfileDelta, Tag, "delta", Record: false).Problem());
    }

    public static TheoryData<string> BadObservations() =>
        ["null", "versions", "release", "definition", "summary", "partDeltaRegion", "partP64", "partP01", "deltaP0", "deltaUpper",
         "badTag", "emptyTag", "longTag", "otherTagUrl", "signedUrl", "otherAsset", "httpUrl", "emptyPart", "bigPart", "emptyDelta", "bigDelta"];

    private static Observation? Bad(string name) => name switch
    {
        "null" => null,
        "versions" => new(ObservationKind.Versions, "wow", "us", new Uri("https://us.version.battle.net/v2/products/wow/versions"), At, [1]),
        "release" => new(ObservationKind.ListfileRelease, "wowlistfile", "latest", new Uri(WowListfileObservations.ReleaseUrl), At, [1]),
        "definition" => new(ObservationKind.Definition, "0123abcd", "wowdbdefs", new Uri(AssetUrl()), At, [1]),
        "summary" => new(ObservationKind.Summary, "", "us", new Uri("https://us.version.battle.net/v2/summary"), At, [1]),
        "partDeltaRegion" => HeldPart(0, [1]) with { Region = "delta" },
        "partP64" => HeldPart(0, [1]) with { Region = "p64" },
        "partP01" => HeldPart(0, [1]) with { Region = "p01" },
        "deltaP0" => Delta([1]) with { Region = "p0" },
        "deltaUpper" => Delta([1]) with { Region = "Delta" },
        "badTag" => HeldPart(0, [1]) with { Product = "V1.0" },
        "emptyTag" => Delta([1]) with { Product = "" },
        "longTag" => Delta([1], new string('9', 65)),
        "otherTagUrl" => Delta([1]) with { Url = new Uri(AssetUrl("202611010041")) },
        "signedUrl" => HeldPart(0, [1]) with { Url = new Uri(AssetUrl() + "?sig=1") },
        "otherAsset" => Delta([1]) with { Url = new Uri($"https://github.com/wowdev/wow-listfile/releases/download/{Tag}/community-listfile.csv") },
        "httpUrl" => Delta([1]) with { Url = new Uri(AssetUrl().Replace("https:", "http:", StringComparison.Ordinal)) },
        "emptyPart" => HeldPart(0, []),
        "bigPart" => HeldPart(0, new byte[PartBytes + 1]),
        "emptyDelta" => Delta([]),
        "bigDelta" => Delta(new byte[DeltaBytes + 1]),
        _ => throw new ArgumentOutOfRangeException(nameof(name)),
    };

    [Fact]
    public async Task AnUnrecordedFetchReturnsThePartAndWritesNothing()
    {
        var s = new Scenario();
        s.Listfile.Part(Tag, 1, "2;b.blp\n"u8.ToArray(), PartBytes + 8);
        var r = await s.Source.ObserveAsync(Part(1, record: false), None);
        Assert.Equal((ObjectState.Present, false), (r.State, r.Recorded));
        Assert.Equal((ObservationKind.ListfilePart, Tag, "p1", AssetUrl(), s.Listfile.At),
            (r.Observation!.Kind, r.Observation.Product, r.Observation.Region, r.Observation.Url.AbsoluteUri, r.Observation.ObservedAt));
        Assert.Equal("2;b.blp\n"u8.ToArray(), r.Observation.Bytes);
        Assert.Empty(s.Store.History);
        Assert.Equal((0, 0), (s.Store.Count, s.Store.Puts));
        Assert.Empty(await s.Source.ReadHistoryAsync(new(), None));
    }

    [Fact]
    public async Task AnUnrecordedFetchStillRefusesAWrongShapeAndKeepsItsFailures()
    {
        var s = new Scenario();
        s.Listfile.FailPart(Tag, 0, CdnFailure.Offline);
        Assert.Equal("OBSERVATION_REQUEST_INVALID", (await s.Source.ObserveAsync(new(ObservationKind.ListfilePart, Tag, "p01", Record: false), None)).Code);
        var offline = await s.Source.ObserveAsync(Part(0, record: false), None);
        Assert.Equal((ObjectState.Unavailable, "CDN_OFFLINE", false), (offline.State, offline.Code, offline.Recorded));
        Assert.Empty(s.Store.History);
    }

    [Fact]
    public async Task ADeltaIsRefusedByObserveBeforeAnySourceIsAsked()
    {
        var s = new Scenario();
        foreach (var record in new[] { true, false })
        {
            var r = await s.Source.ObserveAsync(new(ObservationKind.ListfileDelta, Tag, "delta", Record: record), None);
            Assert.Equal((ObjectState.Refused, "OBSERVATION_REQUEST_INVALID", false), (r.State, r.Code, r.Recorded));
        }
        Assert.Empty(s.Listfile.Fetched);
        Assert.Empty(s.Store.History);
    }

    [Fact]
    public async Task RecordingAHeldPartAndADeltaAppendsOneEntryEach()
    {
        var s = new Scenario();
        s.Listfile.Part(Tag, 0, "1;a.blp\n"u8.ToArray(), 8);
        var fetched = await s.Source.ObserveAsync(Part(0, record: false), None);
        Assert.Empty(s.Store.History);

        var part = await s.Source.RecordAsync(fetched.Observation!, None);
        Assert.Equal((ObjectState.Present, true, null), (part.State, part.Recorded, part.Code));
        Assert.Same(fetched.Observation, part.Observation);
        var delta = await s.Source.RecordAsync(Delta("an opaque delta"u8.ToArray()), None);
        Assert.Equal((ObjectState.Present, true), (delta.State, delta.Recorded));

        var p = Assert.Single(await s.Source.ReadHistoryAsync(new(Kind: HistoryKind.ListfilePart), None));
        Assert.Equal((Tag, "p0", AssetUrl(), s.Listfile.At), (p.Product, p.Region, p.Url, p.ObservedAt));
        var d = Assert.Single(await s.Source.ReadHistoryAsync(new(Kind: HistoryKind.ListfileDelta), None));
        Assert.Equal((Tag, "delta", AssetUrl(), At), (d.Product, d.Region, d.Url, d.ObservedAt));
        Assert.Equal("1;a.blp\n"u8.ToArray(), (await s.Source.GetKeyAsync(new(p.Document), null, FetchPriority.Interactive, None)).Bytes);
        Assert.Equal("an opaque delta"u8.ToArray(), (await s.Source.GetKeyAsync(new(d.Document), null, FetchPriority.Interactive, None)).Bytes);
        Assert.Single(s.Listfile.Fetched);
    }

    [Fact]
    public async Task RecordingAcceptsTheLargestPartAndDelta()
    {
        var s = new Scenario();
        foreach (var o in new[] { HeldPart(63, new byte[PartBytes]), Delta(new byte[DeltaBytes], new string('9', 64)) })
        {
            var r = await s.Source.RecordAsync(o, None);
            Assert.Equal((ObjectState.Present, true), (r.State, r.Recorded));
        }
        Assert.Equal([HistoryKind.ListfilePart, HistoryKind.ListfileDelta], (await s.Source.ReadHistoryAsync(new(), None)).Select(e => e.Kind));
    }

    [Theory]
    [MemberData(nameof(BadObservations))]
    public async Task RecordingRefusesAnythingButAWellFormedPartOrDelta(string name)
    {
        var s = new Scenario();
        var r = await s.Source.RecordAsync(Bad(name)!, None);
        Assert.Equal((ObjectState.Refused, "OBSERVATION_REQUEST_INVALID", false), (r.State, r.Code, r.Recorded));
        Assert.NotNull(r.Message);
        Assert.Empty(s.Store.History);
        Assert.Equal(0, s.Store.Puts);

        var fake = new InMemoryObjectSource();
        var f = await fake.RecordAsync(Bad(name)!, None);
        Assert.Equal((ObjectState.Refused, "OBSERVATION_REQUEST_INVALID", false), (f.State, f.Code, f.Recorded));
        Assert.Empty(await fake.ReadHistoryAsync(new(), None));

        Assert.Equal((ObjectState.Refused, "OBSERVATION_REQUEST_INVALID"),
            ((await CdnOnly().RecordAsync(Bad(name)!, None)).State, (await CdnOnly().RecordAsync(Bad(name)!, None)).Code));
        var local = await new LocalObjectSource(s.Store, s.Store).RecordAsync(Bad(name)!, None);
        Assert.Equal((ObjectState.Refused, "OBSERVATION_REQUEST_INVALID"), (local.State, local.Code));
    }

    [Fact]
    public async Task AStoreFailureMapsAsItDoesForAFetchedObservation()
    {
        var failing = new FailingLog { Failure = new ObjectStoreException(ObjectStoreCodes.DiskFull, "full") };
        var s = new Scenario { HistoryLog = failing };
        s.Listfile.Part(Tag, 0, "x"u8.ToArray(), 1);
        var observed = await s.Source.ObserveAsync(Part(0), None);
        var recorded = await s.Source.RecordAsync(Delta("d"u8.ToArray()), None);
        foreach (var r in new[] { observed, recorded })
        {
            Assert.Equal((ObjectState.StoreFailed, false, ObjectStoreCodes.DiskFull, "full"), (r.State, r.Recorded, r.Code, r.Message));
            Assert.NotNull(r.Observation);
        }

        failing.Failure = null;
        failing.Refusal = new PutResult(default, PutOutcome.Refused, "OBJECT_TOO_LARGE", "too large");
        var refusedObserve = await s.Source.ObserveAsync(Part(0), None);
        var refusedRecord = await s.Source.RecordAsync(Delta("d"u8.ToArray()), None);
        Assert.Equal((refusedObserve.State, refusedObserve.Code, refusedObserve.Recorded), (refusedRecord.State, refusedRecord.Code, refusedRecord.Recorded));
        Assert.Equal((ObjectState.StoreFailed, "OBJECT_TOO_LARGE", false), (refusedRecord.State, refusedRecord.Code, refusedRecord.Recorded));
        Assert.Empty(s.Store.History);
    }

    [Fact]
    public async Task RecordingPublishesTheStoredDocument()
    {
        var s = new Scenario();
        using var subscription = s.Source.Subscribe();
        var r = await s.Source.RecordAsync(Delta("d"u8.ToArray()), None);
        Assert.True(r.Recorded);
        var entry = Assert.Single(await s.Source.ReadHistoryAsync(new(Kind: HistoryKind.ListfileDelta), None));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var commit = await subscription.Reader.ReadAsync(timeout.Token);
        Assert.Contains(entry.Document, commit.Stored);
    }

    [Fact]
    public async Task TheCdnOnlySourceFetchesAndRecordsNothing()
    {
        var client = new Scenario.ListfileClient().Part(Tag, 0, "x"u8.ToArray(), 1);
        var cdnOnly = CdnOnly(client);
        var fetched = await cdnOnly.ObserveAsync(Part(0), None);
        Assert.Equal((ObjectState.Present, false), (fetched.State, fetched.Recorded));
        var unrecorded = await cdnOnly.ObserveAsync(Part(0, record: false), None);
        Assert.Equal((ObjectState.Present, false), (unrecorded.State, unrecorded.Recorded));
        var recorded = await cdnOnly.RecordAsync(fetched.Observation!, None);
        Assert.Equal((ObjectState.Present, false, null), (recorded.State, recorded.Recorded, recorded.Code));
        Assert.Same(fetched.Observation, recorded.Observation);
        Assert.Empty(await cdnOnly.ReadHistoryAsync(new(), None));
    }

    [Fact]
    public async Task TheLocalSourceAnswersRecordingAsItAnswersObserving()
    {
        var s = new Scenario();
        var local = new LocalObjectSource(s.Store, s.Store);
        var observed = await local.ObserveAsync(Part(0), None);
        var recorded = await local.RecordAsync(Delta("d"u8.ToArray()), None);
        Assert.Equal((observed.State, observed.Code, observed.Recorded), (recorded.State, recorded.Code, recorded.Recorded));
        Assert.Equal((ObjectState.NeedsNetwork, "OBJECT_ACCESS_NEEDS_NETWORK"), (recorded.State, recorded.Code));
        Assert.Empty(s.Store.History);
    }

    [Fact]
    public async Task TheFakeFetchesWithoutRecordingAndRecordsHeldObservations()
    {
        var fake = new InMemoryObjectSource { Time = new VibeMiner.Cdn.Testing.ManualTime(At) }.AddListfile(Tag, "{}"u8.ToArray(), "1;a.blp\n"u8.ToArray());
        var fetched = await fake.ObserveAsync(Part(0, record: false), None);
        Assert.Equal((ObjectState.Present, false), (fetched.State, fetched.Recorded));
        Assert.Equal(AssetUrl(), fetched.Observation!.Url.AbsoluteUri);
        Assert.Equal("1;a.blp\n"u8.ToArray(), fetched.Observation.Bytes);
        Assert.Empty(await fake.ReadHistoryAsync(new(), None));
        Assert.Equal(Part(0, record: false), Assert.Single(fake.Observed));

        var part = await fake.RecordAsync(fetched.Observation, None);
        var delta = await fake.RecordAsync(Delta("an opaque delta"u8.ToArray()), None);
        Assert.All([part, delta], r => Assert.Equal((ObjectState.Present, true), (r.State, r.Recorded)));
        Assert.Equal([fetched.Observation.Kind, ObservationKind.ListfileDelta], fake.RecordCalls.Select(o => o.Kind));

        var p = Assert.Single(await fake.ReadHistoryAsync(new(Kind: HistoryKind.ListfilePart), None));
        var d = Assert.Single(await fake.ReadHistoryAsync(new(Kind: HistoryKind.ListfileDelta), None));
        Assert.Equal((Tag, "p0", AssetUrl()), (p.Product, p.Region, p.Url));
        Assert.Equal((Tag, "delta", AssetUrl(), At), (d.Product, d.Region, d.Url, d.ObservedAt));
        Assert.Equal([true, true], await fake.HasKeysAsync([p.Document, d.Document], None));
        Assert.Equal("an opaque delta"u8.ToArray(), (await fake.GetKeyAsync(new(d.Document), null, FetchPriority.Interactive, None)).Bytes);
        Assert.Empty(fake.Fetched);  // recorded documents are stored, not fetched
    }

    [Fact]
    public async Task TheFakesScriptedStoreFailureRecordsNothing()
    {
        var fake = new InMemoryObjectSource().FailRecording();
        var r = await fake.RecordAsync(Delta("d"u8.ToArray()), None);
        Assert.Equal((ObjectState.StoreFailed, "OBJECT_STORE_DISK_FULL", false), (r.State, r.Code, r.Recorded));
        Assert.Empty(await fake.ReadHistoryAsync(new(), None));
        Assert.True((await fake.FailRecording(null).RecordAsync(Delta("d"u8.ToArray()), None)).Recorded);
        Assert.Equal(2, fake.RecordCalls.Count);
    }

    private static CdnObjectSource CdnOnly(Scenario.ListfileClient? client = null) =>
        new(new VibeMiner.Cdn.Testing.ScriptedCdnClient(new VibeMiner.Cdn.Testing.FakeCdn()), [new WowListfileObservations(client ?? new())], ObjectVerification.Default);

    /// <summary>A history log that throws <see cref="Failure"/> or answers <see cref="Refusal"/> for the document.</summary>
    private sealed class FailingLog : IHistoryLog
    {
        public ObjectStoreException? Failure { get; set; }
        public PutResult? Refusal { get; set; }
        public Task<IReadOnlyList<PutResult>> AppendHistoryAsync(HistoryObservation observation, CancellationToken ct = default)
        {
            if (Failure is { } failure) throw failure;
            var objects = HistoryRecordFormat.Objects(observation);
            return Task.FromResult<IReadOnlyList<PutResult>>([Refusal! with { Key = objects[0].Key }, new(objects[1].Key, PutOutcome.Stored)]);
        }
        public ValueTask<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<HistoryEntry>>([]);
    }
}
