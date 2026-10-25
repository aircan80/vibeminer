using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

/// <summary>
/// D093: Home pages of the measured ledger list the files ROOT adds to the download manifest as not checked at Open, and
/// a bounded, cancellable Check verifies them without touching Open's assessment.
/// </summary>
public sealed class DownloadCheckTests
{
    [Fact]
    public async Task The_measured_ledger_lists_root_only_files_as_not_checked_at_Open_and_the_summary_names_both_ledgers()
    {
        await using var h = await Harness.Create();
        var (manifest, measured) = await h.OpenAndMeasure();
        var summary = (await h.Summary()).Download!;
        Assert.Equal((measured, manifest, "measured"), (summary.LedgerId, summary.ManifestLedgerId, summary.ScopeState));

        var page = await h.Read(measured);
        Assert.Equal((measured, manifest, "3", (string?)null, (string?)null), (page.LedgerId, page.ManifestLedgerId, page.TotalCount, page.NextCursor, page.LastCheck?.CheckedAt));
        Assert.Equal(["1", "2", "3"], page.Entries.Select(entry => entry.Id));
        var rootOnly = page.Entries.Single(entry => entry.EncodingKey == h.RootOnly.EncodingKey);
        Assert.Equal((false, false, (string?)null), (rootOnly.CheckedAtOpen, rootOnly.VerifiedAtAssessment, rootOnly.Check));
        Assert.All(page.Entries.Where(entry => entry != rootOnly), entry => Assert.Equal((true, (string?)null), (entry.CheckedAtOpen, entry.Check)));
        Assert.Equal(h.RootOnly.Encoded.Length.ToString(CultureInfo.InvariantCulture), rootOnly.EncodedBytes);

        // The admitted manifest keeps its own pages, with no file outside it.
        var opened = await h.Read(manifest);
        Assert.Equal((manifest, manifest, "2"), (opened.LedgerId, opened.ManifestLedgerId, opened.TotalCount));
        Assert.All(opened.Entries, entry => Assert.True(entry.CheckedAtOpen));
        Assert.DoesNotContain(opened.Entries, entry => entry.EncodingKey == rootOnly.EncodingKey);
    }

