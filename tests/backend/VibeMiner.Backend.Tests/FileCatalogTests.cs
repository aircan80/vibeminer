using System.Net;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

/// <summary>D071: the ROOT and install memberships Files lists are prepared in the background after Open.</summary>
public sealed class FileCatalogTests
{
    private static string Url(InventoryFixture data, string role) => InventoryFixture.Url("data", data.Metadata[role].EncodingKey);

    [Fact]
    public async Task Open_queues_the_catalog_and_the_worker_seals_one_row_per_file_with_unresolved_memberships_kept_explicit()
    {
        await using var h = await Harness.Create(InventoryFixture.Create(rootGap: true));
        await h.Open();
        // Open admits the manifest only; it never reads ROOT or install for the file catalog.
        foreach (var role in new[] { "root", "install" }) Assert.DoesNotContain(Url(h.Data, role), h.Handler.Requests);
        var queued = await h.Status();
        Assert.Equal(("pending", "queued", 0), (queued.State, queued.Stage, queued.Attempts));
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Ready, await h.Worker.PrepareNextAsync(default));
        var ready = await h.Status();
        Assert.Equal("ready", ready.State); Assert.Null(ready.Stage); Assert.Null(ready.Problem);
        var c = ready.Catalog!;
        // ROOT: 1001 (two locales), 1002, 2001 (outside the manifest), 2002 (absent from ENCODING); install: one name.
        Assert.Equal(("4", "1", "5", "1", "1", "0"), (c.RootFileCount, c.InstallFileCount, c.RootMembershipCount, c.InstallMembershipCount, c.UnresolvedMembershipCount, c.StoredMembershipCount));
        Assert.Equal("4", c.UniqueContentCount);
        var page = await h.Files(c.Generation, new());
        Assert.Equal(["root:1001", "root:1002", "root:2001", "root:2002", "install:6"], page.Entries.Select(file => file.Key));
        Assert.Equal("5", page.TotalCount);
        var unresolved = page.Entries.Single(file => file.Key == "root:2002");
        Assert.Equal((1, null, null), (unresolved.UnresolvedCount, unresolved.EncodedBytesMin, unresolved.EncodedBytesMax));
        var detail = await h.Catalog.ReadInventoryFileAsync(new("wow", h.Data.BuildIdentity, "us", c.Generation, "root:1001", new()), default);
        Assert.Equal(2, detail.MembershipCount);
        Assert.All(detail.Memberships, member => Assert.Equal((h.Data.PayloadA.EncodingKey, h.Data.PayloadA.Encoded.Length.ToString(), h.Data.PayloadA.Decoded.Length.ToString()),
            (member.EncodingKey, member.EncodedBytes, member.DecodedBytes)));
        Assert.Equal(new int?[] { 0, 0 }, detail.Memberships.Select(member => member.BlockOrdinal is null ? (int?)null : member.EntryOrdinal));
        var missing = Assert.Single((await h.Catalog.ReadInventoryFileAsync(new("wow", h.Data.BuildIdentity, "us", c.Generation, "root:2002", new()), default)).Memberships);
        Assert.Equal((null, null, null), (missing.EncodingKey, missing.EncodedBytes, missing.DecodedBytes));
        var install = Assert.Single((await h.Catalog.ReadInventoryFileAsync(new("wow", h.Data.BuildIdentity, "us", c.Generation, "install:6", new()), default)).Memberships);
        Assert.Equal((h.Data.PayloadA.EncodingKey, 0), (install.EncodingKey, install.InstallOrdinal));
        // A region the build was never opened through gets nothing, not the build's catalog.
        Assert.Equal("INVENTORY_NOT_FOUND", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadInventoryFilesAsync(new("wow", h.Data.BuildIdentity, "eu", c.Generation, new()), default))).Code);
        Assert.Equal("notOpened", (await h.Catalog.ReadFileCatalogAsync(new("wow", h.Data.BuildIdentity, "eu"), default)).State);
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Idle, await h.Worker.PrepareNextAsync(default));
        // Reopening a ready build keeps its catalog.
        await h.Open(); Assert.Equal(c.Generation, (await h.Status()).Catalog!.Generation);
    }

    private static string Key(string label) => Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.ASCII.GetBytes(label)));

    [Fact]
    public async Task Each_content_resolves_to_its_first_sized_encoded_object_and_unresolvable_ones_stay_unsized()
    {
        // One EKey; several EKeys where the lexicographically first has no size; listed twice; absent from ENCODING; unsized.
        var single = (C: Key("c-single"), E: Key("e-single"));
        var keys = new[] { Key("e-several-1"), Key("e-several-2"), Key("e-several-3") }.Order(StringComparer.Ordinal).ToArray();
        var several = (C: Key("c-several"), E: keys);
        var absent = Key("c-absent");
        var unsized = (C: Key("c-unsized"), E: Key("e-unsized"));
        var root = RootScopeTests.Root([single.C, several.C, single.C, absent, unsized.C]);
        var encoding = RootScopeTests.EncodingManifest([(single.C, [single.E]), (several.C, several.E), (unsized.C, [unsized.E])],
            [(single.E, 10), (several.E[1], 32), (several.E[2], 33)]);
        var index = await FileCatalogIndex.BuildAsync(new MemoryStream(root), root.Length, [], new MemoryStream(encoding), encoding.Length, default);
        Assert.Equal((5L, 5L, 4), (index.RootMembers, index.RootFiles, index.DistinctContent));
        Assert.Equal((single.E, 10L, 100L), Sized(index.Resolve(single.C)));
        Assert.Equal((several.E[1], 32L, 100L), Sized(index.Resolve(several.C)));
        Assert.Equal((null, null, null), index.Resolve(absent));
        Assert.Equal((null, null, 100L), index.Resolve(unsized.C));
        Assert.Equal([(1u, 0x10000000u, 5L)], index.Variants);
        Assert.Equal("FILE_CATALOG_CHANGED", Assert.Throws<DiscoveryException>(() => index.Resolve(Key("never-listed"))).Code);
        static (string?, long?, long?) Sized((string? E, long? Encoded, long? Decoded) value) => value;
    }

    [Fact]
    public async Task Stored_memberships_follow_the_verified_receipt_of_their_encoded_object()
    {
        await using var h = await Harness.Create(InventoryFixture.Create(rootGap: true));
        await h.Open();
        // ROOT is in this fixture's ENCODING but not a file of itself; record a verified receipt for payload A's exact EKey instead.
        await h.Sql($"INSERT INTO metadata_objects VALUES('{h.Data.PayloadA.EncodingKey}','{h.Data.PayloadA.ContentKey}',{h.Data.PayloadA.Encoded.Length},'{{\"sha256\":\"{new string('0', 64)}\"}}')");
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Ready, await h.Worker.PrepareNextAsync(default));
        var c = (await h.Status()).Catalog!;
        Assert.Equal("3", c.StoredMembershipCount);
        Assert.Equal(["root:1001", "install:6"], (await h.Files(c.Generation, new(StoredOnly: true))).Entries.Select(file => file.Key));
    }

    [Fact]
    public async Task A_restart_checks_the_saved_rows_and_continues_after_them()
    {
        await using var h = await Harness.Create(InventoryFixture.Create(rootGap: true), batch: 2);
        await h.Open();
        using var stop = new CancellationTokenSource();
        h.AfterBatch = (written, _) => { if (written == 2) stop.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Worker.PrepareNextAsync(stop.Token));
        var stopped = await h.Status();
        Assert.Equal("pending", stopped.State); Assert.Equal(0, stopped.Attempts);
        Assert.Equal(2L, await h.Count("SELECT written FROM file_catalogs"));
        var staging = await h.Count("SELECT inventory FROM file_catalogs");
        // The rows and their position live in the build's own catalog file, not in the main catalog (D071).
        Assert.Equal((2L, 2L), (await h.FileCount(staging, "SELECT count(*) FROM inventory_origins"), await h.FileCount(staging, "SELECT written FROM catalog_identity")));
        Assert.Equal(0L, await h.Count("SELECT count(*) FROM inventory_origins"));
        await h.Restart();
        var batches = new List<long>(); h.AfterBatch = (written, _) => { batches.Add(written); return Task.CompletedTask; };
        Assert.Equal("queued", (await h.Status()).Stage);
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Ready, await h.Worker.PrepareNextAsync(default));
        // Rows 1 and 2 were checked against what the sources derive, not written again.
        Assert.Equal([4L, 6L], batches);
        var c = (await h.Status()).Catalog!;
        Assert.Equal(staging.ToString(), c.Generation);
        Assert.Equal(6L, await h.FileCount(staging, "SELECT count(*) FROM inventory_origins"));
        Assert.Equal(5, (await h.Files(c.Generation, new())).Entries.Count);
    }

    [Fact]
    public async Task Saved_rows_that_differ_are_abandoned_and_the_preparation_starts_again()
    {
        await using var h = await Harness.Create(InventoryFixture.Create(rootGap: true), batch: 2);
        await h.Open();
        using var stop = new CancellationTokenSource();
        h.AfterBatch = (written, _) => { if (written == 4) stop.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Worker.PrepareNextAsync(stop.Token));
        var abandoned = await h.Count("SELECT inventory FROM file_catalogs");
        await h.FileSql(abandoned, "UPDATE inventory_origins SET encoded=encoded+1 WHERE id=3");
        h.AfterBatch = null;
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Restarted, await h.Worker.PrepareNextAsync(default));
        Assert.Equal("abandoned", await h.Text($"SELECT state FROM inventory_generations WHERE id={abandoned}"));
        Assert.Equal(0L, await h.Count("SELECT count(*) FROM file_catalogs WHERE inventory IS NOT NULL OR written<>0"));
        // Cleanup removes the abandoned generation's file, then its registry row.
        while (await h.Catalog.CleanupFileCatalogsAsync(default)) { }
        Assert.False(File.Exists(h.Catalog.FileCatalogPath(abandoned)));
        Assert.Equal(0L, await h.Count($"SELECT count(*) FROM inventory_generations WHERE id={abandoned}"));
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Ready, await h.Worker.PrepareNextAsync(default));
        var c = (await h.Status()).Catalog!;
        Assert.NotEqual(abandoned.ToString(), c.Generation);
        Assert.Equal(h.Data.PayloadB.Encoded.Length.ToString(), (await h.Files(c.Generation, new(FileDataId: "1002"))).Entries.Single().EncodedBytesMin);
    }

    [Fact]
    public async Task A_failing_preparation_is_tried_three_times_states_its_cause_and_the_next_Open_retries()
    {
        await using var h = await Harness.Create(InventoryFixture.Create(rootGap: true));
        await h.Open();
        h.Handler.Fail = Url(h.Data, "root");
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Retry, await h.Worker.PrepareNextAsync(default));
        var retrying = await h.Status();
        Assert.Equal(("pending", "queued", 1), (retrying.State, retrying.Stage, retrying.Attempts));
        Assert.True(retrying.Problem!.Retryable);
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Retry, await h.Worker.PrepareNextAsync(default));
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Failed, await h.Worker.PrepareNextAsync(default));
        var failed = await h.Status();
        Assert.Equal(("failed", 3), (failed.State, failed.Attempts));
        Assert.Equal(retrying.Problem.Code, failed.Problem!.Code); Assert.Equal("The file list could not be prepared.", failed.Problem.Message);
        Assert.NotNull(failed.Problem.Detail);
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Idle, await h.Worker.PrepareNextAsync(default));
        h.Handler.Fail = null;
        await h.Open();
        Assert.Equal(("pending", 0), ((await h.Status()).State, (await h.Status()).Attempts));
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Ready, await h.Worker.PrepareNextAsync(default));
    }

    [Theory]
    [InlineData("open")]
    [InlineData("resume")]
    public async Task After_three_attempts_whose_retry_waits_ran_out_an_Open_or_Retry_starts_the_next_attempt_at_once(string how)
    {
        // Regression (#35 QA): each retry wait raced its delay against the wake-up and left the losing wait running. Those
        // leftover waits took the one-slot wake-up of a later Open or Retry, and the build sat queued until a later one.
        var clock = new ManualClock();
        await using var h = await Harness.Create(InventoryFixture.Create(rootGap: true), clock: clock, retryDelay: TimeSpan.FromMinutes(1));
        await h.Open();
        h.Handler.Fail = Url(h.Data, "root");
        h.Worker.Start();
        // Time moves a minute at a time, so every download backoff and both retry waits run out rather than being woken.
        await Until(async () => { clock.Advance(TimeSpan.FromMinutes(1)); return (await h.Status()).State == "failed" && h.Catalog.FileCatalogWaiters > 0; });
        await Task.Delay(100);
        // Only the idle wait is left: neither retry wait outlived its turn.
        Assert.Equal(1, h.Catalog.FileCatalogWaiters);
        h.Handler.Fail = null;
        if (how == "open") await h.Open();
        else await h.Catalog.SetFileCatalogAsync(new("wow", h.Data.BuildIdentity, "resume"), default);
        // No clock advance: the wake-up alone starts the attempt, which finishes.
        await Until(async () => (await h.Status()).State == "ready");
    }

    private static async Task Until(Func<Task<bool>> done)
    {
        var limit = DateTime.UtcNow.AddSeconds(20);
        while (!await done()) { Assert.True(DateTime.UtcNow < limit, "Timed out."); await Task.Delay(20); }
    }

    /// <summary>A clock that moves only when the test says so; its timers fire on <see cref="Advance"/>.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly object gate = new(); private readonly List<Timer> timers = []; private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return ticks; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { var timer = new Timer(this, callback, state); lock (gate) { timers.Add(timer); timer.Change(dueTime, period); } return timer; }
        internal void Advance(TimeSpan amount)
        {
            Timer[] ready; lock (gate) { ticks += amount.Ticks; ready = timers.Where(t => t.Due <= ticks).ToArray(); foreach (var timer in ready) timer.Due = long.MaxValue; }
            foreach (var timer in ready) timer.Callback(timer.State);
        }
        private sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            internal readonly TimerCallback Callback = callback; internal readonly object? State = state; internal long Due;
            public bool Change(TimeSpan dueTime, TimeSpan period) { lock (clock.gate) Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + dueTime.Ticks; return true; }
            public void Dispose() { lock (clock.gate) Due = long.MaxValue; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [Fact]
    public async Task An_unreadable_file_list_is_unsupported_with_its_cause_and_the_next_Open_tries_again()
    {
        await using var h = await Harness.Create(InventoryFixture.Create(legacyRoot: true));
        await h.Open();
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Unsupported, await h.Worker.PrepareNextAsync(default));
        var status = await h.Status();
        Assert.Equal(("unsupported", 0), (status.State, status.Attempts));
        Assert.StartsWith("ROOT_", status.Problem!.Code); Assert.False(status.Problem.Retryable); Assert.Contains("won't help", status.Problem.Message);
        Assert.Equal("FILE_CATALOG_STATE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.SetFileCatalogAsync(new("wow", h.Data.BuildIdentity, "resume"), default))).Code);
        Assert.Equal(0L, await h.Count("SELECT count(*) FROM inventory_generations WHERE profile='files-root-install-v1' AND state='ready'"));
        await h.Open();
        Assert.Equal("pending", (await h.Status()).State);
    }

    [Fact]
    public async Task A_result_for_a_preparation_reset_meanwhile_is_discarded()
    {
        await using var h = await Harness.Create(InventoryFixture.Create(rootGap: true), batch: 2);
        await h.Open();
        // Another engine path resets the row (as a later Open of a failed one does) while rows are being written.
        h.AfterBatch = async (written, token) => { if (written == 2) await h.Sql("UPDATE file_catalogs SET generation=generation+1"); };
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Superseded, await h.Worker.PrepareNextAsync(default));
        Assert.Equal(2L, await h.Count("SELECT written FROM file_catalogs"));
        Assert.Equal(0L, await h.Count("SELECT count(*) FROM inventory_generations WHERE state='ready'"));
        Assert.Equal("pending", (await h.Status()).State);
        h.AfterBatch = null;
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Ready, await h.Worker.PrepareNextAsync(default));
    }

    [Fact]
    public async Task A_catalog_whose_generation_was_lost_is_prepared_again_and_the_old_one_is_never_read()
    {
        await using var h = await Harness.Create(InventoryFixture.Create(rootGap: true));
        await h.Open();
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Ready, await h.Worker.PrepareNextAsync(default));
        var first = (await h.Status()).Catalog!.Generation;
        await h.Sql($"UPDATE inventory_generations SET state='staging' WHERE id={first}");
        Assert.Equal("INVENTORY_NOT_FOUND", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Files(first, new()))).Code);
        await h.Open();
        Assert.Equal(("pending", "queued"), ((await h.Status()).State, (await h.Status()).Stage));
        Assert.Equal("abandoned", await h.Text($"SELECT state FROM inventory_generations WHERE id={first}"));
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Ready, await h.Worker.PrepareNextAsync(default));
        var second = (await h.Status()).Catalog!.Generation;
        Assert.NotEqual(first, second);
        Assert.Equal(5, (await h.Files(second, new())).Entries.Count);
    }

    [Fact]
    public async Task Shutdown_during_writing_leaves_the_preparation_pending_and_the_next_run_finishes_it()
    {
        await using var h = await Harness.Create(InventoryFixture.Create(rootGap: true), batch: 2);
        await h.Open();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.AfterBatch = async (written, token) => { if (written == 2) { reached.TrySetResult(); await Task.Delay(Timeout.Infinite, token); } };
        h.Worker.Start();
        await reached.Task;
        await h.Restart();
        Assert.Equal(("pending", 2L), ((await h.Status()).State, await h.Count("SELECT written FROM file_catalogs")));
        h.AfterBatch = null;
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Ready, await h.Worker.PrepareNextAsync(default));
    }

    [Fact]
    public async Task The_file_catalog_and_the_ROOT_scope_measurement_never_hold_their_arrays_together()
    {
        await using var h = await Harness.Create(InventoryFixture.Create(rootGap: true), batch: 2);
        await h.Open();
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.AfterBatch = async (written, token) => { if (written == 2) { holding.TrySetResult(); await release.Task; } };
        var files = h.Worker.PrepareNextAsync(default);
        await holding.Task;
        Assert.True(h.Passes.Held);
        var scope = h.RootScopes.MeasureNextAsync(default);
        // The measurement reaches the gate and waits there while the file catalog writes.
        while (h.Passes.Waiting == 0) { Assert.False(scope.IsCompleted, "The ROOT scope measured while the file catalog held the gate."); await Task.Delay(10); }
        Assert.False(scope.IsCompleted);
        release.TrySetResult();
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Ready, await files);
        Assert.Equal(RootScopeWorker.RootScopeOutcome.Measured, await scope);
        Assert.False(h.Passes.Held);
    }


    private const string Other = "0000000000000000000000000000000000000000000000000000000000000abc";
    /// <summary>A second opened build, as discovery and Open would leave it, whose preparation is pending (its sources are not served).</summary>
    private static async Task OtherBuild(Harness h, long opened)
    {
        await h.Sql($$"""
            INSERT OR IGNORE INTO builds VALUES('{{Other}}','wow','other','{"VersionsName":"9.9.9.99999"}','2026-10-19T00:00:00Z','2026-10-19T00:00:00Z',1,1,0);
            INSERT INTO workspace_admissions(product,build_id,region,profile,source_reference,opened_at) VALUES('wow','{{Other}}','us','home-download-v1','{}','2026-10-19T00:00:00Z');
            INSERT OR REPLACE INTO file_catalogs VALUES('wow','{{Other}}','pending',1,0,NULL,0,NULL,'queued',NULL,{{opened}},'2026-10-19T00:00:00Z');
            """);
    }

    [Fact]
    public async Task A_queued_build_names_the_one_it_waits_for_and_a_newer_Open_goes_first_while_the_older_keeps_its_rows()
    {
        await using var h = await Harness.Create(InventoryFixture.Create(rootGap: true), batch: 2);
        await h.Open();
        FileCatalogStatus? waiting = null;
        h.AfterBatch = async (written, _) =>
        {
            if (written != 2) return;
            await OtherBuild(h, 0);
            waiting = await h.Catalog.ReadFileCatalogAsync(new("wow", Other, "us"), default);
            // The other build is opened again now: it becomes the most recent, so this one yields at its next batch.
            await h.Sql($"UPDATE file_catalogs SET opened=99 WHERE build_id='{Other}'");
        };
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Yielded, await h.Worker.PrepareNextAsync(default));
        Assert.Equal(("pending", "queued", "9.9.9.99999"), (waiting!.State, waiting.Stage, waiting.BuildVersion));
        Assert.Equal((h.Data.BuildIdentity, "1.2.3.12345"), (waiting.WaitingFor!.BuildIdentity, waiting.WaitingFor.BuildVersion));
        Assert.Equal(("pending", 0, 2L), ((await h.Status()).State, (await h.Status()).Attempts, await h.Count($"SELECT written FROM file_catalogs WHERE build_id='{h.Data.BuildIdentity}'")));
        // The newer one goes first (here it fails, its sources are not served), then this one continues after its rows.
        h.AfterBatch = null;
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Retry, await h.Worker.PrepareNextAsync(default));
        Assert.Equal(1, (await h.Catalog.ReadFileCatalogAsync(new("wow", Other, "us"), default)).Attempts);
        await h.Sql($"UPDATE file_catalogs SET state='stopped' WHERE build_id='{Other}'");
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Ready, await h.Worker.PrepareNextAsync(default));
    }

    [Fact]
    public async Task Stop_keeps_the_written_rows_and_Resume_or_the_next_Open_continues_after_them()
    {
        await using var h = await Harness.Create(InventoryFixture.Create(rootGap: true), batch: 2);
        await h.Open();
        h.AfterBatch = async (written, _) => { if (written == 2) await h.Catalog.SetFileCatalogAsync(new("wow", h.Data.BuildIdentity, "stop"), default); };
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Stopped, await h.Worker.PrepareNextAsync(default));
        var stopped = await h.Status();
        Assert.Equal(("stopped", (string?)null), (stopped.State, stopped.Stage));
        Assert.Equal("stopped", Assert.Single((await h.Catalog.ListFileCatalogsAsync(default)).Catalogs).State);
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Idle, await h.Worker.PrepareNextAsync(default));
        Assert.Equal("FILE_CATALOG_STATE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.SetFileCatalogAsync(new("wow", h.Data.BuildIdentity, "stop"), default))).Code);
        var resumed = await h.Catalog.SetFileCatalogAsync(new("wow", h.Data.BuildIdentity, "resume"), default);
        Assert.Equal(("pending", "queued", "us"), (resumed.State, resumed.Stage, resumed.SourceRegion));
        var batches = new List<long>(); h.AfterBatch = (written, _) => { batches.Add(written); return Task.CompletedTask; };
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Ready, await h.Worker.PrepareNextAsync(default));
        Assert.Equal([4L, 6L], batches);
        Assert.Empty((await h.Catalog.ListFileCatalogsAsync(default)).Catalogs);
        // A stopped build is also prepared again by its next Open.
        await h.Sql("UPDATE file_catalogs SET state='stopped'");
        await h.Open(); Assert.Equal("pending", (await h.Status()).State);
    }

    private static string Paused(InventoryFixture data) => $"INSERT INTO retention_jobs VALUES('job','wow','{data.BuildIdentity}','us','{new string('a', 64)}',1,0,1,'{{\"paused\":true}}')";

    [Fact]
    public async Task A_paused_Download_all_pauses_the_build_preparation_before_it_starts_and_between_batches()
    {
        await using var h = await Harness.Create(InventoryFixture.Create(rootGap: true), batch: 2);
        await h.Open();
        await h.Sql(Paused(h.Data));
        Assert.Equal(("pending", "paused"), ((await h.Status()).State, (await h.Status()).Stage));
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Idle, await h.Worker.PrepareNextAsync(default));
        foreach (var role in new[] { "root", "install" }) Assert.DoesNotContain(Url(h.Data, role), h.Handler.Requests);
        await h.Sql("DELETE FROM retention_jobs");
        h.AfterBatch = async (written, _) => { if (written == 2) await h.Sql(Paused(h.Data)); };
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Paused, await h.Worker.PrepareNextAsync(default));
        Assert.Equal(("pending", "paused", 0), ((await h.Status()).State, (await h.Status()).Stage, (await h.Status()).Attempts));
        await h.Sql("DELETE FROM retention_jobs"); h.AfterBatch = null;
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Ready, await h.Worker.PrepareNextAsync(default));
    }

    [Fact]
    public async Task Too_little_free_disk_space_is_refused_before_writing_with_STORAGE_FULL_and_the_next_Open_retries()
    {
        await using var h = await Harness.Create(InventoryFixture.Create(rootGap: true));
        await h.Open();
        h.Free = 1024;
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Failed, await h.Worker.PrepareNextAsync(default));
        var full = await h.Status();
        Assert.Equal(("failed", "STORAGE_FULL", false), (full.State, full.Problem!.Code, full.Problem.Retryable));
        Assert.Contains("1024 bytes free", full.Problem.Detail);
        Assert.Equal(0L, await h.Count("SELECT count(*) FROM file_catalogs WHERE inventory IS NOT NULL"));
        Assert.Empty(Directory.Exists(Path.GetDirectoryName(h.Catalog.FileCatalogPath(1))!) ? Directory.GetFiles(Path.GetDirectoryName(h.Catalog.FileCatalogPath(1))!) : []);
        h.Free = long.MaxValue;
        await h.Open();
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Ready, await h.Worker.PrepareNextAsync(default));
    }

    [Theory]
    [InlineData(28, "STORAGE_FULL")] [InlineData(112, "STORAGE_FULL")] [InlineData(5, "STORAGE_UNAVAILABLE")]
    public void A_full_disk_is_STORAGE_FULL_whichever_layer_reports_it(int error, string code)
    {
        Assert.Equal(code, FileCatalogWorker.Code(new IOException("authored", unchecked((int)0x80070000) | error)));
        Assert.Equal("STORAGE_FULL", FileCatalogWorker.Code(new DiscoveryException("STORAGE_FULL", "full") { Cause = "SQLite 13/13: database or disk is full" }));
    }

    [Fact]
    public async Task A_saved_preparation_that_fails_its_integrity_check_starts_again_instead_of_failing_on_every_Open()
    {
        await using var h = await Harness.Create(InventoryFixture.Create(rootGap: true), batch: 2);
        await h.Open();
        using var stop = new CancellationTokenSource();
        h.AfterBatch = (written, _) => { if (written == 2) stop.Cancel(); return Task.CompletedTask; };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Worker.PrepareNextAsync(stop.Token));
        h.AfterBatch = null;
        var first = await h.Count("SELECT inventory FROM file_catalogs");
        // A registry row that no longer says staging, and a corrupt file: each attempt drops the saved rows and starts again.
        await h.Sql($"UPDATE inventory_generations SET state='abandoned' WHERE id={first}");
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Ready, await h.Worker.PrepareNextAsync(default));
        var second = long.Parse((await h.Status()).Catalog!.Generation);
        Assert.NotEqual(first, second);
        await h.Sql("UPDATE file_catalogs SET state='pending',stage='queued'");
        await h.Sql($"UPDATE inventory_generations SET state='staging' WHERE id={second}");
        await h.Restart();
        await File.WriteAllBytesAsync(h.Catalog.FileCatalogPath(second), new byte[8192]);
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Ready, await h.Worker.PrepareNextAsync(default));
        Assert.NotEqual(second.ToString(), (await h.Status()).Catalog!.Generation);
        // A row that cannot be read at all is settled failed with its staging reference dropped, and the next Open prepares it.
        await h.Sql("UPDATE file_catalogs SET state='pending',attempts=7");
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Idle, await h.Worker.PrepareNextAsync(default));
        Assert.Equal(("failed", 0L), ((await h.Status()).State, await h.Count("SELECT count(*) FROM file_catalogs WHERE inventory IS NOT NULL")));
        await h.Open();
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Ready, await h.Worker.PrepareNextAsync(default));
    }

    [Fact]
    public async Task A_content_with_several_sized_EKeys_prefers_a_stored_one_as_the_legacy_catalog_does()
    {
        var c = Key("c-several"); var e = new[] { Key("e-a"), Key("e-b"), Key("e-c") }.Order(StringComparer.Ordinal).ToArray();
        var root = RootScopeTests.Root([c]);
        var encoding = RootScopeTests.EncodingManifest([(c, e)], [(e[0], 30), (e[1], 31), (e[2], 32)]);
        var index = await FileCatalogIndex.BuildAsync(new MemoryStream(root), root.Length, [], new MemoryStream(encoding), encoding.Length, default);
        Assert.Equal(3, index.Alternatives.Count);
        Assert.Equal(e[0], index.Resolve(c).EncodingKey);
        index.PreferStored(new HashSet<string>([e[2], e[1]]));
        Assert.Equal((e[1], 31L), (index.Resolve(c).EncodingKey, index.Resolve(c).EncodedBytes));
    }

    [Fact]
    public async Task The_file_catalog_lists_the_same_files_and_memberships_as_the_legacy_catalog_of_the_same_build()
    {
        await using var h = await Harness.Create(InventoryFixture.Create());
        var legacy = (await h.LegacyOpen()).Inventory!.Generation;
        await h.Open();
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Ready, await h.Worker.PrepareNextAsync(default));
        var prepared = (await h.Status()).Catalog!.Generation;
        Assert.NotEqual(legacy, prepared);
        var before = await h.Files(legacy, new()); var after = await h.Files(prepared, new());
        Assert.Equal(before.Entries, after.Entries); Assert.Equal(before.TotalCount, after.TotalCount);
        foreach (var file in before.Entries)
        {
            var a = await h.Catalog.ReadInventoryFileAsync(new("wow", h.Data.BuildIdentity, "us", legacy, file.Key, new()), default);
            var b = await h.Catalog.ReadInventoryFileAsync(new("wow", h.Data.BuildIdentity, "us", prepared, file.Key, new()), default);
            Assert.Equal(a.Memberships, b.Memberships);
        }
    }

    [Fact]
    public async Task A_ready_build_whose_file_is_not_sealed_is_prepared_again_on_Open_and_a_sealed_file_under_staging_is_sealed_again()
    {
        await using var h = await Harness.Create(InventoryFixture.Create(rootGap: true), batch: 2);
        await h.Open();
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Ready, await h.Worker.PrepareNextAsync(default));
        var g = long.Parse((await h.Status()).Catalog!.Generation);
        Assert.Equal(1L, await h.FileCount(g, "SELECT sealed FROM catalog_identity"));
        // What a crash between the two halves of an older, single-transaction seal could leave: main ready, file not sealed.
        await h.Restart();
        await h.FileSql(g, "UPDATE catalog_identity SET sealed=0");
        Assert.Equal("INVENTORY_NOT_FOUND", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Files(g.ToString(), new()))).Code);
        await h.Open();
        Assert.Equal(("pending", "queued"), ((await h.Status()).State, (await h.Status()).Stage));
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Ready, await h.Worker.PrepareNextAsync(default));
        var again = long.Parse((await h.Status()).Catalog!.Generation);
        Assert.Equal(5, (await h.Files(again.ToString(), new())).Entries.Count);
        // What a crash between the two halves of the seal leaves now: the file sealed, the main catalog still staging. The
        // next run checks every saved row, writes none and seals again, in the same file.
        await h.Restart();
        await h.Sql($"UPDATE inventory_generations SET state='staging',summary=NULL WHERE id={again}; UPDATE file_catalogs SET state='pending',stage='queued'");
        var batches = new List<long>(); h.AfterBatch = (written, _) => { batches.Add(written); return Task.CompletedTask; };
        Assert.Equal(FileCatalogWorker.FileCatalogOutcome.Ready, await h.Worker.PrepareNextAsync(default));
        Assert.Empty(batches);
        Assert.Equal(again.ToString(), (await h.Status()).Catalog!.Generation);
        Assert.Equal(5, (await h.Files(again.ToString(), new())).Entries.Count);
    }

    [Theory]
    [InlineData("/home/a/data", "/home", "/home")] [InlineData("/home2/data", "/home", "/")] [InlineData("/home", "/home", "/home")]
    [InlineData("/tmp/x", "/tmp/x/y", "/")]
    public void The_free_space_volume_is_matched_on_whole_path_segments(string path, string mount, string expected)
    {
        Assert.Equal(expected, FileCatalogWorker.VolumeOf(path, ["/", mount]));
        Assert.Null(FileCatalogWorker.VolumeOf(path, ["/srv"]));
    }

    [Fact]
    public async Task A_waiting_ROOT_scope_measurement_goes_first_and_cancellation_leaves_the_queue()
    {
        var gate = new BackgroundMetadataGate();
        var held = await gate.EnterAsync(false, default);
        using var cancel = new CancellationTokenSource();
        var canceled = gate.EnterAsync(true, cancel.Token);
        var files = gate.EnterAsync(false, default);
        var scope = gate.EnterAsync(true, default);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.Equal(2, gate.Waiting);
        held.Dispose();
        var first = await scope; Assert.False(files.IsCompleted);
        first.Dispose(); first.Dispose();
        (await files).Dispose();
        Assert.False(gate.Held);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly TempDirectory directory = new();
        internal InventoryFixture Data { get; private init; } = null!;
        internal Handler Handler { get; private init; } = null!;
        internal DiscoveryCatalog Catalog { get; private set; } = null!;
        internal FileCatalogWorker Worker { get; private set; } = null!;
        internal RootScopeWorker RootScopes { get; private set; } = null!;
        internal BackgroundMetadataGate Passes { get; private set; } = null!;
        internal Func<long, CancellationToken, Task>? AfterBatch;
        private int batch; private TimeProvider? clock; private TimeSpan retryDelay;
        private VerifiedObjectStore objects = null!; private HttpClient client = null!; private DownloadWorkspacePreparer preparer = null!;
        private readonly AcquisitionResources resources = new();
        internal static async Task<Harness> Create(InventoryFixture data, int batch = DiscoveryCatalog.FileCatalogBatchRows, TimeProvider? clock = null, TimeSpan? retryDelay = null)
        {
            var h = new Harness { Data = data, Handler = new(data), batch = batch, clock = clock, retryDelay = retryDelay ?? TimeSpan.Zero };
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
            Passes = new();
            Worker = new(Catalog, objects, client, null, resources, Passes, clock, retryDelay)
            { BatchRows = batch, AfterBatch = (written, token) => AfterBatch?.Invoke(written, token) ?? Task.CompletedTask, FreeBytesForTest = () => Free };
            RootScopes = new(Catalog, objects, client, null, resources, () => { }, null, TimeSpan.Zero, Passes);
        }
        internal Task<WorkspaceSnapshot> Open() => preparer.PrepareAsync(new("wow", Data.BuildIdentity, "us"), _ => Task.CompletedTask, default);
        internal async Task<WorkspaceSnapshot> LegacyOpen()
        {
            using var legacy = new InventoryWorkspacePreparer(Catalog, objects, client, resources);
            return await legacy.PrepareAsync(new("wow", Data.BuildIdentity, "us"), _ => Task.CompletedTask, default);
        }
        internal long Free = long.MaxValue;
        private SqliteConnection FileDb(long g) => new($"Data Source={Catalog.FileCatalogPath(g)};Pooling=False");
        internal async Task FileSql(long g, string sql)
        { await using var db = FileDb(g); await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
        internal async Task<long> FileCount(long g, string sql)
        { await using var db = FileDb(g); await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(await command.ExecuteScalarAsync()); }
        internal Task<FileCatalogStatus> Status() => Catalog.ReadFileCatalogAsync(new("wow", Data.BuildIdentity, "us"), default);
        internal Task<InventoryFilesPage> Files(string generation, InventoryFileFilter filter) =>
            Catalog.ReadInventoryFilesAsync(new("wow", Data.BuildIdentity, "us", generation, filter), default);
        internal async Task Restart() { await Stop(); await Start(); }
        private SqliteConnection Db() => new($"Data Source={Path.Combine(directory.Path, "discovery-v1.sqlite3")};Pooling=False");
        internal async Task Sql(string sql)
        { await using var db = Db(); await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
        internal async Task<long> Count(string sql)
        { await using var db = Db(); await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(await command.ExecuteScalarAsync()); }
        internal async Task<string?> Text(string sql)
        { await using var db = Db(); await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = sql; return (string?)await command.ExecuteScalarAsync(); }
        private async Task Stop()
        {
            await Worker.DisposeAsync(); await RootScopes.DisposeAsync(); preparer.Dispose(); await objects.DisposeAsync(); await Catalog.DisposeAsync();
        }
        public async ValueTask DisposeAsync() { await Stop(); client.Dispose(); directory.Dispose(); }
    }

    private sealed class Handler(InventoryFixture data) : HttpMessageHandler
    {
        internal List<string> Requests { get; } = [];
        internal string? Fail;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var url = request.RequestUri!.AbsoluteUri;
            lock (Requests) Requests.Add(url);
            if (url == Fail) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { RequestMessage = request });
            if (!data.Responses.TryGetValue(url, out var bytes) && !data.Payloads.TryGetValue(url.Split('/')[^1], out bytes))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) });
        }
    }
}
