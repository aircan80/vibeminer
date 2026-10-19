using System.Net;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class SharedAssetSourceProofTests
{
    private static Task Ignore(string _) => Task.CompletedTask;

    [Fact]
    public async Task TwoRealPreparedContextsKeepOriginalReceiptsAndPinnedReferencesAcrossRefresh()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var original = await fixture.Sessions.OpenAsync(fixture.Request, "enUS", Ignore);
        var first = Assert.Single((await original.ResolveAsync(new uint[] { 1001 }, Ignore)).Results);
        await fixture.RefreshAsync();
        await using var refreshed = await fixture.Sessions.OpenAsync(fixture.Request, "enUS", Ignore);
        var second = Assert.Single((await refreshed.ResolveAsync(new uint[] { 1001 }, Ignore)).Results);
        Assert.NotEqual(original.SourceContext.SourceReference, refreshed.SourceContext.SourceReference);
        Assert.Equal(first.Expectation, second.Expectation);
        Assert.Equal(first.Association.EncodingSource, second.Association.EncodingSource);
        Assert.Equal(first.Association.RootSource, second.Association.RootSource);
        Assert.Equal(first.Association.SourceReference, original.SourceContext.SourceReference);
        Assert.False((await original.ResolveAsync(new uint[] { 1001 }, Ignore)).ScannedRoot);
        await fixture.RefreshAsync();
        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Sessions.OpenAsync(fixture.Request, "enUS", Ignore));
        Assert.Equal("ASSET_CONTEXT_CAPACITY", failure.Code);
        await original.DisposeAsync();
        await using var newest = await fixture.Sessions.OpenAsync(fixture.Request, "enUS", Ignore);
        Assert.NotEqual(refreshed.SourceContext.SourceReference, newest.SourceContext.SourceReference);
        Assert.Equal(4, fixture.Handler.Requests); // Both configs, EN and ROOT: one physical acquisition each.
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Path, "staging-v1")));
    }

    [Fact]
    public async Task PreparationFenceRejectsRefreshedReferenceOrConfigurationEvenWhenBuildAndVersionMatch()
    {
        await using var fixture = await Fixture.CreateAsync();
        var captured = await fixture.Catalog.ResolveOpeningAsync(fixture.Request, default);
        var key = new AssetSourceContextKey(fixture.Request, "enUS", captured.Reference, captured.Build.BuildConfig, captured.Build.CdnConfig);
        key.RequirePrepared(captured);
        await fixture.RefreshAsync();
        var changed = await fixture.Catalog.ResolveOpeningAsync(fixture.Request, default);
        Assert.Equal(captured.Build.Id, changed.Build.Id); Assert.Equal(captured.Build.Version, changed.Build.Version);
        Assert.Equal("ASSET_SOURCE_CHANGED", Assert.Throws<DiscoveryException>(() => key.RequirePrepared(changed)).Code);
        Assert.Equal("ASSET_SOURCE_CHANGED", Assert.Throws<DiscoveryException>(() => key.RequirePrepared(captured with
            { Build = captured.Build with { CdnConfig = new string('f', 32) } })).Code);
        Assert.Equal("ASSET_SOURCE_CHANGED", Assert.Throws<DiscoveryException>(() => key.RequirePrepared(captured with
            { Reference = captured.Reference with { VersionsRowOrdinal = captured.Reference.VersionsRowOrdinal + 1 } })).Code);
    }

    private sealed class Handler(InventoryFixture data) : HttpMessageHandler
    {
        internal int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Interlocked.Increment(ref Requests);
            if (!data.Responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var bytes)) throw new IOException("Unexpected authored HTTP request.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) });
        }
    }
    private sealed class Fixture : IAsyncDisposable
    {
        internal readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vibeminer-shared-source-proof-" + Guid.NewGuid().ToString("N"));
        internal readonly InventoryFixture Data = InventoryFixture.Create();
        internal DiscoveryCatalog Catalog = null!;
        internal VerifiedObjectStore Objects = null!;
        internal Handler Handler = null!;
        private HttpClient client = null!;
        private EncodedSourcePlans plans = null!;
        private AcquisitionCoordinator shared = null!;
        internal AssetResolutionSessions Sessions = null!;
        internal WorkspaceRequest Request = null!;
        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture();
            fixture.Catalog = await DiscoveryCatalog.OpenAsync(fixture.Path); fixture.Objects = new(fixture.Path);
            fixture.Handler = new(fixture.Data); fixture.client = new(fixture.Handler);
            fixture.Request = new("wow", fixture.Data.BuildIdentity, "us"); await fixture.RefreshAsync();
            var resources = new AcquisitionResources(); fixture.plans = new(fixture.Catalog, fixture.Objects, resources);
            fixture.shared = new(new OfficialEncodedObjectProducer(fixture.Catalog, fixture.Objects, fixture.client, resources, fixture.plans));
            fixture.Sessions = new(fixture.Catalog, fixture.Objects, fixture.client, fixture.shared, resources); return fixture;
        }
        internal async Task RefreshAsync()
        {
            var generation = await Catalog.BeginRefreshAsync("wow", default);
            await Catalog.CommitAsync("wow", generation, Data.Discovery, DateTimeOffset.UtcNow.ToString("O"), default);
        }
        public async ValueTask DisposeAsync()
        {
            await Sessions.DisposeAsync(); await shared.DisposeAsync(); await plans.DisposeAsync();
            client.Dispose(); await Objects.DisposeAsync(); await Catalog.DisposeAsync(); Directory.Delete(Path, true);
        }
    }
}
