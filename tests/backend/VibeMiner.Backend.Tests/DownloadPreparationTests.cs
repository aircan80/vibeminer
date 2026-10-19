using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class DownloadPreparationTests
{
    [Fact]
    public async Task DirectManifestOpensWithConfigurationsAndDownloadOnlyWithoutResearchSqlExpansion()
    {
        await using var h = await Harness.CreateAsync();
        var opened = await h.OpenAsync();
        var summary = Assert.IsType<DownloadSummary>(opened.Download);
        Assert.Equal("home-download-v1", opened.PreparationProfile);
        Assert.Equal("downloadManifestAllVariants", summary.Scope);
        Assert.Equal("notPrepared", opened.InventoryState);
        Assert.Null(opened.Inventory);
        Assert.Null(opened.DownloadProblem);
        Assert.Equal("2", summary.ObjectCount);
        Assert.Equal(N(h.Data.ExpectedEncodedBytes), summary.TotalEncodedBytes);
        Assert.Equal("0", summary.VerifiedEncodedBytes);
        Assert.Equal(summary.TotalEncodedBytes, summary.RemainingEncodedBytes);
        Assert.True(DateTimeOffset.TryParse(summary.AssessedAt, out _));
        Assert.Equal("download", Assert.Single(opened.Metadata!).Role);
        Assert.Equal(3, h.Handler.Requests.Count);
        Assert.Contains(InventoryFixture.Url("data", h.Data.Metadata["download"].EncodingKey), h.Handler.Requests);
        foreach (var role in new[] { "encoding", "root", "install", "size" })
            Assert.DoesNotContain(InventoryFixture.Url("data", h.Data.Metadata[role].EncodingKey), h.Handler.Requests);
        Assert.Equal(0, await h.ScalarAsync("SELECT count(*) FROM en_content"));
        Assert.Equal(0, await h.ScalarAsync("SELECT count(*) FROM en_candidates"));
        Assert.Equal(0, await h.ScalarAsync("SELECT count(*) FROM en_objects"));
        Assert.Equal(0, await h.ScalarAsync("SELECT count(*) FROM inventory_origins"));
        Assert.Equal(0, await h.ScalarAsync("SELECT count(*) FROM inventory_generations"));
        Assert.Equal(1, await h.ScalarAsync("SELECT count(*) FROM download_artifacts"));
        Assert.Equal(1, await h.ScalarAsync("SELECT count(*) FROM workspace_admissions"));
        Assert.All(h.Progress, update => Assert.NotNull(update.Step));
        Assert.All(h.Progress.Where(update => update.Step!.Total is not null), update => Assert.Equal(5, update.Step!.Total));
        Assert.Equal(5, h.Progress[^1].Step!.Index);
        Assert.Empty(h.StagingFiles());
    }

    [Fact]
    public async Task OpeningLeavesUnfinishedLegacyResearchRowsIntact()
    {
        await using var h = await Harness.CreateAsync();
        var context = await h.Catalog.ResolveOpeningAsync(h.Request, default);
        var generation = await h.Catalog.StartInventoryAsync(context, default);
        var key = new string('a', 32);
        await h.Catalog.AddEncodingAsync(generation, [new(key, 19, [key])], [new(key, 11)], default);
        await h.Catalog.AddOriginsAsync(generation,
            [new(1, "root", null, "12345", key, null, 0, null, null, "0", "0", null, "authored legacy staging")], default);
        var before = await h.ScalarAsync("SELECT count(*) FROM en_content") + await h.ScalarAsync("SELECT count(*) FROM inventory_origins");
        var opened = await h.OpenAsync();
        Assert.NotNull(opened.Download);
        Assert.Equal(before, await h.ScalarAsync("SELECT count(*) FROM en_content") + await h.ScalarAsync("SELECT count(*) FROM inventory_origins"));
        Assert.Equal(1, await h.ScalarAsync($"SELECT count(*) FROM inventory_generations WHERE id={generation} AND state='staging'"));
        Assert.DoesNotContain(h.Progress, update => update.Stage.Contains("Cleaning", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(h.Progress, update => update.Stage.Contains("Resolving root", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RestartReopensOfflineWithSameLedgerAndOriginalSourceReceipts()
    {
        await using var h = await Harness.CreateAsync();
        var first = await h.OpenAsync();
        h.Handler.BlockAll = true;
        await h.RestartAsync();
        var second = await h.OpenAsync();
        Assert.Equal(first.Download!.LedgerId, second.Download!.LedgerId);
        Assert.Equal(first.Download.TotalEncodedBytes, second.Download.TotalEncodedBytes);
        Assert.Equal(first.Metadata!.Select(receipt => (receipt.Sha256, receipt.SourceUrl, receipt.RetrievedAt)),
            second.Metadata!.Select(receipt => (receipt.Sha256, receipt.SourceUrl, receipt.RetrievedAt)));
        Assert.Equal(first.Configurations.Select(receipt => (receipt.Sha256, receipt.SourceUrl, receipt.RetrievedAt)),
            second.Configurations.Select(receipt => (receipt.Sha256, receipt.SourceUrl, receipt.RetrievedAt)));
        Assert.Equal(3, h.Handler.Requests.Count);
        Assert.Equal(1, await h.ScalarAsync("SELECT count(*) FROM download_artifacts"));
        Assert.Equal(0, await h.ScalarAsync("SELECT count(*) FROM inventory_generations"));
        Assert.Empty(h.StagingFiles());
    }

    [Fact]
    public async Task AnAbsentDownloadDeclarationOpensWithExplicitUnknownSizeAndNoInventedCatalog()
    {
        await using var h = await Harness.CreateAsync(text => Lines(text, line =>
            !line.StartsWith("download =", StringComparison.Ordinal) && !line.StartsWith("download-size =", StringComparison.Ordinal)));
        var opened = await h.OpenAsync();
        Assert.Null(opened.Download);
        Assert.Equal("DOWNLOAD_MANIFEST_ABSENT", opened.DownloadProblem!.Code);
        Assert.Equal("unknown", opened.RetentionState);
        Assert.Equal("notPrepared", opened.InventoryState);
        Assert.Null(opened.Inventory);
        Assert.Equal(2, h.Handler.Requests.Count);
        Assert.Equal(0, await h.ScalarAsync("SELECT count(*) FROM metadata_objects"));
        Assert.Equal(0, await h.ScalarAsync("SELECT count(*) FROM download_artifacts"));
        Assert.Equal(2, h.Progress[^1].Step!.Total);
        Assert.Equal(2, h.Progress[^1].Step!.Index);
    }

    [Fact]
    public async Task AuthenticUnsupportedDownloadFormatOpensWithExplicitUnavailableSize()
    {
        var data = InventoryFixture.Create();
        var original = data.Metadata["download"];
        var decoded = original.Decoded.ToArray(); decoded[2] = 9;
        var unsupported = Reencode("download", decoded);
        data.Responses[InventoryFixture.Url("data", unsupported.EncodingKey)] = unsupported.Encoded;
        await using var h = await Harness.CreateAsync(text => text.Replace(original.ContentKey, unsupported.ContentKey,
            StringComparison.Ordinal).Replace(original.EncodingKey, unsupported.EncodingKey, StringComparison.Ordinal), data: data);
        var opened = await h.OpenAsync();
        Assert.Null(opened.Download);
        Assert.Null(opened.Inventory);
        Assert.Equal("DOWNLOAD_MANIFEST_UNSUPPORTED", opened.DownloadProblem!.Code);
        Assert.Equal("unknown", opened.RetentionState);
        Assert.Equal(3, h.Handler.Requests.Count);
        Assert.Equal(0, await h.ScalarAsync("SELECT count(*) FROM download_artifacts"));
        Assert.Equal(1, await h.ScalarAsync("SELECT count(*) FROM metadata_objects"));
        Assert.Equal(1, await h.ScalarAsync("SELECT count(*) FROM workspace_admissions"));
        Assert.Empty(h.StagingFiles());
    }

    [Fact]
    public async Task ContentOnlyDownloadSourceUsesNativeEncodingPagesAndLeavesRootInstallAndSizeDeferred()
    {
        var data = InventoryFixture.Create();
        var dl = data.Metadata["download"];
        await using var h = await Harness.CreateAsync(text => text.Replace(
            $"download = {dl.ContentKey} {dl.EncodingKey}", $"download = {dl.ContentKey}", StringComparison.Ordinal), data: data);
        var opened = await h.OpenAsync();
        Assert.Equal(N(data.ExpectedEncodedBytes), opened.Download!.TotalEncodedBytes);
        Assert.Equal(new[] { "download", "encoding" }, opened.Metadata!.Select(receipt => receipt.Role).Order(StringComparer.Ordinal));
        Assert.Equal(4, h.Handler.Requests.Count);
        Assert.Contains(InventoryFixture.Url("data", data.Metadata["encoding"].EncodingKey), h.Handler.Requests);
        Assert.Equal(1, await h.ScalarAsync("SELECT count(*) FROM resolver_artifacts"));
        Assert.Equal(0, await h.ScalarAsync("SELECT count(*) FROM en_content"));
        Assert.Equal(0, await h.ScalarAsync("SELECT count(*) FROM inventory_origins"));
        Assert.Equal(7, h.Progress[^1].Step!.Total);
        Assert.Equal(7, h.Progress[^1].Step!.Index);
        h.Handler.BlockAll = true;
        await h.RestartAsync();
        var second = await h.OpenAsync();
        Assert.Equal(opened.Download.LedgerId, second.Download!.LedgerId);
        Assert.Equal(4, h.Handler.Requests.Count);
        Assert.Empty(h.StagingFiles());
    }

    [Fact]
    public async Task CorruptDownloadedMetadataCannotAdmitOrAcknowledgeTheBuildAndRetryReusesConfigurations()
    {
        await using var h = await Harness.CreateAsync();
        h.Handler.CorruptKey = h.Data.Metadata["download"].EncodingKey;
        Assert.Equal("BLTE_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => h.OpenAsync())).Code);
        Assert.Equal(0, await h.ScalarAsync("SELECT count(*) FROM workspace_admissions"));
        Assert.Equal(0, await h.ScalarAsync("SELECT count(*) FROM open_acknowledgements"));
        Assert.Equal(0, await h.ScalarAsync("SELECT count(*) FROM download_artifacts"));
        Assert.Empty(h.StagingFiles());
        h.Handler.CorruptKey = null;
        Assert.NotNull((await h.OpenAsync()).Download);
        Assert.Equal(4, h.Handler.Requests.Count);
        Assert.Equal(1, await h.ScalarAsync("SELECT count(*) FROM workspace_admissions"));
    }

    [Fact]
    public async Task CancellationAtFinalStagePreservesVerifiedArtifactWithoutPublishingAnAdmission()
    {
        await using var h = await Harness.CreateAsync();
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Preparer.PrepareWithProgressAsync(h.Request,
            update =>
            {
                if (update.Stage == "Opening build overview") cancel.Cancel();
                return Task.CompletedTask;
            }, cancel.Token));
        Assert.Equal(0, await h.ScalarAsync("SELECT count(*) FROM workspace_admissions"));
        Assert.Equal(0, await h.ScalarAsync("SELECT count(*) FROM open_acknowledgements"));
        Assert.Equal(1, await h.ScalarAsync("SELECT count(*) FROM download_artifacts"));
        Assert.Empty(h.StagingFiles());
        h.Handler.BlockAll = true;
        Assert.NotNull((await h.OpenAsync()).Download);
        Assert.Equal(3, h.Handler.Requests.Count);
    }

    [Theory]
    [InlineData(false, "LOCAL_OBJECT_MISSING")]
    [InlineData(true, "LOCAL_OBJECT_CORRUPT")]
    public async Task AnAdmittedLedgerWithMissingOrChangedBytesCannotPublishAnotherSuccessfulAdmission(bool corrupt, string code)
    {
        await using var h = await Harness.CreateAsync();
        var first = await h.OpenAsync();
        var path = h.ObjectPath(first.Download!.LedgerId);
        if (corrupt)
        { var bytes = await File.ReadAllBytesAsync(path); bytes[^1] ^= 1; await File.WriteAllBytesAsync(path, bytes); }
        else File.Delete(path);
        h.Handler.BlockAll = true;
        Assert.Equal(code, (await Assert.ThrowsAsync<DiscoveryException>(() => h.OpenAsync())).Code);
        Assert.Equal(1, await h.ScalarAsync("SELECT count(*) FROM workspace_admissions"));
        Assert.Equal(3, h.Handler.Requests.Count);
        Assert.Empty(h.StagingFiles());
    }

    [Fact]
    public async Task ExactRetainedPayloadGetsDatedCreditAndCorruptionRemovesCreditOnReassessment()
    {
        await using var h = await Harness.CreateAsync();
        var payload = h.Data.PayloadA;
        h.Handler.Additional[InventoryFixture.Url("data", payload.EncodingKey)] = payload.Encoded;
        var acquisition = new MetadataAcquirer(h.Client, h.Objects);
        await using (var source = await acquisition.AcquireAsync(new("https://us.cdn.blizzard.com/tpr/wow/"),
            new("root", payload.ContentKey, payload.EncodingKey, payload.Encoded.Length, payload.Decoded.Length),
            _ => Task.CompletedTask, default))
            await h.Catalog.RecordMetadataAsync(source.Receipt, default);
        var opened = await h.OpenAsync();
        Assert.Equal(N(payload.Encoded.Length), opened.Download!.VerifiedEncodedBytes);
        Assert.Equal(N(h.Data.ExpectedEncodedBytes - payload.Encoded.Length), opened.Download.RemainingEncodedBytes);
        var page = await h.Query.ReadDownloadsAsync(new("wow", h.Request.BuildIdentity, "us", opened.Download.LedgerId), default);
        Assert.Equal(opened.Download.AssessedAt, page.AssessedAt);
        Assert.Equal(payload.EncodingKey, Assert.Single(page.Entries, entry => entry.VerifiedAtAssessment).EncodingKey);
        var receipt = (await h.Catalog.FindMetadataAsync(payload.EncodingKey, default))!;
        var path = h.ObjectPath(receipt.Sha256);
        var changed = await File.ReadAllBytesAsync(path); changed[^1] ^= 1; await File.WriteAllBytesAsync(path, changed);
        h.Handler.BlockAll = true;
        var reassessed = await h.OpenAsync();
        Assert.Equal("0", reassessed.Download!.VerifiedEncodedBytes);
        Assert.Equal(reassessed.Download.TotalEncodedBytes, reassessed.Download.RemainingEncodedBytes);
        var next = await h.Query.ReadDownloadsAsync(new("wow", h.Request.BuildIdentity, "us", reassessed.Download.LedgerId), default);
        Assert.Equal(reassessed.Download.AssessedAt, next.AssessedAt);
        Assert.All(next.Entries, entry => Assert.False(entry.VerifiedAtAssessment));
        Assert.Equal(4, h.Handler.Requests.Count);
    }

    [Fact]
    public async Task DownloadPagesUseExactContextStableOrdinalsAndBoundedCursorsWithoutNetwork()
    {
        await using var h = await Harness.CreateAsync();
        var opened = await h.OpenAsync(); h.Handler.BlockAll = true;
        var request = new DownloadReadRequest("wow", h.Request.BuildIdentity, "us", opened.Download!.LedgerId, 1);
        var first = await h.Query.ReadDownloadsAsync(request, default);
        Assert.Equal("2", first.TotalCount); Assert.Single(first.Entries); Assert.Equal("1", first.NextCursor);
        var second = await h.Query.ReadDownloadsAsync(request with { After = first.NextCursor }, default);
        Assert.Single(second.Entries); Assert.Null(second.NextCursor);
        Assert.Equal(new[] { "1", "2" }, first.Entries.Concat(second.Entries).Select(entry => entry.Id));
        Assert.Equal(h.Data.Payloads.Keys.Order(StringComparer.Ordinal),
            first.Entries.Concat(second.Entries).Select(entry => entry.EncodingKey));
        Assert.Empty((await h.Query.ReadDownloadsAsync(request with { After = "2" }, default)).Entries);
        foreach (var wrong in new[] { request with { SourceRegion = "eu" }, request with { ProductId = "wowt" },
            request with { BuildIdentity = new string('a', 64) }, request with { LedgerId = new string('b', 64) } })
            Assert.Equal("DOWNLOAD_NOT_READY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
                h.Query.ReadDownloadsAsync(wrong, default))).Code);
        foreach (var invalid in new[] { request with { Limit = 101 }, request with { After = "01" }, request with { After = "-1" } })
            Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() =>
                h.Query.ReadDownloadsAsync(invalid, default))).Code);
        Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            h.Query.ReadDownloadsAsync(request with { After = "3" }, default))).Code);
        Assert.Equal(3, h.Handler.Requests.Count);
    }

    [Fact]
    public async Task SameSizeUnrelatedRetainedKeyCannotReplaceAnAdmittedManifestAssessment()
    {
        await using var h = await Harness.CreateAsync();
        var a = h.Data.PayloadA;
        var changed = a.Decoded.ToArray(); changed[0] ^= 1;
        var unrelated = Reencode("root", changed);
        var acquisition = new MetadataAcquirer(h.Client, h.Objects);
        foreach (var item in new[] { a, unrelated })
        {
            h.Handler.Additional[InventoryFixture.Url("data", item.EncodingKey)] = item.Encoded;
            await using var source = await acquisition.AcquireAsync(new("https://us.cdn.blizzard.com/tpr/wow/"),
                new("root", item.ContentKey, item.EncodingKey, item.Encoded.Length, item.Decoded.Length),
                _ => Task.CompletedTask, default);
            await h.Catalog.RecordMetadataAsync(source.Receipt, default);
        }
        Assert.Equal(a.Encoded.Length, unrelated.Encoded.Length);
        var opened = await h.OpenAsync();
        Assert.Equal(N(a.Encoded.Length), opened.Download!.VerifiedEncodedBytes);
        await h.ExecuteAsync("UPDATE download_admissions SET verified_keys=$keys",
            ("$keys", JsonSerializer.Serialize(new[] { unrelated.EncodingKey })));
        h.Handler.BlockAll = true;
        Assert.Equal("DOWNLOAD_CATALOG_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            h.Query.ReadDownloadsAsync(new("wow", h.Request.BuildIdentity, "us", opened.Download.LedgerId), default))).Code);
        Assert.Equal(5, h.Handler.Requests.Count);
    }

    [Fact]
    public async Task SamePhysicalConfigurationInTwoRolesCountsMetadataBytesOnce()
    {
        await using var h = await Harness.CreateAsync(sharedConfiguration: true);
        var opened = await h.OpenAsync();
        Assert.Equal(2, opened.Configurations.Count);
        Assert.Single(opened.Configurations.Select(receipt => receipt.Sha256).Distinct());
        var expected = opened.Configurations[0].ByteLength + h.Data.Metadata["download"].Encoded.Length;
        Assert.Equal(N(expected), opened.Download!.MetadataEncodedBytes);
        Assert.Equal(2, h.Handler.Requests.Count); // One physical config, one download manifest.
    }

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Lines(string text, Func<string, bool> keep) => string.Join('\n', text.Split('\n').Where(keep));
    private static InventoryFixture.FixtureObject Reencode(string role, byte[] decoded)
    {
        byte[] encoded = [.. "BLTE"u8.ToArray(), 0, 0, 0, 0, (byte)'N', .. decoded];
        return new(role, Convert.ToHexStringLower(MD5.HashData(decoded)), Convert.ToHexStringLower(MD5.HashData(encoded)), decoded, encoded);
    }

    private sealed class Handler(InventoryFixture data) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public Dictionary<string, byte[]> Additional { get; } = new(StringComparer.Ordinal);
        public bool BlockAll;
        public string? CorruptKey;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (BlockAll) throw new InvalidOperationException("Authored offline test forbids network requests.");
            var url = request.RequestUri!.AbsoluteUri; Requests.Add(url);
            if (!Additional.TryGetValue(url, out var bytes) && !data.Responses.TryGetValue(url, out bytes))
                throw new InvalidOperationException("Unexpected authored source request: " + url);
            bytes = bytes.ToArray();
            if (CorruptKey is not null && url.EndsWith(CorruptKey, StringComparison.Ordinal)) bytes[^1] ^= 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { RequestMessage = request, Content = new ByteArrayContent(bytes) });
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "vibeminer-download-" + Guid.NewGuid().ToString("N"));
        public InventoryFixture Data { get; private set; } = null!;
        public WorkspaceRequest Request { get; private set; } = null!;
        public DiscoveryCatalog Catalog { get; private set; } = null!;
        public VerifiedObjectStore Objects { get; private set; } = null!;
        public Handler Handler { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;
        public DownloadWorkspacePreparer Preparer { get; private set; } = null!;
        public DownloadQuery Query { get; private set; } = null!;
        public List<WorkspacePreparationProgress> Progress { get; } = [];

        public static async Task<Harness> CreateAsync(Func<string, string>? configuration = null,
            InventoryFixture? data = null, bool sharedConfiguration = false)
        {
            data ??= InventoryFixture.Create();
            var config = Encoding.UTF8.GetBytes(configuration?.Invoke(Encoding.UTF8.GetString(data.BuildConfig)) ?? Encoding.UTF8.GetString(data.BuildConfig));
            var key = Convert.ToHexStringLower(MD5.HashData(config));
            data.Responses[InventoryFixture.Url("config", key)] = config;
            var original = data.Discovery;
            var document = original.Documents.Single(value => value.Role == "versions");
            var versions = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(document.Bytes)
                .Replace(data.BuildKey, key, StringComparison.Ordinal)
                .Replace(data.CdnKey, sharedConfiguration ? key : data.CdnKey, StringComparison.Ordinal));
            var fields = MetadataParser.Parse(versions).Rows.Single().Fields;
            var identity = BuildIdentity.Create("wow", fields);
            var discovery = original with
            {
                Documents = [document with { Bytes = versions, Sha256 = Convert.ToHexStringLower(SHA256.HashData(versions)) },
                    original.Documents.Single(value => value.Role == "cdns")],
                Observations = [original.Observations.Single() with { Identity = identity, OriginalFields = fields }]
            };
            var h = new Harness { Data = data, Request = new("wow", identity.Id, "us") };
            h.Catalog = await DiscoveryCatalog.OpenAsync(h.Directory); h.Objects = new(h.Directory);
            h.Handler = new(data); h.Client = new(h.Handler);
            h.Preparer = new(h.Catalog, h.Objects, h.Client); h.Query = new(h.Catalog, h.Objects);
            var generation = await h.Catalog.BeginRefreshAsync("wow", default);
            await h.Catalog.CommitAsync("wow", generation, discovery, DateTimeOffset.UtcNow.ToString("O"), default);
            return h;
        }

        public Task<WorkspaceSnapshot> OpenAsync() => Preparer.PrepareWithProgressAsync(Request,
            update => { Progress.Add(update); return Task.CompletedTask; }, default);
        public string ObjectPath(string sha) => Path.Combine(Directory, "objects-v1", sha[..2], sha[2..]);
        public IEnumerable<string> StagingFiles() => System.IO.Directory.Exists(Path.Combine(Directory, "staging-v1"))
            ? System.IO.Directory.EnumerateFiles(Path.Combine(Directory, "staging-v1")) : [];
        public async Task RestartAsync()
        {
            Preparer.Dispose(); await Objects.DisposeAsync(); await Catalog.DisposeAsync();
            Catalog = await DiscoveryCatalog.OpenAsync(Directory); Objects = new(Directory);
            Preparer = new(Catalog, Objects, Client); Query = new(Catalog, Objects);
        }
        public async Task<long> ScalarAsync(string sql)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(Directory, "discovery-v1.sqlite3"), Pooling = false }.ToString());
            await connection.OpenAsync();
            using var command = connection.CreateCommand(); command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }
        public async Task ExecuteAsync(string sql, params (string Name, object Value)[] values)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(Directory, "discovery-v1.sqlite3"), Pooling = false }.ToString());
            await connection.OpenAsync();
            using var command = connection.CreateCommand(); command.CommandText = sql;
            foreach (var (name, value) in values) command.Parameters.AddWithValue(name, value);
            await command.ExecuteNonQueryAsync();
        }
        public async ValueTask DisposeAsync()
        { Preparer.Dispose(); Client.Dispose(); await Objects.DisposeAsync(); await Catalog.DisposeAsync(); System.IO.Directory.Delete(Directory, true); }
    }
}
