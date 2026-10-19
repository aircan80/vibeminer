using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Backend.Tests.Protocols;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

[Collection("Bulk archive locator")]
public sealed class BulkArchiveLocatorTests
{
    private static readonly Uri Route = new("https://us.cdn.blizzard.com/tpr/wow/");
    private static readonly string CdnSha = new('a', 64);
    private static Task Progress(string _) => Task.CompletedTask;

    [Fact]
    public async Task MergesOncePerIndexPreservesOverlapsAndRetainsExactProvenance()
    {
        await using var fixture = new Fixture();
        var first = AuthoredArchiveIndex.Build([(Key(2), 7u, 19u), (Key(4), 9u, 29u), (Key(7), 7u, 70u)]);
        var second = AuthoredArchiveIndex.Build([(Key(4), 9u, uint.MaxValue), (Key(6), 11u, 39u)]);
        fixture.Handler.Add(first); fixture.Handler.Add(second);
        var ledger = await fixture.Ledger([(1, 5), (2, 7), (4, 9), (6, 11), (8, 13)]);
        var keys = new[] { first.Identity, second.Identity };
        var map = await fixture.Locator.BuildAsync(Route, CdnSha, keys, ledger, Progress, default);
        Assert.Equal(4, map.LocationCount); Assert.Equal(3, map.LocatedMembers);
        Assert.Equal(32 + 4 * 32, map.MapBytes);
        Assert.Equal(4, fixture.Handler.Requests); // One footer + one whole-index range per source.
        Assert.Equal(first.Bytes.Length + second.Bytes.Length + 56, fixture.Handler.WireBytes);
        await using var session = await fixture.Locator.OpenAsync(map, CdnSha, keys, ledger, default);
        var locations = await session.FindAsync(Key(4), 9, default);
        Assert.Collection(locations, row => { Assert.Equal(first.Identity, row.ArchiveKey); Assert.Equal(29, row.Offset); },
            row => { Assert.Equal(second.Identity, row.ArchiveKey); Assert.Equal((long)uint.MaxValue, row.Offset); });
        Assert.Empty(await session.FindAsync(Key(8), 13, default));
        Assert.Empty(await session.FindAsync(Key(7), 7, default)); // Native key outside the exact DL is excluded.
        Assert.Equal("CONTENT_SIZE_CONFLICT", (await Assert.ThrowsAsync<DiscoveryException>(() => session.FindAsync(Key(4), 8, default))).Code);
        await using var manifestInput = await fixture.Objects.OpenVerifiedAsync(map.ManifestSha256, map.ManifestBytes, default);
        var manifest = (await JsonSerializer.DeserializeAsync<BulkArchiveLocatorManifest>(manifestInput))!;
        Assert.Equal(CdnSha, manifest.CdnConfigurationSha256); Assert.Equal(ledger, manifest.Ledger);
        Assert.Equal(DownloadLedger.Revision, manifest.LedgerRevision);
        Assert.Equal(keys, manifest.Sources.Select(source => source.ArchiveKey));
        foreach (var source in manifest.Sources)
        {
            Assert.Equal(CdnSha, source.CdnConfigurationSha256);
            Assert.Equal(ArchivedContentLocator.Url(Route, source.ArchiveKey, true).AbsoluteUri, source.SourceUrl);
            Assert.Equal(BulkArchiveLocator.NativeRevision, source.ParserRevision);
            Assert.NotEqual(default, source.RetrievedAt);
            await using var native = await fixture.Objects.OpenVerifiedAsync(source.Sha256, source.ByteLength, default);
            Assert.Equal(source.ByteLength, native.Length);
        }
        Assert.Equal(4, fixture.Handler.Requests); // Queries and source verification are entirely local.
        Assert.Empty(fixture.StagingFiles);
    }

    [Fact]
    public async Task MultipleNativePagesUseBoundedWholeIndexRangesRatherThanOneHttpRequestPerPage()
    {
        await using var fixture = new Fixture();
        var source = AuthoredArchiveIndex.Build(Enumerable.Range(1, 171).Select(i => (Key((uint)i), 7u, (uint)i)).ToArray());
        fixture.Handler.Add(source); var ledger = await fixture.Ledger([(1, 7), (170, 7), (171, 7)]);
        var map = await fixture.Locator.BuildAsync(Route, CdnSha, [source.Identity], ledger, Progress, default);
        Assert.Equal(2, fixture.Handler.Requests); Assert.Equal(3, map.LocationCount);
        await using var session = await fixture.Locator.OpenAsync(map, CdnSha, [source.Identity], ledger, default);
        Assert.Single(await session.FindAsync(Key(171), 7, default));
    }

