using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

/// <summary>D066: the scope is the download manifest plus the encoded object of every ROOT-listed file it leaves out, measured after Open.</summary>
public sealed class RootScopeTests
{
    private static string Key(string label) => Convert.ToHexStringLower(MD5.HashData(System.Text.Encoding.ASCII.GetBytes(label)));

    [Fact]
    public async Task Uncovered_files_add_their_first_encoded_object_and_unresolvable_ones_are_counted_apart()
    {
        // Files: in the manifest; outside it; several EKeys, none in the manifest; several EKeys, one in the manifest;
        // listed twice; not in ENCODING; in ENCODING but its EKey has no encoded size.
        var inManifest = (C: Key("c-in"), E: Key("e-in"));
        var outside = (C: Key("c-out"), E: Key("e-out"));
        var several = (C: Key("c-several"), E: new[] { Key("e-several-1"), Key("e-several-2") });
        var covered = (C: Key("c-covered"), E: new[] { Key("e-covered-out"), inManifest.E });
        var absent = Key("c-absent");
        var unsized = (C: Key("c-unsized"), E: Key("e-unsized"));
        var root = Root([inManifest.C, outside.C, several.C, covered.C, outside.C, absent, unsized.C]);
        var encoding = EncodingManifest(
            [(inManifest.C, [inManifest.E]), (outside.C, [outside.E]), (several.C, several.E), (covered.C, covered.E), (unsized.C, [unsized.E])],
            [(inManifest.E, 10), (outside.E, 20), (several.E[0], 30), (several.E[1], 31), (covered.E[0], 40)]);
        using var directory = new TempDirectory(); System.IO.Directory.CreateDirectory(directory.Path);
        var manifest = Path.Combine(directory.Path, "manifest.run");
        await File.WriteAllBytesAsync(manifest, Rows([(inManifest.E, 10)]));
        var scope = await RootScopes.ComputeAsync(new MemoryStream(root), root.Length, new MemoryStream(encoding), encoding.Length,
            _ => Task.FromResult(new DownloadLedgerOrderedCursor(new LedgerRunReader(new FileStream(manifest, FileMode.Open, FileAccess.Read), 24),
                new(new string('0', 64), 56, 1, 10))), _ => Task.CompletedTask, default);
        var expected = new[] { (outside.E, 20L), (several.E[0], 30L) }.OrderBy(row => row.Item1, StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, scope.Objects.Select(value => (value.Key, value.Bytes)));
        // Distinct files: in, outside, several, covered, absent, unsized. Absent and unsized cannot be resolved.
        Assert.Equal((6L, 2L), (scope.DistinctContent, scope.UnresolvableContent));
    }

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);
    private static byte[] Rows((string Key, long Bytes)[] rows)
    {
        var bytes = new byte[24 * rows.Length];
        for (var i = 0; i < rows.Length; i++)
        { Convert.FromHexString(rows[i].Key).CopyTo(bytes, 24 * i); BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(24 * i + 16), rows[i].Bytes); }
        return bytes;
    }
    /// <summary>One unnamed TSFM v2 block listing the content keys in order, FileDataIDs 1, 2, 3, …</summary>
    internal static byte[] Root(string[] contentKeys)
    {
        using var output = new MemoryStream(); using var writer = new BinaryWriter(output);
        writer.Write("TSFM"u8); writer.Write(24u); writer.Write(2u); writer.Write((uint)contentKeys.Length); writer.Write(0u); writer.Write(0u);
        writer.Write((uint)contentKeys.Length); writer.Write(1u); writer.Write(0x10000000u); writer.Write(0u); writer.Write((byte)0);
        writer.Write(1u); for (var i = 1; i < contentKeys.Length; i++) writer.Write(0u);
        foreach (var key in contentKeys) writer.Write(Convert.FromHexString(key));
        writer.Flush(); return output.ToArray();
    }
    /// <summary>A one-page-per-table ENCODING with these content entries (several EKeys allowed) and encoded sizes.</summary>
    internal static byte[] EncodingManifest((string Content, string[] Keys)[] contents, (string Key, long Bytes)[] encodings)
    {
        const int pageBytes = 1024;
        var contentPage = new byte[pageBytes]; var encodingPage = new byte[pageBytes];
        var sortedContents = contents.OrderBy(item => item.Content, StringComparer.Ordinal).ToArray();
        var sortedEncodings = encodings.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray();
        using (var page = new MemoryStream(contentPage, writable: true))
            foreach (var item in sortedContents)
            {
                page.WriteByte((byte)item.Keys.Length); Big(page, 100, 5); page.Write(Convert.FromHexString(item.Content));
                foreach (var key in item.Keys) page.Write(Convert.FromHexString(key));
            }
        using (var page = new MemoryStream(encodingPage, writable: true))
            foreach (var item in sortedEncodings) { page.Write(Convert.FromHexString(item.Key)); Big(page, 0, 4); Big(page, item.Bytes, 5); }
        byte[] specs = "b:*=n\0"u8.ToArray();
        using var output = new MemoryStream();
        output.Write("EN"u8); output.Write([1, 16, 16]); Big(output, 1, 2); Big(output, 1, 2);
        Big(output, 1, 4); Big(output, 1, 4); output.WriteByte(0); Big(output, specs.Length, 4); output.Write(specs);
        output.Write(Convert.FromHexString(sortedContents[0].Content)); output.Write(MD5.HashData(contentPage)); output.Write(contentPage);
        output.Write(Convert.FromHexString(sortedEncodings[0].Key)); output.Write(MD5.HashData(encodingPage)); output.Write(encodingPage);
        return output.ToArray();
    }
    private static void Big(Stream output, long value, int bytes)
    {
        Span<byte> buffer = stackalloc byte[8]; BinaryPrimitives.WriteUInt64BigEndian(buffer, (ulong)value); output.Write(buffer[(8 - bytes)..]);
    }
    [Fact]
    public async Task Open_finishes_before_the_scope_and_the_measured_scope_arrives_later_with_root_only_files()
    {
        await using var h = await Scope.Create(InventoryFixture.Create(rootGap: true));
        var opened = await h.Open();
        // Open admits the manifest only and never reads ENCODING or ROOT.
        Assert.Equal(("downloadManifestAllVariants", "2"), (opened.Download!.Scope, opened.Download.ObjectCount));
        foreach (var role in new[] { "encoding", "root" }) Assert.DoesNotContain(InventoryFixture.Url("data", h.Data.Metadata[role].EncodingKey), h.Handler.Requests);
        await h.Drain();
        var measuring = (await h.Summary()).Download!;
        Assert.Equal(("measuring", StoredCountState.Counting, "downloadManifestAllVariants"), (measuring.ScopeState, measuring.CountState, measuring.Scope));
        Assert.Equal(RootScopeWorker.RootScopeOutcome.Measured, await h.Worker.MeasureNextAsync(default));
        await h.Drain();
        var measured = (await h.Summary()).Download!;
        var rootOnly = h.Data.RootOnly!;
        Assert.Equal(("measured", StoredCountState.Current, "downloadManifestAndRootAllVariants", 3L, 1L),
            (measured.ScopeState, measured.CountState, measured.Scope, measured.ObjectCount, measured.UnresolvableRootContent));
        Assert.Equal(h.Data.ExpectedEncodedBytes + rootOnly.Encoded.Length, measured.TotalEncodedBytes);
        // Encoding and ROOT were read in the background; ROOT is not a manifest member here, so nothing is stored yet.
        Assert.Equal(0, measured.StoredObjects);
        Assert.Equal(RootScopeWorker.RootScopeOutcome.Idle, await h.Worker.MeasureNextAsync(default));
        Assert.Equal(1, h.Settled);
    }

    [Fact]
    public async Task Download_all_waits_for_the_scope_and_then_covers_every_listed_file()
    {
        await using var h = await Scope.Create(InventoryFixture.Create(rootGap: true));
        var opened = await h.Open();
        var target = new RetentionTarget("wow", h.Data.BuildIdentity, "us", opened.Download!.LedgerId);
        var store = (IRetentionStore)h.Catalog; var driver = (IRetentionDriverCatalog)h.Catalog;
        var queued = await store.SetAsync(new(target, RetentionAction.DownloadAll), default);
        Assert.Equal(("Waiting for every file the build lists to be measured", 2L), (queued.Progress!.Stage, queued.ObjectCount));
        Assert.Null(await driver.ClaimNextRetentionAsync(default));
        Assert.Equal(RootScopeWorker.RootScopeOutcome.Measured, await h.Worker.MeasureNextAsync(default));
        var waiting = await store.ReadAsync(target, default);
        Assert.Equal(("downloadManifestAndRootAllVariants", 3L), (waiting.Scope, waiting.ObjectCount));
        var job = (await driver.ClaimNextRetentionAsync(default))!;
        Assert.Equal((3L, "downloadManifestAndRootAllVariants", target), (job.Plan.Ledger.ObjectCount, job.Plan.Scope, job.Plan.Target));
        Assert.NotEqual(target.LedgerId, job.Plan.Ledger.Sha256);
    }

    [Fact]
    public async Task A_cancel_or_shutdown_mid_measurement_leaves_it_pending_and_a_restart_resumes_it()
    {
        await using var h = await Scope.Create(InventoryFixture.Create(rootGap: true));
        await h.Open();
        h.Handler.Hold = InventoryFixture.Url("data", h.Data.Metadata["root"].EncodingKey);
        using var cancel = new CancellationTokenSource();
        var measuring = h.Worker.MeasureNextAsync(cancel.Token);
        await h.Handler.Held.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => measuring);
        Assert.Equal("pending", await h.State());
        // A shutdown mid-measurement is the same cancellation; the next engine run starts over from the pending row.
        h.Handler.Hold = null;
        await h.Restart();
        Assert.Equal("pending", await h.State());
        Assert.Equal(RootScopeWorker.RootScopeOutcome.Measured, await h.Worker.MeasureNextAsync(default));
        Assert.Equal("measured", await h.State());
    }

    [Fact]
    public async Task An_unreadable_file_list_falls_back_to_the_manifest_and_says_why()
    {
        await using var h = await Scope.Create(InventoryFixture.Create(legacyRoot: true));
        await h.Open();
        Assert.Equal(RootScopeWorker.RootScopeOutcome.ManifestOnly, await h.Worker.MeasureNextAsync(default));
        await h.Drain();
        var summary = (await h.Summary()).Download!;
        Assert.Equal(("manifestOnly", "downloadManifestAllVariants", StoredCountState.Current, "ROOT_UNSUPPORTED", false),
            (summary.ScopeState, summary.Scope, summary.CountState, summary.ScopeProblem!.Code, summary.ScopeProblem.Retryable));
        Assert.Equal(RootScopeWorker.RootScopeOutcome.Idle, await h.Worker.MeasureNextAsync(default));
        // A manifest-only outcome is terminal too, so the retention driver is woken for a waiting Download all.
        Assert.Equal(1, h.Settled);
    }

    [Fact]
    public async Task Failed_attempts_are_bounded_then_the_next_open_retries()
    {
        await using var h = await Scope.Create(InventoryFixture.Create(rootGap: true));
        await h.Open();
        h.Handler.Fail = InventoryFixture.Url("data", h.Data.Metadata["root"].EncodingKey);
        for (var attempt = 1; attempt < DiscoveryCatalog.MaxRootScopeAttempts; attempt++)
        {
            Assert.Equal(RootScopeWorker.RootScopeOutcome.Retry, await h.Worker.MeasureNextAsync(default));
            Assert.Equal("pending", await h.State());
        }
        Assert.Equal(0, h.Settled);
        Assert.Equal(RootScopeWorker.RootScopeOutcome.Failed, await h.Worker.MeasureNextAsync(default));
        Assert.Equal("failed", await h.State());
        Assert.Equal(1, h.Settled);
        Assert.Equal(RootScopeWorker.RootScopeOutcome.Idle, await h.Worker.MeasureNextAsync(default));
        var failed = (await h.Summary()).Download!;
        Assert.Equal(("manifestOnly", true), (failed.ScopeState, failed.ScopeProblem!.Retryable));
        h.Handler.Fail = null;
        await h.Open();
        Assert.Equal("pending", await h.State());
        Assert.Equal(RootScopeWorker.RootScopeOutcome.Measured, await h.Worker.MeasureNextAsync(default));
    }

    [Fact]
    public async Task Download_all_waiting_on_an_unreadable_file_list_is_woken_and_claims_the_manifest()
    {
        await using var h = await Scope.Create(InventoryFixture.Create(legacyRoot: true));
        var opened = await h.Open();
        var target = new RetentionTarget("wow", h.Data.BuildIdentity, "us", opened.Download!.LedgerId);
        var driver = (IRetentionDriverCatalog)h.Catalog;
        await ((IRetentionStore)h.Catalog).SetAsync(new(target, RetentionAction.DownloadAll), default);
        Assert.Null(await driver.ClaimNextRetentionAsync(default));
        Assert.Equal(RootScopeWorker.RootScopeOutcome.ManifestOnly, await h.Worker.MeasureNextAsync(default));
        Assert.Equal(1, h.Settled);
        var job = (await driver.ClaimNextRetentionAsync(default))!;
        Assert.Equal(("downloadManifestAllVariants", target.LedgerId), (job.Plan.Scope, job.Plan.Ledger.Sha256));
    }

    [Fact]
    public async Task Faults_of_any_type_count_as_attempts_and_end_failed()
    {
        await using var h = await Scope.Create(InventoryFixture.Create(rootGap: true));
        await h.Open();
        // Not an HTTP, IO or typed failure, and not this worker's own cancellation.
        h.Handler.Throw = InventoryFixture.Url("data", h.Data.Metadata["root"].EncodingKey);
        for (var attempt = 1; attempt < DiscoveryCatalog.MaxRootScopeAttempts; attempt++)
            Assert.Equal(RootScopeWorker.RootScopeOutcome.Retry, await h.Worker.MeasureNextAsync(default));
        Assert.Equal(RootScopeWorker.RootScopeOutcome.Failed, await h.Worker.MeasureNextAsync(default));
        Assert.Equal(("failed", 1), (await h.State(), h.Settled));
        var summary = (await h.Summary()).Download!;
        Assert.Equal(("manifestOnly", "ROOT_SCOPE_UNAVAILABLE", true), (summary.ScopeState, summary.ScopeProblem!.Code, summary.ScopeProblem.Retryable));
    }

    [Fact]
    public async Task A_pending_row_whose_saved_inputs_cannot_be_read_is_settled_failed_instead_of_read_again()
    {
        await using var h = await Scope.Create(InventoryFixture.Create(rootGap: true));
        await h.Open();
        await h.Sql("UPDATE download_admissions SET metadata=NULL");
        Assert.Equal(RootScopeWorker.RootScopeOutcome.Idle, await h.Worker.MeasureNextAsync(default));
        Assert.Equal(("failed", 1), (await h.State(), h.Settled));
        Assert.Equal(RootScopeWorker.RootScopeOutcome.Idle, await h.Worker.MeasureNextAsync(default));
        Assert.Equal(1, h.Settled);
        // Reopening writes a new admission with readable inputs and retries the measurement.
        await h.Open();
        Assert.Equal("pending", await h.State());
        Assert.Equal(RootScopeWorker.RootScopeOutcome.Measured, await h.Worker.MeasureNextAsync(default));
    }

    [Fact]
    public async Task Reopening_resets_an_unavailable_figure_of_a_measured_ledger_without_measuring_again()
    {
        await using var h = await Scope.Create(InventoryFixture.Create(rootGap: true));
        await h.Open();
        Assert.Equal(RootScopeWorker.RootScopeOutcome.Measured, await h.Worker.MeasureNextAsync(default));
        await h.Drain();
        var measured = (await h.Summary()).Download!;
        await h.Catalog.FailStoredCoverageAsync(measured.LedgerId, DiscoveryCatalog.StoredFailure("STORED_COVERAGE_FAILED"), default);
        Assert.Equal(StoredCountState.Unavailable, (await h.Summary()).Download!.CountState);
        await h.Open();
        Assert.Equal("measured", await h.State());
        Assert.Equal(RootScopeWorker.RootScopeOutcome.Idle, await h.Worker.MeasureNextAsync(default));
        await h.Drain();
        var recovered = (await h.Summary()).Download!;
        Assert.Equal((measured.LedgerId, "measured", StoredCountState.Current, (RetentionProblem?)null),
            (recovered.LedgerId, recovered.ScopeState, recovered.CountState, recovered.CountProblem));
    }

    [Fact]
    public async Task Reopening_measures_an_unreadable_measured_ledger_again_while_its_running_job_stays_readable()
    {
        await using var h = await Scope.Create(InventoryFixture.Create(rootGap: true));
        var opened = await h.Open();
        Assert.Equal(RootScopeWorker.RootScopeOutcome.Measured, await h.Worker.MeasureNextAsync(default));
        var target = new RetentionTarget("wow", h.Data.BuildIdentity, "us", opened.Download!.LedgerId);
        var store = (IRetentionStore)h.Catalog; var driver = (IRetentionDriverCatalog)h.Catalog;
        await store.SetAsync(new(target, RetentionAction.DownloadAll), default);
        // The job is claimed on the measured ledger and is running when that ledger's own bytes are found unreadable.
        var job = (await driver.ClaimNextRetentionAsync(default))!;
        Assert.Equal("downloadManifestAndRootAllVariants", job.Plan.Scope);
        await h.Catalog.FailStoredCoverageAsync(job.Plan.Ledger.Sha256, new("LOCAL_OBJECT_MISSING", "Authored missing ledger.", true), default);
        await h.Open();
        Assert.Equal("pending", await h.State());
        // The running job keeps the ledger it was claimed with; reading and changing intents still work.
        Assert.Equal(RetentionMode.DownloadAll, (await store.ReadAsync(target, default)).Mode);
        Assert.Single(await store.ListAsync(default));
        await store.SetAsync(new(target, RetentionAction.Pause), default);
        Assert.Equal(RootScopeWorker.RootScopeOutcome.Measured, await h.Worker.MeasureNextAsync(default));
        Assert.Equal("measured", await h.State());
        Assert.Single(await store.ListAsync(default));
    }

    [Fact]
    public async Task A_measured_build_stops_maintaining_the_manifest_figure_it_no_longer_reads()
    {
        await using var h = await Scope.Create(InventoryFixture.Create(rootGap: true));
        var opened = await h.Open();
        var manifest = opened.Download!.LedgerId;
        Assert.Equal(1L, await h.Count($"SELECT COUNT(*) FROM stored_coverage WHERE ledger='{manifest}'"));
        Assert.Equal(RootScopeWorker.RootScopeOutcome.Measured, await h.Worker.MeasureNextAsync(default));
        Assert.Equal(0L, await h.Count($"SELECT COUNT(*) FROM stored_coverage WHERE ledger='{manifest}'"));
        // A healthy measured build reopened keeps standing on its measured ledger.
        await h.Open();
        Assert.Equal(("measured", 0L), (await h.State(), await h.Count($"SELECT COUNT(*) FROM stored_coverage WHERE ledger='{manifest}'")));
        await h.Drain();
        Assert.Equal(StoredCountState.Current, (await h.Summary()).Download!.CountState);
    }

    private sealed class Scope : IAsyncDisposable
    {
        private readonly TempDirectory directory = new();
        internal InventoryFixture Data { get; private init; } = null!;
        internal Handler Handler { get; private init; } = null!;
        internal DiscoveryCatalog Catalog { get; private set; } = null!;
        internal RootScopeWorker Worker { get; private set; } = null!;
        internal int Settled;
        private VerifiedObjectStore objects = null!; private HttpClient client = null!; private DownloadWorkspacePreparer preparer = null!;
        private StoredCoverageWorker stored = null!; private readonly AcquisitionResources resources = new();
        internal static async Task<Scope> Create(InventoryFixture data)
        {
            var h = new Scope { Data = data, Handler = new(data) };
            h.client = new(h.Handler);
            await h.Start();
            var generation = await h.Catalog.BeginRefreshAsync("wow", default);
            await h.Catalog.CommitAsync("wow", generation, data.Discovery, "2026-10-19T00:00:00Z", default);
            return h;
        }
        private async Task Start()
        {
            Catalog = await DiscoveryCatalog.OpenAsync(directory.Path); objects = new(directory.Path);
            preparer = new(Catalog, objects, client, resources: resources);
            Worker = new(Catalog, objects, client, null, resources, () => Interlocked.Increment(ref Settled), null, TimeSpan.Zero);
            stored = new(Catalog, objects, resources, null, TimeSpan.Zero);
        }
        private string manifest = "";
        internal async Task<WorkspaceSnapshot> Open()
        {
            var opened = await preparer.PrepareAsync(new("wow", Data.BuildIdentity, "us"), _ => Task.CompletedTask, default);
            manifest = opened.Download!.LedgerId; return opened;
        }
        internal Task Drain() => stored.DrainAsync(default);
        internal async Task<BuildDownloadSummary> Summary() => (await new DownloadSummaryService(Catalog).ReadAsync([new("wow", Data.BuildIdentity)])).Single();
        internal Task<string> State() => Catalog.ReadRootScopeStateForTestAsync("wow", Data.BuildIdentity, manifest, default);
        internal async Task Restart() { await Stop(); await Start(); }
        private SqliteConnection Db() => new($"Data Source={Path.Combine(directory.Path, "discovery-v1.sqlite3")};Pooling=False");
        internal async Task Sql(string sql)
        { await using var db = Db(); await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
        internal async Task<long> Count(string sql)
        { await using var db = Db(); await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = sql; return (long)(await command.ExecuteScalarAsync())!; }
        private async Task Stop()
        {
            await Worker.DisposeAsync(); await stored.DisposeAsync(); preparer.Dispose(); await objects.DisposeAsync(); await Catalog.DisposeAsync();
        }
        public async ValueTask DisposeAsync() { await Stop(); client.Dispose(); directory.Dispose(); }
    }
    private sealed class Handler(InventoryFixture data) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];
        internal string? Hold, Fail, Throw;
        internal TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Requests) Requests.Add(url);
            if (url == Hold) { Held.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            if (url == Fail) return new HttpResponseMessage(HttpStatusCode.InternalServerError) { RequestMessage = request };
            if (url == Throw) throw new InvalidOperationException("Authored fault outside the typed transfer failures.");
            if (!data.Responses.TryGetValue(url, out var bytes) && !data.Payloads.TryGetValue(url.Split('/')[^1], out bytes))
                return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) };
        }
    }
}
