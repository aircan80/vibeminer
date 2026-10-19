using System.Net;
using System.Security.Cryptography;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

[Collection("Bulk archive locator")]
public sealed class SourceIntegrityOrderingTests
{
    private static readonly Uri Origin = new("https://us.cdn.blizzard.com/tpr/wow/");

    [Fact]
    public async Task Bulk_native_source_map_and_manifest_publish_and_reopen_with_one_CPU_slot()
    {
        await using var fixture = await SharedAcquisitionIntegrationTests.Fixture.CreateAsync(archiveEncoding: true);
        var resources = new AcquisitionResources(maximumCpu: 1, maximumBackgroundCpu: 1);
        var ledger = await LedgerIntegrityOrderingTests.PublishLedger(fixture.Objects);
        var indexUrl = Assert.Single(fixture.Data.Responses.Keys, url => url.EndsWith(".index", StringComparison.Ordinal));
        var archiveKey = Path.GetFileName(indexUrl)[..^6];
        var locator = new BulkArchiveLocator(fixture.Objects, new OfficialSourceTransport(fixture.Client, resources), resources);
        var map = await locator.BuildAsync(Origin, fixture.Source.CdnConfigurationSha256, [archiveKey], ledger,
            _ => Task.CompletedTask, default).WaitAsync(IntegrityStripeFixture.Deadline);
        Assert.Equal(0, map.LocatedMembers);
        await using var session = await locator.OpenAsync(map, fixture.Source.CdnConfigurationSha256, [archiveKey], ledger, default)
            .WaitAsync(IntegrityStripeFixture.Deadline);
        Assert.Empty(await session.FindAsync(LedgerIntegrityOrderingTests.Key, 7, default));
    }