    [Fact]
    public async Task NativeIndexLargerThanRangeAllocationIsFetchedInFourMiBChunks()
    {
        await using var fixture = new Fixture(); const uint last = 174081;
        var source = AuthoredArchiveIndex.Build(Enumerable.Range(1, (int)last).Select(i => (Key((uint)i), 7u, (uint)i)).ToArray());
        Assert.True(source.Bytes.Length > 4 << 20); fixture.Handler.Add(source);
        var ledger = await fixture.Ledger([(1, 7), (last, 7)]);
        var map = await fixture.Locator.BuildAsync(Route, CdnSha, [source.Identity], ledger, Progress, default);
        Assert.Equal(3, fixture.Handler.Requests); Assert.Equal(source.Bytes.Length + 28, fixture.Handler.WireBytes);
        Assert.Equal(2, map.LocationCount); Assert.Empty(fixture.StagingFiles);
    }

    [Fact]
    public async Task BadFinalNativePagePreventsAnyDerivedReadyArtifactAndRemovesStaging()
    {
        await using var fixture = new Fixture();
        var source = AuthoredArchiveIndex.Build(Enumerable.Range(1, 171).Select(i => (Key((uint)i), 7u, (uint)i)).ToArray());
        source.Bytes[4096 + 100] ^= 1; fixture.Handler.Add(source);
        var ledger = await fixture.Ledger([(1, 7)]);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Locator.BuildAsync(Route, CdnSha,
            [source.Identity], ledger, Progress, default));
        Assert.Equal("ARCHIVE_INDEX_INTEGRITY", error.Code); Assert.Empty(fixture.StagingFiles);
        Assert.Single(fixture.ObjectFiles); // Only the preexisting DL ledger, no native/map publication.
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConflictingNativeOrDownloadLengthsFailRatherThanChooseOneClaim(bool conflictOutsideLedger)
    {
        await using var fixture = new Fixture();
        var first = AuthoredArchiveIndex.Build([(Key(2), 7u, 9u)]); fixture.Handler.Add(first);
        var sources = new List<string> { first.Identity };
        if (conflictOutsideLedger)
        {
            var other = AuthoredArchiveIndex.Build([(Key(2), 8u, 10u)]); fixture.Handler.Add(other); sources.Add(other.Identity);
        }
        var ledger = await fixture.Ledger(conflictOutsideLedger ? [(1, 7)] : [(2, 8)]);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Locator.BuildAsync(Route, CdnSha,
            sources, ledger, Progress, default));
        Assert.Equal("CONTENT_SIZE_CONFLICT", error.Code); Assert.Empty(fixture.StagingFiles);
    }

    [Fact]
    public async Task MissingArchiveRemainsUnresolvedWhileAlreadyVerifiedSourcesSurvive()
    {
        await using var fixture = new Fixture();
        var source = AuthoredArchiveIndex.Build([(Key(1), 7u, 9u)]); fixture.Handler.Add(source);
        var ledger = await fixture.Ledger([(1, 7)]);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Locator.BuildAsync(Route, CdnSha,
            [source.Identity, new string('b', 32)], ledger, Progress, default));
        Assert.Equal("CONTENT_SOURCE_MISSING", error.Code); Assert.Empty(fixture.StagingFiles);
        Assert.Equal(2, fixture.ObjectFiles.Count()); // DL + first fully authenticated native source.
    }

    [Fact]
    public async Task OverlappingOutputAndNativeMetadataHaveIndependentExplicitBounds()
    {
        await using var fixture = new Fixture();
        var first = AuthoredArchiveIndex.Build([(Key(1), 7u, 9u)]);
        var other = AuthoredArchiveIndex.Build([(Key(1), 7u, 10u)]);
        fixture.Handler.Add(first); fixture.Handler.Add(other); var ledger = await fixture.Ledger([(1, 7)]);
        var bounded = fixture.CreateLocator(new(MaxLocations: 1));
        var error = await Assert.ThrowsAsync<DiscoveryException>(() => bounded.BuildAsync(Route, CdnSha,
            [first.Identity, other.Identity], ledger, Progress, default));
        Assert.Equal("BULK_LOCATOR_LIMIT", error.Code); Assert.Empty(fixture.StagingFiles);
        bounded = fixture.CreateLocator(new(MaxNativeIndexBytes: 100));
        var before = fixture.Handler.Requests;
        error = await Assert.ThrowsAsync<DiscoveryException>(() => bounded.BuildAsync(Route, CdnSha,
            [first.Identity], ledger, Progress, default));
        Assert.Equal("BULK_LOCATOR_LIMIT", error.Code); Assert.Equal(before + 1, fixture.Handler.Requests);
    }

    [Fact]
    public async Task CancellationDuringTransferCleansPrivateStagingAndAllowsRetry()
    {
        await using var fixture = new Fixture();
        var source = AuthoredArchiveIndex.Build([(Key(1), 7u, 9u)]); fixture.Handler.Add(source);
        var ledger = await fixture.Ledger([(1, 7)]); using var stop = new CancellationTokenSource();
        Task Report(string stage)
        { if (stage.StartsWith("Downloading archive locator metadata", StringComparison.Ordinal)) stop.Cancel(); return Task.CompletedTask; }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Locator.BuildAsync(Route, CdnSha,
            [source.Identity], ledger, Report, stop.Token));
        Assert.Empty(fixture.StagingFiles); Assert.Single(fixture.ObjectFiles);
        var map = await fixture.Locator.BuildAsync(Route, CdnSha, [source.Identity], ledger, Progress, default);
        Assert.Equal(1, map.LocationCount);
    }

    [Fact]
    public async Task ExactConfigurationLedgerAndArchiveOrderAreRequiredToOpenSealedMap()
    {
        await using var fixture = new Fixture();
        var source = AuthoredArchiveIndex.Build([(Key(1), 7u, 9u)]); fixture.Handler.Add(source);
        var ledger = await fixture.Ledger([(1, 7)]);
        var map = await fixture.Locator.BuildAsync(Route, CdnSha, [source.Identity], ledger, Progress, default);
        Assert.Equal("BULK_LOCATOR_CONTEXT", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Locator.OpenAsync(map,
            new string('c', 64), [source.Identity], ledger, default))).Code);
        var alternate = await fixture.Ledger([(2, 7)]);
        Assert.Equal("BULK_LOCATOR_CONTEXT", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Locator.OpenAsync(map,
            CdnSha, [source.Identity], alternate, default))).Code);
        Assert.Equal("BULK_LOCATOR_CONTEXT", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Locator.OpenAsync(map,
            CdnSha, [new string('d', 32)], ledger, default))).Code);
    }

    [Fact]
    public async Task ChangedPhysicalMapFailsCurrentCasProof()
    {
        await using var fixture = new Fixture();
        var source = AuthoredArchiveIndex.Build([(Key(1), 7u, 9u)]); fixture.Handler.Add(source);
        var ledger = await fixture.Ledger([(1, 7)]);
        var map = await fixture.Locator.BuildAsync(Route, CdnSha, [source.Identity], ledger, Progress, default);
        string path;
        await using (var input = await fixture.Objects.OpenVerifiedAsync(map.MapSha256, map.MapBytes, default)) path = input.Name;
        var bytes = await File.ReadAllBytesAsync(path); bytes[^1] ^= 1; await File.WriteAllBytesAsync(path, bytes);
        Assert.Equal("LOCAL_OBJECT_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Locator.OpenAsync(map,
            CdnSha, [source.Identity], ledger, default))).Code);
    }

    [Fact]
    public async Task CasHashingWaitsForCpuAdmissionBeforeReadingCorruptMap()
    {
        await using var fixture = new Fixture();
        var source = AuthoredArchiveIndex.Build([(Key(1), 7u, 9u)]); fixture.Handler.Add(source);
        var ledger = await fixture.Ledger([(1, 7)]);
        var map = await fixture.Locator.BuildAsync(Route, CdnSha, [source.Identity], ledger, Progress, default);
        string path;
        await using (var input = await fixture.Objects.OpenVerifiedAsync(map.MapSha256, map.MapBytes, default)) path = input.Name;
        var bytes = await File.ReadAllBytesAsync(path); bytes[^1] ^= 1; await File.WriteAllBytesAsync(path, bytes);
        using var first = await fixture.HoldCpu(); using var second = await fixture.HoldCpu();
        var work = new ObservedWork(); var locator = fixture.CreateLocator(context: work);
        var opening = locator.OpenAsync(map, CdnSha, [source.Identity], ledger, default);
        try
        {
            var boundary = await Task.WhenAny(opening, work.Waiting.Task).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Same(work.Waiting.Task, boundary); Assert.False(opening.IsCompleted);
        }
        finally { first.Dispose(); second.Dispose(); }
        Assert.Equal("LOCAL_OBJECT_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() => opening)).Code);
    }

    [Fact]
    public async Task BorrowedValidatedLedgerMustMatchExactRequestedArtifact()
    {
        await using var fixture = new Fixture();
        var source = AuthoredArchiveIndex.Build([(Key(1), 7u, 9u)]); fixture.Handler.Add(source);
        var ledger = await fixture.Ledger([(1, 7)]); var other = await fixture.Ledger([(2, 7)]);
        var ledgerReader = new DownloadLedger(fixture.Objects);
        await using var wrong = await ledgerReader.OpenReadSessionAsync(other, default);
        Assert.Equal("BULK_LOCATOR_CONTEXT", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Locator.BuildAsync(Route,
            CdnSha, [source.Identity], ledger, Progress, default, _ => Task.FromResult(wrong.OpenOrderedCursor())))).Code);
        await using var correct = await ledgerReader.OpenReadSessionAsync(ledger, default);
        var map = await fixture.Locator.BuildAsync(Route, CdnSha, [source.Identity], ledger, Progress, default,
            _ => Task.FromResult(correct.OpenOrderedCursor()));
        Assert.Equal(1, map.LocationCount);
    }

    [Fact]
    public async Task CompletedNativeCheckpointSurvivesCancellationAndResumesWithoutRefetchOrRestamp()
    {
        await using var fixture = new Fixture();
        var first = AuthoredArchiveIndex.Build([(Key(1), 7u, 9u)]);
        var second = AuthoredArchiveIndex.Build([(Key(2), 7u, 10u)]);
        fixture.Handler.Add(first); fixture.Handler.Add(second); var ledger = await fixture.Ledger([(1, 7), (2, 7)]);
        var saved = new Dictionary<string, BulkArchiveIndexSource>(); using var stop = new CancellationTokenSource();
        var checkpoints = new BulkArchiveLocatorCheckpoints(
            (cdn, key, _) => { Assert.Equal(CdnSha, cdn); return Task.FromResult(saved.GetValueOrDefault(key)); },
            (cdn, source, _) =>
            {
                Assert.Equal(CdnSha, cdn); Assert.Equal(cdn, source.CdnConfigurationSha256);
                saved.Add(source.ArchiveKey, source); if (saved.Count == 1) stop.Cancel(); return Task.CompletedTask;
            });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Locator.BuildAsync(Route, CdnSha,
            [first.Identity, second.Identity], ledger, Progress, stop.Token, checkpoints: checkpoints));
        var original = Assert.Single(saved).Value; Assert.Equal(2, fixture.Handler.Requests);
        var map = await fixture.Locator.BuildAsync(Route, CdnSha, [first.Identity, second.Identity], ledger,
            Progress, default, checkpoints: checkpoints);
        Assert.Equal(4, fixture.Handler.Requests); Assert.Equal(2, saved.Count); Assert.Equal(2, map.LocationCount);
        await using var manifestInput = await fixture.Objects.OpenVerifiedAsync(map.ManifestSha256, map.ManifestBytes, default);
        var manifest = (await JsonSerializer.DeserializeAsync<BulkArchiveLocatorManifest>(manifestInput))!;
        Assert.Equal(original, manifest.Sources[0]); Assert.Empty(fixture.StagingFiles);
    }

    [Fact]
    public async Task MissingCheckpointBytesAreReacquiredWhileOriginalReceiptRemainsImmutable()
    {
        await using var fixture = new Fixture();
        var source = AuthoredArchiveIndex.Build([(Key(1), 7u, 9u)]); fixture.Handler.Add(source);
        var ledger = await fixture.Ledger([(1, 7)]); BulkArchiveIndexSource? saved = null;
        var checkpoints = new BulkArchiveLocatorCheckpoints((_, _, _) => Task.FromResult(saved),
            (_, receipt, _) => { saved ??= receipt; return Task.CompletedTask; });
        await fixture.Locator.BuildAsync(Route, CdnSha, [source.Identity], ledger, Progress, default, checkpoints: checkpoints);
        var original = saved!; string path;
        await using (var input = await fixture.Objects.OpenVerifiedAsync(original.Sha256, original.ByteLength, default)) path = input.Name;
        File.Delete(path);
        var map = await fixture.Locator.BuildAsync(Route, CdnSha, [source.Identity], ledger, Progress, default, checkpoints: checkpoints);
        Assert.Equal(4, fixture.Handler.Requests); Assert.Equal(original, saved); Assert.Equal(1, map.LocationCount);
    }

    [Fact]
    public async Task CorruptCheckpointBytesCannotBecomeReadyThroughExistingCasDestination()
    {
        await using var fixture = new Fixture();
        var source = AuthoredArchiveIndex.Build([(Key(1), 7u, 9u)]); fixture.Handler.Add(source);
        var ledger = await fixture.Ledger([(1, 7)]); BulkArchiveIndexSource? saved = null; var recorded = 0;
        var checkpoints = new BulkArchiveLocatorCheckpoints((_, _, _) => Task.FromResult(saved),
            (_, receipt, _) => { saved = receipt; recorded++; return Task.CompletedTask; });
        await fixture.Locator.BuildAsync(Route, CdnSha, [source.Identity], ledger, Progress, default, checkpoints: checkpoints);
        var original = saved!; string path;
        await using (var input = await fixture.Objects.OpenVerifiedAsync(original.Sha256, original.ByteLength, default)) path = input.Name;
        var bytes = await File.ReadAllBytesAsync(path); bytes[^1] ^= 1; await File.WriteAllBytesAsync(path, bytes);
        Assert.Equal("LOCAL_OBJECT_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Locator.BuildAsync(Route,
            CdnSha, [source.Identity], ledger, Progress, default, checkpoints: checkpoints))).Code);
        Assert.Equal(4, fixture.Handler.Requests); Assert.Equal(1, recorded); Assert.Equal(original, saved);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path)); Assert.Empty(fixture.StagingFiles);
    }

    [Fact]
    public async Task NativeCheckpointAssociationFailureLeavesOrphanBytesWithoutSealingMap()
    {
        await using var fixture = new Fixture();
        var source = AuthoredArchiveIndex.Build([(Key(1), 7u, 9u)]); fixture.Handler.Add(source);
        var ledger = await fixture.Ledger([(1, 7)]);
        var checkpoints = new BulkArchiveLocatorCheckpoints((_, _, _) => Task.FromResult<BulkArchiveIndexSource?>(null),
            (_, _, _) => throw new DiscoveryException("TEST_CATALOG_FAILURE", "Authored catalog association failure."));
        Assert.Equal("TEST_CATALOG_FAILURE", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Locator.BuildAsync(Route,
            CdnSha, [source.Identity], ledger, Progress, default, checkpoints: checkpoints))).Code);
        Assert.Equal(2, fixture.ObjectFiles.Count()); Assert.Empty(fixture.StagingFiles);
    }

    [Fact]
    public async Task WrongConfigurationCheckpointCannotBeBorrowedEvenWhenNativeKeyMatches()
    {
        await using var fixture = new Fixture();
        var source = AuthoredArchiveIndex.Build([(Key(1), 7u, 9u)]); fixture.Handler.Add(source);
        var ledger = await fixture.Ledger([(1, 7)]); BulkArchiveIndexSource? saved = null;
        var checkpoints = new BulkArchiveLocatorCheckpoints((_, _, _) => Task.FromResult(saved),
            (_, receipt, _) => { saved = receipt; return Task.CompletedTask; });
        await fixture.Locator.BuildAsync(Route, CdnSha, [source.Identity], ledger, Progress, default, checkpoints: checkpoints);
        saved = saved! with { CdnConfigurationSha256 = new string('c', 64) };
        Assert.Equal("BULK_LOCATOR_CONTEXT", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Locator.BuildAsync(Route,
            CdnSha, [source.Identity], ledger, Progress, default, checkpoints: checkpoints))).Code);
        Assert.Equal(2, fixture.Handler.Requests);
    }

    [Fact]
    public async Task InvalidOrderedDlCannotSealArchiveMembershipEvenWithValidCasBytes()
    {
        await using var fixture = new Fixture();
        var source = AuthoredArchiveIndex.Build([(Key(1), 7u, 9u)]); fixture.Handler.Add(source);
        var ledger = await fixture.Ledger([(2, 7), (1, 7)]);
        Assert.Equal("DOWNLOAD_LEDGER_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Locator.BuildAsync(Route,
            CdnSha, [source.Identity], ledger, Progress, default))).Code);
        Assert.Empty(fixture.StagingFiles);
    }

    [Fact]
    public async Task MoreArchivesThanOpenFileAllowanceStillMergeWithoutRepeatedNetworkRequests()
    {
        await using var fixture = new Fixture(); var sources = new List<string>();
        for (uint i = 1; i <= 5; i++)
        {
            var source = AuthoredArchiveIndex.Build(Enumerable.Range(1, 171).Select(k => (Key((uint)k), 7u, i)).ToArray());
            fixture.Handler.Add(source); sources.Add(source.Identity);
        }
        var ledger = await fixture.Ledger([(1, 7), (171, 7)]);
        var locator = fixture.CreateLocator(new(MaximumOpenFiles: 1));
        var map = await locator.BuildAsync(Route, CdnSha, sources, ledger, Progress, default);
        Assert.Equal(10, fixture.Handler.Requests); Assert.Equal(10, map.LocationCount); Assert.Equal(2, map.LocatedMembers);
        await using var session = await locator.OpenAsync(map, CdnSha, sources, ledger, default);
        Assert.Equal(5, (await session.FindAsync(Key(171), 7, default)).Count);
    }

    private static string Key(uint value) => AuthoredArchiveIndex.Key(value);
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "vibeminer-bulk-locator-" + Guid.NewGuid().ToString("N"));
        internal readonly RangeHandler Handler = new();
        internal readonly VerifiedObjectStore Objects;
        private readonly HttpClient client;
        private readonly AcquisitionResources resources = new();
        internal readonly BulkArchiveLocator Locator;
        internal Fixture()
        {
            Objects = new(directory); client = new(Handler); Locator = CreateLocator();
        }
        internal BulkArchiveLocator CreateLocator(BulkArchiveLocatorLimits? limits = null, IAcquisitionWorkContext? context = null) =>
            new(Objects, new OfficialSourceTransport(client, resources, context), resources, context, limits);
        internal Task<IDisposable> HoldCpu() => resources.EnterCpuAsync(null, default);
        internal IEnumerable<string> StagingFiles => Directory.Exists(Path.Combine(directory, "staging-v1"))
            ? Directory.EnumerateFiles(Path.Combine(directory, "staging-v1")) : [];
        internal IEnumerable<string> ObjectFiles => Directory.Exists(Path.Combine(directory, "objects-v1"))
            ? Directory.EnumerateFiles(Path.Combine(directory, "objects-v1"), "*", SearchOption.AllDirectories).Where(path => Path.GetFileName(path) != ".owner") : [];
        internal async Task<DownloadLedgerArtifact> Ledger((uint Key, long Size)[] rows)
        {
            long total = rows.Sum(row => row.Size); var header = new byte[32]; "VMDLEDG1"u8.CopyTo(header);
            BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(8), 1); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(12), 24);
            BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(16), rows.Length); BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(24), total);
            string path;
            await using (var output = await Objects.CreateStagingFileAsync(default))
            {
                path = output.Name; await output.WriteAsync(header);
                foreach (var row in rows)
                {
                    var data = new byte[24]; Convert.FromHexString(Key(row.Key)).CopyTo(data, 0);
                    BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(16), row.Size); await output.WriteAsync(data);
                }
                await output.FlushAsync();
            }
            var length = 32 + rows.Length * 24; var sha = await Objects.PublishFileAsync(path, length, default);
            return new(sha, length, rows.Length, total);
        }
        public async ValueTask DisposeAsync()
        { client.Dispose(); await Objects.DisposeAsync(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
    private sealed class RangeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> sources = [];
        internal int Requests { get; private set; }
        internal long WireBytes { get; private set; }
        internal void Add(AuthoredArchiveIndex source) => sources.Add(source.Identity, source.Bytes);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Requests++;
            var key = Path.GetFileName(request.RequestUri!.AbsolutePath)[..^6];
            if (!sources.TryGetValue(key, out var bytes)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            var range = request.Headers.Range!.Ranges.Single();
            var from = range.From ?? bytes.Length - range.To!.Value;
            var to = range.From is null ? bytes.Length - 1 : range.To!.Value;
            var length = checked((int)(to - from + 1)); Assert.InRange(length, 1, 4 << 20);
            var content = new ByteArrayContent(bytes, checked((int)from), length);
            content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, bytes.Length);
            WireBytes += length;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content, RequestMessage = request });
        }
    }
    private sealed class ObservedWork : IAcquisitionWorkContext
    {
        internal readonly TaskCompletionSource Waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AcquisitionWorkSnapshot Snapshot => new(1, AcquisitionPriority.Background, []);
        public Task ReportAsync(AcquisitionProgress progress) => Task.CompletedTask;
        public Task WaitForChangeAsync(long observedRevision, CancellationToken token = default)
        { Waiting.TrySetResult(); return Task.Delay(Timeout.Infinite, token); }
    }
}
