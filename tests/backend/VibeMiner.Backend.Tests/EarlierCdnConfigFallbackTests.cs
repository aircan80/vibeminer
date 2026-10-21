using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests;

/// <summary>
/// A republished CDN configuration can drop an archive the build still needs. The shared pipeline then checks the
/// archives listed only by the build's earlier CDN configurations, newest first. Authored synthetic bytes only.
/// </summary>
public sealed class EarlierCdnConfigFallbackTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Object_listed_only_by_an_earlier_cdn_config_comes_from_its_archive_and_records_that_config()
    {
        await using var fixture = await Fixture.CreateAsync();
        var item = fixture.Data.PayloadA;
        Assert.Null(await fixture.Catalog.FindConfigurationAsync(fixture.OldKey, default));

        var receipt = await fixture.Shared.AcquireAsync(new(item.EncodingKey, item.Encoded.Length), fixture.Source,
            AcquisitionPriority.Interactive).WaitAsync(Deadline);

        Assert.Equal("archive", receipt.Location.Kind);
        Assert.Equal(fixture.DroppedArchive, receipt.Location.ArchiveKey);
        Assert.Equal(fixture.OldKey, receipt.CdnConfig);
        Assert.Equal(InventoryFixture.Url("data", fixture.DroppedArchive), receipt.SourceUrl);
        Assert.Equal(fixture.OldKey, (await fixture.Catalog.FindEncodedReceiptAsync(item.EncodingKey, item.Encoded.Length, default))!.CdnConfig);
        // The earlier configuration was fetched once, verified and retained like any configuration.
        var saved = await fixture.Catalog.FindConfigurationAsync(fixture.OldKey, default);
        Assert.NotNull(saved);
        Assert.Equal(InventoryFixture.Url("config", fixture.OldKey), saved.SourceUrl);
        Assert.Equal(fixture.OldConfig, await fixture.Objects.ReadAsync(saved.Sha256, saved.ByteLength, default));
        Assert.Single(fixture.Handler.Requests, url => url == InventoryFixture.Url("config", fixture.OldKey));
        // The current archive was read once (footer, then TOC); the fallback searched only the dropped one.
        Assert.Equal(2, fixture.Handler.Requests.Count(url => url == InventoryFixture.Url("data", fixture.CurrentArchive) + ".index"));
        Assert.NotNull(await fixture.Catalog.FindContentLocatorAsync(item.EncodingKey, saved.Sha256, default));
        Assert.Empty(await fixture.Catalog.ReadKnownCorruptAsync([(item.EncodingKey, item.Encoded.Length)], default));
    }

    [Fact]
    public async Task Object_in_the_current_archives_never_fetches_an_earlier_cdn_config()
    {
        await using var fixture = await Fixture.CreateAsync();
        var item = fixture.Data.PayloadB;
        var receipt = await fixture.Shared.AcquireAsync(new(item.EncodingKey, item.Encoded.Length), fixture.Source,
            AcquisitionPriority.Interactive).WaitAsync(Deadline);
        Assert.Equal(fixture.CurrentArchive, receipt.Location.ArchiveKey);
        Assert.Equal(fixture.NewKey, receipt.CdnConfig);
        Assert.DoesNotContain(fixture.Handler.Requests, url => url == InventoryFixture.Url("config", fixture.OldKey));
        Assert.DoesNotContain(fixture.Handler.Requests, url => url.StartsWith(InventoryFixture.Url("data", fixture.DroppedArchive), StringComparison.Ordinal));
        Assert.Null(await fixture.Catalog.FindConfigurationAsync(fixture.OldKey, default));

        // A loose object records no CDN configuration: no archive list authorized it.
        var loose = Blte("authored loose object\n");
        fixture.Data.Payloads.Add(Hex(MD5.HashData(loose)), loose);
        var looseReceipt = await fixture.Shared.AcquireAsync(new(Hex(MD5.HashData(loose)), loose.Length), fixture.Source,
            AcquisitionPriority.Interactive).WaitAsync(Deadline);
        Assert.Equal("loose", looseReceipt.Location.Kind);
        Assert.Null(looseReceipt.CdnConfig);
    }

    [Fact]
    public async Task Object_no_cdn_config_lists_is_absent_not_corrupt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var missing = Blte("authored object no archive holds\n");
        var key = Hex(MD5.HashData(missing));
        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Shared.AcquireAsync(new(key, missing.Length),
            fixture.Source, AcquisitionPriority.Interactive).WaitAsync(Deadline));
        Assert.Equal("CONTENT_NOT_LOCATED", failure.Code);
        // The earlier configuration and its dropped archive were checked before reporting absent.
        Assert.Contains(fixture.Handler.Requests, url => url == InventoryFixture.Url("config", fixture.OldKey));
        Assert.Contains(fixture.Handler.Requests, url => url == InventoryFixture.Url("data", fixture.DroppedArchive) + ".index");
        Assert.Empty(await fixture.Catalog.ReadKnownCorruptAsync([(key, missing.Length)], default));
        Assert.Null(await fixture.Catalog.FindEncodedReceiptAsync(key, missing.Length, default));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Path, "staging-v1")));
    }

    [Theory]
    [InlineData("archive", "CONTENT_SOURCE_MISSING")]
    [InlineData("index", "CONTENT_LOCATOR_INCOMPLETE")]
    [InlineData("config", "CONTENT_LOCATOR_INCOMPLETE")]
    public async Task Earlier_source_missing_on_the_cdn_is_unavailable_never_corrupt(string missing, string code)
    {
        await using var fixture = await Fixture.CreateAsync();
        var item = fixture.Data.PayloadA;
        Assert.True(fixture.Data.Responses.Remove(missing switch
        {
            "archive" => InventoryFixture.Url("data", fixture.DroppedArchive),
            "index" => InventoryFixture.Url("data", fixture.DroppedArchive) + ".index",
            _ => InventoryFixture.Url("config", fixture.OldKey),
        }));
        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Shared.AcquireAsync(
            new(item.EncodingKey, item.Encoded.Length), fixture.Source, AcquisitionPriority.Interactive).WaitAsync(Deadline));
        Assert.Equal(code, failure.Code);
        Assert.Empty(await fixture.Catalog.ReadKnownCorruptAsync([(item.EncodingKey, item.Encoded.Length)], default));
        Assert.Null(await fixture.Catalog.FindEncodedReceiptAsync(item.EncodingKey, item.Encoded.Length, default));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Path, "staging-v1")));
        if (missing == "config") Assert.Null(await fixture.Catalog.FindConfigurationAsync(fixture.OldKey, default));
    }

    [Theory]
    [InlineData("md5", true)]
    [InlineData("grammar", true)]
    [InlineData("md5", false)]
    [InlineData("grammar", false)]
    public async Task Bad_earlier_cdn_config_is_unavailable_and_never_stops_the_older_ones(string fault, bool olderServed)
    {
        // The bad one is newer than the good earlier config, so the lookup reaches it first.
        var grammar = "# authored earlier cdn config that does not parse\narchives = a = b\n"u8.ToArray();
        var listed = "# authored earlier cdn config the cdn serves altered\narchives =\n"u8.ToArray();
        (string Key, byte[] Served) bad = fault == "md5"
            ? (Hex(MD5.HashData(listed)), "# altered bytes\narchives =\n"u8.ToArray())
            : (Hex(MD5.HashData(grammar)), grammar);
        await using var fixture = await Fixture.CreateAsync(bad);
        Assert.Equal(new[] { bad.Key, fixture.OldKey }, await fixture.Catalog.ReadEarlierCdnConfigsAsync(fixture.Request.BuildIdentity, fixture.NewKey, default));
        if (!olderServed) Assert.True(fixture.Data.Responses.Remove(InventoryFixture.Url("config", fixture.OldKey)));
        var item = fixture.Data.PayloadA;
        var acquiring = fixture.Shared.AcquireAsync(new(item.EncodingKey, item.Encoded.Length), fixture.Source,
            AcquisitionPriority.Interactive).WaitAsync(Deadline);
        if (olderServed)
        {
            var receipt = await acquiring;
            Assert.Equal(fixture.OldKey, receipt.CdnConfig);
            Assert.Equal(fixture.DroppedArchive, receipt.Location.ArchiveKey);
        }
        else
            Assert.Equal("CONTENT_LOCATOR_INCOMPLETE", (await Assert.ThrowsAsync<DiscoveryException>(() => acquiring)).Code);
        Assert.Contains(fixture.Handler.Requests, url => url == InventoryFixture.Url("config", bad.Key));
        // A bad configuration is never retained and never marks the object corrupt.
        Assert.Null(await fixture.Catalog.FindConfigurationAsync(bad.Key, default));
        Assert.Empty(await fixture.Catalog.ReadKnownCorruptAsync([(item.EncodingKey, item.Encoded.Length)], default));
    }

    [Theory]
    [InlineData("corrupt", "LOCAL_CONFIGURATION_CORRUPT")]
    [InlineData("missing", "LOCAL_CONFIGURATION_MISSING")]
    [InlineData("wrong-bytes", "LOCAL_CONFIGURATION_CORRUPT")]
    [InlineData("receipt", "CATALOG_INTEGRITY")]
    public async Task Broken_saved_earlier_cdn_config_is_a_reported_local_failure_not_unavailable(string fault, string code)
    {
        await using var fixture = await Fixture.CreateAsync();
        // The earlier configuration was retained by an earlier lookup or open; its local copy is now broken.
        var bytes = fault == "wrong-bytes" ? "# authored other configuration\narchives =\n"u8.ToArray() : fixture.OldConfig;
        var sha = Hex(SHA256.HashData(bytes));
        await fixture.Objects.PublishAsync(bytes, sha, default);
        var url = fault == "receipt" ? "https://example.com/tpr/wow/config/" + fixture.OldKey : InventoryFixture.Url("config", fixture.OldKey);
        await fixture.Catalog.RecordConfigurationAsync(new(fixture.OldKey, sha, bytes.Length, url, "2026-10-19T00:00:00.0000000Z"), default);
        var path = Path.Combine(fixture.Path, "objects-v1", sha[..2], sha[2..]);
        if (fault == "corrupt") { var changed = bytes.ToArray(); changed[^2] ^= 1; await File.WriteAllBytesAsync(path, changed); }
        if (fault == "missing") File.Delete(path);

        var item = fixture.Data.PayloadA;
        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Shared.AcquireAsync(
            new(item.EncodingKey, item.Encoded.Length), fixture.Source, AcquisitionPriority.Interactive).WaitAsync(Deadline));
        Assert.Equal(code, failure.Code);
        Assert.False(failure.Retryable);
        // Never silently replaced from the CDN, never read as the CDN being unavailable, never the object's corruption.
        Assert.DoesNotContain(fixture.Handler.Requests, request => request == InventoryFixture.Url("config", fixture.OldKey));
        Assert.Null(await fixture.Catalog.FindEncodedReceiptAsync(item.EncodingKey, item.Encoded.Length, default));
        Assert.Empty(await fixture.Catalog.ReadKnownCorruptAsync([(item.EncodingKey, item.Encoded.Length)], default));
    }

    [Theory]
    [InlineData("config")]
    [InlineData("index")]
    public async Task Cancellation_during_the_fallback_stops_its_request_and_leaves_nothing_behind(string held)
    {
        await using var fixture = await Fixture.CreateAsync();
        var item = fixture.Data.PayloadA;
        var heldUrl = held == "config" ? InventoryFixture.Url("config", fixture.OldKey)
            : InventoryFixture.Url("data", fixture.DroppedArchive) + ".index";
        fixture.Gate.Held = heldUrl;
        using var cancellation = new CancellationTokenSource();
        var acquiring = fixture.Shared.AcquireAsync(new(item.EncodingKey, item.Encoded.Length), fixture.Source,
            AcquisitionPriority.Interactive, token: cancellation.Token);
        await fixture.Gate.Entered.Task.WaitAsync(Deadline);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => acquiring.WaitAsync(Deadline));
        // The last consumer's cancellation reached the held HTTP request before the producer retired.
        await fixture.Gate.Stopped.Task.WaitAsync(Deadline);
        Assert.Null(await fixture.Catalog.FindEncodedReceiptAsync(item.EncodingKey, item.Encoded.Length, default));
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Path, "staging-v1")));

        // Nothing was poisoned: the next request completes the same fallback.
        fixture.Gate.Held = null;
        var receipt = await fixture.Shared.AcquireAsync(new(item.EncodingKey, item.Encoded.Length), fixture.Source,
            AcquisitionPriority.Interactive).WaitAsync(Deadline);
        Assert.Equal(fixture.OldKey, receipt.CdnConfig);
    }

    [Fact]
    public async Task Fallback_runs_through_the_one_shared_producer_and_follows_promotion()
    {
        await using var fixture = await Fixture.CreateAsync();
        var item = fixture.Data.PayloadA; var other = fixture.Data.PayloadB;
        // Saturate background HTTP so neither bulk request can make progress on its own.
        using var first = await fixture.Resources.EnterHttpAsync(new Work(AcquisitionPriority.Background), default);
        var second = await fixture.Resources.EnterHttpAsync(new Work(AcquisitionPriority.Background), default);
        await using var bulk = fixture.Shared.Attach(new(item.EncodingKey, item.Encoded.Length), fixture.Source, AcquisitionPriority.Background);
        await using var unrelated = fixture.Shared.Attach(new(other.EncodingKey, other.Encoded.Length), fixture.Source, AcquisitionPriority.Background);

        // An interactive request for the queued object promotes that one producer, fallback included.
        var receipt = await fixture.Shared.AcquireAsync(new(item.EncodingKey, item.Encoded.Length), fixture.Source,
            AcquisitionPriority.Interactive).WaitAsync(Deadline);
        Assert.Equal(fixture.OldKey, receipt.CdnConfig);
        Assert.Equal(receipt.Sha256, (await bulk.Completion.WaitAsync(Deadline)).Sha256);
        Assert.Single(fixture.Handler.Requests, url => url == InventoryFixture.Url("data", item.EncodingKey));
        Assert.Single(fixture.Handler.Requests, url => url == InventoryFixture.Url("config", fixture.OldKey));
        Assert.Single(fixture.Handler.Ranges, span => span.Offset == 19 && span.Length == item.Encoded.Length);

        // Unrelated background work stayed behind the background quota the whole time.
        Assert.False(unrelated.Completion.IsCompleted);
        Assert.DoesNotContain(fixture.Handler.Requests, url => url == InventoryFixture.Url("data", other.EncodingKey));
        second.Dispose();
        Assert.Equal(fixture.NewKey, (await unrelated.Completion.WaitAsync(Deadline)).CdnConfig);
    }

    [Fact]
    public async Task Sealed_map_member_missing_from_current_archives_searches_only_earlier_ones()
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var plans = new EncodedSourcePlans(fixture.Catalog, fixture.Objects, fixture.Resources);
        await fixture.Shared.DisposeAsync();
        fixture.Shared = new(new OfficialEncodedObjectProducer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Resources, plans));
        await using var lease = await plans.AcquireAsync(fixture.Source);
        // Retention's sealed map covered the current archives: both objects are DL members it has no location for.
        await using var map = plans.RegisterMap(lease, (_, _, _) => Task.FromResult(new EncodedMapLookup(true, [])));
        var item = fixture.Data.PayloadA;
        var receipt = await fixture.Shared.AcquireAsync(new(item.EncodingKey, item.Encoded.Length), fixture.Source,
            AcquisitionPriority.Background).WaitAsync(Deadline);
        Assert.Equal(fixture.OldKey, receipt.CdnConfig);
        Assert.Equal(fixture.DroppedArchive, receipt.Location.ArchiveKey);
        Assert.DoesNotContain(fixture.Handler.Requests, url => url.StartsWith(InventoryFixture.Url("data", fixture.CurrentArchive), StringComparison.Ordinal));

        // A member no earlier configuration lists either stays a retryable not-located problem.
        var missing = Blte("authored member no archive holds\n");
        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => fixture.Shared.AcquireAsync(new(Hex(MD5.HashData(missing)), missing.Length),
            fixture.Source, AcquisitionPriority.Background).WaitAsync(Deadline));
        Assert.Equal("CONTENT_NOT_LOCATED", failure.Code);
        Assert.True(failure.Retryable);
    }

    [Fact]
    public async Task Earlier_cdn_configs_are_the_newest_fifteen_after_the_open_one()
    {
        await using var fixture = await Fixture.CreateAsync();
        var build = fixture.Request.BuildIdentity;
        Assert.Equal(new[] { fixture.OldKey }, await fixture.Catalog.ReadEarlierCdnConfigsAsync(build, fixture.NewKey, default));
        // A newer configuration is never an earlier one.
        Assert.Empty(await fixture.Catalog.ReadEarlierCdnConfigsAsync(build, fixture.OldKey, default));

        var keys = new List<string> { fixture.OldKey, fixture.NewKey };
        for (var index = 0; index < 16; index++)
        {
            var key = Hex(MD5.HashData(Encoding.UTF8.GetBytes($"authored republished cdn config {index}\n")));
            await fixture.CommitAsync(key, $"2026-10-19T{index + 2:00}:00:00.0000000Z");
            keys.Add(key);
        }
        var newest = keys[^1];
        var earlier = await fixture.Catalog.ReadEarlierCdnConfigsAsync(build, newest, default);
        Assert.Equal(DiscoveryCatalog.MaxEarlierCdnConfigs, earlier.Count);
        Assert.Equal(Enumerable.Reverse(keys).Skip(1).Take(15), earlier);
        Assert.Equal("CATALOG_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            fixture.Catalog.ReadEarlierCdnConfigsAsync(build, new string('a', 32), default))).Code);
    }

    private static byte[] Blte(string text) => [(byte)'B', (byte)'L', (byte)'T', (byte)'E', 0, 0, 0, 0, (byte)'N', .. Encoding.UTF8.GetBytes(text)];
    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    /// <summary>One authored archive holding one encoded object at offset 19, with its authenticated index.</summary>
    private static (string Key, byte[] Body, byte[] Index) Archive(byte[] encoded)
    {
        var encodingKey = MD5.HashData(encoded);
        var page = new byte[4096]; encodingKey.CopyTo(page, 0);
        BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(16), (uint)encoded.Length);
        BinaryPrimitives.WriteUInt32BigEndian(page.AsSpan(20), 19);
        var toc = new byte[24]; encodingKey.CopyTo(toc, 0);
        MD5.HashData(page).AsSpan(0, 8).CopyTo(toc.AsSpan(16));
        var footer = new byte[28]; MD5.HashData(toc).AsSpan(0, 8).CopyTo(footer);
        new byte[] { 1, 0, 0, 4, 4, 4, 16, 8 }.CopyTo(footer, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(footer.AsSpan(16), 1);
        MD5.HashData(footer.AsSpan(8, 20)).AsSpan(0, 8).CopyTo(footer.AsSpan(20));
        var body = new byte[19 + encoded.Length + 7]; encoded.CopyTo(body, 19);
        return (Hex(MD5.HashData(footer)), body, [.. page, .. toc, .. footer]);
    }

    /// <summary>Holds one exact URL until the request's own cancellation token fires, and says when it did.</summary>
    private sealed class HoldingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        internal volatile string? Held;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (Held is { } held && request.RequestUri!.AbsoluteUri == held)
            {
                Entered.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { Stopped.TrySetResult(); }
            }
            return await base.SendAsync(request, token);
        }
    }

    private sealed class Work(AcquisitionPriority priority) : IAcquisitionWorkContext
    {
        public AcquisitionWorkSnapshot Snapshot => new(0, priority, []);
        public Task WaitForChangeAsync(long observedRevision, CancellationToken token = default) => Task.Delay(Timeout.Infinite, token);
        public Task ReportAsync(AcquisitionProgress progress) => Task.CompletedTask;
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vibeminer-earlier-cdn-" + Guid.NewGuid().ToString("N"));
        internal InventoryFixture Data { get; } = InventoryFixture.Create();
        internal DiscoveryCatalog Catalog = null!;
        internal VerifiedObjectStore Objects = null!;
        internal SharedAcquisitionIntegrationTests.Handler Handler = null!;
        internal HoldingHandler Gate = null!;
        internal HttpClient Client = null!;
        internal AcquisitionResources Resources = new();
        internal AcquisitionCoordinator Shared = null!;
        internal WorkspaceRequest Request = null!;
        internal EncodedSourceContext Source = null!;
        internal string OldKey = "", NewKey = "", CurrentArchive = "", DroppedArchive = "";
        internal byte[] OldConfig = [];

        /// <summary>A bad earlier configuration, when given, is advertised between the old and the current one.</summary>
        internal static async Task<Fixture> CreateAsync((string Key, byte[] Served)? bad = null)
        {
            var fixture = new Fixture();
            var data = fixture.Data;
            // Payload A lives only in the archive the republished configuration dropped; B in the one it kept.
            data.Payloads.Remove(data.PayloadA.EncodingKey); data.Payloads.Remove(data.PayloadB.EncodingKey);
            var dropped = Archive(data.PayloadA.Encoded); var kept = Archive(data.PayloadB.Encoded);
            foreach (var archive in new[] { dropped, kept })
            {
                data.Responses.Add(InventoryFixture.Url("data", archive.Key), archive.Body);
                data.Responses.Add(InventoryFixture.Url("data", archive.Key) + ".index", archive.Index);
            }
            fixture.DroppedArchive = dropped.Key; fixture.CurrentArchive = kept.Key;
            fixture.OldConfig = Encoding.UTF8.GetBytes($"# authored earlier cdn config\narchives = {kept.Key} {dropped.Key}\n");
            var current = Encoding.UTF8.GetBytes($"# authored republished cdn config\narchives = {kept.Key}\n");
            fixture.OldKey = Hex(MD5.HashData(fixture.OldConfig)); fixture.NewKey = Hex(MD5.HashData(current));
            data.Responses.Add(InventoryFixture.Url("config", fixture.OldKey), fixture.OldConfig);
            data.Responses.Add(InventoryFixture.Url("config", fixture.NewKey), current);
            if (bad is { } between) data.Responses.Add(InventoryFixture.Url("config", between.Key), between.Served);

            fixture.Catalog = await DiscoveryCatalog.OpenAsync(fixture.Path);
            fixture.Objects = new(fixture.Path); fixture.Handler = new(data);
            fixture.Gate = new(fixture.Handler); fixture.Client = new(fixture.Gate);
            var build = await fixture.CommitAsync(fixture.OldKey, "2026-10-19T00:00:00.0000000Z");
            if (bad is { } middle) Assert.Equal(build, await fixture.CommitAsync(middle.Key, "2026-10-19T00:30:00.0000000Z"));
            Assert.Equal(build, await fixture.CommitAsync(fixture.NewKey, "2026-10-19T01:00:00.0000000Z"));
            fixture.Request = new("wow", build, "us");
            // The open uses the newest configuration; the earlier one was never retained locally.
            using (var configurations = new ConfigurationWorkspacePreparer(fixture.Catalog, fixture.Objects, fixture.Client))
                await configurations.PrepareAsync(fixture.Request, _ => Task.CompletedTask, default);
            fixture.Source = new("wow", build, "us", Hex(SHA256.HashData(current)));
            fixture.Shared = new(new OfficialEncodedObjectProducer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Resources));
            fixture.Handler.Requests.Clear();
            return fixture;
        }

        /// <summary>One discovery check in which the build is advertised with this CDN configuration.</summary>
        internal async Task<string> CommitAsync(string cdnKey, string at)
        {
            var original = Data.Discovery; var versions = original.Documents.Single(document => document.Role == "versions");
            var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(versions.Bytes).Replace(Data.CdnKey, cdnKey, StringComparison.Ordinal));
            var fields = MetadataParser.Parse(bytes).Rows.Single().Fields; var build = BuildIdentity.Create("wow", fields);
            var discovery = original with
            {
                Documents = [versions with { Bytes = bytes, Sha256 = Hex(SHA256.HashData(bytes)) },
                    original.Documents.Single(document => document.Role == "cdns")],
                Observations = [original.Observations.Single() with { Identity = build, OriginalFields = fields }]
            };
            var generation = await Catalog.BeginRefreshAsync("wow", default);
            await Catalog.CommitAsync("wow", generation, discovery, at, default);
            return build.Id;
        }

        public async ValueTask DisposeAsync()
        {
            if (Shared is not null) await Shared.DisposeAsync();
            Client?.Dispose();
            if (Objects is not null) await Objects.DisposeAsync();
            if (Catalog is not null) await Catalog.DisposeAsync();
            Directory.Delete(Path, true);
        }
    }
}