    [Fact]
    public async Task Table_store_retained_prepare_and_cold_rows_use_resource_aware_reader_with_one_CPU_slot()
    {
        await using var fixture = await ItemsStoreTests.Harness.Create();
        using var offline = new HttpClient(new Offline());
        var resources = new AcquisitionResources(maximumCpu: 1, maximumBackgroundCpu: 1);
        var request = new TableDataRequest(fixture.Request.ProductId, fixture.Request.BuildIdentity,
            fixture.Request.SourceRegion, ItemsProjection.ItemTable, "enUS");
        TableDataSnapshot snapshot;
        await using (var store = new TableDataStore(fixture.Catalog, fixture.Objects, offline, resources: resources))
            snapshot = await store.PrepareAsync(request, _ => Task.CompletedTask, default).WaitAsync(IntegrityStripeFixture.Deadline);
        await using var reopened = new TableDataStore(fixture.Catalog, fixture.Objects, offline, resources: resources);
        Assert.Equal(snapshot.ArtifactId, (await reopened.ReadAsync(request, default).WaitAsync(IntegrityStripeFixture.Deadline)).Snapshot!.ArtifactId);
        var rows = await reopened.ReadRowsAsync(new(request.ProductId, request.BuildIdentity, request.SourceRegion,
            request.TableId, request.ResearchLocale, snapshot.ArtifactId, 1), default).WaitAsync(IntegrityStripeFixture.Deadline);
        Assert.Equal("1", Assert.Single(rows.Rows).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Physical_producer_reuse_or_repair_waits_for_actual_stripe_with_CPU_free(bool collision)
    {
        await using var source = await SharedAcquisitionIntegrationTests.Fixture.CreateAsync();
        var data = source.Data.PayloadA;
        await using var fixture = new IntegrityStripeFixture();
        var sha = await fixture.HoldAsync(data.Encoded, collision);
        await fixture.Objects.PublishAsync(source.Data.CdnConfig, source.Source.CdnConfigurationSha256, fixture.Token);
        var original = new EncodedReceipt(data.EncodingKey, sha, data.Encoded.Length, InventoryFixture.Url("data", data.EncodingKey),
            Time, Time, OfficialEncodedObjectProducer.VerifierRevision, new("loose", "0", data.Encoded.Length.ToString(), null));
        await source.Catalog.RecordEncodedReceiptAsync(original, fixture.Token);
        var producer = new OfficialEncodedObjectProducer(source.Catalog, fixture.Objects, source.Client, fixture.Resources);
        var producing = producer.ProduceAsync(new(data.EncodingKey, data.Encoded.Length), new SourceWork(source.Source), fixture.Token);
        await fixture.AssertCpuAvailableAtArrivalAsync(producing);
        var result = await producing;
        Assert.Equal(original.Sha256, result.Sha256);
        Assert.Empty(await source.Catalog.ReadKnownCorruptAsync([(data.EncodingKey, data.Encoded.Length)], fixture.Token));
        await using var verified = await fixture.Objects.OpenVerifiedAsync(sha, data.Encoded.Length, fixture.Resources, null, fixture.Token);
        Assert.Equal(data.Encoded.Length, verified.Length);
        Assert.Equal(collision ? 0 : 1, source.Handler.Requests.Count(url => url == original.SourceUrl));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selected_resolver_ROOT_and_download_pipeline_remain_functional_with_one_CPU_slot(bool archivedEncoding)
    {
        await using var fixture = await SharedAcquisitionIntegrationTests.Fixture.CreateAsync(archivedEncoding);
        await fixture.Shared.DisposeAsync();
        fixture.Resources = new(maximumCpu: 1, maximumBackgroundCpu: 1);
        fixture.Shared = new(new OfficialEncodedObjectProducer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Resources));
        using var selected = new SelectedFileAcquirer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Shared, fixture.Resources);
        ContentReceipt receipt;
        await using (var content = await selected.AcquireAsync(fixture.Request, 1001, "enUS", _ => Task.CompletedTask, default)
            .WaitAsync(IntegrityStripeFixture.Deadline))
            receipt = content.Receipt;
        using (var preparation = new DownloadWorkspacePreparer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Shared, fixture.Resources))
            await preparation.PrepareAsync(fixture.Request, _ => Task.CompletedTask, default).WaitAsync(IntegrityStripeFixture.Deadline);
        await using (var sessions = new AssetResolutionSessions(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Shared, fixture.Resources))
        await using (var session = await sessions.OpenAsync(fixture.Request, "enUS", _ => Task.CompletedTask).WaitAsync(IntegrityStripeFixture.Deadline))
            Assert.Equal(AssetResolutionStatus.Ready,
                Assert.Single((await session.ResolveAsync([1001], _ => Task.CompletedTask).WaitAsync(IntegrityStripeFixture.Deadline)).Results).Status);
        var requests = fixture.Handler.Requests.Count;
        fixture.Handler.Offline = true;
        await using var reused = await selected.AcquireAsync(fixture.Request, 1001, "enUS", _ => Task.CompletedTask, default)
            .WaitAsync(IntegrityStripeFixture.Deadline);
        Assert.Equal(receipt.Sha256, reused.Receipt.Sha256);
        Assert.Equal(receipt.RetrievedAt, reused.Receipt.RetrievedAt);
        Assert.Equal(requests, fixture.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Metadata_retained_and_shared_opens_arrive_at_same_or_colliding_stripe_with_CPU_free(bool shared, bool collision)
    {
        var data = InventoryFixture.Create().Metadata["encoding"];
        await using var fixture = new IntegrityStripeFixture();
        var sha = await fixture.HoldAsync(data.Encoded, collision);
        using var client = new HttpClient(new Offline());
        var physical = new EncodedReceipt(data.EncodingKey, sha, data.Encoded.Length, InventoryFixture.Url("data", data.EncodingKey),
            Time, Time, OfficialEncodedObjectProducer.VerifierRevision, new("loose", "0", data.Encoded.Length.ToString(), null));
        var acquirer = new MetadataAcquirer(client, fixture.Objects,
            shared: shared ? new Physical(physical) : null, resources: fixture.Resources);
        var retained = new MetadataReceipt("encoding", data.ContentKey, data.EncodingKey, sha,
            data.Encoded.Length.ToString(), data.Decoded.Length.ToString(), physical.SourceUrl, Time, Time, MetadataAcquirer.SourceRevision);
        var source = new EncodedSourceContext("wow", "authored", "us", new string('1', 64));
        var acquiring = acquirer.AcquireAsync(Origin,
            new("encoding", data.ContentKey, data.EncodingKey, data.Encoded.Length, data.Decoded.Length),
            _ => Task.CompletedTask, fixture.Token, retained, shared ? source : null);
        await fixture.AssertCpuAvailableAtArrivalAsync(acquiring);
        if (!collision)
        {
            Assert.Equal("LOCAL_OBJECT_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() => acquiring)).Code);
            return;
        }
        await using var lease = await acquiring;
        Assert.Equal(retained.SourceUrl, lease.Receipt.SourceUrl);
        Assert.Equal(retained.RetrievedAt, lease.Receipt.RetrievedAt);
        Assert.Equal(retained.Sha256, lease.Receipt.Sha256);
        await using var decoded = lease.OpenDecoded();
        using var copy = new MemoryStream(); await decoded.CopyToAsync(copy);
        Assert.Equal(data.Decoded, copy.ToArray());
    }

    [Fact]
    public async Task Fresh_metadata_publishes_with_one_CPU_slot_then_reuses_original_receipt_offline()
    {
        var data = InventoryFixture.Create().Metadata["encoding"];
        using var directory = new TempDirectory();
        await using var objects = new VerifiedObjectStore(directory.Path);
        var resources = new AcquisitionResources(maximumCpu: 1, maximumBackgroundCpu: 1);
        using var client = new HttpClient(new Payload(data.Encoded));
        var expected = new MetadataExpectation("encoding", data.ContentKey, data.EncodingKey, data.Encoded.Length, data.Decoded.Length);
        MetadataReceipt receipt;
        await using (var lease = await new MetadataAcquirer(client, objects, resources: resources)
            .AcquireAsync(Origin, expected, _ => Task.CompletedTask, default).WaitAsync(IntegrityStripeFixture.Deadline))
            receipt = lease.Receipt;
        using var offline = new HttpClient(new Offline());
        await using var reused = await new MetadataAcquirer(offline, objects, resources: resources)
            .AcquireAsync(Origin, expected, _ => Task.CompletedTask, default, receipt).WaitAsync(IntegrityStripeFixture.Deadline);
        Assert.Equal(receipt.Sha256, reused.Receipt.Sha256);
        Assert.Equal(receipt.RetrievedAt, reused.Receipt.RetrievedAt);
        Assert.Equal(receipt.SourceUrl, reused.Receipt.SourceUrl);
    }

    private const string Time = "2026-10-18T00:00:00.0000000+00:00";
    private sealed class SourceWork(EncodedSourceContext source) : IAcquisitionWorkContext
    {
        public AcquisitionWorkSnapshot Snapshot => new(0, AcquisitionPriority.Background, [source]);
        public Task ReportAsync(AcquisitionProgress progress) => Task.CompletedTask;
        public Task WaitForChangeAsync(long observedRevision, CancellationToken token = default) => Task.Delay(Timeout.InfiniteTimeSpan, token);
    }
    private sealed class Physical(EncodedReceipt receipt) : ISharedAcquisition
    {
        public IAcquisitionSubscription Attach(EncodedRequirement requirement, EncodedSourceContext source,
            AcquisitionPriority priority, Func<AcquisitionProgress, Task>? progress = null) => throw new NotSupportedException();
        public Task<EncodedReceipt> AcquireAsync(EncodedRequirement requirement, EncodedSourceContext source,
            AcquisitionPriority priority, Func<AcquisitionProgress, Task>? progress = null, CancellationToken token = default)
        { token.ThrowIfCancellationRequested(); return Task.FromResult(receipt); }
    }
    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new InvalidOperationException("The retained source must not use HTTP.");
    }
    private sealed class Payload(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) });
    }
}

