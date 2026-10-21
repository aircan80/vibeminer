using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using VibeMiner.Protocols.Tact;
using Xunit;

namespace VibeMiner.Backend.Tests;

/// <summary>Running per-build stored figures (#5, D065): incremental credit, corrections, sharing and bounds.</summary>
public sealed class StoredCoverageTests
{
    private const string Time = "2026-10-19T00:00:00Z";
    private static string Key(int value) => (value + 1).ToString("x32", CultureInfo.InvariantCulture);
    private static (string Key, long Bytes)[] Rows(int count, int first = 0, long bytes = 100) =>
        Enumerable.Range(first, count).Select(i => (Key(i), bytes + i)).ToArray();

    [Fact]
    public async Task Fresh_open_is_zero_and_on_demand_receipts_raise_the_figure_without_a_check()
    {
        await using var h = await Harness.Create();
        var build = await h.AddBuild(1, "us");
        var ledger = await h.Ledger(Rows(10));
        await h.Open(build, "us", ledger);
        var counting = await h.Summary(build);
        Assert.Equal(BuildDownloadState.Measured, counting.State);
        Assert.Equal(StoredCountState.Counting, counting.Download!.CountState);
        await h.Drain();
        var fresh = (await h.Summary(build)).Download!;
        Assert.Equal((StoredCountState.Current, 0L, 0L, 10L), (fresh.CountState, fresh.StoredObjects, fresh.StoredBytes, fresh.ObjectCount));
        Assert.Equal("notAssessed", fresh.Presence.State); Assert.False(fresh.DownloadAll); Assert.False(fresh.Running);
        Assert.False(fresh.ChangesPending);
        // Two objects arrive on demand while researching; the figure follows without any presence check or job.
        await h.Receipt(Key(3), 103); await h.Receipt(Key(7), 107); await h.Receipt(Key(99), 9);
        // Current, but with recorded changes not applied yet: the figure is still moving.
        var behind = (await h.Summary(build)).Download!;
        Assert.Equal((StoredCountState.Current, 0L, true), (behind.CountState, behind.StoredObjects, behind.ChangesPending));
        await h.Drain();
        var after = (await h.Summary(build)).Download!;
        Assert.Equal((2L, 210L, false), (after.StoredObjects, after.StoredBytes, after.ChangesPending));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM retention_jobs"));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM stored_changes")); // applied changes are pruned
    }

    [Fact]
    public async Task Shared_content_already_stored_counts_at_open_and_open_metadata_does_not()
    {
        await using var h = await Harness.Create();
        // Content another build already stored, including a key this build shares.
        await h.Receipt(Key(1), 101); await h.Receipt(Key(4), 104); await h.Receipt(Key(50), 9);
        await h.Content(Key(6), 106);
        var build = await h.AddBuild(1, "us");
        var ledger = await h.Ledger(Rows(8));
        var source = await h.Open(build, "us", ledger);
        await h.Drain();
        var summary = (await h.Summary(build)).Download!;
        Assert.Equal((3L, 101L + 104 + 106), (summary.StoredObjects, summary.StoredBytes));
        // The download manifest Open itself verified is a metadata receipt, but it is not a member of its own ledger.
        Assert.NotNull(await h.Catalog.FindMetadataAsync(source.EncodingKey, default));
    }

    [Fact]
    public async Task A_build_opened_through_several_regions_counts_its_content_once()
    {
        await using var h = await Harness.Create();
        var build = await h.AddBuild(1, "us", "eu", "kr");
        var ledger = await h.Ledger(Rows(4));
        var source = await h.Open(build, "us", ledger);
        await h.Open(build, "eu", ledger, source);
        await h.Open(build, "kr", ledger, source);
        await h.Receipt(Key(0), 100); await h.Receipt(Key(2), 102);
        await h.Drain();
        var summary = (await h.Summary(build)).Download!;
        Assert.Equal(["eu", "kr", "us"], summary.SourceRegions);
        Assert.Equal((2L, 202L, 4L), (summary.StoredObjects, summary.StoredBytes, summary.ObjectCount));
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM stored_coverage"));
        // Download all on two regions: one figure, running while any job runs, and the newest dated check wins.
        await h.Set(new(new("wow", build, "us", ledger.Sha256), RetentionAction.DownloadAll), default);
        await h.Set(new(new("wow", build, "eu", ledger.Sha256), RetentionAction.DownloadAll), default);
        var job = (await ((IRetentionDriverCatalog)h.Catalog).ClaimNextRetentionAsync(default))!;
        var running = (await h.Summary(build)).Download!;
        Assert.True(running.DownloadAll); Assert.True(running.Running); Assert.Equal("assessing", running.Presence.State);
        Assert.Equal((2L, 202L), (running.StoredObjects, running.StoredBytes));
        await ((IRetentionDriverCatalog)h.Catalog).SettleRetentionAsync(job.Binding, RetentionPassOutcome.Shutdown, null, default);
        var stopped = (await h.Summary(build)).Download!;
        Assert.False(stopped.Running); Assert.Equal("interrupted", stopped.Presence.State);
        await h.Set(new(new("wow", build, "us", ledger.Sha256), RetentionAction.Pause), default);
        await h.Set(new(new("wow", build, "eu", ledger.Sha256), RetentionAction.Pause), default);
        var paused = (await h.Summary(build)).Download!;
        Assert.False(paused.DownloadAll); Assert.Equal((2L, 202L), (paused.StoredObjects, paused.StoredBytes));
    }

    [Fact]
    public async Task Different_builds_sharing_objects_both_follow_one_commit()
    {
        await using var h = await Harness.Create();
        var first = await h.AddBuild(1, "us"); var second = await h.AddBuild(2, "us");
        var a = await h.Ledger(Rows(6)); var b = await h.Ledger(Rows(6, first: 3));
        await h.Open(first, "us", a); await h.Open(second, "us", b);
        await h.Drain();
        await h.Receipt(Key(4), 104); await h.Receipt(Key(0), 100); await h.Receipt(Key(8), 108);
        await h.Drain();
        Assert.Equal((2L, 204L), ((await h.Summary(first)).Download!.StoredObjects, (await h.Summary(first)).Download!.StoredBytes));
        Assert.Equal((2L, 212L), ((await h.Summary(second)).Download!.StoredObjects, (await h.Summary(second)).Download!.StoredBytes));
    }

    [Fact]
    public async Task Presence_checks_and_corruption_correct_the_figure_and_repair_restores_it()
    {
        await using var h = await Harness.Create();
        var build = await h.AddBuild(1, "us");
        var rows = Rows(3);
        var ledger = await h.Ledger(rows);
        await h.Open(build, "us", ledger);
        foreach (var (key, bytes) in rows) await h.Receipt(key, bytes);
        await h.Drain();
        Assert.Equal(3, (await h.Summary(build)).Download!.StoredObjects);
        // A presence check finds the first object missing and the second corrupt.
        var target = new RetentionTarget("wow", build, "us", ledger.Sha256);
        await h.Set(new(target, RetentionAction.DownloadAll), default);
        var driver = (IRetentionDriverCatalog)h.Catalog;
        var job = (await driver.ClaimNextRetentionAsync(default))!;
        await h.Catalog.RecordObjectInvalidityAsync(Harness.Sha(rows[1].Key), rows[1].Bytes, "LOCAL_OBJECT_CORRUPT", default);
        await using (h.Catalog.RegisterRetentionLedgerSession(job.Binding, (ordinal, key, length, _) =>
            Task.FromResult(ordinal < rows.Length && rows[ordinal].Key == key && rows[ordinal].Bytes == length)))
            Assert.True(await driver.ApplyRetentionCreditAsync(job.Binding, [
                new(0, rows[0].Key, rows[0].Bytes, RetentionPresence.Missing, true),
                new(1, rows[1].Key, rows[1].Bytes, RetentionPresence.Corrupt, true),
                new(2, rows[2].Key, rows[2].Bytes, RetentionPresence.Present, true)], 3, default));
        await driver.SettleRetentionAsync(job.Binding, RetentionPassOutcome.Completed, null, default);
        await h.Drain();
        var corrected = (await h.Summary(build)).Download!;
        Assert.Equal((1L, rows[2].Bytes), (corrected.StoredObjects, corrected.StoredBytes));
        Assert.Equal(("interrupted", 1L, 1L, 3L), (corrected.Presence.State, corrected.Presence.MissingObjects, corrected.Presence.CorruptObjects, corrected.Presence.ObservedObjects));
        // Repair: a fresh verified receipt clears the missing observation; a fresh proof clears the corruption.
        await h.Receipt(rows[0].Key, rows[0].Bytes);
        var epoch = await h.Catalog.CaptureObjectIntegrityEpochAsync(default);
        await h.Catalog.ClearKnownCorruptAsync(rows[1].Key, rows[1].Bytes, Harness.Sha(rows[1].Key), epoch, default);
        await h.Drain();
        Assert.Equal((3L, ledger.TotalEncodedBytes), ((await h.Summary(build)).Download!.StoredObjects, (await h.Summary(build)).Download!.StoredBytes));
    }

    [Fact]
    public async Task One_hundred_percent_needs_every_member_so_an_unstorable_member_keeps_it_below()
    {
        await using var h = await Harness.Create();
        var build = await h.AddBuild(1, "us");
        // A zero-length member can never have a verified receipt, so this ledger is never fully stored.
        var rows = new[] { (Key(0), 0L), (Key(1), 1_000_000L), (Key(2), 9L) };
        var ledger = await h.Ledger(rows);
        await h.Open(build, "us", ledger);
        await h.Receipt(Key(1), 1_000_000); await h.Receipt(Key(2), 9);
        await h.Drain();
        var summary = (await h.Summary(build)).Download!;
        Assert.Equal(summary.TotalEncodedBytes, summary.StoredBytes);
        Assert.Equal(2, summary.StoredObjects); Assert.Equal(3, summary.ObjectCount);
    }

    [Fact]
    public async Task Never_opened_and_unmeasured_builds_are_distinct_states()
    {
        await using var h = await Harness.Create();
        var never = await h.AddBuild(1, "us"); var unmeasured = await h.AddBuild(2, "us");
        var context = await h.Catalog.ResolveOpeningAsync(new("wow", unmeasured, "us"), default);
        await h.Catalog.CommitUnavailableDownloadWorkspaceAsync(context, h.Configurations, new("DOWNLOAD_UNAVAILABLE", "No supported download manifest."), default);
        var summaries = await new DownloadSummaryService(h.Catalog).ReadAsync([new("wow", never), new("wow", unmeasured)]);
        Assert.Equal([BuildDownloadState.NotOpened, BuildDownloadState.NotMeasured], summaries.Select(s => s.State));
        Assert.All(summaries, s => Assert.Null(s.Download));
    }

    [Fact]
    public async Task Reading_never_writes_or_starts_work_and_is_bounded_to_one_hundred_builds()
    {
        await using var h = await Harness.Create();
        var build = await h.AddBuild(1, "us");
        var ledger = await h.Ledger(Rows(4));
        await h.Open(build, "us", ledger);
        await h.Receipt(Key(1), 101);
        var service = new DownloadSummaryService(h.Catalog);
        var keys = Enumerable.Range(0, 99).Select(i => new BuildKey("wow", (i + 1).ToString("x64", CultureInfo.InvariantCulture))).Append(new("wow", build)).ToArray();
        var before = await h.Snapshot();
        var summaries = await service.ReadAsync(keys);
        Assert.Equal(100, summaries.Count);
        Assert.Equal(StoredCountState.Counting, summaries[^1].Download!.CountState);
        Assert.Equal(before, await h.Snapshot());
        foreach (var bad in new BuildKey[][] { [.. keys, new("wow", new string('e', 64))], [], [keys[0], keys[0]], [new("wow", "A" + new string('a', 63))], [new("diablo", build)] })
            await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadAsync(bad));
    }

    [Fact]
    public async Task An_inconsistent_measurement_row_makes_only_its_build_unavailable()
    {
        await using var h = await Harness.Create();
        var broken = await h.AddBuild(1, "us"); var kept = await h.AddBuild(2, "us");
        await h.Open(broken, "us", await h.Ledger(Rows(3))); await h.Open(kept, "us", await h.Ledger(Rows(3, first: 1)));
        await h.Drain();
        await h.Execute($"UPDATE root_scopes SET state='unknown' WHERE build_id='{broken}'");
        var read = await new DownloadSummaryService(h.Catalog).ReadAsync([new("wow", broken), new("wow", kept)]);
        Assert.Equal((StoredCountState.Unavailable, "STORED_COVERAGE_INTEGRITY"), (read[0].Download!.CountState, read[0].Download!.CountProblem!.Code));
        Assert.Equal(StoredCountState.Current, read[1].Download!.CountState);
    }

    [Fact]
    public async Task A_missing_ledger_is_unavailable_without_blocking_others_and_a_new_open_counts_again()
    {
        await using var h = await Harness.Create();
        var lost = await h.AddBuild(1, "us"); var kept = await h.AddBuild(2, "us");
        var lostLedger = await h.Ledger(Rows(3)); var keptLedger = await h.Ledger(Rows(3, first: 1));
        var source = await h.Open(lost, "us", lostLedger); await h.Open(kept, "us", keptLedger);
        await h.Drain();
        await h.Worker.DisposeAsync(); h.Worker = h.NewWorker(); // a later engine run: the ledger is no longer held open
        File.Delete(h.ObjectPath(lostLedger.Sha256));
        await h.Receipt(Key(1), 101);
        await h.Drain();
        var unavailable = (await h.Summary(lost)).Download!;
        Assert.Equal(StoredCountState.Unavailable, unavailable.CountState);
        Assert.Equal("LOCAL_OBJECT_MISSING", unavailable.CountProblem!.Code);
        Assert.Equal(1, (await h.Summary(kept)).Download!.StoredObjects);
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM stored_changes"));
        // Opening the build again republishes the same immutable ledger and starts its count over.
        await h.Ledger(Rows(3));
        await h.Open(lost, "us", lostLedger, source);
        Assert.Equal(StoredCountState.Counting, (await h.Summary(lost)).Download!.CountState);
        await h.Drain();
        var recounted = (await h.Summary(lost)).Download!;
        Assert.Equal((StoredCountState.Current, 1L), (recounted.CountState, recounted.StoredObjects));
        Assert.Null(recounted.CountProblem);
    }

    [Fact]
    public async Task An_interrupted_first_count_resumes_after_restart_and_changes_during_it_are_not_lost()
    {
        await using var h = await Harness.Create();
        // More retained objects than one 1024-key slice, so the first count takes two rounds.
        var rows = Rows(1100);
        foreach (var (key, bytes) in rows) await h.Receipt(key, bytes);
        var build = await h.AddBuild(1, "us");
        var ledger = await h.Ledger(rows.Where((_, i) => i % 2 == 0).ToArray());
        await h.Open(build, "us", ledger);
        await h.Worker.RoundAsync(default);
        var partial = (await h.Summary(build)).Download!;
        Assert.Equal(StoredCountState.Counting, partial.CountState);
        Assert.InRange(partial.StoredObjects, 1, 549);
        // A change behind the scan position while counting, then a restart.
        await h.Catalog.RecordObjectInvalidityAsync(Harness.Sha(Key(0)), 100, "LOCAL_OBJECT_CORRUPT", default);
        await h.Restart();
        await h.Drain();
        var complete = (await h.Summary(build)).Download!;
        Assert.Equal((StoredCountState.Current, 549L), (complete.CountState, complete.StoredObjects));
        Assert.Equal(rows.Where((_, i) => i % 2 == 0).Skip(1).Sum(r => r.Bytes), complete.StoredBytes);
    }

    [Fact]
    public async Task Every_change_source_wakes_the_worker_and_its_loop_applies_them()
    {
        await using var h = await Harness.Create();
        var build = await h.AddBuild(1, "us");
        var ledger = await h.Ledger(Rows(3));
        await h.Open(build, "us", ledger);
        await h.Catalog.WaitForStoredChangeAsync(default).WaitAsync(TimeSpan.FromSeconds(10)); // the admission
        var wake = h.Catalog.WaitForStoredChangeAsync(default);
        Assert.False(wake.IsCompleted);
        await h.Receipt(Key(2), 102);
        await wake.WaitAsync(TimeSpan.FromSeconds(10));
        await h.Worker.DisposeAsync();
        h.Worker = h.NewWorker(TimeSpan.Zero);
        h.Worker.Start();
        // Deadlock guard only: the loop's first round applies the pending change.
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while ((await h.Summary(build)).Download!.StoredObjects != 1)
        { Assert.True(DateTime.UtcNow < deadline, "The worker loop did not apply the pending change."); await Task.Delay(20); }
        await h.Worker.DisposeAsync(); h.Worker = h.NewWorker();
    }

    [Fact]
    public async Task More_than_64_ledgers_all_progress_round_robin_and_reopening_never_proves_a_ledger_twice()
    {
        await using var h = await Harness.Create();
        const int count = 70;
        var builds = new List<(string Build, DownloadLedgerArtifact Ledger)>();
        for (var i = 0; i < count; i++)
        {
            var build = await h.AddBuild(i + 1, "us");
            // Each ledger shares Key(0) and has members of its own.
            var ledger = await h.Ledger([(Key(0), 100), (Key(1000 + 2 * i), 10), (Key(1001 + 2 * i), 11)]);
            await h.Open(build, "us", ledger); builds.Add((build, ledger));
        }
        await h.Drain();
        Assert.Equal(count, h.Worker.LedgerProofs);
        // Every round reopens more ledgers than the eight it holds, but only checks their headers.
        await h.Receipt(Key(0), 100);
        await h.Drain();
        foreach (var (build, _) in builds) Assert.Equal((StoredCountState.Current, 1L), ((await h.Summary(build)).Download!.CountState, (await h.Summary(build)).Download!.StoredObjects));
        Assert.Equal(count, h.Worker.LedgerProofs);
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM stored_changes"));
        // Under steady commits, the ledgers past the first 64 are reached in the next round rather than starved.
        await h.Receipt(Key(1000), 10);
        var firstChange = await h.Scalar("SELECT seq FROM sqlite_sequence WHERE name='stored_changes'");
        Assert.True(await h.Worker.RoundAsync(default));
        await h.Receipt(Key(1002), 10);
        Assert.True(await h.Worker.RoundAsync(default));
        // Two rounds of at most 64 ledgers each reached all 70, so none still waits for the first change.
        Assert.Equal(0, await h.Scalar($"SELECT COUNT(*) FROM stored_coverage WHERE through_seq<{firstChange}"));
        await h.Drain();
        Assert.Equal(2L, (await h.Summary(builds[0].Build)).Download!.StoredObjects);
        Assert.Equal(2L, (await h.Summary(builds[1].Build)).Download!.StoredObjects);
        Assert.Equal(1L, (await h.Summary(builds[2].Build)).Download!.StoredObjects);
        Assert.Equal(count, h.Worker.LedgerProofs);
    }

    [Fact]
    public async Task A_changed_ledger_file_is_proved_again_before_it_is_read()
    {
        await using var h = await Harness.Create();
        var builds = new List<string>();
        for (var i = 0; i < 9; i++)
        {
            var build = await h.AddBuild(i + 1, "us"); builds.Add(build);
            await h.Open(build, "us", await h.Ledger([(Key(0), 100), (Key(500 + i), 10)]));
        }
        await h.Drain();
        Assert.Equal(9, h.Worker.LedgerProofs);
        // Replace one ledger file with the same bytes: same content, different file, so it is proved once more.
        await h.Receipt(Key(0), 100); await h.Drain();
        Assert.Equal(9, h.Worker.LedgerProofs);
        var sha = await h.Text("SELECT ledger FROM stored_coverage ORDER BY ledger LIMIT 1");
        var path = h.ObjectPath(sha); var bytes = await File.ReadAllBytesAsync(path);
        File.Delete(path); await File.WriteAllBytesAsync(path, bytes); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        await h.Worker.CloseLedgersAsync();
        for (var i = 0; i < 9; i++) await h.Receipt(Key(500 + i), 10);
        await h.Drain();
        Assert.Equal(10, h.Worker.LedgerProofs);
        foreach (var build in builds) Assert.Equal(2L, (await h.Summary(build)).Download!.StoredObjects);
    }

    [Fact]
    public async Task A_ledger_that_keeps_failing_is_given_up_alone_and_stops_holding_back_pruning()
    {
        await using var h = await Harness.Create();
        var bad = await h.AddBuild(1, "us"); var good = await h.AddBuild(2, "us");
        await h.Open(bad, "us", await h.Ledger(Rows(3))); await h.Open(good, "us", await h.Ledger(Rows(3, first: 1)));
        await h.Receipt(Key(1), 101);
        await h.Drain();
        var badLedger = (await h.Summary(bad)).Download!.LedgerId;
        // An inconsistent bit page makes every change batch of this ledger fail.
        await h.Execute($"UPDATE stored_pages SET stored_count=stored_count+1 WHERE ledger='{badLedger}'");
        await h.Receipt(Key(2), 102);
        for (var round = 1; round < StoredCoverageWorker.MaximumLedgerFailures; round++)
        {
            Assert.True(await h.Worker.RoundAsync(default));
            Assert.Equal(StoredCountState.Current, (await h.Summary(bad)).Download!.CountState);
            Assert.Equal(2L, (await h.Summary(good)).Download!.StoredObjects); // the other ledger was not held back
        }
        Assert.True(await h.Worker.RoundAsync(default));
        var given = (await h.Summary(bad)).Download!;
        Assert.Equal((StoredCountState.Unavailable, "STORED_COVERAGE_FAILED"), (given.CountState, given.CountProblem!.Code));
        Assert.False(await h.Worker.RoundAsync(default));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM stored_changes"));
        Assert.Equal(2L, (await h.Summary(good)).Download!.StoredObjects);
    }

    [Fact]
    public async Task Rounds_are_paced_even_while_work_remains()
    {
        await using var h = await Harness.Create();
        foreach (var (key, bytes) in Rows(1100)) await h.Receipt(key, bytes);
        var build = await h.AddBuild(1, "us");
        await h.Open(build, "us", await h.Ledger(Rows(1100)));
        var clock = new ManualClock();
        await h.Worker.DisposeAsync(); h.Worker = h.NewWorker(TimeSpan.FromSeconds(1), clock);
        var drain = h.Worker.DrainAsync(default);
        // The first round counts 1024 keys; the second waits for the interval before it starts.
        await clock.Created.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(StoredCountState.Counting, (await h.Summary(build)).Download!.CountState);
        Assert.False(drain.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        // The second round finishes the count, and even the next, empty round waits its turn.
        await clock.Created.WaitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((StoredCountState.Current, 1100L), ((await h.Summary(build)).Download!.CountState, (await h.Summary(build)).Download!.StoredObjects));
        Assert.False(drain.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await drain.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task One_inconsistent_build_does_not_fail_the_whole_read()
    {
        await using var h = await Harness.Create();
        var bad = await h.AddBuild(1, "us"); var good = await h.AddBuild(2, "us");
        var badLedger = await h.Ledger(Rows(3));
        await h.Open(bad, "us", badLedger); await h.Open(good, "us", await h.Ledger(Rows(3, first: 1)));
        await h.Receipt(Key(1), 101); await h.Drain();
        await h.Execute($"UPDATE stored_coverage SET stored_count=99 WHERE ledger='{badLedger.Sha256}'");
        var summaries = await new DownloadSummaryService(h.Catalog).ReadAsync([new("wow", bad), new("wow", good)]);
        Assert.Equal((StoredCountState.Unavailable, "STORED_COVERAGE_INTEGRITY"), (summaries[0].Download!.CountState, summaries[0].Download!.CountProblem!.Code));
        Assert.Equal(["us"], summaries[0].Download!.SourceRegions);
        Assert.Equal((StoredCountState.Current, 1L), (summaries[1].Download!.CountState, summaries[1].Download!.StoredObjects));
        // The worker gives up that row on its own as well, and keeps the other ledger going.
        await h.Receipt(Key(2), 102); await h.Drain();
        Assert.Equal("STORED_COVERAGE_INTEGRITY", (await h.Summary(bad)).Download!.CountProblem!.Code);
        Assert.Equal(2L, (await h.Summary(good)).Download!.StoredObjects);
    }

    [Fact]
    public async Task Presence_checks_correct_members_stored_only_as_content_or_metadata_receipts()
    {
        await using var h = await Harness.Create();
        var build = await h.AddBuild(1, "us");
        var rows = Rows(2);
        var ledger = await h.Ledger(rows);
        await h.Open(build, "us", ledger);
        await h.Content(rows[0].Key, rows[0].Bytes);
        await h.Metadata(rows[1].Key, rows[1].Bytes);
        await h.Drain();
        Assert.Equal(2L, (await h.Summary(build)).Download!.StoredObjects);
        var target = new RetentionTarget("wow", build, "us", ledger.Sha256);
        await h.Set(new(target, RetentionAction.DownloadAll), default);
        var driver = (IRetentionDriverCatalog)h.Catalog;
        var job = (await driver.ClaimNextRetentionAsync(default))!;
        await using (h.Catalog.RegisterRetentionLedgerSession(job.Binding, (ordinal, key, length, _) =>
            Task.FromResult(ordinal < rows.Length && rows[ordinal].Key == key && rows[ordinal].Bytes == length)))
            Assert.True(await driver.ApplyRetentionCreditAsync(job.Binding, [
                new(0, rows[0].Key, rows[0].Bytes, RetentionPresence.Missing, true),
                new(1, rows[1].Key, rows[1].Bytes, RetentionPresence.Missing, true)], 2, default));
        await h.Drain();
        Assert.Equal(0L, (await h.Summary(build)).Download!.StoredObjects);
        // A fresh verified content receipt is current proof again.
        await h.Content(rows[0].Key, rows[0].Bytes);
        await h.Drain();
        Assert.Equal(1L, (await h.Summary(build)).Download!.StoredObjects);
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<(long Due, TimerCallback Callback, object? State)> timers = [];
        private long ticks;
        internal SemaphoreSlim Created { get; } = new(0);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return ticks; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (gate) timers.Add((ticks + dueTime.Ticks, callback, state));
            Created.Release();
            return new Timer();
        }
        internal void Advance(TimeSpan by)
        {
            (long Due, TimerCallback Callback, object? State)[] ready;
            lock (gate) { ticks += by.Ticks; ready = timers.Where(t => t.Due <= ticks).ToArray(); timers.RemoveAll(t => t.Due <= ticks); }
            foreach (var timer in ready) timer.Callback(timer.State);
        }
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    internal sealed class Harness : IAsyncDisposable
    {
        internal InventoryFixture Fixture { get; } = InventoryFixture.Create();
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "vibeminer-stored-coverage-" + Guid.NewGuid().ToString("N"));
        internal DiscoveryCatalog Catalog { get; private set; } = null!;
        internal VerifiedObjectStore Objects { get; private set; } = null!;
        internal AcquisitionResources Resources { get; } = new();
        internal StoredCoverageWorker Worker { get; set; } = null!;
        internal IReadOnlyList<ConfigurationReceipt> Configurations { get; private set; } = [];
        private readonly Dictionary<string, MetadataReceipt> sources = new(StringComparer.Ordinal);
        internal static string Sha(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(key)));

        internal static async Task<Harness> Create()
        {
            var h = new Harness();
            h.Catalog = await DiscoveryCatalog.OpenAsync(h.Directory); h.Objects = new VerifiedObjectStore(h.Directory); h.Worker = h.NewWorker();
            var configs = new List<ConfigurationReceipt>();
            foreach (var (role, key, bytes) in new[] { ("buildConfig", h.Fixture.BuildKey, h.Fixture.BuildConfig), ("cdnConfig", h.Fixture.CdnKey, h.Fixture.CdnConfig) })
            {
                var url = InventoryFixture.Url("config", key); await h.Catalog.RecordConfigurationAsync(new(key, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, url, Time), default);
                configs.Add(new(role, key, Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, url, Time, Time, 1));
            }
            h.Configurations = configs;
            return h;
        }
        // Tests drain without the one-second pacing unless they set a clock.
        internal StoredCoverageWorker NewWorker(TimeSpan? interval = null, TimeProvider? clock = null) => new(Catalog, Objects, Resources, clock, interval ?? TimeSpan.Zero);

        /// <summary>Commits one build observed in the given regions and returns its exact identity.</summary>
        internal async Task<string> AddBuild(int index, params string[] regions)
        {
            var build = (12345 + index).ToString(CultureInfo.InvariantCulture);
            var versions = Encoding.UTF8.GetBytes("Region!STRING:0|BuildConfig!HEX:16|CDNConfig!HEX:16|KeyRing!HEX:16|BuildId!DEC:4|VersionsName!STRING:0|ProductConfig!HEX:16\n" +
                string.Concat(regions.Select(r => $"{r}|{Fixture.BuildKey}|{Fixture.CdnKey}||{build}|1.2.3.{build}|\n")));
            var cdns = Encoding.UTF8.GetBytes("Name!STRING:0|Path!STRING:0|Hosts!STRING:0|Servers!STRING:0\n" +
                string.Concat(regions.Select(r => $"{r}|tpr/wow|{r}.cdn.blizzard.com|https://{r}.cdn.blizzard.com/\n")));
            var versionRows = MetadataParser.Parse(versions).Rows; var cdnRows = MetadataParser.Parse(cdns).Rows;
            var identity = BuildIdentity.Create("wow", versionRows[0].Fields);
            SourceDocument Document(string role, byte[] bytes) => new(role, "https://us.version.battle.net/v2/products/wow/" + role, Time,
                Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes);
            var observations = versionRows.Select(row =>
            {
                var cdn = cdnRows.Single(c => c.Fields["Name"] == row.Fields["Region"]);
                return new BuildObservation(identity, row.Fields["Region"], row.Ordinal, cdn.Ordinal, row.Fields, cdn.Fields);
            }).ToArray();
            var attempt = new DiscoveryAttempt([Document("versions", versions), Document("cdns", cdns)], observations, regions, regions, [], "online");
            var generation = await Catalog.BeginRefreshAsync("wow", default);
            await Catalog.CommitAsync("wow", generation, attempt, Time, default);
            return identity.Id;
        }

        /// <summary>Publishes an immutable sorted ledger with these exact members into the object store.</summary>
        internal async Task<DownloadLedgerArtifact> Ledger((string Key, long Bytes)[] rows)
        {
            var sorted = rows.OrderBy(r => r.Key, StringComparer.Ordinal).ToArray();
            var bytes = new byte[32 + 24 * sorted.Length];
            "VMDLEDG1"u8.CopyTo(bytes); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), 1); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12), 24);
            BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(16), sorted.Length); BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(24), sorted.Sum(r => r.Bytes));
            for (var i = 0; i < sorted.Length; i++)
            {
                Convert.FromHexString(sorted[i].Key).CopyTo(bytes, 32 + 24 * i);
                BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(32 + 24 * i + 16), sorted[i].Bytes);
            }
            var sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            await Objects.PublishAsync(bytes, sha, default);
            return new(sha, bytes.Length, sorted.Length, sorted.Sum(r => r.Bytes));
        }
        internal string ObjectPath(string sha) => Path.Combine(Directory, "objects-v1", sha[..2], sha[2..]);

        /// <summary>Admits the ledger for this build and region, as a measured Open does.</summary>
        internal async Task<MetadataReceipt> Open(string build, string region, DownloadLedgerArtifact ledger, MetadataReceipt? source = null)
        {
            if (source is null && !sources.TryGetValue(ledger.Sha256, out source))
            {
                // Every ledger comes from its own authenticated download manifest.
                var ekey = Convert.ToHexStringLower(MD5.HashData(Encoding.ASCII.GetBytes("dl:" + ledger.Sha256)));
                source = new("download", Convert.ToHexStringLower(MD5.HashData(Encoding.ASCII.GetBytes("ck:" + ledger.Sha256))), ekey,
                    Sha(ekey), "64", "55", InventoryFixture.Url("data", ekey), Time, Time, "download-v1");
                await Catalog.RecordMetadataAsync(source, default);
                await Catalog.RecordDownloadArtifactAsync(source, ledger, default);
                sources.Add(ledger.Sha256, source);
            }
            var context = await Catalog.ResolveOpeningAsync(new("wow", build, region), default);
            await Catalog.CommitDownloadWorkspaceAsync(context, Configurations, [source], ledger, [],
                Configurations.Sum(c => (long)c.ByteLength) + 64, default);
            // These authored builds have no readable file list, so their scope is the manifest (D066).
            while (await Catalog.ReadNextRootScopeAsync(default) is { } work)
                await Catalog.FailRootScopeAsync(work, true, new("ROOT_UNSUPPORTED", "Authored build without a file list.", false), default);
            return source;
        }
        internal Task Receipt(string key, long bytes) => Catalog.RecordEncodedReceiptAsync(new(key, Sha(key), bytes, InventoryFixture.Url("data", key), Time, Time,
            "blte-encoded-v1", new("loose", "0", bytes.ToString(CultureInfo.InvariantCulture), null)), default);
        internal Task Content(string key, long bytes) => Catalog.RecordContentAsync(new ContentReceipt(
            Convert.ToHexStringLower(MD5.HashData(Encoding.ASCII.GetBytes("ck:" + key))), key, Sha(key), bytes.ToString(CultureInfo.InvariantCulture), "1",
            InventoryFixture.Url("data", key), Time, Time, "content-blte-v1", new("loose", "0", bytes.ToString(CultureInfo.InvariantCulture), null)), default);
        internal Task Drain() => Worker.DrainAsync(default);
        internal async Task Execute(string sql)
        { await using var db = new SqliteConnection($"Data Source={Path.Combine(Directory, "discovery-v1.sqlite3")};Pooling=False"); await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
        /// <summary>A verified ROOT-like metadata receipt; ROOT is a DL member.</summary>
        internal Task Metadata(string key, long bytes) => Catalog.RecordMetadataAsync(new("root",
            Convert.ToHexStringLower(MD5.HashData(Encoding.ASCII.GetBytes("ck:" + key))), key, Sha(key), bytes.ToString(CultureInfo.InvariantCulture), "1",
            InventoryFixture.Url("data", key), Time, Time, "root-v1"), default);
        internal Task<RetentionSnapshot> Set(RetentionSetRequest request, CancellationToken token) => ((IRetentionStore)Catalog).SetAsync(request, token);
        internal async Task<BuildDownloadSummary> Summary(string build) => (await new DownloadSummaryService(Catalog).ReadAsync([new("wow", build)])).Single();
        internal async Task<string> Snapshot()
        {
            var parts = new List<string>();
            foreach (var table in new[] { "stored_changes", "stored_coverage", "stored_pages", "object_absence", "retention_jobs", "encoded_objects" })
                parts.Add(table + ":" + await Scalar($"SELECT COUNT(*) FROM {table}"));
            parts.Add("coverage:" + await Text("SELECT COALESCE(group_concat(ledger||stored_count||stored_bytes||through_seq||COALESCE(scan_after,'-')||updated_at),'') FROM stored_coverage"));
            return string.Join(',', parts);
        }
        internal async Task<long> Scalar(string sql)
        { await using var db = new SqliteConnection($"Data Source={Path.Combine(Directory, "discovery-v1.sqlite3")};Pooling=False"); await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture); }
        internal async Task<string> Text(string sql)
        { await using var db = new SqliteConnection($"Data Source={Path.Combine(Directory, "discovery-v1.sqlite3")};Pooling=False"); await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = sql; return (string)(await command.ExecuteScalarAsync())!; }
        internal async Task Restart()
        {
            await Worker.DisposeAsync(); await Objects.DisposeAsync(); await Catalog.DisposeAsync();
            Catalog = await DiscoveryCatalog.OpenAsync(Directory); Objects = new VerifiedObjectStore(Directory); Worker = NewWorker();
        }
        public async ValueTask DisposeAsync()
        {
            await Worker.DisposeAsync(); await Objects.DisposeAsync(); await Catalog.DisposeAsync();
            System.IO.Directory.Delete(Directory, true);
        }
    }
}
