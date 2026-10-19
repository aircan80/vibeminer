using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class ResolverArtifactsTests
{
    private static readonly Uri Origin = new("https://us.cdn.blizzard.com/tpr/wow/");
    private static Task Progress(string stage) => Task.CompletedTask;

    [Fact]
    public async Task FreshNativeLookupPublishesSourceBoundDecodedArtifactWithoutPerEntrySql()
    {
        await using var fixture = await Fixture.CreateAsync();
        var stages = new List<string>();
        await using var lease = await fixture.Resolver.OpenEncodingAsync(Origin, fixture.Encoding,
            stage => { stages.Add(stage); return Task.CompletedTask; });

        Assert.Equal(fixture.Data.PayloadA.Decoded.Length,
            (await lease.Lookup.FindContentAsync(fixture.Data.PayloadA.ContentKey))!.DecodedBytes);
        Assert.Equal(fixture.Data.PayloadB.Encoded.Length,
            (await lease.Lookup.FindEncodingAsync(fixture.Data.PayloadB.EncodingKey))!.EncodedBytes);
        var artifact = await fixture.Catalog.FindResolverArtifactAsync(lease.Receipt.Sha256,
            "encoding", ResolverArtifacts.EncodingRevision, default);
        Assert.NotNull(artifact);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(fixture.Data.Metadata["encoding"].Decoded)),
            artifact.DecodedSha256);
        Assert.Equal(fixture.Encoding.DecodedBytes, artifact.DecodedBytes);
        Assert.Equal(lease.Receipt.SourceUrl, artifact.Source.SourceUrl);
        Assert.Equal(lease.Receipt.RetrievedAt, artifact.Source.RetrievedAt);
        await using (var decoded = await fixture.Objects.OpenVerifiedAsync(artifact.DecodedSha256,
            artifact.DecodedBytes, default))
            Assert.Equal(fixture.Encoding.DecodedBytes, decoded.Length);
        Assert.Equal(1, fixture.Handler.Requests);
        Assert.Contains("Validating native encoding lookup", stages);
        Assert.Contains("Saving verified encoding lookup", stages);
        Assert.Contains("Verifying saved encoding lookup", stages);
        Assert.Empty(fixture.StagingFiles());
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = System.IO.Path.Combine(fixture.Path, "discovery-v1.sqlite3"), Mode = SqliteOpenMode.ReadOnly }.ToString()))
        {
            await connection.OpenAsync();
            using var rows = connection.CreateCommand();
            rows.CommandText = "SELECT (SELECT count(*) FROM en_content)+(SELECT count(*) FROM en_candidates)+(SELECT count(*) FROM en_objects)";
            Assert.Equal(0L, await rows.ExecuteScalarAsync());
        }
        // Metadata acquisition has been released before returning the independent lookup lease.
        await using var download = await fixture.Acquirer.AcquireAsync(Origin, fixture.Download, Progress, default);
        Assert.Equal("download", download.Receipt.Role);
        Assert.Equal("RESOLVER_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            fixture.Resolver.OpenEncodingAsync(Origin, fixture.Encoding, Progress))).Code);
    }

    [Fact]
    public async Task WarmLookupWorksOfflineAndPreservesOriginalReceiptsWithoutRepublishing()
    {
        await using var fixture = await Fixture.CreateAsync();
        MetadataReceipt original;
        ResolverArtifact artifact;
        await using (var first = await fixture.Resolver.OpenEncodingAsync(Origin, fixture.Encoding, Progress))
        {
            original = first.Receipt;
            artifact = (await fixture.Catalog.FindResolverArtifactAsync(original.Sha256,
                "encoding", ResolverArtifacts.EncodingRevision, default))!;
        }
        fixture.Handler.Offline = true;
        var stages = new List<string>();
        await using (var second = await fixture.Resolver.OpenEncodingAsync(new("https://eu.cdn.blizzard.com/tpr/wow/"),
            fixture.Encoding, stage => { stages.Add(stage); return Task.CompletedTask; }))
        {
            Assert.Equal(original.SourceUrl, second.Receipt.SourceUrl);
            Assert.Equal(original.RetrievedAt, second.Receipt.RetrievedAt);
            Assert.Equal(original.Sha256, second.Receipt.Sha256);
            Assert.NotNull(await second.Lookup.FindContentAsync(fixture.Data.PayloadA.ContentKey));
            Assert.Equal(artifact, await fixture.Catalog.FindResolverArtifactAsync(original.Sha256,
                "encoding", ResolverArtifacts.EncodingRevision, default));
        }
        Assert.Equal(1, fixture.Handler.Requests);
        Assert.Contains("Verifying saved encoding metadata", stages);
        Assert.Contains("Verifying saved encoding lookup", stages);
        Assert.DoesNotContain("Saving verified encoding lookup", stages);
        Assert.Empty(fixture.StagingFiles());
    }

    [Fact]
    public async Task FirstSharedPhysicalRevalidationPreservesTheEarlierCanonicalMetadataReceipt()
    {
        const string originalTime = "2026-10-17T12:00:00.0000000+00:00";
        const string physicalTime = "2026-10-19T12:00:00.0000000+00:00";
        await using var fixture = await Fixture.CreateAsync(clock: new FixedClock(DateTimeOffset.Parse(originalTime)));
        MetadataReceipt original;
        await using (var acquired = await fixture.Acquirer.AcquireAsync(Origin, fixture.Encoding, Progress, default))
        {
            original = acquired.Receipt;
            await fixture.Catalog.RecordMetadataAsync(original, default);
        }
        var physical = new EncodedReceipt(original.EncodingKey, original.Sha256, fixture.Encoding.EncodedBytes,
            original.SourceUrl.Replace("us.cdn.blizzard.com", "eu.cdn.blizzard.com", StringComparison.Ordinal),
            physicalTime, physicalTime, "blte-encoded-v1", new("loose", "0", original.EncodedBytes, null));
        Assert.NotEqual(original.SourceUrl, physical.SourceUrl);
        Assert.NotEqual(original.RetrievedAt, physical.RetrievedAt);
        var shared = new SharedPhysical(physical);
        var resolver = new ResolverArtifacts(fixture.Catalog, fixture.Objects, fixture.SharedAcquirer(shared));
        var source = new EncodedSourceContext("wow", fixture.Data.BuildIdentity, "us", new string('a', 64));
        fixture.Handler.Offline = true;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            await using var lease = await resolver.OpenEncodingAsync(Origin, fixture.Encoding, Progress, source: source);
            Assert.Equal(original, lease.Receipt);
            Assert.NotNull(await lease.Lookup.FindContentAsync(fixture.Data.PayloadA.ContentKey));
            var artifact = await fixture.Catalog.FindResolverArtifactAsync(original.Sha256, "encoding", ResolverArtifacts.EncodingRevision, default);
            Assert.NotNull(artifact); Assert.Equal(original, artifact.Source);
            Assert.Equal(original, await fixture.Catalog.FindMetadataAsync(original.EncodingKey, default));
        }
        Assert.Equal(2, shared.Calls);
        Assert.Equal(1, fixture.Handler.Requests);
        Assert.Empty(fixture.StagingFiles());
        // Returning original history must not turn the canonical receipt into a replay shortcut.
        var encodedPath = fixture.ObjectPath(original.Sha256);
        var corrupt = await File.ReadAllBytesAsync(encodedPath); corrupt[^1] ^= 1;
        await File.WriteAllBytesAsync(encodedPath, corrupt);
        Assert.Equal("LOCAL_OBJECT_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            resolver.OpenEncodingAsync(Origin, fixture.Encoding, Progress, source: source))).Code);
        Assert.Empty(fixture.StagingFiles());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CanonicalLeaseCannotAcceptAnArtifactWithForgedOriginalSourceHistory(bool changeTime)
    {
        await using var fixture = await Fixture.CreateAsync();
        ResolverArtifact artifact;
        await using (var first = await fixture.Resolver.OpenEncodingAsync(Origin, fixture.Encoding, Progress))
            artifact = (await fixture.Catalog.FindResolverArtifactAsync(first.Receipt.Sha256,
                "encoding", ResolverArtifacts.EncodingRevision, default))!;
        var forged = changeTime
            ? artifact.Source with { RetrievedAt = "2026-10-01T12:00:00.0000000+00:00" }
            : artifact.Source with { SourceUrl = artifact.Source.SourceUrl.Replace("us.cdn.blizzard.com", "eu.cdn.blizzard.com", StringComparison.Ordinal) };
        await using (var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = System.IO.Path.Combine(fixture.Path, "discovery-v1.sqlite3"), Pooling = false }.ToString()))
        {
            await db.OpenAsync(); using var change = db.CreateCommand();
            change.CommandText = "UPDATE resolver_artifacts SET receipt=$receipt WHERE source_sha256=$source";
            change.Parameters.AddWithValue("$receipt", JsonSerializer.Serialize(forged, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            change.Parameters.AddWithValue("$source", artifact.Source.Sha256);
            Assert.Equal(1, await change.ExecuteNonQueryAsync());
        }
        fixture.Handler.Offline = true;
        Assert.Equal("DOWNLOAD_CATALOG_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            fixture.Resolver.OpenEncodingAsync(Origin, fixture.Encoding, Progress))).Code);
        Assert.Empty(fixture.StagingFiles());
    }

    [Fact]
    public async Task InvalidSourceGrammarCannotPublishAResolverArtifactAndReleasesBothAdmissions()
    {
        var data = InventoryFixture.Create();
        var invalid = data.Metadata["encoding"].Decoded.ToArray();
        invalid[0] = (byte)'X';
        var source = Encoded(invalid);
        await using var fixture = await Fixture.CreateAsync(data, source);
        var error = await Assert.ThrowsAsync<DiscoveryException>(() =>
            fixture.Resolver.OpenEncodingAsync(Origin, fixture.Encoding, Progress));
        Assert.Equal("EN_INVALID", error.Code);
        var receipt = await fixture.Catalog.FindMetadataAsync(fixture.Encoding.EncodingKey, default);
        Assert.NotNull(receipt); // Valid BLTE bytes remain source evidence, not a parsed EN artifact.
        Assert.Null(await fixture.Catalog.FindResolverArtifactAsync(receipt.Sha256, "encoding",
            ResolverArtifacts.EncodingRevision, default));
        Assert.Single(fixture.ObjectFiles());
        Assert.Empty(fixture.StagingFiles());
        var next = await Assert.ThrowsAsync<DiscoveryException>(() =>
            fixture.Resolver.OpenEncodingAsync(Origin, fixture.Encoding, Progress));
        Assert.Equal("EN_INVALID", next.Code);
        Assert.Equal(1, fixture.Handler.Requests);
    }

    [Theory]
    [InlineData(false, "LOCAL_OBJECT_MISSING")]
    [InlineData(true, "LOCAL_OBJECT_CORRUPT")]
    public async Task MissingOrCorruptDerivedBytesAreExplicitAndNeverCreditedFromHistoricalReceipt(bool corrupt, string code)
    {
        await using var fixture = await Fixture.CreateAsync();
        ResolverArtifact artifact;
        await using (var first = await fixture.Resolver.OpenEncodingAsync(Origin, fixture.Encoding, Progress))
            artifact = (await fixture.Catalog.FindResolverArtifactAsync(first.Receipt.Sha256,
                "encoding", ResolverArtifacts.EncodingRevision, default))!;
        var path = fixture.ObjectPath(artifact.DecodedSha256);
        var saved = await File.ReadAllBytesAsync(path);
        if (corrupt)
        {
            var changed = saved.ToArray(); changed[^1] ^= 1;
            await File.WriteAllBytesAsync(path, changed);
        }
        else File.Delete(path);
        fixture.Handler.Offline = true;
        Assert.Equal(code, (await Assert.ThrowsAsync<DiscoveryException>(() =>
            fixture.Resolver.OpenEncodingAsync(Origin, fixture.Encoding, Progress))).Code);
        Assert.Equal(1, fixture.Handler.Requests);
        Assert.Empty(fixture.StagingFiles());
        Assert.Equal(artifact, await fixture.Catalog.FindResolverArtifactAsync(artifact.Source.Sha256,
            "encoding", ResolverArtifacts.EncodingRevision, default));
        // Restoring the authored bytes demonstrates the failure released service admission.
        await File.WriteAllBytesAsync(path, saved);
        await using var retry = await fixture.Resolver.OpenEncodingAsync(Origin, fixture.Encoding, Progress);
        Assert.NotNull(await retry.Lookup.FindContentAsync(fixture.Data.PayloadA.ContentKey));
    }

    [Fact]
    public async Task CachedDecodedProofCannotBypassCorruptionInItsOriginalEncodedSource()
    {
        await using var fixture = await Fixture.CreateAsync();
        MetadataReceipt receipt;
        await using (var first = await fixture.Resolver.OpenEncodingAsync(Origin, fixture.Encoding, Progress))
            receipt = first.Receipt;
        var path = fixture.ObjectPath(receipt.Sha256);
        var changed = await File.ReadAllBytesAsync(path); changed[^1] ^= 1;
        await File.WriteAllBytesAsync(path, changed);
        fixture.Handler.Offline = true;
        Assert.Equal("LOCAL_OBJECT_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            fixture.Resolver.OpenEncodingAsync(Origin, fixture.Encoding, Progress))).Code);
        Assert.Equal(1, fixture.Handler.Requests);
        Assert.Empty(fixture.StagingFiles());
    }

    [Fact]
    public async Task AValidSameLengthEncodingArtifactCannotBeReboundToADifferentVerifiedSource()
    {
        await using var fixture = await Fixture.CreateAsync();
        ResolverArtifact original;
        await using (var first = await fixture.Resolver.OpenEncodingAsync(Origin, fixture.Encoding, Progress))
            original = (await fixture.Catalog.FindResolverArtifactAsync(first.Receipt.Sha256,
                "encoding", ResolverArtifacts.EncodingRevision, default))!;
        var different = fixture.Data.Metadata["encoding"].Decoded.ToArray();
        different[22] = (byte)'c'; // Another valid printable specification of exactly the same length.
        using (var validation = new MemoryStream(different))
            _ = await EncodingFileLookup.OpenAsync(validation, new());
        string path;
        await using (var staged = await fixture.Objects.CreateStagingFileAsync(default))
        { path = staged.Name; await staged.WriteAsync(different); }
        var sha = await fixture.Objects.PublishFileAsync(path, different.Length, default);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = System.IO.Path.Combine(fixture.Path, "discovery-v1.sqlite3"), Pooling = false }.ToString());
        await connection.OpenAsync();
        using (var change = connection.CreateCommand())
        {
            change.CommandText = "UPDATE resolver_artifacts SET decoded_sha256=$sha WHERE source_sha256=$source";
            change.Parameters.AddWithValue("$sha", sha); change.Parameters.AddWithValue("$source", original.Source.Sha256);
            Assert.Equal(1, await change.ExecuteNonQueryAsync());
        }
        fixture.Handler.Offline = true;
        Assert.Equal("RESOLVER_SOURCE_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            fixture.Resolver.OpenEncodingAsync(Origin, fixture.Encoding, Progress))).Code);
        Assert.Equal(1, fixture.Handler.Requests);
        Assert.Empty(fixture.StagingFiles());
    }

    [Fact]
    public async Task CancellationBeforePublicationPreservesEncodedSourceAndRetryCanFinishOffline()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Resolver.OpenEncodingAsync(Origin,
            fixture.Encoding, stage =>
            {
                if (stage == "Saving verified encoding lookup") cancel.Cancel();
                return Task.CompletedTask;
            }, cancel.Token));
        var receipt = await fixture.Catalog.FindMetadataAsync(fixture.Encoding.EncodingKey, default);
        Assert.NotNull(receipt);
        Assert.Null(await fixture.Catalog.FindResolverArtifactAsync(receipt.Sha256, "encoding",
            ResolverArtifacts.EncodingRevision, default));
        Assert.Empty(fixture.StagingFiles());
        Assert.Single(fixture.ObjectFiles());
        fixture.Handler.Offline = true;
        await using var retry = await fixture.Resolver.OpenEncodingAsync(Origin, fixture.Encoding, Progress);
        Assert.NotNull(await retry.Lookup.FindContentAsync(fixture.Data.PayloadA.ContentKey));
        Assert.Equal(1, fixture.Handler.Requests);
    }

    [Fact]
    public async Task ProgressFailureDoesNotLeakTheLookupLeaseOrAcquisitionAdmission()
    {
        await using var fixture = await Fixture.CreateAsync();
        var failure = new IOException("authored observer failure");
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => fixture.Resolver.OpenEncodingAsync(Origin,
            fixture.Encoding, stage => stage == "Verifying saved encoding lookup" ? throw failure : Task.CompletedTask)));
        Assert.Empty(fixture.StagingFiles());
        fixture.Handler.Offline = true;
        var lease = await fixture.Resolver.OpenEncodingAsync(Origin, fixture.Encoding, Progress);
        await lease.DisposeAsync();
        await lease.DisposeAsync();
        await using var next = await fixture.Resolver.OpenEncodingAsync(Origin, fixture.Encoding, Progress);
        Assert.NotNull(await next.Lookup.FindContentAsync(fixture.Data.PayloadA.ContentKey));
        Assert.Equal(1, fixture.Handler.Requests);
    }

    [Fact]
    public async Task ConcurrentLeaseDisposalAwaitsOneActualCloseBeforeReleasingAdmission()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var source = await fixture.Resolver.OpenEncodingAsync(Origin, fixture.Encoding, Progress);
        var path = System.IO.Path.Combine(fixture.Path, "controlled-lease.bin");
        await File.WriteAllBytesAsync(path, fixture.Data.Metadata["encoding"].Decoded);
        var stream = new DelayedClose(path);
        var lookup = await EncodingFileLookup.OpenAsync(stream, new());
        var released = 0;
        var lease = new EncodingLookupLease(source.Receipt, lookup, stream, () => released++);
        var first = lease.DisposeAsync().AsTask();
        await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = lease.DisposeAsync().AsTask();
        Assert.False(second.IsCompleted);
        Assert.Equal(0, released);
        stream.Release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, released);
        Assert.False(stream.CanRead);
    }

    [Fact]
    public async Task UnsupportedRoleOversizeAndCanceledRequestsCannotAcquireOrPublish()
    {
        await using var fixture = await Fixture.CreateAsync();
        Assert.Equal("RESOLVER_UNSUPPORTED", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            fixture.Resolver.OpenEncodingAsync(Origin, fixture.Download, Progress))).Code);
        Assert.Equal("RESOLVER_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            fixture.Resolver.OpenEncodingAsync(Origin, fixture.Encoding with
                { DecodedBytes = VerifiedObjectStore.MaxObjectBytes + 1 }, Progress))).Code);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Resolver.OpenEncodingAsync(Origin, fixture.Encoding, Progress, cancel.Token));
        Assert.Equal(0, fixture.Handler.Requests);
        Assert.Empty(fixture.ObjectFiles());
        Assert.Empty(fixture.StagingFiles());
    }

    private static (byte[] Bytes, MetadataExpectation Expectation) Encoded(byte[] decoded)
    {
        byte[] encoded = [.. "BLTE"u8.ToArray(), 0, 0, 0, 0, (byte)'N', .. decoded];
        return (encoded, new("encoding", Convert.ToHexStringLower(MD5.HashData(decoded)),
            Convert.ToHexStringLower(MD5.HashData(encoded)), encoded.Length, decoded.Length));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "vibeminer-resolver-" + Guid.NewGuid().ToString("N"));
        public InventoryFixture Data { get; private set; } = null!;
        public DiscoveryCatalog Catalog { get; private set; } = null!;
        public VerifiedObjectStore Objects { get; private set; } = null!;
        public MetadataAcquirer Acquirer { get; private set; } = null!;
        public ResolverArtifacts Resolver { get; private set; } = null!;
        public Handler Handler { get; private set; } = null!;
        public MetadataExpectation Encoding { get; private set; } = null!;
        public MetadataExpectation Download { get; private set; } = null!;
        private HttpClient client = null!;

        public static async Task<Fixture> CreateAsync(InventoryFixture? data = null,
            (byte[] Bytes, MetadataExpectation Expectation)? encoding = null, TimeProvider? clock = null)
        {
            data ??= InventoryFixture.Create();
            var source = data.Metadata["encoding"];
            var download = data.Metadata["download"];
            encoding ??= (source.Encoded, new("encoding", source.ContentKey, source.EncodingKey,
                source.Encoded.Length, source.Decoded.Length));
            var result = new Fixture { Data = data, Encoding = encoding.Value.Expectation,
                Download = new("download", download.ContentKey, download.EncodingKey, download.Encoded.Length, download.Decoded.Length) };
            Directory.CreateDirectory(result.Path);
            result.Catalog = await DiscoveryCatalog.OpenAsync(result.Path);
            result.Objects = new(result.Path);
            var responses = new Dictionary<string, byte[]>(data.Responses, StringComparer.Ordinal)
                { [InventoryFixture.Url("data", result.Encoding.EncodingKey)] = encoding.Value.Bytes };
            result.Handler = new(responses);
            result.client = new(result.Handler);
            result.Acquirer = new(result.client, result.Objects, timeProvider: clock);
            result.Resolver = new(result.Catalog, result.Objects, result.Acquirer);
            return result;
        }

        public IEnumerable<string> StagingFiles() => Directory.Exists(System.IO.Path.Combine(Path, "staging-v1"))
            ? Directory.EnumerateFiles(System.IO.Path.Combine(Path, "staging-v1")) : [];
        public IEnumerable<string> ObjectFiles() => Directory.Exists(System.IO.Path.Combine(Path, "objects-v1"))
            ? Directory.EnumerateFiles(System.IO.Path.Combine(Path, "objects-v1"), "*", SearchOption.AllDirectories)
                .Where(path => !path.EndsWith(".owner", StringComparison.Ordinal)) : [];
        public string ObjectPath(string sha) => System.IO.Path.Combine(Path, "objects-v1", sha[..2], sha[2..]);
        public MetadataAcquirer SharedAcquirer(ISharedAcquisition shared) => new(client, Objects, shared: shared);
        public async ValueTask DisposeAsync()
        { client.Dispose(); await Catalog.DisposeAsync(); await Objects.DisposeAsync(); Directory.Delete(Path, true); }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
    private sealed class SharedPhysical(EncodedReceipt receipt) : ISharedAcquisition
    {
        internal int Calls;
        public IAcquisitionSubscription Attach(EncodedRequirement requirement, EncodedSourceContext source,
            AcquisitionPriority priority, Func<AcquisitionProgress, Task>? progress = null) => throw new InvalidOperationException("Only direct acquisition is expected.");
        public Task<EncodedReceipt> AcquireAsync(EncodedRequirement requirement, EncodedSourceContext source,
            AcquisitionPriority priority, Func<AcquisitionProgress, Task>? progress = null, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            Assert.Equal(receipt.EncodingKey, requirement.EncodingKey); Assert.Equal(receipt.EncodedBytes, requirement.EncodedBytes);
            Calls++; return Task.FromResult(receipt);
        }
    }
    private sealed class Handler(IReadOnlyDictionary<string, byte[]> responses) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public bool Offline { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Offline) throw new InvalidOperationException("Authored offline fixture forbids network access.");
            Requests++;
            Assert.True(responses.TryGetValue(request.RequestUri!.AbsoluteUri, out var bytes));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { RequestMessage = request, Content = new ByteArrayContent(bytes) });
        }
    }

    private sealed class DelayedClose(string path) : FileStream(path, FileMode.Open, FileAccess.Read,
        FileShare.Read, 4096, FileOptions.Asynchronous)
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask DisposeAsync()
        { Entered.TrySetResult(); await Release.Task; await base.DisposeAsync(); }
    }
}
