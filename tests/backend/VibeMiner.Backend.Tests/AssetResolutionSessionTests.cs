using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class AssetResolutionSessionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task FactoryPinsOriginalSourcesConfigurationsAndCompleteRootArtifactThenReopensOffline()
    {
        await using var fixture = await FactoryFixture.CreateAsync();
        AssetResolutionResult original;
        await using (var session = await fixture.Sessions.OpenAsync(fixture.Request, "enUS", Ignore))
        {
            var batch = await session.ResolveAsync(new uint[] { 1002, 1001, 1001, 999 }, Ignore);
            Assert.True(batch.ScannedRoot);
            Assert.Equal(new uint[] { 999, 1001, 1002 }, batch.Results.Select(result => result.FileDataId));
            Assert.Equal(AssetResolutionStatus.MissingMembership, batch.Results[0].Status);
            original = batch.Results[1];
            Assert.Equal(AssetResolutionStatus.Ready, original.Status);
            Assert.Equal(fixture.Data.PayloadA.ContentKey, original.Expectation!.ContentKey);
            Assert.Equal(2, original.Association.Memberships.Count);
            Assert.Equal(new uint[] { 1, 2 }, original.Association.Memberships.Select(match => match.Entry.LocaleFlags));
            Assert.Equal(AssetResolutionSession.VariantPolicy, original.Association.VariantPolicy);
            Assert.Equal(fixture.Request, original.Association.Request);
            Assert.Equal("enUS", original.Association.ResearchLocale);
            Assert.Equal(2, original.Association.Configurations.Count);
            Assert.Equal(original.Association.SourceReference, session.SourceContext.SourceReference);
            Assert.Equal("root-v1", original.Association.RootSource.ParserRevision);
            Assert.NotNull(await fixture.Catalog.FindResolverArtifactAsync(original.Association.RootSource.Sha256,
                "root", AssetResolutionSessions.RootArtifactRevision, default));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Path, "staging-v1")));
            var reused = await session.ResolveAsync(new uint[] { 1001, 999 }, Ignore);
            Assert.False(reused.ScannedRoot);
        }
        var requests = fixture.Handler.Requests.Count; fixture.Handler.Offline = true;
        await using var offline = await fixture.Sessions.OpenAsync(fixture.Request, "enUS", Ignore);
        var result = Assert.Single((await offline.ResolveAsync(new uint[] { 1001 }, Ignore)).Results);
        Assert.Equal(original.Expectation, result.Expectation);
        Assert.Equal(original.Association.RootSource.RetrievedAt, result.Association.RootSource.RetrievedAt);
        Assert.Equal(original.Association.SourceReference, result.Association.SourceReference);
        Assert.Equal(requests, fixture.Handler.Requests.Count);
    }

    [Fact]
    public async Task FactoryRejectsUnsupportedLocaleButIdleFeatureHandlesShareTheirSource()
    {
        await using var fixture = await FactoryFixture.CreateAsync();
        var requests = fixture.Handler.Requests.Count;
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Sessions.OpenAsync(fixture.Request, "frFR", Ignore));
        Assert.Equal("ASSET_LOCALE_UNSUPPORTED", error.Code);
        Assert.Equal(requests, fixture.Handler.Requests.Count);
        await using var session = await fixture.Sessions.OpenAsync(fixture.Request, "enUS", Ignore);
        await using var second = await fixture.Sessions.OpenAsync(fixture.Request, "enUS", Ignore);
        Assert.NotSame(session, second);
        Assert.Equal(session.SourceContext, second.SourceContext);
        Assert.Equal(AssetResolutionStatus.Ready, Assert.Single((await second.ResolveAsync(new uint[] { 1001 }, Ignore)).Results).Status);
    }

    [Fact]
    public async Task SavedRootArtifactCannotSubstituteDifferentLocallyValidDecodedBytes()
    {
        await using var fixture = await FactoryFixture.CreateAsync();
        await using (var session = await fixture.Sessions.OpenAsync(fixture.Request, "enUS", Ignore))
            await session.ResolveAsync(new uint[] { 1001 }, Ignore);
        var changed = fixture.Data.Metadata["root"].Decoded.ToArray(); changed[^1] ^= 1;
        string path;
        await using (var staged = await fixture.Objects.CreateStagingFileAsync(default))
        { path = staged.Name; await staged.WriteAsync(changed); }
        var wrongSha = await fixture.Objects.PublishFileAsync(path, changed.Length, default);
        await using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(fixture.Path, "discovery-v1.sqlite3"), Pooling = false }.ToString()))
        {
            await db.OpenAsync(); await using var command = db.CreateCommand();
            command.CommandText = "UPDATE resolver_artifacts SET decoded_sha256=$sha WHERE role='root'";
            command.Parameters.AddWithValue("$sha", wrongSha); Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Sessions.OpenAsync(fixture.Request, "enUS", Ignore));
        Assert.Equal("RESOLVER_SOURCE_INTEGRITY", error.Code);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Path, "staging-v1")));
    }

    [Fact]
    public async Task DifferentExactBuildCannotReuseTheCurrentSessionsSourceOrCache()
    {
        await using var fixture = await FactoryFixture.CreateAsync();
        await using (var session = await fixture.Sessions.OpenAsync(fixture.Request, "enUS", Ignore))
            await session.ResolveAsync(new uint[] { 999 }, Ignore);
        var requests = fixture.Handler.Requests.Count;
        await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Sessions.OpenAsync(
            fixture.Request with { BuildIdentity = new string('f', 64) }, "enUS", Ignore));
        Assert.Equal(requests, fixture.Handler.Requests.Count);
    }

    [Fact]
    public async Task FactoryShutdownAwaitsOpeningCleanupAndRejectsFurtherAdmission()
    {
        await using var fixture = await FactoryFixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var opening = fixture.Sessions.OpenAsync(fixture.Request, "enUS", async _ => { entered.TrySetResult(); await release.Task; });
        await entered.Task.WaitAsync(Deadline);
        var disposal = fixture.Sessions.DisposeAsync().AsTask(); Assert.False(disposal.IsCompleted);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening.WaitAsync(Deadline));
        await disposal.WaitAsync(Deadline);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Sessions.OpenAsync(fixture.Request, "enUS", Ignore));
    }

    [Fact]
    public async Task AllVariantPolicyPreservesAmbiguityMissingAndEncodingUnresolvedSeparately()
    {
        var data = InventoryFixture.Create();
        await using var fixture = await SessionFixture.CreateAsync(
            (1U, data.PayloadA.ContentKey, 0U), (1U, data.PayloadB.ContentKey, 2U),
            (2U, new string('f', 32), 4U), (3U, data.PayloadA.ContentKey, 0U), (3U, data.PayloadA.ContentKey, 4U));
        var batch = await fixture.Session.ResolveAsync(new uint[] { 1, 2, 3, 4 }, Ignore);
        Assert.Equal(new[] { AssetResolutionStatus.AmbiguousVariants, AssetResolutionStatus.EncodingUnresolved,
            AssetResolutionStatus.Ready, AssetResolutionStatus.MissingMembership }, batch.Results.Select(result => result.Status));
        Assert.Equal(2, batch.Results[0].Association.Memberships.Count);
        Assert.Null(batch.Results[0].Expectation);
        Assert.Equal(new uint[] { 0, 4 }, batch.Results[2].Association.Memberships.Select(match => match.Entry.LocaleFlags));
    }

    // D095: the session status stays ambiguous for every consumer; only a HighResTexture-only split carries the standard choice.
    [Fact]
    public async Task HighResTextureOnlySplitStaysAmbiguousAndCarriesTheStandardChoiceWithItsEncoding()
    {
        var data = InventoryFixture.Create();
        const uint locale = 0x173F6, flags = 0x2080000;
        var (standard, high) = (data.PayloadA.ContentKey, data.PayloadB.ContentKey);
        await using var fixture = await SessionFixture.CreateWithContentAsync(
            (1U, high, locale, flags | 1), (1U, standard, locale, flags), (1U, high, locale, flags | 0x81), (1U, standard, locale, flags | 0x80),
            (2U, high, locale, flags | 1), (2U, standard, 0x2U, flags),
            (3U, high, locale, flags | 1), (3U, new string('f', 32), locale, flags));
        var batch = await fixture.Session.ResolveAsync(new uint[] { 1, 2, 3 }, Ignore);
        Assert.All(batch.Results, r => { Assert.Equal(AssetResolutionStatus.AmbiguousVariants, r.Status); Assert.Null(r.Expectation); });
        var variant = batch.Results[0].TextureVariant!;
        const uint stored = flags | RootInventoryReader.NoNames;
        Assert.Equal((standard, stored, high, stored | 1), (variant.Choice.ContentKey, variant.Choice.ContentFlags, variant.Choice.HighResolutionContentKey, variant.Choice.HighResolutionContentFlags));
        Assert.Equal(new[] { stored, stored | 0x80 }, variant.Choice.Selected.Select(m => m.Entry.ContentFlags));
        Assert.Equal(4, batch.Results[0].Association.Memberships.Count);
        Assert.Equal((data.PayloadA.EncodingKey, data.PayloadA.Decoded.LongLength), (variant.Expectation!.EncodingKey, variant.Expectation.DecodedBytes));
        Assert.Null(batch.Results[1].TextureVariant);
        Assert.Null(Assert.IsType<AssetTextureVariant>(batch.Results[2].TextureVariant).Expectation);
        var cached = await fixture.Session.ResolveAsync(new uint[] { 1 }, Ignore);
        Assert.False(cached.ScannedRoot); Assert.Equal(variant, cached.Results[0].TextureVariant);
    }

    [Fact]
    public async Task OneRootPassAndDeduplicatedNativeEncodingPagesServeManyIdsAndRetainedMappings()
    {
        var key = InventoryFixture.Create().PayloadA.ContentKey;
        await using var fixture = await SessionFixture.CreateAsync((1U, key, 1U), (2U, key, 2U), (3U, key, 0U));
        var first = await fixture.Session.ResolveAsync(new uint[] { 1, 2, 99 }, Ignore);
        Assert.Equal(fixture.Root.Length, fixture.Root.BytesRead);
        Assert.Equal(2, fixture.Encoding.ReadCalls); // One CKey page and one EKey page, shared by both IDs.
        Assert.True(first.ScannedRoot);
        var bytes = fixture.Root.BytesRead;
        Assert.False((await fixture.Session.ResolveAsync(new uint[] { 99, 2, 1 }, Ignore)).ScannedRoot);
        Assert.Equal(bytes, fixture.Root.BytesRead);
        await fixture.Session.ResolveAsync(new uint[] { 3 }, Ignore);
        Assert.Equal(2, fixture.Encoding.ReadCalls);
        Assert.Equal(bytes * 2, fixture.Root.BytesRead);
    }

    [Fact]
    public async Task LateEofFailureCannotCacheProvisionalAbsenceOrSuccess()
    {
        var key = InventoryFixture.Create().PayloadA.ContentKey;
        await using var fixture = await SessionFixture.CreateAsync((1U, key, 2U));
        fixture.Root.ThrowOnEof = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Session.ResolveAsync(new uint[] { 1, 2 }, Ignore));
        fixture.Root.ThrowOnEof = false;
        // Observer dispatch is now outside traversal; deterministic pre-commit cancellation is
        // covered by SharedAssetContextsTests' gated EOF, rather than observer timing.
        var retried = await fixture.Session.ResolveAsync(new uint[] { 1, 2 }, Ignore);
        Assert.True(retried.ScannedRoot);
        Assert.Equal(new[] { AssetResolutionStatus.Ready, AssetResolutionStatus.MissingMembership }, retried.Results.Select(result => result.Status));
    }

    [Fact]
    public async Task LateEncodingCorruptionFailsWholeBatchWithoutOptionalUnresolvedOrPartialCache()
    {
        var data = InventoryFixture.Create();
        await using var fixture = await SessionFixture.CreateAsync((1U, data.PayloadA.ContentKey, 2U), (2U, data.PayloadB.ContentKey, 2U));
        fixture.Encoding.CorruptAtReadCall = 3;
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Session.ResolveAsync(new uint[] { 1, 2 }, Ignore));
        Assert.Equal("EN_INTEGRITY", error.Code);
        fixture.Encoding.CorruptAtReadCall = 0;
        var retry = await fixture.Session.ResolveAsync(new uint[] { 1, 2 }, Ignore);
        Assert.True(retry.ScannedRoot);
        Assert.All(retry.Results, result => Assert.Equal(AssetResolutionStatus.Ready, result.Status));
    }

    [Fact]
    public async Task BoundedIdCacheEvictsOldestAndNeverTurnsEvictionIntoAbsence()
    {
        await using var fixture = await SessionFixture.CreateAsync();
        for (var batch = 0; batch < 8; batch++)
            await fixture.Session.ResolveAsync(Enumerable.Range(batch * 128 + 1, 128).Select(id => (uint)id).ToArray(), Ignore);
        Assert.False((await fixture.Session.ResolveAsync(new uint[] { 1 }, Ignore)).ScannedRoot);
        await fixture.Session.ResolveAsync(new uint[] { 1025 }, Ignore);
        Assert.False((await fixture.Session.ResolveAsync(new uint[] { 1 }, Ignore)).ScannedRoot);
        Assert.True((await fixture.Session.ResolveAsync(new uint[] { 2 }, Ignore)).ScannedRoot);
    }

    [Fact]
    public async Task CacheMembershipAndChargedMemoryBoundsEvictRatherThanRetainUnboundedProofs()
    {
        var key = InventoryFixture.Create().PayloadA.ContentKey;
        var memberships = Enumerable.Range(1, 9).SelectMany(id => Enumerable.Repeat(((uint)id, key, 0U), 4096)).ToArray();
        await using var fixture = await SessionFixture.CreateAsync(memberships);
        var first = await fixture.Session.ResolveAsync(Enumerable.Range(1, 8).Select(id => (uint)id).ToArray(), Ignore);
        Assert.Equal(32_768, first.Results.Sum(result => result.Association.Memberships.Count));
        Assert.True((await fixture.Session.ResolveAsync(new uint[] { 1 }, Ignore)).ScannedRoot);
        await fixture.Session.ResolveAsync(new uint[] { 9 }, Ignore);
        Assert.False((await fixture.Session.ResolveAsync(new uint[] { 9 }, Ignore)).ScannedRoot);
    }

    [Fact]
    public async Task InvalidInputsFailBeforeReadsAndConcurrentBatchesReceiveTheirOwnSubset()
    {
        await using var fixture = await SessionFixture.CreateAsync();
        foreach (var ids in new[] { new uint[] { 0 }, new uint[129] })
        {
            await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Session.ResolveAsync(ids, Ignore));
            Assert.Equal(0, fixture.Root.BytesRead);
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolving = fixture.Session.ResolveAsync(new uint[] { 1 }, async _ => { entered.TrySetResult(); await release.Task; });
        await entered.Task.WaitAsync(Deadline);
        var other = fixture.Session.ResolveAsync(new uint[] { 2 }, Ignore);
        Assert.Equal(2U, Assert.Single((await other.WaitAsync(Deadline)).Results).FileDataId);
        Assert.False(resolving.IsCompleted); // A blocked observer retains only its own subscription.
        release.SetResult(); await resolving.WaitAsync(Deadline);
    }

    [Fact]
    public async Task DisposalCancelsAndAwaitsActiveObserverBeforeClosingBothLeases()
    {
        await using var fixture = await SessionFixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolving = fixture.Session.ResolveAsync(new uint[] { 1 }, async _ => { entered.TrySetResult(); await release.Task; });
        await entered.Task.WaitAsync(Deadline);
        var disposal = fixture.Session.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        Assert.True(fixture.Root.CanRead); Assert.True(fixture.Encoding.CanRead);
        release.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolving.WaitAsync(Deadline));
        await disposal.WaitAsync(Deadline);
        Assert.False(fixture.Root.CanRead); Assert.False(fixture.Encoding.CanRead);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Session.ResolveAsync(new uint[] { 1 }, Ignore));
    }

    [Fact]
    public async Task IndependentSessionsNeverShareNegativeResultsOrVariantPolicyState()
    {
        var key = InventoryFixture.Create().PayloadA.ContentKey;
        await using var absent = await SessionFixture.CreateAsync();
        await using var present = await SessionFixture.CreateAsync((1U, key, 0U));
        Assert.Equal(AssetResolutionStatus.MissingMembership, Assert.Single((await absent.Session.ResolveAsync(new uint[] { 1 }, Ignore)).Results).Status);
        Assert.Equal(AssetResolutionStatus.Ready, Assert.Single((await present.Session.ResolveAsync(new uint[] { 1 }, Ignore)).Results).Status);
    }

    private static Task Ignore(string _) => Task.CompletedTask;

    // Direct source-session fixture supplies explicit authored immutable proof inputs. Factory tests
    // above separately establish real configuration/CAS/catalog/shared-acquisition composition.
    private sealed class SessionFixture : IAsyncDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), "vibeminer-asset-session-" + Guid.NewGuid().ToString("N"));
        internal CountingInput Root = null!, Encoding = null!;
        internal AssetResolutionSession Session = null!;
        internal static Task<SessionFixture> CreateAsync(params (uint Id, string CKey, uint Locale)[] memberships) =>
            CreateWithContentAsync(memberships.Select(m => (m.Id, m.CKey, m.Locale, 0U)).ToArray());
        internal static async Task<SessionFixture> CreateWithContentAsync(params (uint Id, string CKey, uint Locale, uint Content)[] memberships)
        {
            var fixture = new SessionFixture(); Directory.CreateDirectory(fixture.path);
            var data = InventoryFixture.Create();
            using var bytes = new MemoryStream();
            using (var writer = new BinaryWriter(bytes, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                writer.Write("TSFM"u8); writer.Write(24U); writer.Write(2U); writer.Write((uint)memberships.Length); writer.Write(0U); writer.Write(0U);
                foreach (var (id, key, locale, content) in memberships)
                { writer.Write(1U); writer.Write(locale); writer.Write(RootInventoryReader.NoNames | content); writer.Write(0U); writer.Write((byte)0); writer.Write(id); writer.Write(Convert.FromHexString(key)); }
            }
            var rootBytes = bytes.ToArray();
            await File.WriteAllBytesAsync(Path.Combine(fixture.path, "root"), rootBytes);
            await File.WriteAllBytesAsync(Path.Combine(fixture.path, "encoding"), data.Metadata["encoding"].Decoded);
            fixture.Root = new(Path.Combine(fixture.path, "root")); fixture.Encoding = new(Path.Combine(fixture.path, "encoding"));
            var lookup = await EncodingFileLookup.OpenAsync(fixture.Encoding, new()); fixture.Encoding.ResetCounts();
            var now = DateTimeOffset.UtcNow.ToString("O");
            MetadataReceipt Receipt(InventoryFixture.FixtureObject value) => new(value.Role, value.ContentKey, value.EncodingKey,
                Convert.ToHexStringLower(SHA256.HashData(value.Encoded)), value.Encoded.Length.ToString(), value.Decoded.Length.ToString(),
                InventoryFixture.Url("data", value.EncodingKey), now, now, value.Role + "-v1");
            var enReceipt = Receipt(data.Metadata["encoding"]);
            byte[] encodedRoot = [.. "BLTE"u8, 0, 0, 0, 0, (byte)'N', .. rootBytes];
            var rootKey = Convert.ToHexStringLower(MD5.HashData(encodedRoot));
            var rootReceipt = new MetadataReceipt("root", Convert.ToHexStringLower(MD5.HashData(rootBytes)), rootKey,
                Convert.ToHexStringLower(SHA256.HashData(encodedRoot)), encodedRoot.Length.ToString(), rootBytes.Length.ToString(),
                InventoryFixture.Url("data", rootKey), now, now, "root-v1");
            var reference = new OpeningSourceReference("authored", "us", new string('1', 64), 0, new string('2', 64), 0);
            var source = new EncodedSourceContext("wow", data.BuildIdentity, "us", new string('3', 64), reference);
            fixture.Session = new(new(enReceipt, lookup, fixture.Encoding, () => { }), fixture.Root,
                new(new("wow", data.BuildIdentity, "us"), "enUS", reference, [], enReceipt, rootReceipt,
                    AssetResolutionSession.VariantPolicy, []), source, new(), default, () => { });
            return fixture;
        }
        public async ValueTask DisposeAsync() { await Session.DisposeAsync(); Directory.Delete(path, true); }
    }

    private sealed class CountingInput(string path) : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        65_536, FileOptions.Asynchronous | FileOptions.SequentialScan)
    {
        internal long BytesRead;
        internal int ReadCalls;
        internal bool ThrowOnEof;
        internal int CorruptAtReadCall;
        internal void ResetCounts() { BytesRead = 0; ReadCalls = 0; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (ThrowOnEof && Position == Length) throw new IOException("Authored late EOF read failure.");
            var read = await base.ReadAsync(buffer, token); ReadCalls++; BytesRead += read;
            if (read > 0 && ReadCalls == CorruptAtReadCall) buffer.Span[0] ^= 1;
            return read;
        }
    }

    private sealed class Handler(InventoryFixture data) : HttpMessageHandler
    {
        internal readonly List<string> Requests = [];
        internal bool Offline;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Offline) throw new InvalidOperationException("Authored offline fixture forbids HTTP.");
            var url = request.RequestUri!.AbsoluteUri; lock (Requests) Requests.Add(url);
            if (!data.Responses.TryGetValue(url, out var bytes)) throw new InvalidOperationException("Unexpected authored request.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) });
        }
    }

    private sealed class FactoryFixture : IAsyncDisposable
    {
        internal readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vibeminer-asset-factory-" + Guid.NewGuid().ToString("N"));
        internal InventoryFixture Data = InventoryFixture.Create();
        internal DiscoveryCatalog Catalog = null!;
        internal VerifiedObjectStore Objects = null!;
        internal Handler Handler = null!;
        internal HttpClient Client = null!;
        internal EncodedSourcePlans Plans = null!;
        internal AcquisitionCoordinator Shared = null!;
        internal AssetResolutionSessions Sessions = null!;
        internal WorkspaceRequest Request = null!;
        internal static async Task<FactoryFixture> CreateAsync()
        {
            var fixture = new FactoryFixture();
            fixture.Catalog = await DiscoveryCatalog.OpenAsync(fixture.Path); fixture.Objects = new(fixture.Path);
            fixture.Handler = new(fixture.Data); fixture.Client = new(fixture.Handler);
            var generation = await fixture.Catalog.BeginRefreshAsync("wow", default);
            await fixture.Catalog.CommitAsync("wow", generation, fixture.Data.Discovery, DateTimeOffset.UtcNow.ToString("O"), default);
            fixture.Request = new("wow", fixture.Data.BuildIdentity, "us");
            var resources = new AcquisitionResources(); fixture.Plans = new(fixture.Catalog, fixture.Objects, resources);
            fixture.Shared = new(new OfficialEncodedObjectProducer(fixture.Catalog, fixture.Objects, fixture.Client, resources, fixture.Plans));
            fixture.Sessions = new(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Shared, resources);
            return fixture;
        }
        public async ValueTask DisposeAsync()
        {
            await Sessions.DisposeAsync(); await Shared.DisposeAsync(); await Plans.DisposeAsync();
            Client.Dispose(); await Objects.DisposeAsync(); await Catalog.DisposeAsync(); Directory.Delete(Path, true);
        }
    }
}
