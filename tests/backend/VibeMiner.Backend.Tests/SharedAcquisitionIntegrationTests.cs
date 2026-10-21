using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Text;
using VibeMiner.Protocols.Tact;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class SharedAcquisitionIntegrationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Metadata_and_background_retention_share_one_transfer_then_keep_independent_decoded_proof()
    {
        await using var fixture = await Fixture.CreateAsync();
        var item = fixture.Data.Metadata["encoding"];
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Handler.Before = async request =>
        {
            if (request.RequestUri!.AbsoluteUri == InventoryFixture.Url("data", item.EncodingKey))
            { entered.TrySetResult(); await release.Task; }
        };
        await using var bulk = fixture.Shared.Attach(new(item.EncodingKey, item.Encoded.Length), fixture.Source, AcquisitionPriority.Background);
        await entered.Task.WaitAsync(Deadline);
        var metadata = new MetadataAcquirer(fixture.Client, fixture.Objects, shared: fixture.Shared, resources: fixture.Resources);
        var acquiring = metadata.AcquireAsync(new("https://us.cdn.blizzard.com/tpr/wow/"),
            new("encoding", item.ContentKey, item.EncodingKey, item.Encoded.Length, item.Decoded.Length),
            _ => Task.CompletedTask, default, source: fixture.Source);
        release.SetResult();
        await using var decoded = await acquiring.WaitAsync(Deadline);
        var physical = await bulk.Completion.WaitAsync(Deadline);
        await using var source = decoded.OpenDecoded();
        using var bytes = new MemoryStream(); await source.CopyToAsync(bytes);
        Assert.Equal(item.Decoded, bytes.ToArray());
        Assert.Equal(physical.Sha256, decoded.Receipt.Sha256);
        Assert.Equal(item.ContentKey, decoded.Receipt.ContentKey);
        Assert.Single(fixture.Handler.Requests, url => url == InventoryFixture.Url("data", item.EncodingKey));
        Assert.Equal("blte-encoded-v1", physical.VerifierRevision);
    }

    [Fact]
    public async Task Selected_file_and_metadata_reuse_shared_receipts_offline_without_losing_association()
    {
        await using var fixture = await Fixture.CreateAsync();
        using var selected = new SelectedFileAcquirer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Shared, fixture.Resources);
        ContentReceipt first;
        await using (var lease = await selected.AcquireAsync(fixture.Request, 1001, "enUS", _ => Task.CompletedTask, default))
        {
            first = lease.Receipt;
            Assert.Equal(fixture.Data.PayloadA.ContentKey, first.ContentKey);
            Assert.NotEmpty(lease.Association.Memberships);
        }
        var requests = fixture.Handler.Requests.Count;
        fixture.Handler.Offline = true;
        await using var reused = await selected.AcquireAsync(fixture.Request, 1001, "enUS", _ => Task.CompletedTask, default);
        Assert.Equal(first.Sha256, reused.Receipt.Sha256);
        Assert.Equal(requests, fixture.Handler.Requests.Count);
        Assert.Single(Directory.EnumerateFiles(Path.Combine(fixture.Path, "staging-v1")));
    }

    [Fact]
    public async Task Retained_bytes_do_not_authorize_a_different_pinned_source_context()
    {
        await using var fixture = await Fixture.CreateAsync();
        var item = fixture.Data.PayloadA;
        var need = new EncodedRequirement(item.EncodingKey, item.Encoded.Length);
        _ = await fixture.Shared.AcquireAsync(need, fixture.Source, AcquisitionPriority.Background);
        var requests = fixture.Handler.Requests.Count;
        var invalid = fixture.Source with { CdnConfigurationSha256 = new string('f', 64) };
        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Shared.AcquireAsync(need, invalid, AcquisitionPriority.Interactive));
        Assert.Equal("ENCODED_SOURCE_CONTEXT_CONFLICT", failure.Code);
        Assert.Equal(requests, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retained_proof_corruption_is_journaled_until_real_verification_and_missing_is_distinct(bool corrupt)
    {
        await using var fixture = await Fixture.CreateAsync();
        var item = fixture.Data.PayloadA; var need = new EncodedRequirement(item.EncodingKey, item.Encoded.Length);
        var receipt = await fixture.Shared.AcquireAsync(need, fixture.Source, AcquisitionPriority.Interactive);
        var path = Path.Combine(fixture.Path, "objects-v1", receipt.Sha256[..2], receipt.Sha256[2..]);
        if (corrupt)
        {
            var changed = item.Encoded.ToArray(); changed[^1] ^= 1; await File.WriteAllBytesAsync(path, changed);
        }
        else File.Delete(path);
        fixture.Handler.Before = _ => throw new DiscoveryException("NETWORK_UNAVAILABLE", "Authored repair source offline.", true);
        _ = await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Shared.AcquireAsync(need, fixture.Source, AcquisitionPriority.Interactive));
        var observed = await fixture.Catalog.ReadKnownCorruptAsync([(item.EncodingKey, item.Encoded.Length)], default);
        Assert.Equal(corrupt, observed.Contains(item.EncodingKey));
        await fixture.Catalog.RecordEncodedReceiptAsync(receipt, default);
        Assert.Equal(corrupt, (await fixture.Catalog.ReadKnownCorruptAsync([(item.EncodingKey, item.Encoded.Length)], default)).Contains(item.EncodingKey));
        await File.WriteAllBytesAsync(path, item.Encoded);
        fixture.Handler.Before = null; fixture.Handler.Offline = true;
        _ = await fixture.Shared.AcquireAsync(need, fixture.Source, AcquisitionPriority.Interactive);
        Assert.Empty(await fixture.Catalog.ReadKnownCorruptAsync([(item.EncodingKey, item.Encoded.Length)], default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Authorized_demand_repairs_corrupt_canonical_bytes_only_after_new_encoded_proof_then_clears_journal(bool centralObserver)
    {
        await using var fixture = await Fixture.CreateAsync();
        if (centralObserver)
        {
            await fixture.Shared.DisposeAsync(); await fixture.Objects.DisposeAsync();
            fixture.Objects = new(fixture.Path, fixture.Catalog.RecordObjectInvalidityAsync);
            fixture.Shared = new(new OfficialEncodedObjectProducer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Resources));
        }
        var item = fixture.Data.PayloadA; var need = new EncodedRequirement(item.EncodingKey, item.Encoded.Length);
        var original = await fixture.Shared.AcquireAsync(need, fixture.Source, AcquisitionPriority.Interactive);
        var path = Path.Combine(fixture.Path, "objects-v1", original.Sha256[..2], original.Sha256[2..]);
        var damaged = item.Encoded.ToArray(); damaged[^1] ^= 1; await File.WriteAllBytesAsync(path, damaged);
        var repaired = await fixture.Shared.AcquireAsync(need, fixture.Source, AcquisitionPriority.Interactive);
        Assert.Equal(original.Sha256, repaired.Sha256);
        Assert.Equal(item.Encoded, await File.ReadAllBytesAsync(path));
        Assert.Equal(2, fixture.Handler.Requests.Count(url => url == InventoryFixture.Url("data", item.EncodingKey)));
        Assert.Empty(await fixture.Catalog.ReadKnownCorruptAsync([(item.EncodingKey, item.Encoded.Length)], default));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Path, "staging-v1")));
    }

    [Fact]
    public async Task Failed_source_authority_does_not_prevent_another_prepared_context_from_supplying_bytes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var valid = await fixture.PrepareAlternateSourceAsync("# independent authored retained source\narchives =\n"u8.ToArray());
        File.Delete(Path.Combine(fixture.Path, "objects-v1", fixture.Source.CdnConfigurationSha256[..2], fixture.Source.CdnConfigurationSha256[2..]));
        var item = fixture.Data.PayloadA;
        var producer = new OfficialEncodedObjectProducer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Resources);
        var context = new Work(AcquisitionPriority.Interactive, [fixture.Source, valid]);
        var receipt = await producer.ProduceAsync(new(item.EncodingKey, item.Encoded.Length), context, default);
        Assert.Equal(item.EncodingKey, receipt.EncodingKey);
        Assert.Single(fixture.Handler.Requests, url => url == InventoryFixture.Url("data", item.EncodingKey));
        Assert.NotNull(await fixture.Catalog.FindEncodedReceiptAsync(item.EncodingKey, item.Encoded.Length, default));
    }

    [Fact]
    public async Task Resolver_uses_reserved_foreground_cpu_and_releases_its_phase_on_cancellation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var item = fixture.Data.Metadata["encoding"];
        var metadata = new MetadataAcquirer(fixture.Client, fixture.Objects, shared: fixture.Shared, resources: fixture.Resources);
        var resolver = new ResolverArtifacts(fixture.Catalog, fixture.Objects, metadata, fixture.Resources);
        var expected = new MetadataExpectation("encoding", item.ContentKey, item.EncodingKey, item.Encoded.Length, item.Decoded.Length);
        using var bulk = await fixture.Resources.EnterCpuAsync(new Work(AcquisitionPriority.Background), default);
        await using (var prepared = await resolver.OpenEncodingAsync(new("https://us.cdn.blizzard.com/tpr/wow/"),
            expected, _ => Task.CompletedTask, source: fixture.Source).WaitAsync(Deadline))
            Assert.Equal(item.ContentKey, prepared.Receipt.ContentKey);
        using var cancellation = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? blocker = null;
        var attempt = resolver.OpenEncodingAsync(new("https://us.cdn.blizzard.com/tpr/wow/"), expected,
            async stage =>
            {
                if (stage == "Verifying saved encoding lookup")
                {
                    blocker = await fixture.Resources.EnterCpuAsync(null, default);
                    waiting.SetResult();
                }
            }, cancellation.Token, fixture.Source);
        await waiting.Task.WaitAsync(Deadline);
        Assert.False(attempt.IsCompleted); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
        blocker!.Dispose();
        await using var retry = await resolver.OpenEncodingAsync(new("https://us.cdn.blizzard.com/tpr/wow/"),
            expected, _ => Task.CompletedTask, source: fixture.Source).WaitAsync(Deadline);
        Assert.Equal(item.ContentKey, retry.Receipt.ContentKey);
    }

    [Fact]
    public async Task Archive_backed_metadata_preserves_real_range_source_and_reuses_it_offline()
    {
        await using var fixture = await Fixture.CreateAsync(archiveEncoding: true);
        var item = fixture.Data.Metadata["encoding"];
        var metadata = new MetadataAcquirer(fixture.Client, fixture.Objects, shared: fixture.Shared, resources: fixture.Resources);
        var expected = new MetadataExpectation("encoding", item.ContentKey, item.EncodingKey, item.Encoded.Length, item.Decoded.Length);
        MetadataReceipt receipt;
        await using (var decoded = await metadata.AcquireAsync(new("https://us.cdn.blizzard.com/tpr/wow/"),
            expected, _ => Task.CompletedTask, default, source: fixture.Source))
        {
            receipt = decoded.Receipt; Assert.Equal("archive", receipt.Location!.Kind);
            Assert.EndsWith(receipt.Location.ArchiveKey!, receipt.SourceUrl);
            await fixture.Catalog.RecordMetadataAsync(receipt, default);
        }
        var requests = fixture.Handler.Requests.Count; fixture.Handler.Offline = true;
        await using var retained = await metadata.AcquireAsync(new("https://us.cdn.blizzard.com/tpr/wow/"),
            expected, _ => Task.CompletedTask, default, receipt, fixture.Source);
        Assert.Equal(receipt.Location, retained.Receipt.Location);
        Assert.Equal(requests, fixture.Handler.Requests.Count);
        Assert.Equal(receipt.Location, (await fixture.Catalog.FindEncodedReceiptAsync(item.EncodingKey, item.Encoded.Length, default))!.Location);
    }

    [Fact]
    public async Task Large_loose_payload_uses_bounded_exact_ranges_and_publishes_only_encoded_proof()
    {
        await using var fixture = await Fixture.CreateAsync();
        var bytes = new byte[(5 << 20) + 9]; "BLTE"u8.CopyTo(bytes); bytes[8] = (byte)'N';
        var key = Convert.ToHexStringLower(MD5.HashData(bytes)); fixture.Data.Payloads.Add(key, bytes);
        var receipt = await fixture.Shared.AcquireAsync(new(key, bytes.Length), fixture.Source, AcquisitionPriority.Background);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), receipt.Sha256);
        Assert.Equal(2, fixture.Handler.Ranges.Count);
        Assert.All(fixture.Handler.Ranges, span => Assert.InRange(span.Length, 1L, 4L << 20));
        Assert.Equal(bytes.LongLength, fixture.Handler.Ranges.Sum(span => span.Length));
        Assert.Null(await fixture.Catalog.FindContentAsync(key, default));
        Assert.NotNull(await fixture.Catalog.FindEncodedReceiptAsync(key, bytes.Length, default));
    }

    [Fact]
    public async Task Active_promotion_releases_background_quota_and_detach_recomputes_it()
    {
        var resources = new AcquisitionResources(maximumHttp: 4, maximumBackgroundHttp: 2);
        var first = new Work(AcquisitionPriority.Background); var second = new Work(AcquisitionPriority.Background);
        using var a = await resources.EnterHttpAsync(first, default);
        using var b = await resources.EnterHttpAsync(second, default);
        var third = new Work(AcquisitionPriority.Background);
        var pending = resources.EnterHttpAsync(third, default);
        Assert.False(pending.IsCompleted);
        first.Set(AcquisitionPriority.Interactive);
        using var c = await pending.WaitAsync(Deadline);
        first.Set(AcquisitionPriority.Background);
        using var foreground = await resources.EnterHttpAsync(new Work(AcquisitionPriority.Interactive), default).WaitAsync(Deadline);
        using var cancel = new CancellationTokenSource();
        var queued = resources.EnterHttpAsync(new Work(AcquisitionPriority.Background), cancel.Token);
        Assert.False(queued.IsCompleted); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
    }

    [Fact]
    public async Task Demoted_staging_drains_with_hard_limit_and_admits_foreground_before_more_background()
    {
        var resources = new AcquisitionResources(maximumStagingBytes: 100, maximumBackgroundStagingBytes: 25);
        var contexts = Enumerable.Range(0, 4).Select(_ => new Work(AcquisitionPriority.Interactive)).ToArray();
        var reservations = new List<IDisposable>();
        try
        {
            foreach (var context in contexts) reservations.Add(await resources.ReserveStagingAsync(25, context, default));
            foreach (var context in contexts) context.Set(AcquisitionPriority.Background);
            using var cancellation = new CancellationTokenSource();
            var background = resources.ReserveStagingAsync(25, new Work(AcquisitionPriority.Background), cancellation.Token);
            var foregroundContext = new Work(AcquisitionPriority.Interactive);
            var foreground = resources.ReserveStagingAsync(25, foregroundContext, default);
            Assert.False(background.IsCompleted); Assert.False(foreground.IsCompleted);
            Assert.Contains(foregroundContext.Progress, state => state.Stage == "Waiting for temporary storage" && state.Completed is null && state.Total is null);
            reservations[0].Dispose();
            using var admitted = await foreground.WaitAsync(Deadline);
            Assert.False(background.IsCompleted);
            var noOversubscription = resources.ReserveStagingAsync(1, new Work(AcquisitionPriority.Interactive), cancellation.Token);
            Assert.False(noOversubscription.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => background);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => noOversubscription);
        }
        finally { foreach (var reservation in reservations) reservation.Dispose(); }
        using var full = await resources.ReserveStagingAsync(100, new Work(AcquisitionPriority.Interactive), default).WaitAsync(Deadline);
    }

    [Fact]
    public async Task Cpu_and_staging_keep_foreground_capacity_and_release_canceled_waiters()
    {
        var resources = new AcquisitionResources(maximumStagingBytes: 100, maximumBackgroundStagingBytes: 40);
        var bulk = new Work(AcquisitionPriority.Background); var interactive = new Work(AcquisitionPriority.Interactive);
        using var staging = await resources.ReserveStagingAsync(40, bulk, default);
        using var cpu = await resources.EnterCpuAsync(bulk, default);
        using var cancellation = new CancellationTokenSource();
        var waiting = resources.ReserveStagingAsync(1, bulk, cancellation.Token);
        var parsing = resources.EnterCpuAsync(bulk, cancellation.Token);
        Assert.False(waiting.IsCompleted); Assert.False(parsing.IsCompleted);
        using var foregroundStaging = await resources.ReserveStagingAsync(60, interactive, default).WaitAsync(Deadline);
        using var foregroundCpu = await resources.EnterCpuAsync(interactive, default).WaitAsync(Deadline);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => parsing);
    }

    private sealed class Work(AcquisitionPriority priority, IReadOnlyList<EncodedSourceContext>? sources = null) : IAcquisitionWorkContext
    {
        private readonly object gate = new();
        private long revision;
        private AcquisitionPriority current = priority;
        private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AcquisitionWorkSnapshot Snapshot { get { lock (gate) return new(revision, current, sources ?? []); } }
        public Task WaitForChangeAsync(long observedRevision, CancellationToken token = default)
        { lock (gate) return observedRevision != revision ? Task.CompletedTask : changed.Task.WaitAsync(token); }
        internal readonly ConcurrentQueue<AcquisitionProgress> Progress = new();
        public Task ReportAsync(AcquisitionProgress progress) { Progress.Enqueue(progress); return Task.CompletedTask; }
        internal void Set(AcquisitionPriority value)
        {
            lock (gate) { current = value; revision++; var previous = changed; changed = new(TaskCreationOptions.RunContinuationsAsynchronously); previous.SetResult(); }
        }
    }

    internal sealed class Handler(InventoryFixture data) : HttpMessageHandler
    {
        internal readonly ConcurrentQueue<string> Requests = new();
        internal readonly ConcurrentQueue<(long Offset, long Length)> Ranges = new();
        internal Func<HttpRequestMessage, Task>? Before;
        internal bool Offline;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Offline) throw new InvalidOperationException("Authored offline test forbids HTTP.");
            var url = request.RequestUri!.AbsoluteUri; Requests.Enqueue(url);
            if (Before is not null) await Before(request).WaitAsync(token);
            byte[]? bytes = null;
            if (!data.Responses.TryGetValue(url, out bytes))
                bytes = data.Payloads.GetValueOrDefault(Path.GetFileName(request.RequestUri.AbsolutePath));
            if (bytes is not null && request.Headers.Range is { } range)
            {
                var span = Assert.Single(range.Ranges); var from = span.From ?? bytes.LongLength - span.To!.Value;
                var to = span.From is null ? bytes.LongLength - 1 : span.To!.Value;
                Ranges.Enqueue((from, to - from + 1));
                var content = new ByteArrayContent(bytes.AsSpan((int)from, (int)(to - from + 1)).ToArray());
                content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, bytes.LongLength);
                return new(HttpStatusCode.PartialContent) { RequestMessage = request, Content = content };
            }
            return new(bytes is null ? HttpStatusCode.NotFound : HttpStatusCode.OK)
            { RequestMessage = request, Content = new ByteArrayContent(bytes ?? []) };
        }
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vibeminer-shared-" + Guid.NewGuid().ToString("N"));
        internal InventoryFixture Data = InventoryFixture.Create();
        internal DiscoveryCatalog Catalog = null!;
        internal VerifiedObjectStore Objects = null!;
        internal Handler Handler = null!;
        internal HttpClient Client = null!;
        internal AcquisitionResources Resources = new();
        internal AcquisitionCoordinator Shared = null!;
        internal WorkspaceRequest Request = null!;
        internal EncodedSourceContext Source = null!;
        internal static async Task<Fixture> CreateAsync(bool archiveEncoding = false)
        {
            var fixture = new Fixture();
            fixture.Catalog = await DiscoveryCatalog.OpenAsync(fixture.Path);
            fixture.Objects = new(fixture.Path); fixture.Handler = new(fixture.Data); fixture.Client = new(fixture.Handler);
            var discovery = fixture.Data.Discovery;
            var cdnBytes = fixture.Data.CdnConfig;
            var identity = fixture.Data.BuildIdentity;
            if (archiveEncoding)
            {
                var payload = fixture.Data.Metadata["encoding"];
                var page = new byte[4096]; Convert.FromHexString(payload.EncodingKey).CopyTo(page, 0);
                BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(16), (uint)payload.Encoded.Length);
                BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(20), 19);
                var toc = new byte[24]; Convert.FromHexString(payload.EncodingKey).CopyTo(toc, 0);
                MD5.HashData(page).AsSpan(0, 8).CopyTo(toc.AsSpan(16));
                var footer = new byte[28]; MD5.HashData(toc).AsSpan(0, 8).CopyTo(footer);
                new byte[] { 1, 0, 0, 4, 4, 4, 16, 8 }.CopyTo(footer, 8);
                BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(16), 1);
                MD5.HashData(footer.AsSpan(8, 20)).AsSpan(0, 8).CopyTo(footer.AsSpan(20));
                var archiveKey = Convert.ToHexStringLower(MD5.HashData(footer));
                var body = new byte[19 + payload.Encoded.Length + 7]; payload.Encoded.CopyTo(body, 19);
                fixture.Data.Responses.Remove(InventoryFixture.Url("data", payload.EncodingKey));
                fixture.Data.Responses.Add(InventoryFixture.Url("data", archiveKey), body);
                fixture.Data.Responses.Add(InventoryFixture.Url("data", archiveKey) + ".index", [.. page, .. toc, .. footer]);
                cdnBytes = Encoding.UTF8.GetBytes($"archives = {archiveKey}\narchives-index-size = 1\n");
                var cdnKey = Convert.ToHexStringLower(MD5.HashData(cdnBytes));
                fixture.Data.Responses.Add(InventoryFixture.Url("config", cdnKey), cdnBytes);
                var versions = discovery.Documents.Single(document => document.Role == "versions");
                var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(versions.Bytes).Replace(fixture.Data.CdnKey, cdnKey, StringComparison.Ordinal));
                var fields = MetadataParser.Parse(bytes).Rows.Single().Fields;
                var build = BuildIdentity.Create("wow", fields); identity = build.Id;
                discovery = discovery with
                {
                    Documents = [versions with { Bytes = bytes, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) },
                        discovery.Documents.Single(document => document.Role == "cdns")],
                    Observations = [discovery.Observations.Single() with { Identity = build, OriginalFields = fields }]
                };
            }
            fixture.Request = new("wow", identity, "us");
            var generation = await fixture.Catalog.BeginRefreshAsync("wow", default);
            await fixture.Catalog.CommitAsync("wow", generation, discovery, "2026-10-19T00:00:00.0000000Z", default);
            using var configurations = new ConfigurationWorkspacePreparer(fixture.Catalog, fixture.Objects, fixture.Client);
            await configurations.PrepareAsync(fixture.Request, _ => Task.CompletedTask, default);
            fixture.Source = new("wow", identity, "us", Convert.ToHexStringLower(SHA256.HashData(cdnBytes)));
            fixture.Shared = new(new OfficialEncodedObjectProducer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Resources));
            return fixture;
        }
        /// <summary>Advertise the build with another CDN configuration; prepare=false leaves it unretained locally.</summary>
        internal async Task<EncodedSourceContext> PrepareAlternateSourceAsync(byte[] cdnBytes, bool prepare = true,
            string at = "2026-10-19T01:00:00.0000000Z")
        {
            var cdnKey = Convert.ToHexStringLower(MD5.HashData(cdnBytes));
            Data.Responses.Add(InventoryFixture.Url("config", cdnKey), cdnBytes);
            var original = Data.Discovery; var versions = original.Documents.Single(document => document.Role == "versions");
            var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(versions.Bytes).Replace(Data.CdnKey, cdnKey, StringComparison.Ordinal));
            var fields = MetadataParser.Parse(bytes).Rows.Single().Fields; var build = BuildIdentity.Create("wow", fields);
            var discovery = original with
            {
                Documents = [versions with { Bytes = bytes, Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) },
                    original.Documents.Single(document => document.Role == "cdns")],
                Observations = [original.Observations.Single() with { Identity = build, OriginalFields = fields }]
            };
            var generation = await Catalog.BeginRefreshAsync("wow", default);
            await Catalog.CommitAsync("wow", generation, discovery, at, default);
            if (prepare)
            {
                using var configurations = new ConfigurationWorkspacePreparer(Catalog, Objects, Client, resources: Resources);
                await configurations.PrepareAsync(new("wow", build.Id, "us"), _ => Task.CompletedTask, default);
            }
            return new("wow", build.Id, "us", Convert.ToHexStringLower(SHA256.HashData(cdnBytes)));
        }

        public async ValueTask DisposeAsync()
        {
            await Shared.DisposeAsync(); Client.Dispose(); await Objects.DisposeAsync(); await Catalog.DisposeAsync();
            Directory.Delete(Path, true);
        }
    }
}
