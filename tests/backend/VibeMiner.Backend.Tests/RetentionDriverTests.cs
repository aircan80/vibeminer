using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using VibeMiner.Backend.Tests.Protocols;
using Microsoft.Data.Sqlite;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace VibeMiner.Backend.Tests;

// Independent fixtures share the process-wide one-map construction admission gate.
[Collection("Bulk archive locator")]
public sealed class RetentionDriverTests
{
    private readonly ITestOutputHelper output;
    public RetentionDriverTests(ITestOutputHelper output) => this.output = output;
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static string Key(int id) => id.ToString("x32");

    [Fact]
    public async Task Restore_is_idempotent_and_a_zero_member_job_finishes_without_acquisition()
    {
        var catalog = new DriverCatalog(Job(1, 0)); var factory = new Sessions();
        var driver = new BuildRetentionDriver(catalog, factory);
        await driver.RestoreAsync(default); await driver.RestoreAsync(default);
        var settled = await catalog.NextSettlement();
        Assert.Equal(RetentionPassOutcome.Completed, settled.Outcome);
        Assert.Equal(1, catalog.Restores); Assert.Equal(0, factory.Created.Single().Attachments);
        await driver.ShutdownAsync(default); Assert.True(factory.Created.Single().Disposed);
    }

    [Fact]
    public async Task Feeder_bounds_subscription_frontier_and_catalog_slices_and_counts_final_presence()
    {
        var catalog = new DriverCatalog(Job(1, 97)); var release = Signal();
        var factory = new Sessions { Configure = session => session.Release = release.Task };
        var driver = new BuildRetentionDriver(catalog, factory); await driver.RestoreAsync(default);
        var session = await factory.NextOpened(); await session.FirstFrontier.Task.WaitAsync(Deadline);
        Assert.Equal(32, session.Attachments); Assert.Equal(32, session.PeakPending);
        release.SetResult();
        Assert.Equal(RetentionPassOutcome.Completed, (await catalog.NextSettlement()).Outcome);
        Assert.Equal(97, session.Attachments); Assert.InRange(session.PeakPending, 1, 32);
        Assert.All(catalog.Batches, batch => Assert.InRange(batch.Count, 1, 64));
        Assert.Equal(Enumerable.Range(0, 97).Select(value => (long)value), catalog.Batches.SelectMany(batch => batch).Select(item => item.Ordinal));
        Assert.All(catalog.Batches.SelectMany(batch => batch), item => Assert.Equal(RetentionPresence.Present, item.Presence));
        Assert.All(session.ReceiptBatchSizes, count => Assert.InRange(count, 0, 128));
        await driver.ShutdownAsync(default);
    }

    [Fact]
    public async Task Pause_waits_for_actual_subscription_cleanup_before_claiming_the_next_job()
    {
        var first = Job(1, 1); var second = Job(2, 0);
        var catalog = new DriverCatalog(first, second); var cleanup = Signal();
        var factory = new Sessions { Configure = session => { session.Release = Never(); session.Cleanup = cleanup.Task; } };
        var driver = new BuildRetentionDriver(catalog, factory); await driver.RestoreAsync(default);
        var session = await factory.NextOpened(); await session.Attached.Task.WaitAsync(Deadline);
        catalog.Change(first.Binding.JobId, new(2, RetentionMode.DownloadAll, true)); driver.NotifyIntentChanged();
        await session.CleanupEntered.Task.WaitAsync(Deadline);
        Assert.Single(factory.Created); Assert.Empty(catalog.Settled);
        cleanup.SetResult();
        var paused = await catalog.NextSettlement(); Assert.Equal(RetentionPassOutcome.IntentChanged, paused.Outcome);
        Assert.Equal(RetentionPassOutcome.Completed, (await catalog.NextSettlement()).Outcome);
        Assert.Equal(2, factory.Created.Count); Assert.True(session.Disposed);
        await driver.ShutdownAsync(default);
    }

    [Fact]
    public async Task On_demand_and_shutdown_withdraw_only_job_ownership_and_preserve_durable_intent()
    {
        foreach (var normalExit in new[] { false, true })
        {
            var job = Job(1, 1); var catalog = new DriverCatalog(job);
            var factory = new Sessions { Configure = session => session.Release = Never() };
            var driver = new BuildRetentionDriver(catalog, factory); await driver.RestoreAsync(default);
            var session = await factory.NextOpened(); await session.Attached.Task.WaitAsync(Deadline);
            if (normalExit) await driver.ShutdownAsync(default).WaitAsync(Deadline);
            else { catalog.Change(job.Binding.JobId, new(2, RetentionMode.OnDemand, false)); driver.NotifyIntentChanged(); }
            var settled = await catalog.NextSettlement();
            Assert.Equal(normalExit ? RetentionPassOutcome.Shutdown : RetentionPassOutcome.IntentChanged, settled.Outcome);
            var intent = await catalog.ReadRetentionIntentAsync(job.Binding.JobId, default);
            Assert.Equal(normalExit ? RetentionMode.DownloadAll : RetentionMode.OnDemand, intent!.Mode);
            Assert.False(intent.Paused); Assert.True(session.Disposed); Assert.Equal(0, session.Pending);
            await driver.ShutdownAsync(default);
        }
    }

    [Fact]
    public async Task Earlier_failed_member_prevents_EOF_from_claiming_completion()
    {
        var catalog = new DriverCatalog(Job(1, 65));
        var factory = new Sessions { Configure = session => session.FailOrdinal = 0 };
        var driver = new BuildRetentionDriver(catalog, factory); await driver.RestoreAsync(default);
        var settled = await catalog.NextSettlement(); Assert.Equal(RetentionPassOutcome.Failed, settled.Outcome);
        Assert.Equal("AUTHORED_INTEGRITY", settled.Problem!.Code);
        Assert.Equal(65, catalog.Batches.Sum(batch => batch.Count));
        Assert.Equal(RetentionPresence.Unknown, catalog.Batches.First()[0].Presence);
        await driver.ShutdownAsync(default);
    }

    [Fact]
    public async Task Stale_credit_stops_the_pass_after_cleanup()
    {
        var catalog = new DriverCatalog(Job(1, 1)) { RejectCredits = true };
        var factory = new Sessions(); var driver = new BuildRetentionDriver(catalog, factory);
        await driver.RestoreAsync(default);
        Assert.Equal(RetentionPassOutcome.IntentChanged, (await catalog.NextSettlement()).Outcome);
        Assert.True(factory.Created.Single().Disposed); await driver.ShutdownAsync(default);
    }

    [Fact]
    public async Task Command_committing_between_cleanup_and_final_CAS_releases_only_the_old_generation()
    {
        var catalog = new DriverCatalog(Job(1, 0)) { ChangeAtSettlement = true }; var factory = new Sessions();
        var driver = new BuildRetentionDriver(catalog, factory); await driver.RestoreAsync(default);
        Assert.Equal(RetentionPassOutcome.IntentChanged, (await catalog.NextSettlement()).Outcome);
        Assert.Equal(RetentionMode.OnDemand, (await catalog.ReadRetentionIntentAsync("job-1", default))!.Mode);
        Assert.True(factory.Created.Single().Disposed); await driver.ShutdownAsync(default);
    }

    [Fact]
    public async Task Failed_object_details_stop_at_the_exact_bounded_limit()
    {
        var catalog = new DriverCatalog(Job(1, BuildRetentionDriver.MaximumProblems + 17));
        var factory = new Sessions { Configure = session => session.FailEveryMember = true };
        var driver = new BuildRetentionDriver(catalog, factory); await driver.RestoreAsync(default);
        var settled = await catalog.NextSettlement(); Assert.Equal("RETENTION_PROBLEM_LIMIT", settled.Problem!.Code);
        Assert.Equal(BuildRetentionDriver.MaximumProblems, factory.Created.Single().Attachments);
        Assert.Equal(BuildRetentionDriver.MaximumProblems, catalog.Batches.Sum(batch => batch.Count));
        await driver.ShutdownAsync(default);
    }

