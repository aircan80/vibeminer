using VibeMiner.Cdn;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;

namespace VibeMiner.ObjectAccess.Tests;

public sealed class DefinitionObservationTests
{
    private static readonly CancellationToken None = CancellationToken.None;
    private const string Id = "0123abcd", Rev = "2222222222222222222222222222222222222222";
    private static readonly byte[] Dbd = "COLUMNS\nint ID\n"u8.ToArray();
    private static ObservationRequest Map => new(ObservationKind.Definition, Id, "wowdbdefs", "Map");

    [Fact]
    public async Task TheAdapterServesOnlyDefinitionsWithTheReplysUrlAndTime()
    {
        var client = new Scenario.DefinitionClient().Add("Map", Rev, Dbd);
        var adapter = new WoWDBDefsObservations(client);
        Assert.Equal([ObservationKind.Definition], adapter.Kinds);
        var o = await adapter.FetchAsync(Map, None);
        Assert.Equal((ObservationKind.Definition, Id, "wowdbdefs"), (o.Kind, o.Product, o.Region));
        Assert.Equal($"https://raw.githubusercontent.com/wowdev/WoWDBDefs/{Rev}/definitions/Map.dbd", o.Url.AbsoluteUri);
        Assert.Equal(client.At, o.ObservedAt);
        Assert.Equal(Dbd, o.Bytes);
        Assert.Equal(["Map"], client.Fetched);
    }

    [Theory]
    [InlineData(ObservationKind.Versions, Id, "wowdbdefs", "Map")]
    [InlineData(ObservationKind.Definition, "0123abcG", "wowdbdefs", "Map")]
    [InlineData(ObservationKind.Definition, "0123abcd0", "wowdbdefs", "Map")]
    [InlineData(ObservationKind.Definition, Id, "eu", "Map")]
    [InlineData(ObservationKind.Definition, Id, "wowdbdefs", null)]
    [InlineData(ObservationKind.Definition, Id, "wowdbdefs", "Map.dbd")]
    public async Task TheAdapterRefusesAWrongShapeWithoutAskingTheClient(ObservationKind kind, string product, string region, string? name)
    {
        var client = new Scenario.DefinitionClient().Add("Map", Rev, Dbd);
        await Assert.ThrowsAsync<ArgumentException>(() => new WoWDBDefsObservations(client).FetchAsync(new(kind, product, region, name), None));
        Assert.Empty(client.Fetched);
    }

    [Fact]
    public async Task TheAdapterLetsTheClientsFailureThrough()
    {
        var client = new Scenario.DefinitionClient().Fail("Map", CdnFailure.HttpStatus);
        var e = await Assert.ThrowsAsync<CdnException>(() => new WoWDBDefsObservations(client).FetchAsync(Map, None));
        Assert.Equal(CdnFailure.HttpStatus, e.Overall);
    }

    [Fact]
    public async Task ADefinitionGoesIntoTheHistoryLogAndReadsBackByTable()
    {
        var s = new Scenario();
        s.Definitions.Add("Map", Rev, Dbd).Add("Spell", Rev, "COLUMNS\n"u8.ToArray());
        var r = await s.Source.ObserveAsync(Map, None);
        await s.Source.ObserveAsync(new(ObservationKind.Definition, "89abcdef", "wowdbdefs", "Spell"), None);
        await s.Source.ObserveAsync(new(ObservationKind.Versions, "wow", "us"), None);
        Assert.Equal((ObjectState.Present, true), (r.State, r.Recorded));
        var h = Assert.Single(s.Store.History, h => h.Product == Id);
        Assert.Equal((HistoryKind.Definition, "wowdbdefs", WoWDBDefsClient.DefinitionUrl(Rev, "Map"), s.Definitions.At), (h.Kind, h.Region, h.Url, h.ObservedAt));
        Assert.Equal(Dbd, h.Reply.ToArray());

        var entry = Assert.Single(await s.Source.ReadHistoryAsync(new(Product: Id, Kind: HistoryKind.Definition), None));
        Assert.Equal(h.Url, entry.Url);
        Assert.Equal(2, (await s.Source.ReadHistoryAsync(new(Kind: HistoryKind.Definition), None)).Count);
        var local = new LocalObjectSource(s.Store, s.Store);
        Assert.Equal(Dbd, (await local.GetKeyAsync(new(entry.Document), null, FetchPriority.Interactive, None)).Bytes);
    }