// The journal owns a real integrity stripe. The CAS-owned observer reports the adapter's
// actual pre-WaitAsync arrival, after all its upstream work, without blocking/reentering CAS.
internal sealed class IntegrityStripeFixture : IAsyncDisposable
{
    internal static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private readonly TempDirectory directory = new();
    private readonly CancellationTokenSource cancellation = new(Deadline);
    private readonly TaskCompletionSource journalEntered = Signal();
    private readonly TaskCompletionSource releaseJournal = Signal();
    private readonly TaskCompletionSource callerArrived = Signal();
    private int journalCalls;
    private string? observing;
    private Task? blockedRead;
    private Task? adapter;
    internal AcquisitionResources Resources { get; } = new(maximumCpu: 1, maximumBackgroundCpu: 1);
    internal VerifiedObjectStore Objects { get; }
    internal CancellationToken Token => cancellation.Token;
    internal IntegrityStripeFixture()
    {
        Objects = new(directory.Path, async (_, _, _, token) =>
        {
            Assert.False(token.CanBeCanceled);
            if (Interlocked.Increment(ref journalCalls) == 1)
            { journalEntered.TrySetResult(); await releaseJournal.Task; }
        }, sha => { if (sha == Volatile.Read(ref observing)) callerArrived.TrySetResult(); });
    }
    internal async Task<string> HoldAsync(byte[] target, bool collision)
    {
        var targetSha = await Publish(target);
        var blocker = collision ? Enumerable.Range(0, 65536).Select(i => new byte[] { (byte)i, (byte)(i >> 8), 7 })
            .First(bytes => Sha(bytes) != targetSha && Stripe(Sha(bytes)) == Stripe(targetSha)) : target;
        var blockerSha = collision ? await Publish(blocker) : targetSha;
        await File.WriteAllBytesAsync(Path.Combine(directory.Path, "objects-v1", blockerSha[..2], blockerSha[2..]), [9]);
        blockedRead = Objects.OpenVerifiedAsync(blockerSha, blocker.Length, Resources, null, Token);
        await journalEntered.Task.WaitAsync(Deadline);
        Volatile.Write(ref observing, targetSha);
        return targetSha;
    }
    internal async Task AssertCpuAvailableAtArrivalAsync(Task operation)
    {
        adapter = operation;
        try
        {
            await callerArrived.Task.WaitAsync(Deadline);
            Assert.False(operation.IsCompleted);
            using var foreground = await Resources.EnterCpuAsync(null, Token).WaitAsync(Deadline);
            Assert.False(releaseJournal.Task.IsCompleted);
            Assert.False(operation.IsCompleted);
        }
        finally { releaseJournal.TrySetResult(); }
    }
    private async Task<string> Publish(byte[] bytes)
    {
        string path;
        await using (var file = await Objects.CreateStagingFileAsync(Token)) { path = file.Name; await file.WriteAsync(bytes, Token); }
        return await Objects.PublishFileAsync(path, bytes.Length, Token);
    }
    private static int Stripe(string sha) => Convert.ToByte(sha[..2], 16) % 64;
    private static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async ValueTask DisposeAsync()
    {
        releaseJournal.TrySetResult(); cancellation.Cancel();
        try { if (adapter is not null) await adapter.WaitAsync(Deadline); }
        catch (Exception error) when (error is not TimeoutException) { /* Observe the adapter's failed/canceled completion during cleanup. */ }
        try { if (blockedRead is not null) await blockedRead.WaitAsync(Deadline); } catch (DiscoveryException failure) when (failure.Code == "LOCAL_OBJECT_CORRUPT") { }
        finally { await Objects.DisposeAsync(); cancellation.Dispose(); directory.Dispose(); }
    }
}