    [Fact]
    public async Task Retryable_sources_use_bounded_clock_delays_then_settle_waiting_without_changing_intent()
    {
        var clock = new ManualClock(); var catalog = new DriverCatalog(Job(1, 1));
        var factory = new Sessions { Configure = session => session.RetryableFailure = true };
        var driver = new BuildRetentionDriver(catalog, factory, clock); await driver.RestoreAsync(default);
        var session = await factory.NextOpened();
        await clock.NextTimer(TimeSpan.FromSeconds(1)); await clock.NextTimer(TimeSpan.FromSeconds(1));
        Assert.Equal(1, session.Attachments); clock.Advance(TimeSpan.FromSeconds(1));
        await clock.NextTimer(TimeSpan.FromSeconds(4)); Assert.Equal(2, session.Attachments);
        clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(RetentionPassOutcome.Waiting, (await catalog.NextSettlement()).Outcome);
        Assert.Equal(3, session.Attachments); Assert.Equal(1, (await catalog.ReadRetentionIntentAsync("job-1", default))!.Revision);
        await driver.ShutdownAsync(default);
    }

    [Fact]
    public async Task Claim_failures_park_after_bounded_retries_and_explicit_notification_recovers_the_same_worker()
    {
        var clock = new ManualClock(); var catalog = new DriverCatalog(Job(1, 0)) { ClaimFailures = 3 };
        var factory = new Sessions(); var driver = new BuildRetentionDriver(catalog, factory, clock);
        await driver.RestoreAsync(default);
        await clock.NextTimer(TimeSpan.FromSeconds(1)); clock.Advance(TimeSpan.FromSeconds(1));
        await clock.NextTimer(TimeSpan.FromSeconds(4)); clock.Advance(TimeSpan.FromSeconds(4));
        var fault = await Faulted(driver); Assert.Equal("RETENTION_STATE_RETRY", fault.Problem.Code); Assert.Null(fault.JobId);
        Assert.Equal(3, catalog.ClaimCalls); Assert.Empty(factory.Created);
        clock.Advance(TimeSpan.FromDays(1)); Assert.Equal(3, catalog.ClaimCalls);
        await driver.RestoreAsync(default); Assert.Equal(3, catalog.ClaimCalls);
        driver.NotifyIntentChanged(); Assert.Null(driver.ReadFault());
        Assert.Equal(RetentionPassOutcome.Completed, (await catalog.NextSettlement()).Outcome);
        Assert.Single(factory.Created); await driver.ShutdownAsync(default);
    }

    [Fact]
    public async Task Claim_commit_followed_by_failure_is_reconciled_before_another_claim()
    {
        var clock = new ManualClock(); var catalog = new DriverCatalog(Job(1, 0)) { ClaimAfterCommitFailures = 1 };
        var factory = new Sessions(); var driver = new BuildRetentionDriver(catalog, factory, clock);
        await driver.RestoreAsync(default); await clock.NextTimer(TimeSpan.FromSeconds(1));
        Assert.Empty(factory.Created); clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(RetentionPassOutcome.Completed, (await catalog.NextSettlement()).Outcome);
        Assert.Equal(2, catalog.Restores); Assert.Single(factory.Created); Assert.Null(driver.ReadFault());
        await driver.ShutdownAsync(default);
    }

    [Fact]
    public async Task Notification_during_the_third_failed_claim_cannot_be_lost_when_parking()
    {
        var clock = new ManualClock(); var catalog = new DriverCatalog(Job(1, 0)) { ClaimFailures = 3 };
        var factory = new Sessions(); var driver = new BuildRetentionDriver(catalog, factory, clock);
        catalog.OnThirdClaimFailure = driver.NotifyIntentChanged;
        await driver.RestoreAsync(default); await clock.NextTimer(TimeSpan.FromSeconds(1)); clock.Advance(TimeSpan.FromSeconds(1));
        await clock.NextTimer(TimeSpan.FromSeconds(4)); clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal(RetentionPassOutcome.Completed, (await catalog.NextSettlement()).Outcome);
        Assert.Single(factory.Created); Assert.Null(driver.ReadFault()); await driver.ShutdownAsync(default);
    }

    [Fact]
    public async Task Settlement_failures_retain_the_cleaned_generation_and_block_the_next_job_until_explicit_retry()
    {
        var clock = new ManualClock(); var catalog = new DriverCatalog(Job(1, 0), Job(2, 0)) { SettleFailures = 3 };
        var factory = new Sessions(); var driver = new BuildRetentionDriver(catalog, factory, clock); await driver.RestoreAsync(default);
        await clock.NextTimer(TimeSpan.FromSeconds(1)); await clock.NextTimer(TimeSpan.FromSeconds(1)); clock.Advance(TimeSpan.FromSeconds(1));
        await clock.NextTimer(TimeSpan.FromSeconds(4)); clock.Advance(TimeSpan.FromSeconds(4));
        var fault = await Faulted(driver); Assert.Equal("job-1", fault.JobId);
        Assert.True(factory.Created.Single().Disposed); Assert.Empty(catalog.Settled); Assert.Equal(1, catalog.ClaimCalls);
        driver.NotifyIntentChanged(); Assert.Null(driver.ReadFault());
        Assert.Equal(RetentionPassOutcome.Completed, (await catalog.NextSettlement()).Outcome);
        Assert.Equal(RetentionPassOutcome.Completed, (await catalog.NextSettlement()).Outcome);
        Assert.Equal(2, factory.Created.Count); await driver.ShutdownAsync(default);
    }

    [Fact]
    public async Task Shutdown_settlement_failure_is_typed_and_a_later_shutdown_retries_retirement_without_claiming()
    {
        var clock = new ManualClock(); var catalog = new DriverCatalog(Job(1, 1), Job(2, 0)) { SettleFailures = 3 };
        var factory = new Sessions { Configure = session => session.Release = Never() };
        var driver = new BuildRetentionDriver(catalog, factory, clock); await driver.RestoreAsync(default);
        var session = await factory.NextOpened(); await session.Attached.Task.WaitAsync(Deadline);
        var stopping = driver.ShutdownAsync(default);
        await clock.NextTimer(TimeSpan.FromSeconds(1)); await clock.NextTimer(TimeSpan.FromSeconds(1)); clock.Advance(TimeSpan.FromSeconds(1));
        await clock.NextTimer(TimeSpan.FromSeconds(4)); clock.Advance(TimeSpan.FromSeconds(4));
        Assert.Equal("RETENTION_STATE_RETRY", (await Assert.ThrowsAsync<DiscoveryException>(() => stopping.WaitAsync(Deadline))).Code);
        Assert.True(session.Disposed); Assert.Equal(0, session.Pending); Assert.NotNull(driver.ReadFault());
        await Task.WhenAll(driver.ShutdownAsync(default), driver.ShutdownAsync(default)).WaitAsync(Deadline);
        Assert.Equal(RetentionPassOutcome.Shutdown, (await catalog.NextSettlement()).Outcome);
        Assert.Equal(1, catalog.ClaimCalls); Assert.Single(factory.Created); Assert.Null(driver.ReadFault());
    }

    private static async Task<RetentionDriverFault> Faulted(BuildRetentionDriver driver)
    {
        async Task<RetentionDriverFault> Read()
        { while (true) { if (driver.ReadFault() is { } fault) return fault; await Task.Yield(); } }
        return await Read().WaitAsync(Deadline);
    }