    [Fact]
    public async Task From_starts_a_page_at_the_first_member_with_that_key_or_after_it()
    {
        await using var h = await Harness.Create();
        var (_, measured) = await h.OpenAndMeasure();
        var all = (await h.Read(measured)).Entries;
        var second = await h.Read(measured, limit: 1, from: all[1].EncodingKey);
        Assert.Equal((all[1].Id, all[1].EncodingKey, (string?)all[1].Id), (second.Entries.Single().Id, second.Entries.Single().EncodingKey, second.NextCursor));
        // A key between two members starts at the next one; one past the last member gives the empty terminal page.
        var between = Successor(all[0].EncodingKey);
        Assert.Equal(all[1].EncodingKey, (await h.Read(measured, limit: 1, from: between)).Entries.Single().EncodingKey);
        var end = await h.Read(measured, from: new string('f', 32));
        Assert.Equal((0, (string?)null, "3"), (end.Entries.Count, end.NextCursor, end.TotalCount));
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Read(measured, from: all[0].EncodingKey, after: "1"))).Code);
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Read(measured, from: "ABC"))).Code);
        Assert.Equal("INVALID_CURSOR", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Read(measured, after: "4"))).Code);
    }

    [Fact]
    public async Task A_check_records_not_stored_verified_missing_and_corrupt_and_pages_show_the_latest_one()
    {
        await using var h = await Harness.Create();
        var (manifest, measured) = await h.OpenAndMeasure();
        var key = h.RootOnly.EncodingKey;
        var progress = new List<DownloadCheckProgress>();

        var first = await h.Check(measured, value => { lock (progress) progress.Add(value); return Task.CompletedTask; });
        Assert.Equal((manifest, "1", "0", "1", "0", "0"), (first.ManifestLedgerId, first.CheckedObjects, first.VerifiedObjects, first.NotStoredObjects, first.MissingObjects, first.CorruptObjects));
        Assert.Equal(new DownloadCheckProgress(0, 1), progress[0]); Assert.Equal(new DownloadCheckProgress(1, 1), progress[^1]);
        var page = await h.Read(measured);
        Assert.Equal(new DownloadCheckSummary(first.CheckedAt, "1", "0", "1", "0", "0"), page.LastCheck);
        Assert.Equal(("notStored", false), (page.Entries.Single(entry => entry.EncodingKey == key).Check, page.Entries.Single(entry => entry.EncodingKey == key).VerifiedAtAssessment));
        // Open's assessment is not changed by a check.
        Assert.All(page.Entries.Where(entry => entry.EncodingKey != key), entry => Assert.Null(entry.Check));

        await h.Store(h.RootOnly);
        Assert.Equal("1", (await h.Check(measured)).VerifiedObjects);
        Assert.Equal("verified", (await h.Read(measured)).Entries.Single(entry => entry.EncodingKey == key).Check);

        // Same length, different bytes: the hash fails, the store records the corruption, and the stored figure drops it.
        await File.WriteAllBytesAsync(h.ObjectPath(h.RootOnly), new byte[h.RootOnly.Encoded.Length]);
        Assert.Equal("1", (await h.Check(measured)).CorruptObjects);
        Assert.Equal("corrupt", (await h.Read(measured)).Entries.Single(entry => entry.EncodingKey == key).Check);
        Assert.Equal(1L, await h.Count("SELECT count(*) FROM object_invalidity"));
        // Only the completed check's rows are kept.
        Assert.Equal(1L, await h.Count("SELECT count(*) FROM download_check_objects"));
    }

    [Fact]
    public async Task A_receipt_without_its_file_is_missing_and_the_manifest_ledger_has_nothing_to_check()
    {
        await using var h = await Harness.Create();
        var (manifest, measured) = await h.OpenAndMeasure();
        await h.Receipt(h.RootOnly);
        Assert.Equal("1", (await h.Check(measured)).MissingObjects);
        Assert.Equal("missing", (await h.Read(measured)).Entries.Single(entry => entry.EncodingKey == h.RootOnly.EncodingKey).Check);
        Assert.Equal("DOWNLOAD_CHECK_NOT_NEEDED", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Check(manifest))).Code);
        Assert.Equal("DOWNLOAD_NOT_READY", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Check(new string('b', 64)))).Code);
        Assert.Equal("DOWNLOAD_NOT_READY", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Check(measured, region: "eu"))).Code);
    }

    [Fact]
    public async Task A_cancelled_check_records_nothing_and_keeps_the_previous_one()
    {
        await using var h = await Harness.Create();
        var (_, measured) = await h.OpenAndMeasure();
        await h.Receipt(h.RootOnly);
        var earlier = await h.Check(measured);
        await h.Store(h.RootOnly);
        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Check(measured, value =>
        {
            // Stop once the walk has started, before the object is hashed.
            if (value.Completed == 0) cancel.Cancel();
            return Task.CompletedTask;
        }, cancel.Token));
        var page = await h.Read(measured);
        Assert.Equal((earlier.CheckedAt, "missing"), (page.LastCheck?.CheckedAt, page.Entries.Single(entry => entry.EncodingKey == h.RootOnly.EncodingKey).Check));
        Assert.Equal(1L, await h.Count("SELECT count(*) FROM download_check_objects"));
        // The next check runs normally and replaces it.
        Assert.Equal("1", (await h.Check(measured)).VerifiedObjects);
    }

    [Fact]
    public async Task One_check_runs_at_a_time_and_a_second_is_refused_until_it_retires()
    {
        await using var h = await Harness.Create();
        var (_, measured) = await h.OpenAndMeasure();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = h.Check(measured, async value => { if (value.Completed == 0) { started.TrySetResult(); await release.Task; } });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var busy = await Assert.ThrowsAsync<DiscoveryException>(() => h.Check(measured));
        Assert.Equal(("DOWNLOAD_CHECK_BUSY", true), (busy.Code, busy.Retryable));
        // A page read meanwhile is served and still shows no completed check.
        Assert.Null((await h.Read(measured)).LastCheck);
        release.SetResult();
        Assert.Equal("1", (await running).CheckedObjects);
        Assert.Equal("1", (await h.Check(measured)).CheckedObjects);
    }

    [Fact]
    public async Task A_check_cut_short_by_a_restart_leaves_no_rows_a_page_could_read()
    {
        await using var h = await Harness.Create();
        var (_, measured) = await h.OpenAndMeasure();
        await h.Receipt(h.RootOnly);
        var earlier = await h.Check(measured);
        // An unfinished later generation, as a process that ended mid-check leaves it.
        await h.Sql($"UPDATE download_checks SET staging=staging+1; INSERT INTO download_check_objects SELECT ledger,staging,'{new string('c', 32)}','verified' FROM download_checks");
        await h.Restart();
        Assert.Equal(earlier.CheckedAt, (await h.Read(measured)).LastCheck?.CheckedAt);
        Assert.Equal("1", (await h.Check(measured)).MissingObjects);
        Assert.Equal(0L, await h.Count($"SELECT count(*) FROM download_check_objects WHERE ekey='{new string('c', 32)}'"));
    }

    [Fact]
    public async Task A_staged_finding_for_a_listed_file_never_reaches_a_page_over_the_completed_one()
    {
        await using var h = await Harness.Create();
        var (_, measured) = await h.OpenAndMeasure();
        await h.Receipt(h.RootOnly);
        var completed = await h.Check(measured);
        // A running (or crashed) Check has staged "verified" for the same file the completed one found missing.
        await h.Sql($"UPDATE download_checks SET staging=generation+1; INSERT INTO download_check_objects SELECT ledger,staging,'{h.RootOnly.EncodingKey}','verified' FROM download_checks");
        Assert.Equal(2L, await h.Count($"SELECT count(*) FROM download_check_objects WHERE ekey='{h.RootOnly.EncodingKey}'"));
        var page = await h.Read(measured);
        Assert.Equal(("missing", completed.CheckedAt, "1"), (page.Entries.Single(entry => entry.EncodingKey == h.RootOnly.EncodingKey).Check, page.LastCheck!.CheckedAt, page.LastCheck.MissingObjects));
    }

    [Fact]
    public async Task A_check_cancelled_after_staging_its_findings_removes_them_and_keeps_the_completed_one()
    {
        await using var h = await Harness.Create();
        var (_, measured) = await h.OpenAndMeasure();
        await h.Receipt(h.RootOnly);
        var earlier = await h.Check(measured);
        await h.Store(h.RootOnly);
        using var cancel = new CancellationTokenSource();
        long staged = -1;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Check(measured, async value =>
        {
            // The final report comes after the file's "verified" finding is written under the staging generation (a throttled
            // per-file report may come just before the write, so wait for the row).
            if (value.Completed != value.Total) return;
            staged = await h.Count("SELECT count(*) FROM download_check_objects o JOIN download_checks c ON c.ledger=o.ledger WHERE o.generation=c.staging AND o.generation<>c.generation");
            if (staged == 1) cancel.Cancel();
        }, cancel.Token));
        Assert.Equal(1L, staged);
        Assert.Equal(0L, await h.Count("SELECT count(*) FROM download_check_objects o JOIN download_checks c ON c.ledger=o.ledger WHERE o.generation<>c.generation"));
        var page = await h.Read(measured);
        Assert.Equal((earlier.CheckedAt, "missing"), (page.LastCheck!.CheckedAt, page.Entries.Single(entry => entry.EncodingKey == h.RootOnly.EncodingKey).Check));
    }

    private static string Successor(string key)
    {
        var bytes = Convert.FromHexString(key);
        for (var i = bytes.Length - 1; i >= 0 && ++bytes[i] == 0; i--) { }
        return Convert.ToHexStringLower(bytes);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly TempDirectory directory = new();
        private readonly AcquisitionResources resources = new();
        internal InventoryFixture Data { get; private init; } = null!;
        internal InventoryFixture.FixtureObject RootOnly => Data.RootOnly!;
        internal DiscoveryCatalog Catalog { get; private set; } = null!;
        private VerifiedObjectStore objects = null!;
        private HttpClient client = null!;
        private DownloadWorkspacePreparer preparer = null!;
        private RootScopeWorker worker = null!;
        private StoredCoverageWorker stored = null!;
        private DownloadService downloads = null!;
        internal static async Task<Harness> Create()
        {
            var h = new Harness { Data = InventoryFixture.Create(rootGap: true) };
            h.client = new(new Responses(h.Data));
            await h.Start();
            var generation = await h.Catalog.BeginRefreshAsync("wow", default);
            await h.Catalog.CommitAsync("wow", generation, h.Data.Discovery, "2026-10-19T00:00:00Z", default);
            return h;
        }
        private async Task Start()
        {
            Catalog = await DiscoveryCatalog.OpenAsync(directory.Path);
            objects = new(directory.Path, Catalog.RecordObjectInvalidityAsync);
            preparer = new(Catalog, objects, client, resources: resources);
            worker = new(Catalog, objects, client, null, resources, () => { }, null, TimeSpan.Zero);
            stored = new(Catalog, objects, resources, null, TimeSpan.Zero);
            downloads = new(new DownloadQuery(Catalog, objects, resources));
        }
        internal async Task<(string Manifest, string Measured)> OpenAndMeasure()
        {
            var opened = await preparer.PrepareAsync(new("wow", Data.BuildIdentity, "us"), _ => Task.CompletedTask, default);
            Assert.Equal(RootScopeWorker.RootScopeOutcome.Measured, await worker.MeasureNextAsync(default));
            await stored.DrainAsync(default);
            var summary = (await Summary()).Download!;
            Assert.NotEqual(opened.Download!.LedgerId, summary.LedgerId);
            return (opened.Download.LedgerId, summary.LedgerId);
        }
        internal async Task<BuildDownloadSummary> Summary() => (await new DownloadSummaryService(Catalog).ReadAsync([new("wow", Data.BuildIdentity)])).Single();
        internal Task<DownloadPage> Read(string ledger, int limit = 50, string? after = null, string? from = null) =>
            downloads.ReadAsync(new("wow", Data.BuildIdentity, "us", ledger, limit, after, from));
        internal Task<DownloadCheckResult> Check(string ledger, Func<DownloadCheckProgress, Task>? progress = null, CancellationToken token = default, string region = "us") =>
            downloads.CheckAsync(new("wow", Data.BuildIdentity, region, ledger), progress ?? (_ => Task.CompletedTask), token);
        private static string Sha(InventoryFixture.FixtureObject value) => Convert.ToHexStringLower(SHA256.HashData(value.Encoded));
        internal string ObjectPath(InventoryFixture.FixtureObject value) { var sha = Sha(value); return Path.Combine(directory.Path, "objects-v1", sha[..2], sha[2..]); }
        /// <summary>An exact encoded receipt only, as Download all records it; its bytes are not put in the store.</summary>
        internal Task Receipt(InventoryFixture.FixtureObject value) => Catalog.RecordEncodedReceiptAsync(new(value.EncodingKey, Sha(value), value.Encoded.Length,
            InventoryFixture.Url("data", value.EncodingKey), "2026-10-19T00:00:00Z", "2026-10-19T00:00:00Z", "blte-encoded-v1",
            new("loose", "0", value.Encoded.Length.ToString(CultureInfo.InvariantCulture), null)), default);
        internal async Task Store(InventoryFixture.FixtureObject value)
        {
            await objects.PublishAsync(value.Encoded, Sha(value), default);
            await Receipt(value);
        }
        private SqliteConnection Db() => new($"Data Source={Path.Combine(directory.Path, "discovery-v1.sqlite3")};Pooling=False");
        internal async Task Sql(string sql)
        { await using var db = Db(); await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
        internal async Task<long> Count(string sql)
        { await using var db = Db(); await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = sql; return (long)(await command.ExecuteScalarAsync())!; }
        internal async Task Restart() { await Stop(); await Start(); }
        private async Task Stop() { await worker.DisposeAsync(); await stored.DisposeAsync(); preparer.Dispose(); await objects.DisposeAsync(); await Catalog.DisposeAsync(); }
        public async ValueTask DisposeAsync() { await Stop(); client.Dispose(); directory.Dispose(); }
    }

    private sealed class Responses(InventoryFixture data) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (!data.Responses.TryGetValue(url, out var bytes) && !data.Payloads.TryGetValue(url.Split('/')[^1], out bytes))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) });
        }
    }
}
