using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class RetentionCatalogTests
{
    [Fact]
    public async Task Read_does_not_create_intent_and_exact_admitted_identity_is_required()
    {
        await using var h = await Harness.Create();
        var snapshot = await h.Store.ReadAsync(h.Target, default);
        Assert.Null(snapshot.JobId); Assert.Equal(RetentionState.OnDemand, snapshot.State);
        Assert.Empty(await h.Store.ListAsync(default)); Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM retention_jobs"));
        foreach (var target in new[] { h.Target with { ProductId = "wowt" }, h.Target with { BuildIdentity = new('f', 64) },
            h.Target with { SourceRegion = "eu" }, h.Target with { LedgerId = new('e', 64) } })
            Assert.Equal("DOWNLOAD_NOT_READY", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.ReadAsync(target, default))).Code);
        Assert.Null(await h.Driver.ClaimNextRetentionAsync(default));
    }

    [Fact]
    public async Task Intent_ack_idempotence_revisions_waiting_retry_and_original_plan_survive_restart()
    {
        await using var h = await Harness.Create();
        var first = await h.Set(RetentionAction.DownloadAll); Assert.Equal(1, first.Revision);
        Assert.Equal(first, await h.Set(RetentionAction.DownloadAll));
        var job = (await h.Driver.ClaimNextRetentionAsync(default))!;
        Assert.Equal(h.Context.Reference, job.Plan.SourceReference); Assert.Equal(h.Source, job.Plan.DownloadSource);
        await h.Driver.SettleRetentionAsync(job.Binding, RetentionPassOutcome.Waiting, new("OFFLINE", "Offline.", true), default);
        Assert.Equal(RetentionState.Waiting, (await h.Set(RetentionAction.DownloadAll)).State);
        Assert.Null(await h.Driver.ClaimNextRetentionAsync(default));
        await h.Restart(); await h.Driver.RestoreDriverAsync(default);
        Assert.Null(await h.Driver.ClaimNextRetentionAsync(default));
        var resumed = await h.Set(RetentionAction.Resume); Assert.Equal(2, resumed.Revision); Assert.Equal(RetentionState.Queued, resumed.State);
        Assert.Equal(resumed, await h.Set(RetentionAction.Resume));
        var current = (await h.Driver.ClaimNextRetentionAsync(default))!;
        Assert.Equal(job.Plan.SourceReference, current.Plan.SourceReference); Assert.True(current.Binding.Generation > job.Binding.Generation);
        Assert.Equal("RETENTION_STALE", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.SetAsync(new(h.Target, RetentionAction.Pause, 1), default))).Code);
    }

    [Fact]
    public async Task Nine_slots_include_paused_and_cleanup_owned_on_demand_without_reordering_fifo()
    {
        await using var h = await Harness.Create(); var targets = new List<RetentionTarget> { h.Target };
        for (var i = 1; i < 10; i++) targets.Add(await h.AddBuild(i));
        foreach (var target in targets.Take(9)) await h.Store.SetAsync(new(target, RetentionAction.DownloadAll), default);
        await h.Store.SetAsync(new(targets[1], RetentionAction.Pause), default);
        Assert.Equal(9, (await h.Store.ListAsync(default)).Count);
        Assert.Equal("RETENTION_CAPACITY", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.SetAsync(new(targets[9], RetentionAction.DownloadAll), default))).Code);
        var active = (await h.Driver.ClaimNextRetentionAsync(default))!; Assert.Equal(targets[0], active.Plan.Target);
        Assert.Null(await h.Driver.ClaimNextRetentionAsync(default));
        var stopping = await h.Store.SetAsync(new(targets[0], RetentionAction.OnDemand), default); Assert.Equal(RetentionState.Stopping, stopping.State);
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Store.SetAsync(new(targets[9], RetentionAction.DownloadAll), default));
        await h.Driver.SettleRetentionAsync(active.Binding, RetentionPassOutcome.IntentChanged, null, default);
        await h.Store.SetAsync(new(targets[9], RetentionAction.DownloadAll), default);
        Assert.Equal(targets[2], (await h.Driver.ClaimNextRetentionAsync(default))!.Plan.Target);
    }

    [Fact]
    public async Task Credit_requires_exact_registered_membership_and_committed_receipt_then_counts_once_across_restart()
    {
        await using var h = await Harness.Create(); await h.Set(RetentionAction.DownloadAll);
        var job = (await h.Driver.ClaimNextRetentionAsync(default))!;
        var first = h.Observation(0, RetentionPresence.Present);
        Assert.False(await h.Driver.ApplyRetentionCreditAsync(job.Binding, [first], 1, default));
        await using (h.Register(job))
        {
            // A caller's HasReceipt=true cannot publish historical proof without a durable alias.
            await h.Driver.ApplyRetentionCreditAsync(job.Binding, [first], 1, default);
            var missingProof = await h.Read(); Assert.Equal(0, missingProof.Coverage.ReceiptObjects); Assert.Equal(1, missingProof.UnresolvedObjects);
            await h.Driver.ApplyRetentionCreditAsync(job.Binding, [h.Observation(1, RetentionPresence.Missing)], 2, default);
            await Assert.ThrowsAsync<DiscoveryException>(() => h.Driver.ApplyRetentionCreditAsync(job.Binding, [first], 1, default));
        }
        await h.Driver.SettleRetentionAsync(job.Binding, RetentionPassOutcome.Completed, null, default);
        Assert.Equal(RetentionState.Failed, (await h.Read()).State); // EOF is insufficient.
        await h.RecordPayloads(); await h.Set(RetentionAction.Resume);
        var retry = (await h.Driver.ClaimNextRetentionAsync(default))!;
        await using (h.Register(retry)) await h.Driver.ApplyRetentionCreditAsync(retry.Binding, [h.Observation(0), h.Observation(1)], 2, default);
        await h.Driver.SettleRetentionAsync(retry.Binding, RetentionPassOutcome.Completed, null, default);
        var complete = await h.Read(); Assert.Equal(RetentionState.Completed, complete.State); Assert.Equal(2, complete.Coverage.ReceiptObjects);
        Assert.Equal(h.Artifact.TotalEncodedBytes, complete.Coverage.ReceiptBytes); Assert.Equal("assessed", complete.Coverage.PresenceState);
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM retention_bits"));
        await h.Set(RetentionAction.OnDemand); await h.Set(RetentionAction.DownloadAll); await h.Restart(); await h.Driver.RestoreDriverAsync(default);
        var another = (await h.Driver.ClaimNextRetentionAsync(default))!;
        await using (h.Register(another)) await h.Driver.ApplyRetentionCreditAsync(another.Binding, [h.Observation(0), h.Observation(1)], 2, default);
        Assert.Equal(2, (await h.Read()).Coverage.ReceiptObjects);
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM inventory_units"));
    }

    [Fact]
    public async Task Another_build_shares_historical_ledger_bits_but_gets_its_own_presence_pass()
    {
        await using var h = await Harness.Create(); await h.RecordPayloads(); await h.Set(RetentionAction.DownloadAll);
        var job = (await h.Driver.ClaimNextRetentionAsync(default))!;
        await using (h.Register(job)) await h.Driver.ApplyRetentionCreditAsync(job.Binding, [h.Observation(0), h.Observation(1)], 2, default);
        await h.Driver.SettleRetentionAsync(job.Binding, RetentionPassOutcome.Completed, null, default);
        var other = await h.AddBuild(1); var snapshot = await h.Store.ReadAsync(other, default);
        Assert.Equal(2, snapshot.Coverage.ReceiptObjects); Assert.Equal("notAssessed", snapshot.Coverage.PresenceState);
        Assert.Equal(0, snapshot.Coverage.PresentObjects);
    }

    [Fact]
    public async Task Pause_resume_late_results_and_cleanup_settlement_are_generation_fenced()
    {
        await using var h = await Harness.Create(); await h.RecordPayloads(); await h.Set(RetentionAction.DownloadAll);
        var job = (await h.Driver.ClaimNextRetentionAsync(default))!; await using var session = h.Register(job);
        await h.Set(RetentionAction.Pause); await h.Set(RetentionAction.Resume);
        Assert.False(await h.Driver.ReportRetentionProgressAsync(job.Binding, new("Late", "objects"), 1, 10, default));
        Assert.False(await h.Driver.ApplyRetentionCreditAsync(job.Binding, [h.Observation(0)], 1, default));
        await h.Driver.SettleRetentionAsync(job.Binding, RetentionPassOutcome.Completed, null, default);
        Assert.Equal(RetentionState.Stopping, (await h.Read()).State);
        await h.Driver.SettleRetentionAsync(job.Binding, RetentionPassOutcome.IntentChanged, null, default);
        var next = (await h.Driver.ClaimNextRetentionAsync(default))!;
        await h.Driver.SettleRetentionAsync(job.Binding, RetentionPassOutcome.IntentChanged, null, default);
        Assert.Equal(RetentionState.Preparing, (await h.Read()).State); Assert.True(next.Binding.Generation > job.Binding.Generation);
    }

    [Fact]
    public async Task Session_withdrawal_drains_async_membership_and_pause_during_check_prevents_SQL_credit()
    {
        await using var h = await Harness.Create(); await h.RecordPayloads(); await h.Set(RetentionAction.DownloadAll);
        var job = (await h.Driver.ClaimNextRetentionAsync(default))!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = h.Catalog.RegisterRetentionLedgerSession(job.Binding, async (_, _, _, _) => { entered.SetResult(); await release.Task; return true; });
        var apply = h.Driver.ApplyRetentionCreditAsync(job.Binding, [h.Observation(0)], 1, default);
        await entered.Task; var dispose = registration.DisposeAsync().AsTask(); Assert.False(dispose.IsCompleted);
        // SQL remains responsive while the independent ledger callback is waiting.
        await h.Set(RetentionAction.Pause); release.SetResult();
        Assert.False(await apply); await dispose; Assert.Equal(0, (await h.Read()).Coverage.ReceiptObjects);
    }

    [Fact]
    public async Task Present_stat_cannot_clear_known_corruption_and_historical_receipts_survive_missing_bytes()
    {
        await using var h = await Harness.Create(); await h.RecordPayloads();
        var receipt = (await h.Catalog.FindEncodedReceiptAsync(h.Members[0].EncodingKey, h.Members[0].EncodedBytes, default))!;
        await h.Catalog.RecordObjectInvalidityAsync(receipt.Sha256, receipt.EncodedBytes, "LOCAL_OBJECT_CORRUPT", default);
        await h.Set(RetentionAction.DownloadAll); var job = (await h.Driver.ClaimNextRetentionAsync(default))!;
        await using (h.Register(job)) await h.Driver.ApplyRetentionCreditAsync(job.Binding,
            [h.Observation(0), h.Observation(1, RetentionPresence.Missing)], 2, default);
        var snapshot = await h.Read(); Assert.Equal(2, snapshot.Coverage.ReceiptObjects);
        Assert.Equal(1, snapshot.Coverage.CorruptObjects); Assert.Equal(1, snapshot.Coverage.MissingObjects); Assert.Equal(0, snapshot.Coverage.PresentObjects);
        await h.Catalog.RecordEncodedReceiptAsync(receipt, default); // Historical publication is not a new physical proof.
        Assert.Single(await h.Catalog.ReadKnownCorruptAsync([(receipt.EncodingKey, receipt.EncodedBytes)], default));
        await h.Catalog.ClearKnownCorruptAsync(receipt.EncodingKey, receipt.EncodedBytes, receipt.Sha256, await h.Catalog.CaptureObjectIntegrityEpochAsync(default), default);
        Assert.Empty(await h.Catalog.ReadKnownCorruptAsync([(receipt.EncodingKey, receipt.EncodedBytes)], default));
    }

    [Fact]
    public async Task Corruption_journal_is_SHA_scoped_validates_aliases_and_is_bounded()
    {
        await using var h = await Harness.Create(); await h.RecordPayloads();
        var member = h.Members[0]; var receipt = (await h.Catalog.FindEncodedReceiptAsync(member.EncodingKey, member.EncodedBytes, default))!;
        var alias = receipt with { EncodingKey = new('a', 32), SourceUrl = InventoryFixture.Url("data", new('a', 32)) };
        await h.Catalog.RecordEncodedReceiptAsync(alias, default);
        await h.Catalog.RecordEncodedInvalidityAsync(receipt.EncodingKey, receipt.EncodedBytes, receipt.Sha256, "BLTE_INVALID", default);
        Assert.Equal(2, (await h.Catalog.ReadKnownCorruptAsync([(receipt.EncodingKey, receipt.EncodedBytes), (alias.EncodingKey, alias.EncodedBytes)], default)).Count);
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ClearKnownCorruptAsync(receipt.EncodingKey, receipt.EncodedBytes, new('f', 64), 0, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordEncodedInvalidityAsync(new('b', 32), receipt.EncodedBytes, receipt.Sha256, "BLTE_INVALID", default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordObjectInvalidityAsync(receipt.Sha256, receipt.EncodedBytes, "LOCAL_OBJECT_MISSING", default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ReadKnownCorruptAsync(Enumerable.Repeat((receipt.EncodingKey, receipt.EncodedBytes), 129).ToArray(), default));
        await h.Restart(); Assert.Single(await h.Catalog.ReadKnownCorruptAsync([(alias.EncodingKey, alias.EncodedBytes)], default));
    }

    [Theory]
    [InlineData("member")]
    [InlineData("ordinal")]
    [InlineData("size")]
    [InlineData("batch")]
    public async Task Invalid_membership_or_batch_never_changes_coverage(string defect)
    {
        await using var h = await Harness.Create(); await h.RecordPayloads(); await h.Set(RetentionAction.DownloadAll);
        var job = (await h.Driver.ClaimNextRetentionAsync(default))!; await using var session = h.Register(job);
        var observation = h.Observation(0);
        var batch = defect switch { "member" => new[] { observation with { EncodingKey = new('f', 32) } },
            "ordinal" => [observation with { Ordinal = 1 }], "size" => [observation with { EncodedBytes = observation.EncodedBytes + 1 }],
            _ => Enumerable.Repeat(observation, 65).ToArray() };
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Driver.ApplyRetentionCreditAsync(job.Binding, batch, batch.Length, default));
        Assert.Equal(0, (await h.Read()).Coverage.ReceiptObjects);
    }

    [Theory]
    [InlineData("UPDATE retention_coverage SET receipt_count=3")]
    [InlineData("UPDATE retention_bits SET bits=zeroblob(512)")]
    [InlineData("UPDATE retention_bits SET page=2")]
    [InlineData("UPDATE retention_bits SET receipt_bytes=receipt_bytes+1")]
    public async Task Corrupt_compact_credit_geometry_is_refused(string sql)
    {
        await using var h = await Harness.Create(); await h.RecordPayloads(); await h.Set(RetentionAction.DownloadAll);
        var job = (await h.Driver.ClaimNextRetentionAsync(default))!;
        await using (h.Register(job)) await h.Driver.ApplyRetentionCreditAsync(job.Binding, [h.Observation(0)], 1, default);
        await h.Execute(sql);
        Assert.Equal("RETENTION_STORAGE_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Read())).Code);
    }

    [Fact]
    public async Task Cancellation_and_failed_credit_commit_publish_neither_bits_nor_pass_progress()
    {
        await using var h = await Harness.Create(); await h.RecordPayloads(); await h.Set(RetentionAction.DownloadAll);
        var job = (await h.Driver.ClaimNextRetentionAsync(default))!; await using var session = h.Register(job);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Driver.ApplyRetentionCreditAsync(job.Binding, [h.Observation(0)], 1, cancellation.Token));
        await h.Execute("CREATE TRIGGER reject_credit BEFORE UPDATE ON retention_coverage BEGIN SELECT RAISE(ABORT,'authored write failure'); END");
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Driver.ApplyRetentionCreditAsync(job.Binding, [h.Observation(0)], 1, default));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM retention_bits")); Assert.Equal(0, (await h.Read()).Coverage.ObservedObjects);
        await h.Execute("DROP TRIGGER reject_credit");
        Assert.True(await h.Driver.ApplyRetentionCreditAsync(job.Binding, [h.Observation(0)], 1, default));
    }

    [Fact]
    public async Task Restore_preserves_pause_and_shutdown_intent_and_marks_interrupted_presence()
    {
        await using var h = await Harness.Create(); await h.Set(RetentionAction.DownloadAll);
        var job = (await h.Driver.ClaimNextRetentionAsync(default))!; await h.Restart(); await h.Driver.RestoreDriverAsync(default);
        Assert.Equal("interrupted", (await h.Read()).Coverage.PresenceState);
        var restarted = (await h.Driver.ClaimNextRetentionAsync(default))!;
        await h.Driver.SettleRetentionAsync(restarted.Binding, RetentionPassOutcome.Shutdown, null, default);
        Assert.Equal(RetentionState.Queued, (await h.Read()).State); Assert.False((await h.Read()).Paused);
        var again = (await h.Driver.ClaimNextRetentionAsync(default))!; await h.Set(RetentionAction.Pause);
        await h.Restart(); await h.Driver.RestoreDriverAsync(default);
        Assert.Equal(RetentionState.Paused, (await h.Read()).State); Assert.Null(await h.Driver.ClaimNextRetentionAsync(default));
        Assert.True(again.Binding.Generation > job.Binding.Generation);
    }

    [Fact]
    public async Task Native_checkpoint_reuse_preserves_original_receipt_and_checks_exact_CDN_archive_binding()
    {
        await using var h = await Harness.Create(); var cdn = new string('c', 64); var archive = new string('a', 32);
        var source = new BulkArchiveIndexSource(cdn, archive, new('b', 64), 100, InventoryFixture.Url("data", archive) + ".index",
            DateTimeOffset.Parse("2026-10-19T00:00:00Z", CultureInfo.InvariantCulture), "archive-index-v1");
        Assert.Null(await h.Catalog.FindRetentionNativeSourceAsync(cdn, archive, default));
        await h.Catalog.RecordRetentionNativeSourceAsync(cdn, source, default);
        await h.Catalog.RecordRetentionNativeSourceAsync(cdn, source with { RetrievedAt = source.RetrievedAt.AddDays(1), SourceUrl = source.SourceUrl.Replace("us.cdn", "eu.cdn", StringComparison.Ordinal) }, default);
        await h.Restart(); Assert.Equal(source, await h.Catalog.FindRetentionNativeSourceAsync(cdn, archive, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordRetentionNativeSourceAsync(new('d', 64), source, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordRetentionNativeSourceAsync(cdn, source with { ByteLength = 101 }, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordRetentionNativeSourceAsync(cdn, source with { SourceUrl = "https://example.org/x.index" }, default));
    }

    [Fact]
    public async Task Additive_v8_upgrade_preserves_original_receipts_and_never_creates_jobs_or_backfills_coverage()
    {
        await using var h = await Harness.Create(); await h.RecordPayloads();
        await h.Execute(CatalogMigrationFixture.RemoveV9 + "PRAGMA user_version=8");
        await h.Restart(); Assert.Equal(21, await h.Scalar("PRAGMA user_version"));
        Assert.Equal(2, await h.Scalar("SELECT COUNT(*) FROM encoded_objects"));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM retention_jobs"));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM retention_coverage"));
        Assert.Null((await h.Read()).JobId);
    }

    [Fact]
    public async Task Additive_v9_icon_upgrade_preserves_retention_intent_and_encoded_receipts()
    {
        await using var h = await Harness.Create(); await h.RecordPayloads();
        var saved = await h.Set(RetentionAction.Pause);
        await h.Execute(CatalogMigrationFixture.RemoveV10 + "PRAGMA user_version=9");
        await h.Restart();
        Assert.Equal(21, await h.Scalar("PRAGMA user_version"));
        Assert.Equal(2, await h.Scalar("SELECT COUNT(*) FROM encoded_objects"));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM icon_artifacts"));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM item_icon_admissions"));
        var restored = await h.Read();
        Assert.Equal(saved.JobId, restored.JobId); Assert.Equal(saved.Revision, restored.Revision);
        Assert.Equal(RetentionMode.DownloadAll, restored.Mode); Assert.True(restored.Paused);
        Assert.Equal(RetentionState.Paused, restored.State);
    }

    [Fact]
    public async Task Additive_v10_classification_upgrade_preserves_existing_evidence_and_paused_intent()
    {
        await using var h = await Harness.Create(); await h.RecordPayloads();
        var saved = await h.Set(RetentionAction.Pause);
        await h.Execute("INSERT INTO items_artifacts VALUES('original-items','item-evidence');" +
            "INSERT INTO icon_artifacts VALUES('original-icon','icon-evidence');" +
            CatalogMigrationFixture.RemoveV11 + "PRAGMA user_version=10");
        await h.Restart();
        Assert.Equal(21, await h.Scalar("PRAGMA user_version"));
        Assert.Equal(2, await h.Scalar("SELECT COUNT(*) FROM encoded_objects"));
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM items_artifacts WHERE descriptor='item-evidence'"));
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM icon_artifacts WHERE descriptor='icon-evidence'"));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM item_classification_artifacts"));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM item_classification_admissions"));
        var restored = await h.Read();
        Assert.Equal(saved.JobId, restored.JobId); Assert.Equal(saved.Revision, restored.Revision);
        Assert.Equal(RetentionMode.DownloadAll, restored.Mode); Assert.True(restored.Paused);
        Assert.Equal(RetentionState.Paused, restored.State);
    }

    [Fact]
    public async Task Additive_v11_creature_upgrade_preserves_research_evidence_and_paused_retention()
    {
        await using var h = await Harness.Create(); await h.RecordPayloads();
        var saved = await h.Set(RetentionAction.Pause);
        await h.Execute("INSERT INTO items_artifacts VALUES('original-items','item-evidence');" +
            "INSERT INTO icon_artifacts VALUES('original-icon','icon-evidence');" +
            "INSERT INTO item_classification_artifacts VALUES('original-label','label-evidence');" +
            CatalogMigrationFixture.RemoveV12 + "PRAGMA user_version=11");
        await h.Restart();
        Assert.Equal(21, await h.Scalar("PRAGMA user_version"));
        Assert.Equal(2, await h.Scalar("SELECT COUNT(*) FROM encoded_objects"));
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM items_artifacts WHERE descriptor='item-evidence'"));
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM icon_artifacts WHERE descriptor='icon-evidence'"));
        Assert.Equal(1, await h.Scalar("SELECT COUNT(*) FROM item_classification_artifacts WHERE descriptor='label-evidence'"));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM creatures_artifacts"));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM creatures_admissions"));
        var restored = await h.Read();
        Assert.Equal(saved.JobId, restored.JobId); Assert.Equal(saved.Revision, restored.Revision);
        Assert.Equal(RetentionMode.DownloadAll, restored.Mode); Assert.True(restored.Paused);
        Assert.Equal(RetentionState.Paused, restored.State);
    }

    [Fact]
    public async Task Default_on_demand_ack_has_no_phantom_job_and_empty_ledger_completes_without_members()
    {
        await using var h = await Harness.Create(0);
        var initial = await h.Set(RetentionAction.OnDemand); Assert.Null(initial.JobId); Assert.Equal(0, initial.Revision);
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM retention_jobs"));
        await h.Set(RetentionAction.DownloadAll); var job = (await h.Driver.ClaimNextRetentionAsync(default))!;
        await h.Driver.SettleRetentionAsync(job.Binding, RetentionPassOutcome.Completed, null, default);
        var complete = await h.Read(); Assert.Equal(RetentionState.Completed, complete.State);
        Assert.Equal(0, complete.Coverage.ObservedObjects); Assert.Equal(0, complete.Coverage.ReceiptBytes);
        Assert.Equal("assessed", complete.Coverage.PresenceState);
        await h.Driver.SettleRetentionAsync(job.Binding, RetentionPassOutcome.IntentChanged, null, default);
        Assert.Equal(RetentionState.Completed, (await h.Read()).State);
    }

    [Fact]
    public async Task Failure_examples_stop_at_ten_thousand_and_resume_starts_a_new_bounded_problem_generation()
    {
        // Storage-only synthetic ledger descriptor exercises the cap; no physical parser or
        // game-source completeness claim follows from this internal catalog fixture.
        await using var h = await Harness.Create(10001); await h.Set(RetentionAction.DownloadAll);
        var job = (await h.Driver.ClaimNextRetentionAsync(default))!;
        await using var registration = h.Catalog.RegisterRetentionLedgerSession(job.Binding,
            (ordinal, key, length, _) => Task.FromResult(key == (ordinal + 1).ToString("x32", CultureInfo.InvariantCulture) && length == 10));
        for (var after = 0; after < 9984; after += 64)
        {
            var batch = Enumerable.Range(after, 64).Select(i => new RetentionMemberObservation(i,
                (i + 1).ToString("x32", CultureInfo.InvariantCulture), 10, RetentionPresence.Unknown, false)).ToArray();
            Assert.True(await h.Driver.ApplyRetentionCreditAsync(job.Binding, batch, after + 64, default));
        }
        var final = Enumerable.Range(9984, 16).Select(i => new RetentionMemberObservation(i,
            (i + 1).ToString("x32", CultureInfo.InvariantCulture), 10, RetentionPresence.Unknown, false)).ToArray();
        Assert.Equal("RETENTION_PROBLEM_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            h.Driver.ApplyRetentionCreditAsync(job.Binding, final, 10000, default))).Code);
        Assert.Equal(10000, await h.Scalar("SELECT COUNT(*) FROM retention_problems"));
        Assert.Equal(10000, (await h.Read()).UnresolvedObjects);
        await h.Driver.SettleRetentionAsync(job.Binding, RetentionPassOutcome.Failed,
            new("RETENTION_PROBLEM_LIMIT", "This pass reached its problem limit.", false), default);
        await h.Set(RetentionAction.Resume); var retry = (await h.Driver.ClaimNextRetentionAsync(default))!;
        Assert.True(retry.Binding.Generation > job.Binding.Generation);
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM retention_problems"));
        Assert.Equal(0, (await h.Read()).UnresolvedObjects);
    }

    [Theory]
    [InlineData("UPDATE workspace_configurations SET key=(SELECT key FROM workspace_configurations WHERE role='cdnConfig') WHERE role='buildConfig'")]
    [InlineData("UPDATE workspace_admissions SET source_reference=json_set(source_reference,'$.versionsRowOrdinal',99)")]
    [InlineData("UPDATE download_artifact_sources SET receipt=json_set(receipt,'$.retrievedAt','2026-10-18T00:00:00Z')")]
    public async Task Unrelated_valid_source_facts_cannot_replace_the_exact_admitted_plan(string mutation)
    {
        await using var h = await Harness.Create(); await h.Execute(mutation);
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Set(RetentionAction.DownloadAll));
        Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM retention_jobs"));
    }

    [Fact]
    public async Task Download_all_plan_keeps_its_admitted_CDN_config_after_the_build_is_republished()
    {
        await using var h = await Harness.Create();
        await h.Set(RetentionAction.DownloadAll);
        var job = (await h.Driver.ClaimNextRetentionAsync(default))!;
        Assert.Equal(h.Fixture.CdnKey, job.Plan.Configurations.Single(c => c.Role == "cdnConfig").Key);
        await h.Driver.SettleRetentionAsync(job.Binding, RetentionPassOutcome.Waiting, new("OFFLINE", "Offline.", true), default);

        // Same build, republished CDN configuration: one build row, newest config first, the plan unchanged.
        var republished = new string('e', 32);
        var attempt = h.Fixture.Discovery; var row = attempt.Observations.Single();
        var fields = row.OriginalFields.ToDictionary(p => p.Key, p => p.Value); fields["CDNConfig"] = republished;
        var docs = attempt.Documents.Select(d =>
        {
            if (d.Role != "versions") return d;
            var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(d.Bytes).Replace(h.Fixture.CdnKey, republished, StringComparison.Ordinal));
            return d with { Bytes = bytes, Sha256 = Harness.Sha(bytes) };
        }).ToArray();
        var generation = await h.Catalog.BeginRefreshAsync("wow", default);
        var page = await h.Catalog.CommitAsync("wow", generation, attempt with { Documents = docs, Observations = [row with { OriginalFields = fields }] }, "2026-10-19T01:00:00Z", default);
        var build = Assert.Single(page.Builds);
        Assert.Equal(h.Target.BuildIdentity, build.Id); Assert.Equal(republished, build.CdnConfig);
        Assert.Equal(new[] { republished, h.Fixture.CdnKey }, build.CdnConfigHistory.Select(entry => entry.CdnConfig));

        var resumed = await h.Set(RetentionAction.Resume); Assert.Equal(RetentionState.Queued, resumed.State);
        var current = (await h.Driver.ClaimNextRetentionAsync(default))!;
        Assert.Equal(job.Plan.SourceReference, current.Plan.SourceReference);
        Assert.Equal(h.Fixture.CdnKey, current.Plan.Configurations.Single(c => c.Role == "cdnConfig").Key);
        var pinned = await h.Catalog.ResolveOpeningAsync(new("wow", h.Target.BuildIdentity, "us"), current.Plan.SourceReference, default);
        Assert.Equal(h.Fixture.CdnKey, pinned.Build.CdnConfig);
        // A plan whose CDN configuration differs from its pinned observation is refused.
        await h.Execute($"UPDATE observations SET cdn_config='{republished}' WHERE generation={job.Plan.SourceReference.Generation} AND ordinal={job.Plan.SourceReference.VersionsRowOrdinal}");
        Assert.Equal("RETENTION_STORAGE_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Read())).Code);
    }

    [Fact]
    public async Task Failed_intent_commit_and_precommit_cancellation_return_no_durable_ack_or_queue_slot()
    {
        await using var h = await Harness.Create(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Store.SetAsync(new(h.Target, RetentionAction.DownloadAll), cancel.Token));
        await h.Execute("CREATE TRIGGER reject_intent BEFORE INSERT ON retention_jobs BEGIN SELECT RAISE(ABORT,'authored intent failure'); END");
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Set(RetentionAction.DownloadAll));
        Assert.Empty(await h.Store.ListAsync(default)); Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM retention_coverage"));
        await h.Execute("DROP TRIGGER reject_intent"); Assert.Equal(1, (await h.Set(RetentionAction.DownloadAll)).Revision);
    }

    [Fact]
    public async Task Delayed_old_proof_clear_cannot_erase_new_corruption_even_after_delete_reinsert_and_restart()
    {
        await using var h = await Harness.Create(); await h.RecordPayloads(); var member = h.Members[0];
        var receipt = (await h.Catalog.FindEncodedReceiptAsync(member.EncodingKey, member.EncodedBytes, default))!;
        var beforeProof = await h.Catalog.CaptureObjectIntegrityEpochAsync(default);
        await h.Catalog.RecordObjectInvalidityAsync(receipt.Sha256, receipt.EncodedBytes, "LOCAL_OBJECT_CORRUPT", default);
        await h.Catalog.ClearKnownCorruptAsync(member.EncodingKey, member.EncodedBytes, receipt.Sha256, beforeProof, default);
        Assert.Single(await h.Catalog.ReadKnownCorruptAsync([(member.EncodingKey, member.EncodedBytes)], default));
        var verifiedFirstMarker = await h.Catalog.CaptureObjectIntegrityEpochAsync(default);
        await h.Catalog.ClearKnownCorruptAsync(member.EncodingKey, member.EncodedBytes, receipt.Sha256, verifiedFirstMarker, default);
        Assert.Empty(await h.Catalog.ReadKnownCorruptAsync([(member.EncodingKey, member.EncodedBytes)], default));
        await h.Catalog.RecordObjectInvalidityAsync(receipt.Sha256, receipt.EncodedBytes, "BLTE_INVALID", default);
        await h.Restart(); Assert.True(await h.Catalog.CaptureObjectIntegrityEpochAsync(default) > verifiedFirstMarker);
        await h.Catalog.ClearKnownCorruptAsync(member.EncodingKey, member.EncodedBytes, receipt.Sha256, verifiedFirstMarker, default);
        Assert.Single(await h.Catalog.ReadKnownCorruptAsync([(member.EncodingKey, member.EncodedBytes)], default));
        var freshBeforeProof = await h.Catalog.CaptureObjectIntegrityEpochAsync(default);
        await h.Catalog.ClearKnownCorruptAsync(member.EncodingKey, member.EncodedBytes, receipt.Sha256, freshBeforeProof, default);
        Assert.Empty(await h.Catalog.ReadKnownCorruptAsync([(member.EncodingKey, member.EncodedBytes)], default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.ClearKnownCorruptAsync(member.EncodingKey, member.EncodedBytes, receipt.Sha256, freshBeforeProof + 1, default));
    }

    [Fact]
    public async Task Corruption_epoch_and_marker_commit_together_or_both_rollback()
    {
        await using var h = await Harness.Create(); var sha = new string('a', 64);
        var before = await h.Catalog.CaptureObjectIntegrityEpochAsync(default);
        await h.Execute("CREATE TRIGGER reject_invalidity BEFORE INSERT ON object_invalidity BEGIN SELECT RAISE(ABORT,'authored marker failure'); END");
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Catalog.RecordObjectInvalidityAsync(sha, 20, "LOCAL_OBJECT_CORRUPT", default));
        Assert.Equal(before, await h.Catalog.CaptureObjectIntegrityEpochAsync(default)); Assert.Equal(0, await h.Scalar("SELECT COUNT(*) FROM object_invalidity"));
        await h.Execute("DROP TRIGGER reject_invalidity");
        await h.Catalog.RecordObjectInvalidityAsync(sha, 20, "LOCAL_OBJECT_CORRUPT", default);
        Assert.Equal(before + 1, await h.Catalog.CaptureObjectIntegrityEpochAsync(default));
    }

    internal sealed class Harness : IAsyncDisposable
    {
        internal InventoryFixture Fixture { get; } = InventoryFixture.Create();
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "vibeminer-retention-catalog-" + Guid.NewGuid().ToString("N"));
        internal DiscoveryCatalog Catalog { get; private set; } = null!;
        internal IRetentionStore Store => Catalog;
        internal IRetentionDriverCatalog Driver => Catalog;
        internal CapturedOpeningContext Context { get; private set; } = null!;
        internal IReadOnlyList<ConfigurationReceipt> Configurations { get; private set; } = [];
        private DownloadLedgerArtifact? overriddenArtifact;
        internal DownloadLedgerArtifact Artifact => overriddenArtifact ?? new(new('d', 64), 80, 2, Fixture.ExpectedEncodedBytes);
        internal MetadataReceipt Source => new("download", Fixture.Metadata["download"].ContentKey, Fixture.Metadata["download"].EncodingKey,
            Sha(Fixture.Metadata["download"].Encoded), Fixture.Metadata["download"].Encoded.Length.ToString(CultureInfo.InvariantCulture),
            Fixture.Metadata["download"].Decoded.Length.ToString(CultureInfo.InvariantCulture), InventoryFixture.Url("data", Fixture.Metadata["download"].EncodingKey), Time, Time, "download-v1");
        internal RetentionTarget Target => new("wow", Context.Build.Id, "us", Artifact.Sha256);
        internal DownloadLedgerRow[] Members => new[] { Fixture.PayloadA, Fixture.PayloadB }.OrderBy(p => p.EncodingKey, StringComparer.Ordinal)
            .Select((p, i) => new DownloadLedgerRow(i + 1, p.EncodingKey, p.Encoded.Length)).ToArray();
        private const string Time = "2026-10-19T00:00:00Z";
        internal static string Sha(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        internal static async Task<Harness> Create(long? objectCount = null)
        {
            var h = new Harness();
            if (objectCount is not null) h.overriddenArtifact = new(new('d', 64), 32 + 24 * objectCount.Value, objectCount.Value, 10 * objectCount.Value);
            h.Catalog = await DiscoveryCatalog.OpenAsync(h.Directory);
            var configs = new List<ConfigurationReceipt>();
            foreach (var (role, key, bytes) in new[] { ("buildConfig", h.Fixture.BuildKey, h.Fixture.BuildConfig), ("cdnConfig", h.Fixture.CdnKey, h.Fixture.CdnConfig) })
            {
                var url = InventoryFixture.Url("config", key); await h.Catalog.RecordConfigurationAsync(new(key, Sha(bytes), bytes.Length, url, Time), default);
                configs.Add(new(role, key, Sha(bytes), bytes.Length, url, Time, Time, 1));
            }
            h.Configurations = configs; await h.Catalog.RecordMetadataAsync(h.Source, default);
            await h.Catalog.RecordDownloadArtifactAsync(h.Source, h.Artifact, default);
            await h.AddBuild(0); return h;
        }
        internal async Task<RetentionTarget> AddBuild(int index)
        {
            var attempt = Fixture.Discovery;
            if (index > 0)
            {
                var row = attempt.Observations.Single(); var fields = row.OriginalFields.ToDictionary(p => p.Key, p => p.Value);
                fields["VersionsName"] = $"1.2.3.{12345 + index}"; fields["BuildId"] = (12345 + index).ToString(CultureInfo.InvariantCulture);
                var identity = BuildIdentity.Create("wow", fields);
                var docs = attempt.Documents.Select(d =>
                {
                    if (d.Role != "versions") return d;
                    var bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(d.Bytes).Replace("12345", (12345 + index).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));
                    return d with { Bytes = bytes, Sha256 = Sha(bytes) };
                }).ToArray();
                attempt = attempt with { Documents = docs, Observations = [row with { Identity = identity, OriginalFields = fields }] };
            }
            var generation = await Catalog.BeginRefreshAsync("wow", default);
            await Catalog.CommitAsync("wow", generation, attempt, Time, default);
            var context = await Catalog.ResolveOpeningAsync(new("wow", attempt.Observations.Single().Identity.Id, "us"), default);
            await Catalog.CommitDownloadWorkspaceAsync(context, Configurations, [Source], Artifact, [], Configurations.Sum(c => (long)c.ByteLength) + Fixture.Metadata["download"].Encoded.Length, default);
            // This harness commits Open directly, without a file list, so the scope is the manifest (D066).
            while (await Catalog.ReadNextRootScopeAsync(default) is { } work)
                await Catalog.FailRootScopeAsync(work, true, new("ROOT_UNSUPPORTED", "Authored build without a file list.", false), default);
            if (index == 0) Context = context;
            return new("wow", context.Build.Id, "us", Artifact.Sha256);
        }
        internal Task<RetentionSnapshot> Set(RetentionAction action) => Store.SetAsync(new(Target, action), default);
        internal Task<RetentionSnapshot> Read() => Store.ReadAsync(Target, default);
        internal RetentionMemberObservation Observation(int ordinal, RetentionPresence presence = RetentionPresence.Present) =>
            new(ordinal, Members[ordinal].EncodingKey, Members[ordinal].EncodedBytes, presence, true);
        internal IAsyncDisposable Register(RetentionDriverJob job) => Catalog.RegisterRetentionLedgerSession(job.Binding,
            (ordinal, key, length, _) => Task.FromResult(ordinal >= 0 && ordinal < Members.Length && Members[ordinal].EncodingKey == key && Members[ordinal].EncodedBytes == length));
        internal async Task RecordPayloads()
        {
            foreach (var p in new[] { Fixture.PayloadA, Fixture.PayloadB })
                await Catalog.RecordEncodedReceiptAsync(new(p.EncodingKey, Sha(p.Encoded), p.Encoded.Length, InventoryFixture.Url("data", p.EncodingKey), Time, Time,
                    "blte-encoded-v1", new("loose", "0", p.Encoded.Length.ToString(CultureInfo.InvariantCulture), null)), default);
        }
        internal async Task Execute(string sql)
        { await using var db = new SqliteConnection($"Data Source={Path.Combine(Directory, "discovery-v1.sqlite3")};Pooling=False"); await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync(); }
        internal async Task<long> Scalar(string sql)
        { await using var db = new SqliteConnection($"Data Source={Path.Combine(Directory, "discovery-v1.sqlite3")};Pooling=False"); await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture); }
        internal async Task Restart() { await Catalog.DisposeAsync(); Catalog = await DiscoveryCatalog.OpenAsync(Directory); }
        public async ValueTask DisposeAsync() { await Catalog.DisposeAsync(); System.IO.Directory.Delete(Directory, true); }
    }
}