    [Fact]
    public async Task Real_SQLite_CAS_HTTP_retention_reconciles_offline_and_repairs_missing_bytes_without_double_credit()
    {
        await using var fixture = await SharedAcquisitionIntegrationTests.Fixture.CreateAsync();
        await using var plans = new EncodedSourcePlans(fixture.Catalog, fixture.Objects, fixture.Resources);
        await fixture.Shared.DisposeAsync();
        fixture.Shared = new(new OfficialEncodedObjectProducer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Resources, plans));
        using var opening = new DownloadWorkspacePreparer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Shared, fixture.Resources);
        var workspace = await opening.PrepareAsync(fixture.Request, _ => Task.CompletedTask, default);
        Assert.DoesNotContain(fixture.Handler.Requests, url => url == InventoryFixture.Url("data", fixture.Data.PayloadA.EncodingKey));
        var target = new RetentionTarget("wow", fixture.Request.BuildIdentity, "us", workspace.Download!.LedgerId);
        var observed = new ObservedCatalog((IRetentionDriverCatalog)fixture.Catalog);
        var driver = new BuildRetentionDriver(observed, new BuildRetentionDriver.PhysicalSessions(fixture.Catalog,
            fixture.Objects, fixture.Shared, plans, fixture.Client, fixture.Resources, TimeProvider.System));
        var service = new RetentionService((IRetentionStore)fixture.Catalog, driver);
        await service.RestoreAsync(default);
        try
        {
            var ack = await service.SetAsync(new(target, RetentionAction.DownloadAll), default);
            Assert.Equal(RetentionMode.DownloadAll, ack.Mode);
            Assert.Equal(RetentionPassOutcome.Completed, (await observed.NextSettlement()).Outcome);
            var complete = await service.ReadAsync(target);
            Assert.Equal(RetentionState.Completed, complete.State); Assert.Equal(2, complete.Coverage.ReceiptObjects);
            Assert.Equal(2, complete.Coverage.PresentObjects); Assert.Equal("assessed", complete.Coverage.PresenceState);
            Assert.Equal(1, plans.ConfigurationLoads);
            var originalRequests = fixture.Handler.Requests.Count;
            await service.SetAsync(new(target, RetentionAction.OnDemand));
            fixture.Handler.Offline = true;
            await service.SetAsync(new(target, RetentionAction.DownloadAll));
            Assert.Equal(RetentionPassOutcome.Completed, (await observed.NextSettlement()).Outcome);
            Assert.Equal(originalRequests, fixture.Handler.Requests.Count);
            Assert.Equal(2, (await service.ReadAsync(target)).Coverage.ReceiptObjects);
            fixture.Handler.Offline = false;
            var retained = await fixture.Catalog.FindEncodedReceiptAsync(fixture.Data.PayloadA.EncodingKey, fixture.Data.PayloadA.Encoded.Length, default);
            File.Delete(Path.Combine(fixture.Path, "objects-v1", retained!.Sha256[..2], retained.Sha256[2..]));
            await service.SetAsync(new(target, RetentionAction.OnDemand));
            await service.SetAsync(new(target, RetentionAction.DownloadAll));
            Assert.Equal(RetentionPassOutcome.Completed, (await observed.NextSettlement()).Outcome);
            var repaired = await service.ReadAsync(target);
            Assert.Equal(2, repaired.Coverage.ReceiptObjects); Assert.Equal(2, repaired.Coverage.PresentObjects);
            Assert.Equal(0, repaired.Coverage.MissingObjects); Assert.Equal(1, plans.ConfigurationLoads);
            Assert.Equal(2, fixture.Handler.Requests.Count(url => url == InventoryFixture.Url("data", fixture.Data.PayloadA.EncodingKey)));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Path, "staging-v1")));
        }
        finally { await service.ShutdownAsync(default); }
    }

    [Fact]
    public async Task Real_foreground_consumer_survives_retention_pause_and_normal_exit_preserves_paused_intent()
    {
        await using var fixture = await SharedAcquisitionIntegrationTests.Fixture.CreateAsync();
        await using var plans = new EncodedSourcePlans(fixture.Catalog, fixture.Objects, fixture.Resources);
        await fixture.Shared.DisposeAsync();
        fixture.Shared = new(new OfficialEncodedObjectProducer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Resources, plans));
        using var opening = new DownloadWorkspacePreparer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Shared, fixture.Resources);
        var workspace = await opening.PrepareAsync(fixture.Request, _ => Task.CompletedTask, default);
        var target = new RetentionTarget("wow", fixture.Request.BuildIdentity, "us", workspace.Download!.LedgerId);
        var entered = Signal(); var release = Signal(); var payload = fixture.Data.PayloadA;
        fixture.Handler.Before = async request =>
        { if (request.RequestUri!.AbsoluteUri == InventoryFixture.Url("data", payload.EncodingKey)) { entered.TrySetResult(); await release.Task; } };
        var observed = new ObservedCatalog((IRetentionDriverCatalog)fixture.Catalog);
        var driver = new BuildRetentionDriver(observed, new BuildRetentionDriver.PhysicalSessions(fixture.Catalog,
            fixture.Objects, fixture.Shared, plans, fixture.Client, fixture.Resources, TimeProvider.System));
        var service = new RetentionService((IRetentionStore)fixture.Catalog, driver); await service.RestoreAsync(default);
        try
        {
            await service.SetAsync(new(target, RetentionAction.DownloadAll)); await entered.Task.WaitAsync(Deadline);
            await using var foreground = fixture.Shared.Attach(new(payload.EncodingKey, payload.Encoded.Length), fixture.Source,
                AcquisitionPriority.Interactive);
            var pausedAck = await service.SetAsync(new(target, RetentionAction.Pause));
            Assert.True(pausedAck.Paused);
            Assert.Equal(RetentionPassOutcome.IntentChanged, (await observed.NextSettlement()).Outcome);
            Assert.False(foreground.Completion.IsCompleted);
            Assert.Equal(RetentionState.Paused, (await service.ReadAsync(target)).State);
            release.SetResult(); await foreground.Completion.WaitAsync(Deadline);
            Assert.Single(fixture.Handler.Requests, url => url == InventoryFixture.Url("data", payload.EncodingKey));
            await service.ShutdownAsync(default);
            var restored = new BuildRetentionDriver(fixture.Catalog, fixture.Objects, fixture.Shared, plans, fixture.Client, fixture.Resources);
            await restored.RestoreAsync(default);
            try
            {
                var snapshot = await ((IRetentionStore)fixture.Catalog).ReadAsync(target, default);
                Assert.True(snapshot.Paused); Assert.Equal(RetentionMode.DownloadAll, snapshot.Mode);
                Assert.Equal(RetentionState.Paused, snapshot.State);
            }
            finally { await restored.ShutdownAsync(default); }
        }
        finally { release.TrySetResult(); await service.ShutdownAsync(default); }
    }

    [Fact]
    public async Task Real_archive_retention_uses_one_shared_map_before_loose_HTTP_and_reuses_it_for_repair()
    {
        await using var fixture = await SharedAcquisitionIntegrationTests.Fixture.CreateAsync();
        var payloads = new[] { fixture.Data.PayloadA, fixture.Data.PayloadB };
        var body = new MemoryStream(); body.Write(new byte[19]);
        var locations = new List<(string Key, uint Size, uint Offset)>();
        foreach (var item in payloads)
        {
            locations.Add((item.EncodingKey, (uint)item.Encoded.Length, (uint)body.Position)); body.Write(item.Encoded); body.Write(new byte[7]);
            fixture.Data.Payloads.Remove(item.EncodingKey); fixture.Data.Responses.Remove(InventoryFixture.Url("data", item.EncodingKey));
        }
        var index = AuthoredArchiveIndex.Build(locations.OrderBy(item => item.Key, StringComparer.Ordinal).ToArray());
        var archiveUrl = InventoryFixture.Url("data", index.Identity);
        fixture.Data.Responses[archiveUrl] = body.ToArray(); fixture.Data.Responses[archiveUrl + ".index"] = index.Bytes;
        var alternate = await fixture.PrepareAlternateSourceAsync(Encoding.UTF8.GetBytes($"archives = {index.Identity}\n"));
        fixture.Request = new("wow", alternate.BuildIdentity, "us"); fixture.Source = alternate;
        await using var plans = new EncodedSourcePlans(fixture.Catalog, fixture.Objects, fixture.Resources);
        await fixture.Shared.DisposeAsync();
        fixture.Shared = new(new OfficialEncodedObjectProducer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Resources, plans));
        using var opening = new DownloadWorkspacePreparer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Shared, fixture.Resources);
        var workspace = await opening.PrepareAsync(fixture.Request, _ => Task.CompletedTask, default);
        var target = new RetentionTarget("wow", fixture.Request.BuildIdentity, "us", workspace.Download!.LedgerId);
        var observed = new ObservedCatalog((IRetentionDriverCatalog)fixture.Catalog);
        var driver = new BuildRetentionDriver(observed, new BuildRetentionDriver.PhysicalSessions(fixture.Catalog,
            fixture.Objects, fixture.Shared, plans, fixture.Client, fixture.Resources, TimeProvider.System));
        var service = new RetentionService((IRetentionStore)fixture.Catalog, driver); await service.RestoreAsync(default);
        var repairEntered = Signal(); var releaseRepair = Signal();
        try
        {
            await service.SetAsync(new(target, RetentionAction.DownloadAll));
            Assert.Equal(RetentionPassOutcome.Completed, (await observed.NextSettlement()).Outcome);
            Assert.Equal(2, fixture.Handler.Requests.Count(url => url == archiveUrl + ".index"));
            Assert.Equal(2, fixture.Handler.Requests.Count(url => url == archiveUrl));
            Assert.DoesNotContain(fixture.Handler.Requests, url => payloads.Any(item => url == InventoryFixture.Url("data", item.EncodingKey)));
            var receipt = await fixture.Catalog.FindEncodedReceiptAsync(payloads[0].EncodingKey, payloads[0].Encoded.Length, default);
            Assert.Equal("archive", receipt!.Location.Kind);
            File.Delete(Path.Combine(fixture.Path, "objects-v1", receipt.Sha256[..2], receipt.Sha256[2..]));
            fixture.Handler.Before = async request =>
            {
                if (request.RequestUri!.AbsoluteUri == archiveUrl)
                { repairEntered.TrySetResult(); await releaseRepair.Task; }
            };
            await service.SetAsync(new(target, RetentionAction.OnDemand)); await service.SetAsync(new(target, RetentionAction.DownloadAll));
            await repairEntered.Task.WaitAsync(Deadline);
            Assert.Equal(RetentionState.Running, (await service.ReadAsync(target)).State);
            Assert.Equal(2, fixture.Handler.Requests.Count(url => url == archiveUrl + ".index"));
            Assert.Equal(3, fixture.Handler.Requests.Count(url => url == archiveUrl));
            releaseRepair.TrySetResult();
            Assert.Equal(RetentionPassOutcome.Completed, (await observed.NextSettlement()).Outcome);
            Assert.Equal(2, fixture.Handler.Requests.Count(url => url == archiveUrl + ".index"));
            Assert.Equal(3, fixture.Handler.Requests.Count(url => url == archiveUrl));
            Assert.Equal(2, (await service.ReadAsync(target)).Coverage.ReceiptObjects);
            Assert.Equal(1, plans.ConfigurationLoads);
        }
        finally { releaseRepair.TrySetResult(); await service.ShutdownAsync(default); }
    }

    [Fact]
    public async Task Real_known_corruption_overrides_same_length_stat_and_only_verified_repair_clears_it()
    {
        await using var fixture = await SharedAcquisitionIntegrationTests.Fixture.CreateAsync();
        await fixture.Shared.DisposeAsync(); await fixture.Objects.DisposeAsync();
        fixture.Objects = new(fixture.Path, fixture.Catalog.RecordObjectInvalidityAsync);
        await using var plans = new EncodedSourcePlans(fixture.Catalog, fixture.Objects, fixture.Resources);
        fixture.Shared = new(new OfficialEncodedObjectProducer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Resources, plans));
        using var opening = new DownloadWorkspacePreparer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Shared, fixture.Resources);
        var workspace = await opening.PrepareAsync(fixture.Request, _ => Task.CompletedTask, default);
        var target = new RetentionTarget("wow", fixture.Request.BuildIdentity, "us", workspace.Download!.LedgerId);
        var observed = new ObservedCatalog((IRetentionDriverCatalog)fixture.Catalog);
        var driver = new BuildRetentionDriver(observed, new BuildRetentionDriver.PhysicalSessions(fixture.Catalog,
            fixture.Objects, fixture.Shared, plans, fixture.Client, fixture.Resources, TimeProvider.System));
        var service = new RetentionService((IRetentionStore)fixture.Catalog, driver); await service.RestoreAsync(default);
        try
        {
            await service.SetAsync(new(target, RetentionAction.DownloadAll));
            var initial = await observed.NextSettlement();
            Assert.True(initial.Outcome == RetentionPassOutcome.Completed, $"The initial pass did not complete: {initial}");
            var item = fixture.Data.PayloadA;
            var receipt = await fixture.Catalog.FindEncodedReceiptAsync(item.EncodingKey, item.Encoded.Length, default);
            Assert.NotNull(receipt);
            var path = Path.Combine(fixture.Path, "objects-v1", receipt!.Sha256[..2], receipt.Sha256[2..]);
            var corrupt = item.Encoded.ToArray(); corrupt[^1] ^= 1; await File.WriteAllBytesAsync(path, corrupt);
            Assert.Equal("LOCAL_OBJECT_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
                fixture.Objects.OpenVerifiedAsync(receipt.Sha256, receipt.EncodedBytes, default))).Code);
            Assert.Equal(ObjectPresenceKind.Present, (await fixture.Objects.ObservePresenceAsync(receipt.Sha256, receipt.EncodedBytes, default)).Kind);
            Assert.Contains(item.EncodingKey, await fixture.Catalog.ReadKnownCorruptAsync([(item.EncodingKey, item.Encoded.Length)], default));
            await service.SetAsync(new(target, RetentionAction.OnDemand)); await service.SetAsync(new(target, RetentionAction.DownloadAll));
            Assert.Equal(RetentionPassOutcome.Completed, (await observed.NextSettlement()).Outcome);
            Assert.Empty(await fixture.Catalog.ReadKnownCorruptAsync([(item.EncodingKey, item.Encoded.Length)], default));
            var snapshot = await service.ReadAsync(target); Assert.Equal(2, snapshot.Coverage.ReceiptObjects);
            Assert.Equal(2, snapshot.Coverage.PresentObjects); Assert.Equal(0, snapshot.Coverage.CorruptObjects);
            Assert.Equal(item.Encoded, await File.ReadAllBytesAsync(path));
            Assert.Equal(2, fixture.Handler.Requests.Count(url => url == InventoryFixture.Url("data", item.EncodingKey)));
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Path, "staging-v1")));
        }
        finally { await service.ShutdownAsync(default); }
    }

    [Fact]
    public async Task Real_receipt_commit_failure_credits_no_orphan_bytes_and_resume_recovers_without_reset()
    {
        var clock = new ManualClock();
        await using var fixture = await SharedAcquisitionIntegrationTests.Fixture.CreateAsync();
        await using var plans = new EncodedSourcePlans(fixture.Catalog, fixture.Objects, fixture.Resources);
        await fixture.Shared.DisposeAsync();
        fixture.Shared = new(new OfficialEncodedObjectProducer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Resources, plans));
        using var opening = new DownloadWorkspacePreparer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Shared, fixture.Resources);
        var workspace = await opening.PrepareAsync(fixture.Request, _ => Task.CompletedTask, default);
        var target = new RetentionTarget("wow", fixture.Request.BuildIdentity, "us", workspace.Download!.LedgerId);
        var observed = new ObservedCatalog((IRetentionDriverCatalog)fixture.Catalog);
        var driver = new BuildRetentionDriver(observed, new BuildRetentionDriver.PhysicalSessions(fixture.Catalog,
            fixture.Objects, fixture.Shared, plans, fixture.Client, fixture.Resources, clock), clock);
        var service = new RetentionService((IRetentionStore)fixture.Catalog, driver); await service.RestoreAsync(default);
        try
        {
            await ExecuteSql(fixture.Path, "CREATE TRIGGER authored_receipt_failure BEFORE INSERT ON encoded_objects BEGIN SELECT RAISE(FAIL,'authored receipt failure'); END");
            await service.SetAsync(new(target, RetentionAction.DownloadAll));
            await clock.NextTimer(TimeSpan.FromSeconds(1)); await clock.NextTimer(TimeSpan.FromSeconds(1)); await clock.NextTimer(TimeSpan.FromSeconds(1));
            clock.Advance(TimeSpan.FromSeconds(1));
            await clock.NextTimer(TimeSpan.FromSeconds(4)); await clock.NextTimer(TimeSpan.FromSeconds(4)); clock.Advance(TimeSpan.FromSeconds(4));
            var outcome = await observed.NextSettlement(); Assert.Equal(RetentionPassOutcome.Waiting, outcome.Outcome);
            Assert.Equal("STORAGE_FAILURE", outcome.Problem!.Code);
            var failed = await service.ReadAsync(target); Assert.Equal(0, failed.Coverage.ReceiptObjects);
            Assert.Equal(0, failed.Coverage.PresentObjects);
            var sha = Convert.ToHexStringLower(SHA256.HashData(fixture.Data.PayloadA.Encoded));
            Assert.True(File.Exists(Path.Combine(fixture.Path, "objects-v1", sha[..2], sha[2..])));
            await ExecuteSql(fixture.Path, "DROP TRIGGER authored_receipt_failure");
            await service.SetAsync(new(target, RetentionAction.Resume));
            Assert.Equal(RetentionPassOutcome.Completed, (await observed.NextSettlement()).Outcome);
            var repaired = await service.ReadAsync(target); Assert.Equal(2, repaired.Coverage.ReceiptObjects);
            Assert.Equal(2, repaired.Coverage.PresentObjects); Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Path, "staging-v1")));
        }
        finally { await service.ShutdownAsync(default); }
    }

    [Fact]
    public async Task Real_claim_and_settlement_commit_response_losses_reconcile_without_orphaning_or_overwriting_new_intent()
    {
        var clock = new ManualClock();
        await using var fixture = await SharedAcquisitionIntegrationTests.Fixture.CreateAsync();
        await using var plans = new EncodedSourcePlans(fixture.Catalog, fixture.Objects, fixture.Resources);
        await fixture.Shared.DisposeAsync(); fixture.Shared = new(new OfficialEncodedObjectProducer(fixture.Catalog,
            fixture.Objects, fixture.Client, fixture.Resources, plans));
        using var opening = new DownloadWorkspacePreparer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Shared, fixture.Resources);
        var workspace = await opening.PrepareAsync(fixture.Request, _ => Task.CompletedTask, default);
        var target = new RetentionTarget("wow", fixture.Request.BuildIdentity, "us", workspace.Download!.LedgerId);
        var observed = new ObservedCatalog((IRetentionDriverCatalog)fixture.Catalog)
            { ClaimAfterCommitFailures = 1, SettlementAfterCommitFailures = 3 };
        var sessions = new CountingSessions(new BuildRetentionDriver.PhysicalSessions(fixture.Catalog, fixture.Objects,
            fixture.Shared, plans, fixture.Client, fixture.Resources, clock));
        var driver = new BuildRetentionDriver(observed, sessions, clock);
        var service = new RetentionService((IRetentionStore)fixture.Catalog, driver); await service.RestoreAsync(default);
        try
        {
            await service.SetAsync(new(target, RetentionAction.DownloadAll));
            await clock.NextTimer(TimeSpan.FromSeconds(1)); Assert.Equal(0, sessions.Opened); clock.Advance(TimeSpan.FromSeconds(1));
            await clock.NextTimer(TimeSpan.FromSeconds(1)); await clock.NextTimer(TimeSpan.FromSeconds(1)); clock.Advance(TimeSpan.FromSeconds(1));
            await clock.NextTimer(TimeSpan.FromSeconds(4)); clock.Advance(TimeSpan.FromSeconds(4));
            Assert.Equal("RETENTION_STATE_RETRY", (await Faulted(driver)).Problem.Code);
            var committed = await service.ReadAsync(target);
            Assert.Equal(RetentionState.Completed, committed.State); Assert.Equal(2, committed.Coverage.ReceiptObjects);
            Assert.Equal(1, sessions.Opened); Assert.NotNull(driver.ReadFault());
            await service.SetAsync(new(target, RetentionAction.OnDemand)); await service.SetAsync(new(target, RetentionAction.DownloadAll));
            await observed.NextSettlement(); await observed.NextSettlement();
            var current = await service.ReadAsync(target); Assert.Equal(RetentionState.Completed, current.State);
            Assert.Equal(RetentionMode.DownloadAll, current.Mode); Assert.Equal(3, current.Revision);
            Assert.Equal(2, current.Coverage.ReceiptObjects); Assert.Equal(2, sessions.Opened); Assert.Null(driver.ReadFault());
        }
        finally { await service.ShutdownAsync(default); }
    }

    [Fact]
    public async Task Real_unsupported_declared_lengths_are_typed_member_problems_and_supported_peers_still_retain()
    {
        await using var fixture = await SharedAcquisitionIntegrationTests.Fixture.CreateAsync();
        var supported = fixture.Data.PayloadA;
        await RegisterAuthoredScope(fixture, [(Key(12345), 0), (Key(54321), VerifiedObjectStore.MaxObjectBytes + 1),
            (supported.EncodingKey, supported.Encoded.Length)]);
        await using var plans = new EncodedSourcePlans(fixture.Catalog, fixture.Objects, fixture.Resources);
        await fixture.Shared.DisposeAsync(); fixture.Shared = new(new OfficialEncodedObjectProducer(fixture.Catalog,
            fixture.Objects, fixture.Client, fixture.Resources, plans));
        using var opening = new DownloadWorkspacePreparer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Shared, fixture.Resources);
        var workspace = await opening.PrepareAsync(fixture.Request, _ => Task.CompletedTask, default);
        var target = new RetentionTarget("wow", fixture.Request.BuildIdentity, "us", workspace.Download!.LedgerId);
        var observed = new ObservedCatalog((IRetentionDriverCatalog)fixture.Catalog);
        var driver = new BuildRetentionDriver(observed, new BuildRetentionDriver.PhysicalSessions(fixture.Catalog,
            fixture.Objects, fixture.Shared, plans, fixture.Client, fixture.Resources, TimeProvider.System));
        var service = new RetentionService((IRetentionStore)fixture.Catalog, driver); await service.RestoreAsync(default);
        try
        {
            await service.SetAsync(new(target, RetentionAction.DownloadAll));
            var settled = await observed.NextSettlement(); Assert.Equal(RetentionPassOutcome.Failed, settled.Outcome);
            Assert.Equal("RETENTION_OBJECT_UNSUPPORTED", settled.Problem!.Code); Assert.False(settled.Problem.Retryable);
            var snapshot = await service.ReadAsync(target); Assert.Equal(1, snapshot.Coverage.ReceiptObjects);
            Assert.Equal(1, snapshot.Coverage.PresentObjects); Assert.Equal(2, snapshot.FailedObjects);
            Assert.Equal(0, snapshot.Coverage.MissingObjects); Assert.Equal(0, snapshot.Coverage.CorruptObjects);
            Assert.DoesNotContain(fixture.Handler.Requests, url => url.EndsWith(Key(12345), StringComparison.Ordinal) || url.EndsWith(Key(54321), StringComparison.Ordinal));
            Assert.Single(fixture.Handler.Requests, url => url == InventoryFixture.Url("data", supported.EncodingKey));
        }
        finally { await service.ShutdownAsync(default); }
    }

    private static async Task ExecuteSql(string directory, string sql)
    {
        await using var db = new SqliteConnection($"Data Source={Path.Combine(directory, "discovery-v1.sqlite3")};Pooling=False");
        await db.OpenAsync(); using var command = db.CreateCommand(); command.CommandText = sql; await command.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Real_authored_257_member_cohort_includes_large_ranged_payload_and_bounded_catalog_batches()
    {
        await using var fixture = await SharedAcquisitionIntegrationTests.Fixture.CreateAsync();
        var payloads = new List<(string Key, long Bytes)>(); long payloadBytes = 0;
        for (var i = 0; i < 257; i++)
        {
            var bytes = new byte[i == 0 ? (5 << 20) + 9 : 41]; "BLTE"u8.CopyTo(bytes); bytes[8] = (byte)'N';
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(9), i); var key = Convert.ToHexStringLower(MD5.HashData(bytes));
            fixture.Data.Payloads[key] = bytes; payloads.Add((key, bytes.Length)); payloadBytes += bytes.Length;
        }
        var encodedDlBytes = await RegisterAuthoredScope(fixture, payloads);
        await using var plans = new EncodedSourcePlans(fixture.Catalog, fixture.Objects, fixture.Resources);
        await fixture.Shared.DisposeAsync(); fixture.Shared = new(new OfficialEncodedObjectProducer(fixture.Catalog,
            fixture.Objects, fixture.Client, fixture.Resources, plans));
        using var opening = new DownloadWorkspacePreparer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Shared, fixture.Resources);
        var workspace = await opening.PrepareAsync(fixture.Request, _ => Task.CompletedTask, default);
        var target = new RetentionTarget("wow", fixture.Request.BuildIdentity, "us", workspace.Download!.LedgerId);
        var observed = new ObservedCatalog((IRetentionDriverCatalog)fixture.Catalog);
        var driver = new BuildRetentionDriver(observed, new BuildRetentionDriver.PhysicalSessions(fixture.Catalog,
            fixture.Objects, fixture.Shared, plans, fixture.Client, fixture.Resources, TimeProvider.System));
        var service = new RetentionService((IRetentionStore)fixture.Catalog, driver); await service.RestoreAsync(default);
        var started = Stopwatch.StartNew(); var allocated = GC.GetTotalAllocatedBytes(); var requests = fixture.Handler.Requests.Count;
        try
        {
            await service.SetAsync(new(target, RetentionAction.DownloadAll));
            Assert.Equal(RetentionPassOutcome.Completed, (await observed.NextSettlement()).Outcome);
            var snapshot = await service.ReadAsync(target); started.Stop();
            Assert.Equal(257, snapshot.Coverage.PresentObjects); Assert.Equal(payloadBytes, snapshot.Coverage.ReceiptBytes);
            Assert.Equal(payloadBytes, snapshot.WireBytesThisSession);
            Assert.Equal(258, fixture.Handler.Requests.Count - requests);
            Assert.All(observed.CreditSizes, size => Assert.InRange(size, 1, 64));
            Assert.Equal(1, plans.ConfigurationLoads); Assert.Empty(Directory.EnumerateFiles(Path.Combine(fixture.Path, "staging-v1")));
            output.WriteLine($"Authored 257 members: 256 x 41 bytes plus one 5 MiB+9 byte BLTE; encoded DL {encodedDlBytes} bytes; {payloadBytes} retained/payload wire bytes; {fixture.Handler.Requests.Count - requests} payload requests.");
            output.WriteLine($"Runtime {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}; CPUs {Environment.ProcessorCount}; elapsed {started.Elapsed.TotalSeconds:F3}s; process peak working set {Process.GetCurrentProcess().PeakWorkingSet64}; measured allocation delta {GC.GetTotalAllocatedBytes() - allocated}; max catalog credit batch {observed.CreditSizes.Max()}; config loads {plans.ConfigurationLoads}.");
            output.WriteLine("Controlled in-memory HTTP handler, fresh disposable SQLite/CAS, warm configurations, no native archive sources; process peak includes fixture/opening and the test runner. No throughput or foreground-latency promise.");
        }
        finally { await service.ShutdownAsync(default); }
    }

    private static async Task<int> RegisterAuthoredScope(SharedAcquisitionIntegrationTests.Fixture fixture, IReadOnlyList<(string Key, long Bytes)> payloads)
    {
        using var dl = new MemoryStream(); dl.Write("DL"u8); dl.Write([3, 16, 0]); Big(dl, payloads.Count, 4); Big(dl, 1, 2); dl.Write([2, 0xff, 1, 2, 3]);
        foreach (var item in payloads) { dl.Write(Convert.FromHexString(item.Key)); Big(dl, item.Bytes, 5); dl.Write([0x80, 1, 2]); }
        dl.Write("all"u8); dl.WriteByte(0); Big(dl, 5, 2); dl.Write(new byte[(payloads.Count + 7) / 8]);
        var decoded = dl.ToArray(); byte[] encoded = [.. "BLTE"u8, 0, 0, 0, 0, (byte)'N', .. decoded];
        var contentKey = Convert.ToHexStringLower(MD5.HashData(decoded)); var encodingKey = Convert.ToHexStringLower(MD5.HashData(encoded));
        fixture.Data.Payloads[encodingKey] = encoded;
        var buildConfig = Encoding.UTF8.GetBytes($"download = {contentKey} {encodingKey}\ndownload-size = {decoded.Length} {encoded.Length}\n");
        var buildKey = Convert.ToHexStringLower(MD5.HashData(buildConfig)); fixture.Data.Responses[InventoryFixture.Url("config", buildKey)] = buildConfig;
        var original = fixture.Data.Discovery; var versions = original.Documents.Single(document => document.Role == "versions");
        var versionBytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(versions.Bytes).Replace(fixture.Data.BuildKey, buildKey, StringComparison.Ordinal));
        var fields = VibeMiner.Protocols.Tact.MetadataParser.Parse(versionBytes).Rows.Single().Fields; var identity = BuildIdentity.Create("wow", fields);
        var discovery = original with
        {
            Documents = [versions with { Bytes = versionBytes, Sha256 = Convert.ToHexStringLower(SHA256.HashData(versionBytes)) }, original.Documents.Single(document => document.Role == "cdns")],
            Observations = [original.Observations.Single() with { Identity = identity, OriginalFields = fields }]
        };
        var generation = await fixture.Catalog.BeginRefreshAsync("wow", default);
        await fixture.Catalog.CommitAsync("wow", generation, discovery, "2026-10-19T02:00:00Z", default);
        fixture.Request = new("wow", identity.Id, "us");
        return encoded.Length;
    }

    private static void Big(Stream output, long value, int width)
    { Span<byte> bytes = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(bytes, value); output.Write(bytes[(8 - width)..]); }

    private sealed class ObservedCatalog(IRetentionDriverCatalog inner) : IRetentionDriverCatalog
    {
        private readonly HashSet<(string, long)> seen = [];
        private readonly Channel<Settlement> settled = Channel.CreateUnbounded<Settlement>();
        internal readonly ConcurrentQueue<int> CreditSizes = new();
        internal int ClaimAfterCommitFailures, SettlementAfterCommitFailures;
        public Task RestoreDriverAsync(CancellationToken token) => inner.RestoreDriverAsync(token);
        public async Task<RetentionDriverJob?> ClaimNextRetentionAsync(CancellationToken token)
        {
            var job = await inner.ClaimNextRetentionAsync(token);
            if (job is not null && ClaimAfterCommitFailures > 0) { ClaimAfterCommitFailures--; throw ResponseLoss(); }
            return job;
        }
        public Task<RetentionDriverIntent?> ReadRetentionIntentAsync(string id, CancellationToken token) => inner.ReadRetentionIntentAsync(id, token);
        public Task<bool> RegisterRetentionMapAsync(RetentionDriverBinding binding, BulkArchiveLocatorArtifact map, CancellationToken token)
            => inner.RegisterRetentionMapAsync(binding, map, token);
        public Task<bool> ApplyRetentionCreditAsync(RetentionDriverBinding binding, IReadOnlyList<RetentionMemberObservation> items, long nextOrdinal, CancellationToken token)
        { CreditSizes.Enqueue(items.Count); return inner.ApplyRetentionCreditAsync(binding, items, nextOrdinal, token); }
        public Task<bool> ReportRetentionProgressAsync(RetentionDriverBinding binding, AcquisitionProgress? progress, int pending, long wireBytes, CancellationToken token)
            => inner.ReportRetentionProgressAsync(binding, progress, pending, wireBytes, token);
        public async Task SettleRetentionAsync(RetentionDriverBinding binding, RetentionPassOutcome outcome, RetentionProblem? problem, CancellationToken token)
        {
            await inner.SettleRetentionAsync(binding, outcome, problem, token);
            if (SettlementAfterCommitFailures > 0) { SettlementAfterCommitFailures--; throw ResponseLoss(); }
            if (seen.Add((binding.JobId, binding.Generation))) settled.Writer.TryWrite(new(binding, outcome, problem));
        }
        internal Task<Settlement> NextSettlement() => settled.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        private static DiscoveryException ResponseLoss() => new("AUTHORED_RESPONSE_LOSS", "The authored commit response was lost.", true);
    }

    private sealed class CountingSessions(IRetentionSessionFactory inner) : IRetentionSessionFactory
    {
        internal int Opened;
        public Task<IRetentionSession> OpenAsync(RetentionDriverJob job, Func<AcquisitionProgress, Task> progress, CancellationToken token)
        { Interlocked.Increment(ref Opened); return inner.OpenAsync(job, progress, token); }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Never() => Signal().Task;
    private static RetentionDriverJob Job(int id, int count)
    {
        var target = new RetentionTarget("wow", id.ToString("x64"), "us", (id + 100).ToString("x64"));
        var source = new OpeningSourceReference("authored", "us", new string('a', 64), 0, new string('b', 64), 0);
        var download = new MetadataReceipt("download", Key(1), Key(2), new string('c', 64), "10", "1",
            "https://us.cdn.blizzard.com/tpr/wow/data/00/00/" + Key(2), "2026-10-19T00:00:00Z", "2026-10-19T00:00:00Z", "metadata-v1");
        return new(new("job-" + id, 1, 1), new(target, source, [], download,
            new(target.LedgerId, 32 + count * 24L, count, count * 10L, DownloadLedger.Revision),
            "downloadManifestAllVariants", "retention-encoded-v1"));
    }

    private sealed record Settlement(RetentionDriverBinding Binding, RetentionPassOutcome Outcome, RetentionProblem? Problem);
    private sealed class DriverCatalog(params RetentionDriverJob[] jobs) : IRetentionDriverCatalog
    {
        private readonly object gate = new();
        private readonly Queue<RetentionDriverJob> queue = new(jobs);
        private readonly Dictionary<string, RetentionDriverIntent> intents = jobs.ToDictionary(job => job.Binding.JobId,
            _ => new RetentionDriverIntent(1, RetentionMode.DownloadAll, false));
        private readonly Channel<Settlement> settlements = Channel.CreateUnbounded<Settlement>();
        private readonly HashSet<string> inactive = [];
        internal readonly ConcurrentQueue<IReadOnlyList<RetentionMemberObservation>> Batches = new();
        internal readonly ConcurrentQueue<Settlement> Settled = new();
        internal int Restores;
        internal int ClaimCalls, ClaimFailures, ClaimAfterCommitFailures, SettleFailures;
        internal Action? OnThirdClaimFailure;
        private RetentionDriverJob? uncertainClaim;
        internal bool RejectCredits, ChangeAtSettlement;
        public Task RestoreDriverAsync(CancellationToken token)
        {
            Interlocked.Increment(ref Restores);
            lock (gate)
                if (uncertainClaim is not null)
                { var remaining = queue.ToArray(); queue.Clear(); queue.Enqueue(uncertainClaim); foreach (var item in remaining) queue.Enqueue(item); uncertainClaim = null; }
            return Task.CompletedTask;
        }
        public Task<RetentionDriverJob?> ClaimNextRetentionAsync(CancellationToken token)
        {
            lock (gate)
            {
                ClaimCalls++;
                if (ClaimFailures > 0)
                { ClaimFailures--; if (ClaimCalls == 3) OnThirdClaimFailure?.Invoke(); throw CatalogFailure(); }
                if (ClaimAfterCommitFailures > 0) { ClaimAfterCommitFailures--; uncertainClaim = queue.Dequeue(); throw CatalogFailure(); }
                return Task.FromResult(queue.Count > 0 ? queue.Dequeue() : null);
            }
        }
        public Task<RetentionDriverIntent?> ReadRetentionIntentAsync(string id, CancellationToken token)
        { lock (gate) return Task.FromResult(intents.GetValueOrDefault(id)); }
        internal void Change(string id, RetentionDriverIntent intent) { lock (gate) intents[id] = intent; }
        public Task<bool> RegisterRetentionMapAsync(RetentionDriverBinding binding, BulkArchiveLocatorArtifact map, CancellationToken token)
            => Task.FromResult(true);
        public Task<bool> ApplyRetentionCreditAsync(RetentionDriverBinding binding, IReadOnlyList<RetentionMemberObservation> items, long nextOrdinal, CancellationToken token)
        {
            Batches.Enqueue(items.ToArray());
            if (RejectCredits) Change(binding.JobId, new(2, RetentionMode.OnDemand, false));
            return Task.FromResult(!RejectCredits);
        }
        public Task<bool> ReportRetentionProgressAsync(RetentionDriverBinding binding, AcquisitionProgress? progress, int pending, long wireBytes, CancellationToken token)
            => Task.FromResult(true);
        public Task SettleRetentionAsync(RetentionDriverBinding binding, RetentionPassOutcome outcome, RetentionProblem? problem, CancellationToken token)
        {
            lock (gate)
            {
                if (SettleFailures > 0) { SettleFailures--; throw CatalogFailure(); }
                if (ChangeAtSettlement) { ChangeAtSettlement = false; intents[binding.JobId] = new(2, RetentionMode.OnDemand, false); }
                if (outcome != RetentionPassOutcome.IntentChanged && intents[binding.JobId].Revision != binding.IntentRevision)
                    return Task.CompletedTask;
                if (!inactive.Add(binding.JobId)) return Task.CompletedTask;
            }
            var result = new Settlement(binding, outcome, problem); Settled.Enqueue(result); settlements.Writer.TryWrite(result);
            return Task.CompletedTask;
        }
        internal Task<Settlement> NextSettlement() => settlements.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
        private static DiscoveryException CatalogFailure() => new("AUTHORED_CATALOG", "The authored catalog operation failed.", true);
    }

    private sealed class Sessions : IRetentionSessionFactory
    {
        internal Action<Session>? Configure;
        internal readonly ConcurrentQueue<Session> Created = new();
        private readonly Channel<Session> opened = Channel.CreateUnbounded<Session>();
        public Task<IRetentionSession> OpenAsync(RetentionDriverJob job, Func<AcquisitionProgress, Task> progress, CancellationToken token)
        {
            var session = new Session((int)job.Plan.Ledger.ObjectCount); Configure?.Invoke(session);
            Created.Enqueue(session); opened.Writer.TryWrite(session); return Task.FromResult<IRetentionSession>(session);
        }
        internal Task<Session> NextOpened() => opened.Reader.ReadAsync().AsTask().WaitAsync(Deadline);
    }
    private sealed class Session(int count) : IRetentionSession
    {
        private readonly ConcurrentDictionary<long, bool> present = new();
        internal Task Release = Task.CompletedTask;
        internal Task Cleanup = Task.CompletedTask;
        internal readonly TaskCompletionSource Attached = Signal(), FirstFrontier = Signal(), CleanupEntered = Signal();
        internal readonly ConcurrentQueue<int> ReceiptBatchSizes = new();
        internal int Attachments, Pending, PeakPending;
        internal long? FailOrdinal;
        internal bool FailEveryMember, RetryableFailure, Disposed;
        public Task<IReadOnlyList<DownloadLedgerRow>> ReadAsync(long nextOrdinal, int limit, CancellationToken token)
            => Task.FromResult<IReadOnlyList<DownloadLedgerRow>>(Enumerable.Range((int)nextOrdinal, Math.Min(limit, count - (int)nextOrdinal))
                .Select(id => new DownloadLedgerRow(id + 1L, Key(id + 1), 10)).ToArray());
        public Task<IReadOnlyList<RetentionMemberObservation>> ObserveAsync(IReadOnlyList<DownloadLedgerRow> rows, CancellationToken token)
        {
            ReceiptBatchSizes.Enqueue(rows.Count);
            return Task.FromResult<IReadOnlyList<RetentionMemberObservation>>(rows.Select(row => new RetentionMemberObservation(row.Id - 1,
                row.EncodingKey, row.EncodedBytes, present.ContainsKey(row.Id) ? RetentionPresence.Present : RetentionPresence.Unknown,
                present.ContainsKey(row.Id))).ToArray());
        }
        public Task<IAcquisitionSubscription> AttachAsync(DownloadLedgerRow row, Func<AcquisitionProgress, Task> progress, CancellationToken token)
        {
            Interlocked.Increment(ref Attachments); var pending = Interlocked.Increment(ref Pending);
            PeakPending = Math.Max(PeakPending, pending); Attached.TrySetResult(); if (pending == 32) FirstFrontier.TrySetResult();
            return Task.FromResult<IAcquisitionSubscription>(new Subscription(this, row));
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        private sealed class Subscription(Session owner, DownloadLedgerRow row) : IAcquisitionSubscription
        {
            private Task<EncodedReceipt>? completion;
            private bool disposed;
            public Task<EncodedReceipt> Completion => completion ??= Produce();
            private async Task<EncodedReceipt> Produce()
            {
                await owner.Release;
                if (owner.RetryableFailure) throw new DiscoveryException("AUTHORED_NETWORK", "The authored source is offline.", true);
                if (owner.FailEveryMember || owner.FailOrdinal == row.Id - 1)
                    throw new DiscoveryException("AUTHORED_INTEGRITY", "An authored member cannot be retained.");
                owner.present[row.Id] = true;
                return new(row.EncodingKey, new string('d', 64), row.EncodedBytes,
                    "https://us.cdn.blizzard.com/tpr/wow/data/00/00/" + row.EncodingKey,
                    "2026-10-19T00:00:00Z", "2026-10-19T00:00:00Z", BuildRetentionDriver.VerifierRevision,
                    new("loose", "0", "10", null));
            }
            public Task<EncodedReceipt> WaitAsync(CancellationToken token = default) => Completion.WaitAsync(token);
            public void SetPriority(AcquisitionPriority priority) { }
            public async ValueTask DisposeAsync()
            {
                if (disposed) return; disposed = true; owner.CleanupEntered.TrySetResult(); await owner.Cleanup;
                Interlocked.Decrement(ref owner.Pending);
            }
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private readonly object gate = new();
        private readonly List<Timer> timers = [];
        private readonly Channel<TimeSpan> created = Channel.CreateUnbounded<TimeSpan>();
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (gate) return ticks; }
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state); timer.Change(dueTime, period);
            lock (gate) timers.Add(timer); created.Writer.TryWrite(dueTime); return timer;
        }
        internal async Task NextTimer(TimeSpan due)
        { while (await created.Reader.ReadAsync().AsTask().WaitAsync(Deadline) != due) { } }
        internal void Advance(TimeSpan by)
        {
            Timer[] ready;
            lock (gate)
            {
                ticks += by.Ticks; ready = timers.Where(timer => timer.Due <= ticks).ToArray();
                foreach (var timer in ready) timer.Due = timer.Period == Timeout.InfiniteTimeSpan ? long.MaxValue : ticks + timer.Period.Ticks;
            }
            foreach (var timer in ready) timer.Callback(timer.State);
        }
        private sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            internal readonly TimerCallback Callback = callback;
            internal readonly object? State = state;
            internal long Due = long.MaxValue;
            internal TimeSpan Period;
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock.gate)
                {
                    if (disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock.ticks + dueTime.Ticks; Period = period; return true;
                }
            }
            public void Dispose() { lock (clock.gate) { disposed = true; Due = long.MaxValue; } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    [Fact]
    public async Task Validated_ledger_session_pages_and_membership_share_one_handle_and_cursor_does_not_own_it()
    {
        using var directory = new TempDirectory();
        await using var objects = new VerifiedObjectStore(directory.Path);
        var artifact = await Ledger(objects, 257);
        var resources = new AcquisitionResources();
        var context = new BackgroundWork();
        await using var session = await new DownloadLedger(objects).OpenReadSessionAsync(artifact, default, resources, context);
        // A new full proof would need this background CPU lane. Session lookups do not.
        using var cpu = await resources.EnterCpuAsync(context, default);
        var first = await session.ReadPageAsync(0, 128, default).WaitAsync(Deadline);
        Assert.Equal(128, first.Count); Assert.Equal(1, first[0].Id); Assert.Equal(Key(128), first[^1].EncodingKey);
        var reads = Enumerable.Range(1, 257).Select(id => session.FindAsync(Key(id), id + 9, default));
        var found = await Task.WhenAll(reads).WaitAsync(Deadline);
        Assert.Equal(Enumerable.Range(1, 257).Select(id => (long)id), found.Select(row => row!.Id));
        await using (var cursor = session.OpenOrderedCursor())
        {
            Assert.Equal(artifact, cursor.Artifact); var count = 0;
            while (await cursor.NextAsync(default)) { count++; Assert.Equal(Key(count), cursor.Current.Key); }
            Assert.Equal(257, count);
        }
        Assert.Equal(Key(257), (await session.ReadPageAsync(256, 128, default)).Single().EncodingKey);
        Assert.Null(await session.FindAsync(Key(258), 267, default));
        Assert.Equal("CONTENT_SIZE_CONFLICT", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            session.FindAsync(Key(1), 99, default))).Code);
        await Assert.ThrowsAsync<DiscoveryException>(() => session.ReadPageAsync(0, 129, default));
    }

    [Fact]
    public async Task Zero_member_session_has_honest_EOF_and_no_member()
    {
        using var directory = new TempDirectory(); await using var objects = new VerifiedObjectStore(directory.Path);
        var artifact = await Ledger(objects, 0);
        await using var session = await new DownloadLedger(objects).OpenReadSessionAsync(artifact, default);
        Assert.Empty(await session.ReadPageAsync(0, 128, default));
        Assert.Null(await session.FindAsync(Key(1), 10, default));
        await using var cursor = session.OpenOrderedCursor(); Assert.False(await cursor.NextAsync(default));
    }

    [Fact]
    public async Task Session_refuses_valid_CAS_with_invalid_ledger_accounting_before_exposing_rows()
    {
        using var directory = new TempDirectory(); await using var objects = new VerifiedObjectStore(directory.Path);
        var artifact = await Ledger(objects, 2, wrongTotal: true);
        Assert.Equal("DOWNLOAD_LEDGER_INTEGRITY", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            new DownloadLedger(objects).OpenReadSessionAsync(artifact, default))).Code);
    }

    private static async Task<DownloadLedgerArtifact> Ledger(VerifiedObjectStore objects, int count, bool wrongTotal = false)
    {
        var bytes = new byte[DownloadLedger.HeaderBytes + count * DownloadLedger.RecordBytes];
        "VMDLEDG1"u8.CopyTo(bytes); BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12), DownloadLedger.RecordBytes);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(16), count);
        long total = 0;
        for (var i = 1; i <= count; i++)
        {
            LedgerValue.Parse(Key(i), i + 9).Write(bytes.AsSpan(DownloadLedger.HeaderBytes + (i - 1) * DownloadLedger.RecordBytes));
            total += i + 9;
        }
        if (wrongTotal) total++;
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(24), total);
        string path;
        await using (var output = await objects.CreateStagingFileAsync(default))
        { path = output.Name; await output.WriteAsync(bytes); await output.FlushAsync(); }
        return new(await objects.PublishFileAsync(path, bytes.Length, default), bytes.Length, count, total);
    }

    private sealed class BackgroundWork : IAcquisitionWorkContext
    {
        public AcquisitionWorkSnapshot Snapshot => new(1, AcquisitionPriority.Background, []);
        public Task WaitForChangeAsync(long observedRevision, CancellationToken token = default) => Task.Delay(Timeout.Infinite, token);
        public Task ReportAsync(AcquisitionProgress progress) => Task.CompletedTask;
    }
}