    [Fact]
    public async Task ClientFailuresKeepTheirCdnMapping()
    {
        var s = new Scenario();
        s.Definitions.Fail("Map", CdnFailure.Offline).Fail("Big", CdnFailure.TooLarge);
        var offline = await s.Source.ObserveAsync(Map, None);
        Assert.Equal((ObjectState.Unavailable, "CDN_OFFLINE", s.Definitions.At), (offline.State, offline.Code, offline.ObservedAt!.Value));
        var big = await s.Source.ObserveAsync(new(ObservationKind.Definition, Id, "wowdbdefs", "Big"), None);
        Assert.Equal((ObjectState.Refused, "CDN_TOO_LARGE"), (big.State, big.Code));
        Assert.Empty(s.Store.History);
    }

    [Fact]
    public async Task ARefusedShapeNeverReachesTheClient()
    {
        var s = new Scenario();
        s.Definitions.Add("Map", Rev, Dbd);
        var r = await s.Source.ObserveAsync(new(ObservationKind.Definition, "0123ABCD", "wowdbdefs", "Map"), None);
        Assert.Equal((ObjectState.Refused, "OBSERVATION_REQUEST_INVALID"), (r.State, r.Code));
        var named = await s.Source.ObserveAsync(new(ObservationKind.Cdns, "wow", "us", "Map"), None);
        Assert.Equal("OBSERVATION_REQUEST_INVALID", named.Code);
        Assert.Empty(s.Definitions.Fetched);
        Assert.Equal(0, s.Versions.Requests);
    }

    [Fact]
    public async Task ABadVersionRequestThrowsFromItsSourceAndFetchesNothing()
    {
        var s = new Scenario();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => s.Source.ObserveAsync(new(ObservationKind.Versions, "wow", "mars"), None));
        Assert.Equal(0, s.Versions.Requests);
    }

    [Fact]
    public async Task TheLocalOnlySourceNeverFetchesADefinition()
    {
        var store = new InMemoryObjectStore();
        var local = new LocalObjectSource(store, store);
        var r = await local.ObserveAsync(Map, None);
        Assert.Equal((ObjectState.NeedsNetwork, "OBJECT_ACCESS_NEEDS_NETWORK", false), (r.State, r.Code, r.Recorded));
        Assert.Empty(store.History);
    }

    [Fact]
    public async Task TheCdnOnlySourceObservesADefinitionWithoutRecordingIt()
    {
        var s = new Scenario();
        var client = new Scenario.DefinitionClient().Add("Map", Rev, Dbd);
        var cdnOnly = new CdnObjectSource(new Cdn.Testing.ScriptedCdnClient(s.Cdn), [new WoWDBDefsObservations(client)], ObjectVerification.Default);
        var r = await cdnOnly.ObserveAsync(Map, None);
        Assert.Equal((ObjectState.Present, false), (r.State, r.Recorded));
        Assert.Equal(WoWDBDefsClient.DefinitionUrl(Rev, "Map"), r.Observation!.Url.AbsoluteUri);
        Assert.Empty(await cdnOnly.ReadHistoryAsync(new(), None));
        var versions = await cdnOnly.ObserveAsync(new(ObservationKind.Versions, "wow", "us"), None);
        Assert.Equal("OBSERVATION_SOURCE_MISSING", versions.Code);
    }

    [Fact]
    public async Task TheFakeScriptsFailuresAndCountsEveryAsk()
    {
        var fake = new InMemoryObjectSource().AddDefinition(Id, "Map", Rev, Dbd)
            .FailObservation(Id, "wowdbdefs", ObservationKind.Definition, ObjectState.Unavailable, "CDN_HTTP_STATUS", "rate limited");
        var failed = await fake.ObserveAsync(Map, None);
        Assert.Equal((ObjectState.Unavailable, "CDN_HTTP_STATUS", "rate limited", false), (failed.State, failed.Code, failed.Message, failed.Recorded));
        Assert.NotNull(failed.ObservedAt);
        Assert.Empty(await fake.ReadHistoryAsync(new(), None));

        var otherName = await fake.ObserveAsync(Map with { Name = "Spell" }, None);
        Assert.Equal(ObjectState.Unavailable, otherName.State);  // the failure is per (product, region, kind)
        fake.AddDefinition(Id, "Map", Rev, Dbd);
        Assert.Equal(ObjectState.NotFound, (await fake.ObserveAsync(Map with { Name = "Spell" }, None)).State);
        var served = await fake.ObserveAsync(Map, None);
        Assert.Equal(WoWDBDefsClient.DefinitionUrl(Rev, "Map"), served.Observation!.Url.AbsoluteUri);
        await fake.ObserveAsync(Map with { Product = "nothex!!" }, None);

        Assert.Equal(5, fake.Observed.Count);
        Assert.Equal(4, fake.Observed.Count(o => o.Product == Id));
        Assert.Single(await fake.ReadHistoryAsync(new(Product: Id, Kind: HistoryKind.Definition), None));
    }
}
