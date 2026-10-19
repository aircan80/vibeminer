using VibeMiner.Application;
using VibeMiner.Domain;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class TableDataServiceTests
{
    private static readonly TableDataRequest Request = new("wow", new string('a', 64), "us", "50238ec2", "enUS");
    private static readonly TableRowsRequest Rows = new(Request.ProductId, Request.BuildIdentity, Request.SourceRegion,
        Request.TableId, Request.ResearchLocale, new string('b', 64));
    private static readonly TableDataSnapshot Saved = Snapshot();
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static IEnumerable<object[]> InvalidContexts()
    {
        yield return [Request with { ProductId = "../wow" }];
        yield return [Request with { BuildIdentity = null! }];
        yield return [Request with { BuildIdentity = new string('A', 64) }];
        yield return [Request with { BuildIdentity = new string('a', 63) }];
        yield return [Request with { SourceRegion = null! }];
        yield return [Request with { SourceRegion = "US" }];
        yield return [Request with { SourceRegion = "us/path" }];
        yield return [Request with { SourceRegion = "us\n" }];
        yield return [Request with { TableId = null! }];
        yield return [Request with { TableId = "50238EC2" }];
        yield return [Request with { TableId = "Item" }];
        yield return [Request with { ResearchLocale = null! }];
        yield return [Request with { ResearchLocale = "enus" }];
        yield return [Request with { ResearchLocale = "deDE" }];
    }

    [Theory, MemberData(nameof(InvalidContexts))]
    public async Task Invalid_contexts_never_prepare_or_read_store(TableDataRequest request)
    {
        var store = new Store(); await using var service = new TableDataService(store);
        await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareAsync(request));
        await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadAsync(request));
        Assert.Equal(0, store.Prepares); Assert.Equal(0, store.Reads);
    }

    public static IEnumerable<object[]> InvalidRows()
    {
        yield return [Rows with { ArtifactId = null! }];
        yield return [Rows with { ArtifactId = new string('B', 64) }];
        yield return [Rows with { ArtifactId = new string('b', 63) }];
        yield return [Rows with { Limit = 0 }]; yield return [Rows with { Limit = 51 }];
        foreach (var after in new[] { "", "-1", "01", "1.0", " 1", "9223372036854775808" }) yield return [Rows with { After = after }];
        foreach (var id in new[] { "", "-1", "01", "4294967296", "1 OR 1=1" }) yield return [Rows with { RecordId = id }];
        yield return [Rows with { ResearchLocale = "deDE" }];
        yield return [Rows with { TableId = "../Item" }];
    }

    [Theory, MemberData(nameof(InvalidRows))]
    public async Task Invalid_row_queries_never_reach_store(TableRowsRequest request)
    {
        var store = new Store(); await using var service = new TableDataService(store);
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadRowsAsync(request))).Code);
        Assert.Equal(0, store.RowReads);
    }

    [Fact]
    public async Task Exact_queries_preserve_locale_artifact_numeric_boundaries_and_cancellation_token()
    {
        var store = new Store(); await using var service = new TableDataService(store);
        using var cancellation = new CancellationTokenSource();
        Assert.Null((await service.ReadAsync(Request, cancellation.Token)).Snapshot);
        Assert.Same(Request, store.LastRead); Assert.Equal(cancellation.Token, store.ReadToken);
        var query = Rows with { After = "9223372036854775807", RecordId = "4294967295", Limit = 1 };
        await service.ReadRowsAsync(query, cancellation.Token);
        Assert.Same(query, store.LastRows); Assert.Equal(cancellation.Token, store.RowsToken);
        await service.ReadRowsAsync(Rows with { After = "0", RecordId = "0" });
        Assert.Equal(2, store.RowReads);
    }

    [Fact]
    public async Task Pre_canceled_calls_do_not_start_any_store_work()
    {
        var store = new Store(); await using var service = new TableDataService(store);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareAsync(Request, cancellationToken: cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadAsync(Request, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadRowsAsync(Rows, cancellation.Token));
        Assert.Equal(0, store.Prepares + store.Reads + store.RowReads);
    }

    [Fact]
    public async Task Shared_consumers_use_one_producer_and_canceling_one_preserves_the_other()
    {
        var started = Signal(); var release = Signal(); CancellationToken producer = default;
        var store = new Store(async (_, progress, token) =>
        { producer = token; started.SetResult(); await release.Task.WaitAsync(token); await progress("Decoding table records"); return Saved; });
        await using var service = new TableDataService(store); using var cancellation = new CancellationTokenSource();
        var firstProgress = new List<string>(); var secondProgress = new List<string>();
        var first = service.PrepareAsync(Request, stage => { firstProgress.Add(stage); return Task.CompletedTask; }, cancellation.Token);
        await started.Task.WaitAsync(Deadline);
        var second = service.PrepareAsync(Request with { }, stage => { secondProgress.Add(stage); return Task.CompletedTask; });
        var busy = await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareAsync(Request with { TableId = "b977271e" }));
        Assert.Equal("TABLE_DATA_BUSY", busy.Code); Assert.True(busy.Retryable);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Deadline));
        Assert.False(producer.IsCancellationRequested); Assert.False(second.IsCompleted);
        // Local readiness/row queries do not consume or replace producer ownership.
        await service.ReadAsync(Request); await service.ReadRowsAsync(Rows);
        release.SetResult(); Assert.Same(Saved, await second.WaitAsync(Deadline));
        Assert.Equal(1, store.Prepares); Assert.Empty(firstProgress); Assert.Equal(["Decoding table records"], secondProgress);
    }

    [Fact]
    public async Task Last_consumer_cancellation_waits_cleanup_and_refuses_reattachment_until_retired()
    {
        var started = Signal(); var cleanupStarted = Signal(); var cleanup = Signal(); var attempt = 0;
        var store = new Store(async (_, _, token) =>
        {
            if (Interlocked.Increment(ref attempt) > 1) return Saved;
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cleanupStarted.SetResult(); await cleanup.Task.WaitAsync(Deadline); }
            return Saved;
        });
        await using var service = new TableDataService(store); using var cancellation = new CancellationTokenSource();
        var loading = service.PrepareAsync(Request, cancellationToken: cancellation.Token);
        await started.Task.WaitAsync(Deadline); cancellation.Cancel(); await cleanupStarted.Task.WaitAsync(Deadline);
        Assert.False(loading.IsCompleted);
        Assert.Equal("TABLE_DATA_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareAsync(Request))).Code);
        Assert.Equal("TABLE_DATA_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareAsync(Request with { SourceRegion = "eu" }))).Code);
        cleanup.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading.WaitAsync(Deadline));
        Assert.Same(Saved, await service.PrepareAsync(Request).WaitAsync(Deadline)); Assert.Equal(2, store.Prepares);
    }

    [Fact]
    public async Task Canceled_shared_observer_drains_before_terminal_and_never_receives_later_progress()
    {
        var callbackStarted = Signal(); var releaseCallback = Signal(); var continueProducer = Signal(); var callbacks = 0;
        var store = new Store(async (_, progress, token) =>
        { await progress("Locating table"); await continueProducer.Task.WaitAsync(token); await progress("Decoding table"); return Saved; });
        await using var service = new TableDataService(store); using var cancellation = new CancellationTokenSource();
        var first = service.PrepareAsync(Request, async _ => { Interlocked.Increment(ref callbacks); callbackStarted.SetResult(); await releaseCallback.Task.WaitAsync(Deadline); }, cancellation.Token);
        await callbackStarted.Task.WaitAsync(Deadline); var second = service.PrepareAsync(Request);
        cancellation.Cancel(); Assert.False(first.IsCompleted); releaseCallback.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Deadline));
        continueProducer.SetResult(); await second.WaitAsync(Deadline); Assert.Equal(1, callbacks);
    }

    [Fact]
    public async Task Faulty_observer_isolated_from_shared_producer_and_other_observers()
    {
        var started = Signal(); var release = Signal(); var brokenCalls = 0; var received = new List<string>();
        var store = new Store(async (_, progress, token) =>
        { started.SetResult(); await release.Task.WaitAsync(token); await progress("Locating table"); await progress("Decoding table"); return Saved; });
        await using var service = new TableDataService(store);
        var first = service.PrepareAsync(Request, _ => { brokenCalls++; throw new IOException("Authored observer failure"); });
        await started.Task.WaitAsync(Deadline);
        var second = service.PrepareAsync(Request, stage => { received.Add(stage); return Task.CompletedTask; });
        release.SetResult(); await Task.WhenAll(first, second).WaitAsync(Deadline);
        Assert.Equal(1, store.Prepares); Assert.Equal(1, brokenCalls); Assert.Equal(["Locating table", "Decoding table"], received);
    }

    [Fact]
    public async Task Concurrent_reports_coalesce_and_terminal_drains_even_a_nonawaited_report()
    {
        var callbackStarted = Signal(); var release = Signal(); var producerReturned = Signal(); var calls = 0;
        Func<string, Task>? report = null; Task? outstanding = null;
        var store = new Store(async (_, progress, _) =>
        {
            report = progress; outstanding = progress("Decoding table"); await callbackStarted.Task.WaitAsync(Deadline);
            for (var i = 0; i < 1000; i++) await progress("Intermediate row progress");
            producerReturned.SetResult(); return Saved;
        });
        await using var service = new TableDataService(store);
        var loading = service.PrepareAsync(Request, async _ => { Interlocked.Increment(ref calls); callbackStarted.SetResult(); await release.Task.WaitAsync(Deadline); });
        await producerReturned.Task.WaitAsync(Deadline); Assert.False(loading.IsCompleted); Assert.Equal(1, calls);
        release.SetResult(); await loading.WaitAsync(Deadline); await outstanding!;
        await report!("Late progress"); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Consumer_capacity_is_bounded_and_detachment_releases_capacity()
    {
        var started = Signal(); var release = Signal();
        var store = new Store(async (_, _, token) => { started.SetResult(); await release.Task.WaitAsync(token); return Saved; });
        await using var service = new TableDataService(store); using var cancellation = new CancellationTokenSource();
        var first = service.PrepareAsync(Request, cancellationToken: cancellation.Token); await started.Task.WaitAsync(Deadline);
        var remaining = Enumerable.Range(0, 31).Select(_ => service.PrepareAsync(Request)).ToList();
        Assert.Equal("TABLE_DATA_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareAsync(Request))).Code);
        cancellation.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Deadline));
        remaining.Add(service.PrepareAsync(Request)); release.SetResult();
        await Task.WhenAll(remaining).WaitAsync(Deadline); Assert.Equal(1, store.Prepares);
    }

    [Fact]
    public async Task Publication_success_wins_last_consumer_cancellation_after_cleanup()
    {
        var started = Signal(); var canceled = Signal(); var committed = Signal();
        var store = new Store(async (_, _, token) =>
        { using var registration = token.Register(() => canceled.SetResult()); started.SetResult(); await committed.Task.WaitAsync(Deadline); return Saved; });
        await using var service = new TableDataService(store); using var cancellation = new CancellationTokenSource();
        var loading = service.PrepareAsync(Request, cancellationToken: cancellation.Token); await started.Task.WaitAsync(Deadline);
        cancellation.Cancel(); await canceled.Task.WaitAsync(Deadline); Assert.False(loading.IsCompleted);
        committed.SetResult(); Assert.Same(Saved, await loading.WaitAsync(Deadline));
    }

    [Fact]
    public async Task Shutdown_waits_producer_cleanup_and_rejects_new_work()
    {
        var started = Signal(); var cleanupStarted = Signal(); var cleanup = Signal();
        var store = new Store(async (_, _, token) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cleanupStarted.SetResult(); await cleanup.Task.WaitAsync(Deadline); }
            return Saved;
        });
        var service = new TableDataService(store);
        var first = service.PrepareAsync(Request); await started.Task.WaitAsync(Deadline); var second = service.PrepareAsync(Request);
        var stopping = service.DisposeAsync().AsTask(); await cleanupStarted.Task.WaitAsync(Deadline); Assert.False(stopping.IsCompleted);
        Assert.Equal("ENGINE_STOPPING", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareAsync(Request))).Code);
        Assert.Equal("ENGINE_STOPPING", (await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadAsync(Request))).Code);
        Assert.Equal("ENGINE_STOPPING", (await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadRowsAsync(Rows))).Code);
        cleanup.SetResult(); await stopping.WaitAsync(Deadline);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await service.DisposeAsync();
    }

    [Fact]
    public async Task Availability_when_idle_neither_prepares_nor_adds_a_consumer()
    {
        var store = new Store(); await using var service = new TableDataService(store);
        await service.WaitForAvailabilityAsync(); Assert.Equal(0, store.Prepares);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.WaitForAvailabilityAsync(cancellation.Token));
    }

    [Fact]
    public async Task Availability_observes_retirement_even_when_the_unrelated_table_failed()
    {
        var entered = Signal(); var release = Signal();
        var failure = new DiscoveryException("TABLE_SOURCE_MISSING", "Authored unavailable source.");
        var store = new Store(async (_, _, _) => { entered.SetResult(); await release.Task; throw failure; });
        await using var service = new TableDataService(store);
        var loading = service.PrepareAsync(Request); await entered.Task.WaitAsync(Deadline);
        var waiting = service.WaitForAvailabilityAsync(); Assert.False(waiting.IsCompleted);
        release.SetResult(); await waiting.WaitAsync(Deadline);
        Assert.Same(failure, await Assert.ThrowsAsync<DiscoveryException>(() => loading));
        await service.WaitForAvailabilityAsync();
    }

    [Fact]
    public async Task Canceling_an_availability_wait_leaves_the_active_table_consumer_running()
    {
        var entered = Signal(); var release = Signal(); CancellationToken producer = default;
        var store = new Store(async (_, _, token) =>
        { producer = token; entered.SetResult(); await release.Task.WaitAsync(token); return Saved; });
        await using var service = new TableDataService(store);
        var loading = service.PrepareAsync(Request); await entered.Task.WaitAsync(Deadline);
        using var cancellation = new CancellationTokenSource();
        var waiting = service.WaitForAvailabilityAsync(cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Deadline));
        Assert.False(producer.IsCancellationRequested); Assert.False(loading.IsCompleted);
        release.SetResult(); Assert.Same(Saved, await loading.WaitAsync(Deadline));
    }

    [Fact]
    public async Task Availability_at_consumer_capacity_uses_no_demand_slot()
    {
        var entered = Signal(); var release = Signal();
        var store = new Store(async (_, _, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return Saved; });
        await using var service = new TableDataService(store);
        var consumers = Enumerable.Range(0, 32).Select(_ => service.PrepareAsync(Request)).ToArray();
        await entered.Task.WaitAsync(Deadline);
        var waiting = service.WaitForAvailabilityAsync(); Assert.False(waiting.IsCompleted);
        release.SetResult(); await Task.WhenAll(consumers).WaitAsync(Deadline); await waiting.WaitAsync(Deadline);
        Assert.Equal(1, store.Prepares);
    }

    [Fact]
    public async Task Availability_waits_for_last_consumer_cleanup_and_reports_engine_shutdown()
    {
        var entered = Signal(); var cleanupStarted = Signal(); var cleanup = Signal(); var never = Signal();
        var store = new Store(async (_, _, token) =>
        {
            entered.SetResult();
            try { await never.Task.WaitAsync(token); }
            finally { cleanupStarted.SetResult(); await cleanup.Task; }
            return Saved;
        });
        var service = new TableDataService(store);
        var loading = service.PrepareAsync(Request); await entered.Task.WaitAsync(Deadline);
        var waiting = service.WaitForAvailabilityAsync();
        var shutdown = service.DisposeAsync().AsTask(); await cleanupStarted.Task.WaitAsync(Deadline);
        Assert.False(waiting.IsCompleted); Assert.False(shutdown.IsCompleted);
        cleanup.SetResult(); await shutdown.WaitAsync(Deadline);
        Assert.Equal("ENGINE_STOPPING", (await Assert.ThrowsAsync<DiscoveryException>(() => waiting.WaitAsync(Deadline))).Code);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        Assert.Equal("ENGINE_STOPPING", (await Assert.ThrowsAsync<DiscoveryException>(() => service.WaitForAvailabilityAsync())).Code);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData("bad\nstage")]
    public async Task Invalid_progress_fails_producer_instead_of_crossing_observer_boundary(string? stage)
    {
        var calls = 0;
        var store = new Store(async (_, progress, _) => { await progress(stage!); return Saved; });
        await using var service = new TableDataService(store);
        Assert.Equal("TABLE_DATA_PROGRESS_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            service.PrepareAsync(Request, _ => { calls++; return Task.CompletedTask; }))).Code);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Overlong_progress_fails_and_a_typed_store_failure_releases_next_request()
    {
        var store = new Store(async (_, progress, _) => { await progress(new string('x', 129)); return Saved; });
        await using var service = new TableDataService(store);
        Assert.Equal("TABLE_DATA_PROGRESS_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareAsync(Request))).Code);
        var failure = new DiscoveryException("TABLE_SOURCE_MISSING", "Authored unavailable source.", true);
        store.Prepare = (_, _, _) => Task.FromException<TableDataSnapshot>(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareAsync(Request)));
        store.Prepare = (_, _, _) => Task.FromResult(Saved);
        Assert.Same(Saved, await service.PrepareAsync(Request)); Assert.Equal(3, store.Prepares);
    }

    private static TableDataSnapshot Snapshot()
    {
        const string time = "2026-10-19T00:00:00Z";
        var metadata = new MetadataReceipt("encoding", new string('c', 32), new string('d', 32), new string('e', 64),
            "10", "10", "https://us.cdn.blizzard.com/tpr/wow/data/authored", time, time, "authored-v1");
        return new(Request.ProductId, Request.BuildIdentity, Request.SourceRegion, Request.TableId, Request.ResearchLocale,
            Rows.ArtifactId, time, "996192AA", new string('f', 64), new string('a', 40), "authored-db2-v1",
            new(new string('c', 32), new string('d', 32), new string('e', 64), "10", "10", metadata.SourceUrl,
                time, time, "authored-v1", new("loose", "0", "10", null)),
            new("841626", "enUS", "authored-v1", metadata, metadata with { Role = "root" }, []), [],
            new("complete", "0", "0", "0", "0", "0", "0", "0", "0"), "0");
    }
    private sealed class Store(Func<TableDataRequest, Func<string, Task>, CancellationToken, Task<TableDataSnapshot>>? prepare = null) : ITableDataStore
    {
        public Func<TableDataRequest, Func<string, Task>, CancellationToken, Task<TableDataSnapshot>> Prepare = prepare ?? ((_, _, _) => Task.FromResult(Saved));
        public int Prepares, Reads, RowReads;
        public TableDataRequest? LastRead;
        public TableRowsRequest? LastRows;
        public CancellationToken ReadToken, RowsToken;
        public Task<TableDataSnapshot> PrepareAsync(TableDataRequest request, Func<string, Task> progress, CancellationToken token)
        { Interlocked.Increment(ref Prepares); return Prepare(request, progress, token); }
        public Task<TableDataReadiness> ReadAsync(TableDataRequest request, CancellationToken token)
        { Reads++; LastRead = request; ReadToken = token; return Task.FromResult(new TableDataReadiness(null)); }
        public Task<TableDataPage> ReadRowsAsync(TableRowsRequest request, CancellationToken token)
        {
            RowReads++; LastRows = request; RowsToken = token;
            return Task.FromResult(new TableDataPage(request.ProductId, request.BuildIdentity, request.SourceRegion,
                request.TableId, request.ResearchLocale, request.ArtifactId, [], "0", null));
        }
    }
}
