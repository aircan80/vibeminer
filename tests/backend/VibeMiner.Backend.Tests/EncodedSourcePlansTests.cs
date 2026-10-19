using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class EncodedSourcePlansTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Concurrent_creation_reads_configuration_once_and_one_canceled_waiter_does_not_cancel_another()
    {
        await using var fixture = await SharedAcquisitionIntegrationTests.Fixture.CreateAsync();
        await using var plans = new EncodedSourcePlans(fixture.Catalog, fixture.Objects, fixture.Resources);
        var source = fixture.Source with { SourceReference = (await fixture.Catalog.ResolveOpeningAsync(fixture.Request, default)).Reference };
        using var firstCpu = await fixture.Resources.EnterCpuAsync(null, default);
        using var secondCpu = await fixture.Resources.EnterCpuAsync(null, default);
        using var cancellation = new CancellationTokenSource();
        var abandoned = plans.AcquireAsync(source, cancellation.Token);
        var survivor = plans.AcquireAsync(source);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        Assert.False(survivor.IsCompleted);
        firstCpu.Dispose(); secondCpu.Dispose();
        await using var retained = await survivor.WaitAsync(Deadline);
        Assert.Equal(source, retained.Plan.Source);
        File.Delete(Path.Combine(fixture.Path, "objects-v1", source.CdnConfigurationSha256[..2], source.CdnConfigurationSha256[2..]));
        var borrowed = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => plans.AcquireAsync(source)));
        Assert.All(borrowed, lease => Assert.Same(retained.Plan, lease.Plan));
        foreach (var lease in borrowed) await lease.DisposeAsync();
        Assert.Equal(1, plans.ConfigurationLoads);
    }

    [Fact]
    public async Task Last_pending_consumer_cancellation_retires_cpu_wait_and_releases_source_capacity()
    {
        await using var fixture = await SharedAcquisitionIntegrationTests.Fixture.CreateAsync();
        await using var plans = new EncodedSourcePlans(fixture.Catalog, fixture.Objects, fixture.Resources, maximumEntries: 1);
        var source = fixture.Source with { SourceReference = (await fixture.Catalog.ResolveOpeningAsync(fixture.Request, default)).Reference };
        using var firstCpu = await fixture.Resources.EnterCpuAsync(null, default);
        using var secondCpu = await fixture.Resources.EnterCpuAsync(null, default);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new Work(source) { Reporter = _ => { entered.TrySetResult(); return Task.CompletedTask; } };
        using var cancellation = new CancellationTokenSource();
        var abandoned = plans.AcquireAsync(source, cancellation.Token, work);
        await entered.Task.WaitAsync(Deadline);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        var replacement = plans.AcquireAsync(source);
        Assert.False(replacement.IsCompleted);
        firstCpu.Dispose(); secondCpu.Dispose();
        await using var retained = await replacement.WaitAsync(Deadline);
        Assert.Equal(1, plans.ConfigurationLoads);
    }

    [Fact]
    public async Task Concurrent_registry_disposal_waits_for_the_same_pending_creation_cleanup()
    {
        await using var fixture = await SharedAcquisitionIntegrationTests.Fixture.CreateAsync();
        var plans = new EncodedSourcePlans(fixture.Catalog, fixture.Objects, fixture.Resources);
        var source = fixture.Source with { SourceReference = (await fixture.Catalog.ResolveOpeningAsync(fixture.Request, default)).Reference };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new Work(source) { Reporter = _ => { entered.TrySetResult(); return release.Task; } };
        var pending = plans.AcquireAsync(source, context: work);
        await entered.Task.WaitAsync(Deadline);
        var first = plans.DisposeAsync().AsTask(); var second = plans.DisposeAsync().AsTask();
        Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
        release.SetResult(); await Task.WhenAll(first, second).WaitAsync(Deadline);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Live_source_capacity_is_typed_and_idle_source_is_evicted_without_mutating_a_lease()
    {
        await using var fixture = await SharedAcquisitionIntegrationTests.Fixture.CreateAsync();
        await using var plans = new EncodedSourcePlans(fixture.Catalog, fixture.Objects, fixture.Resources, maximumEntries: 1);
        var first = await plans.AcquireAsync(fixture.Source);
        var another = await fixture.PrepareAlternateSourceAsync("# second immutable source\narchives =\n"u8.ToArray());
        var failure = await Assert.ThrowsAsync<DiscoveryException>(() => plans.AcquireAsync(another));
        Assert.Equal("ENCODED_SOURCE_CAPACITY", failure.Code);
        Assert.Equal(fixture.Source.CdnConfigurationSha256, first.Plan.Sha256);
        await first.DisposeAsync();
        await using var replacement = await plans.AcquireAsync(another);
        Assert.Equal(another.CdnConfigurationSha256, replacement.Plan.Sha256);
        Assert.Equal(2, plans.ConfigurationLoads);
    }

    [Fact]
    public async Task Pinned_original_source_does_not_drift_after_discovery_refresh_changes_route()
    {
        await using var fixture = await SharedAcquisitionIntegrationTests.Fixture.CreateAsync();
        var old = (await fixture.Catalog.ResolveOpeningAsync(fixture.Request, default)).Reference;
        var original = fixture.Data.Discovery;
        var cdns = original.Documents.Single(document => document.Role == "cdns");
        var bytes = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(cdns.Bytes)
            .Replace("https://us.cdn.blizzard.com/", "https://level3.ssl.blizzard.com/", StringComparison.Ordinal));
        var fields = VibeMiner.Protocols.Tact.MetadataParser.Parse(bytes).Rows.Single().Fields;
        var discovery = original with
        {
            Documents = [original.Documents.Single(document => document.Role == "versions"),
                cdns with { Bytes = bytes, Sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) }],
            Observations = [original.Observations.Single() with { RouteFields = fields }]
        };
        var generation = await fixture.Catalog.BeginRefreshAsync("wow", default);
        await fixture.Catalog.CommitAsync("wow", generation, discovery, "2026-10-19T02:00:00.0000000Z", default);
        await using var plans = new EncodedSourcePlans(fixture.Catalog, fixture.Objects, fixture.Resources);
        await using var pinned = await plans.AcquireAsync(fixture.Source with { SourceReference = old });
        await using var current = await plans.AcquireAsync(fixture.Source);
        Assert.Equal("us.cdn.blizzard.com", pinned.Plan.Routes[0].Host);
        Assert.Equal("level3.ssl.blizzard.com", current.Plan.Routes[0].Host);
        Assert.NotEqual(pinned.Plan.Source.SourceReference, current.Plan.Source.SourceReference);
        Assert.Equal(2, plans.ConfigurationLoads);
    }

    [Fact]
    public async Task Map_withdrawal_waits_for_borrowers_and_new_foreground_work_cannot_enter_retired_session()
    {
        await using var fixture = await SharedAcquisitionIntegrationTests.Fixture.CreateAsync();
        await using var plans = new EncodedSourcePlans(fixture.Catalog, fixture.Objects, fixture.Resources);
        await using var plan = await plans.AcquireAsync(fixture.Source);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registration = plans.RegisterMap(plan, async (_, _, token) =>
        {
            entered.SetResult(); await release.Task.WaitAsync(token); return new(true, []);
        });
        var item = fixture.Data.PayloadA;
        var lookup = plans.LookupMapAsync(plan, item.EncodingKey, item.Encoded.Length, default);
        await entered.Task.WaitAsync(Deadline);
        var withdrawal = registration.DisposeAsync().AsTask();
        Assert.False(withdrawal.IsCompleted);
        Assert.Null(await plans.LookupMapAsync(plan, item.EncodingKey, item.Encoded.Length, default));
        release.SetResult(); Assert.True((await lookup)!.IsMember);
        await withdrawal.WaitAsync(Deadline);
    }

    [Fact]
    public async Task Complete_map_member_miss_uses_loose_without_per_object_archive_scan()
    {
        await using var fixture = await SharedAcquisitionIntegrationTests.Fixture.CreateAsync();
        await using var plans = new EncodedSourcePlans(fixture.Catalog, fixture.Objects, fixture.Resources);
        await using var plan = await plans.AcquireAsync(fixture.Source);
        await using var registration = plans.RegisterMap(plan, (_, _, _) => Task.FromResult(new EncodedMapLookup(true, [])));
        var producer = new OfficialEncodedObjectProducer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Resources, plans);
        var item = fixture.Data.PayloadA;
        var context = new Work(plan.Plan.Source);
        var receipt = await producer.ProduceAsync(new(item.EncodingKey, item.Encoded.Length), context, default);
        Assert.Equal(item.EncodingKey, receipt.EncodingKey);
        Assert.Contains(context.Progress, value => value.Unit == "wireBytes" && value.Completed == item.Encoded.Length);
        Assert.DoesNotContain(fixture.Handler.Requests, url => url.EndsWith(".index", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task Registered_map_hit_precedes_loose_and_only_proven_member_miss_skips_exact_archive_scan(bool member, bool hit)
    {
        await using var fixture = await SharedAcquisitionIntegrationTests.Fixture.CreateAsync(archiveEncoding: true);
        await using var plans = new EncodedSourcePlans(fixture.Catalog, fixture.Objects, fixture.Resources);
        await using var plan = await plans.AcquireAsync(fixture.Source);
        var item = fixture.Data.Metadata["encoding"];
        await using var registration = plans.RegisterMap(plan, (_, _, _) => Task.FromResult(new EncodedMapLookup(member,
            hit ? [new(plan.Plan.ArchiveKeys.Single(), 19, item.Encoded.Length)] : [])));
        var producer = new OfficialEncodedObjectProducer(fixture.Catalog, fixture.Objects, fixture.Client, fixture.Resources, plans);
        var producing = producer.ProduceAsync(new(item.EncodingKey, item.Encoded.Length), new Work(plan.Plan.Source), default);
        if (member && !hit)
        {
            var failure = await Assert.ThrowsAsync<DiscoveryException>(() => producing);
            Assert.Equal("CONTENT_NOT_LOCATED", failure.Code);
        }
        else Assert.Equal(item.EncodingKey, (await producing).EncodingKey);
        Assert.Equal(member ? 0 : 3, fixture.Handler.Requests.Count(url => url.EndsWith(".index", StringComparison.Ordinal)));
        if (hit) Assert.DoesNotContain(InventoryFixture.Url("data", item.EncodingKey), fixture.Handler.Requests);
    }

    private sealed class Work(EncodedSourceContext source) : IAcquisitionWorkContext
    {
        public AcquisitionWorkSnapshot Snapshot => new(0, AcquisitionPriority.Interactive, [source]);
        public Task WaitForChangeAsync(long revision, CancellationToken token = default) => Task.Delay(Timeout.InfiniteTimeSpan, token);
        internal readonly System.Collections.Concurrent.ConcurrentQueue<AcquisitionProgress> Progress = new();
        internal Func<AcquisitionProgress, Task>? Reporter;
        public Task ReportAsync(AcquisitionProgress progress) { Progress.Enqueue(progress); return Reporter?.Invoke(progress) ?? Task.CompletedTask; }
    }
}
