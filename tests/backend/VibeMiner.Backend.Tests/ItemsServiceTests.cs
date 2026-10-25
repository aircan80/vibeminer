using VibeMiner.Application;
using VibeMiner.Domain;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class ItemsServiceTests
{
    private static readonly ItemsContext Context = new("wow", new string('a', 64), "us", "enUS");
    private static readonly ItemsSnapshot Saved = Snapshot();
    private static readonly ItemsPageRequest Page = new(Context, new string('b', 64));
    private static readonly ItemInspectRequest Inspect = new(Context, Page.ArtifactId, "4294967295");
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static IEnumerable<object[]> InvalidContexts()
    {
        yield return [null!];
        yield return [Context with { ProductId = null! }];
        yield return [Context with { ProductId = "../wow" }];
        yield return [Context with { BuildIdentity = null! }];
        yield return [Context with { BuildIdentity = new string('A', 64) }];
        yield return [Context with { BuildIdentity = new string('a', 63) }];
        yield return [Context with { SourceRegion = null! }];
        yield return [Context with { SourceRegion = "US" }];
        yield return [Context with { SourceRegion = "us\n" }];
        yield return [Context with { SourceRegion = "us/path" }];
        yield return [Context with { ResearchLocale = null! }];
        yield return [Context with { ResearchLocale = "enus" }];
        yield return [Context with { ResearchLocale = "deDE" }];
    }

    [Theory, MemberData(nameof(InvalidContexts))]
    public async Task Invalid_contexts_never_query_or_start_feature_work(ItemsContext context)
    {
        var store = new Store(); await using var service = new ItemsService(store);
        await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareAsync(context));
        await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadAsync(context));
        await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadPageAsync(Page with { Context = context }));
        await Assert.ThrowsAsync<DiscoveryException>(() => service.InspectAsync(Inspect with { Context = context }));
        Assert.Equal(0, store.TotalCalls);
    }

    public static IEnumerable<object[]> InvalidPages()
    {
        yield return [null!];
        yield return [Page with { ArtifactId = null! }];
        yield return [Page with { ArtifactId = new string('B', 64) }];
        yield return [Page with { Limit = 0 }];
        yield return [Page with { Limit = 51 }];
        yield return [Page with { Query = null! }];
        yield return [Page with { Query = "bad\0name" }];
        yield return [Page with { Query = new string((char)0xd800, 1) }];
        yield return [Page with { Query = new string((char)0xdc00, 1) }];
        yield return [Page with { Query = new string('a', 129) }];
        yield return [Page with { Query = string.Concat(Enumerable.Repeat("\U0001F600", 129)) }];
        yield return [Page with { Query = "42", ExactItemId = "42" }];
        foreach (var id in new[] { "", "01", "-1", "4294967296", " 1", "1.0", "1\n" })
            yield return [Page with { ExactItemId = id }];
        foreach (var cursor in new[] { "", "a", "AB", "e30=", "e30/", "e30\n", new string('a', 2049) })
            yield return [Page with { After = cursor }];
    }

    [Theory, MemberData(nameof(InvalidPages))]
    public async Task Invalid_queries_never_reach_the_projection(ItemsPageRequest request)
    {
        var store = new Store(); await using var service = new ItemsService(store);
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadPageAsync(request))).Code);
        Assert.Equal(0, store.TotalCalls);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("-1")] [InlineData("01")] [InlineData("4294967296")]
    public async Task Inspect_requires_an_exact_canonical_unsigned_id(string? id)
    {
        var store = new Store(); await using var service = new ItemsService(store);
        Assert.Equal("INVALID_REQUEST", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            service.InspectAsync(Inspect with { ItemId = id! }))).Code);
        Assert.Equal(0, store.TotalCalls);
    }

    [Fact]
    public async Task Literal_queries_unicode_scalar_boundary_and_exact_id_modes_are_preserved()
    {
        var store = new Store(); await using var service = new ItemsService(store);
        foreach (var query in new[] { "", "  untouched  ", "%_'\\ OR 1=1", "Épée", new string('a', 128),
            string.Concat(Enumerable.Repeat("\U0001F600", 128)) })
        {
            var request = Page with { Query = query, After = "eyJuZXh0Ijo0Mn0", Limit = 1 };
            await service.ReadPageAsync(request); Assert.Same(request, store.LastPage); Assert.Equal(query, store.LastPage!.Query);
        }
        foreach (var id in new[] { "0", "4294967295" })
        {
            await service.ReadPageAsync(Page with { ExactItemId = id }); Assert.Equal(id, store.LastPage!.ExactItemId);
            await service.InspectAsync(Inspect with { ItemId = id }); Assert.Equal(id, store.LastInspect!.ItemId);
        }
        Assert.Equal(0, store.Prepares);
    }

    [Fact]
    public async Task Canceled_requests_do_not_probe_prepare_search_or_inspect()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var store = new Store(); await using var service = new ItemsService(store);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadAsync(Context, cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PrepareAsync(Context, cancellationToken: cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadPageAsync(Page, cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.InspectAsync(Inspect, cancel.Token));
        Assert.Equal(0, store.TotalCalls);
    }

    [Fact]
    public async Task Same_context_consumers_share_one_preparation_while_queries_remain_independent()
    {
        var entered = Signal(); var release = Signal(); CancellationToken producer = default;
        var store = new Store { Prepare = async (_, progress, token) =>
        { producer = token; entered.SetResult(); await release.Task.WaitAsync(token); await progress(new("Joining Items", "rows", 0, 0)); return Saved; } };
        await using var service = new ItemsService(store); using var cancel = new CancellationTokenSource();
        var firstProgress = 0; var secondProgress = 0;
        var first = service.PrepareAsync(Context, _ => { firstProgress++; return Task.CompletedTask; }, cancel.Token);
        await entered.Task.WaitAsync(Deadline);
        var second = service.PrepareAsync(Context with { }, _ => { secondProgress++; return Task.CompletedTask; });
        var busy = await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareAsync(Context with { SourceRegion = "eu" }));
        Assert.Equal("ITEMS_BUSY", busy.Code); Assert.True(busy.Retryable);
        using var query = new CancellationTokenSource();
        await service.ReadAsync(Context, query.Token); await service.ReadPageAsync(Page, query.Token); await service.InspectAsync(Inspect, query.Token);
        Assert.Equal(query.Token, store.ReadToken); Assert.Equal(query.Token, store.PageToken); Assert.Equal(query.Token, store.InspectToken);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first.WaitAsync(Deadline));
        Assert.False(producer.IsCancellationRequested); Assert.False(second.IsCompleted);
        release.SetResult(); Assert.Same(Saved, await second.WaitAsync(Deadline));
        Assert.Equal(1, store.Prepares); Assert.Equal(0, firstProgress); Assert.Equal(1, secondProgress);
    }

    [Fact]
    public async Task Consumer_capacity_is_bounded_without_starting_another_feature_producer()
    {
        var entered = Signal(); var release = Signal();
        var store = new Store { Prepare = async (_, _, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return Saved; } };
        await using var service = new ItemsService(store);
        var consumers = Enumerable.Range(0, 32).Select(_ => service.PrepareAsync(Context)).ToArray();
        await entered.Task.WaitAsync(Deadline);
        Assert.Equal("ITEMS_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareAsync(Context))).Code);
        Assert.Equal(1, store.Prepares); release.SetResult(); await Task.WhenAll(consumers).WaitAsync(Deadline);
    }

    [Fact]
    public async Task Last_consumer_cancel_waits_cleanup_and_refuses_reattachment_until_retirement()
    {
        var entered = Signal(); var cleanupStarted = Signal(); var cleanup = Signal(); var never = Signal();
        var store = new Store { Prepare = async (_, _, token) =>
        {
            entered.SetResult();
            try { await never.Task.WaitAsync(token); }
            finally { cleanupStarted.SetResult(); await cleanup.Task; }
            return Saved;
        } };
        await using var service = new ItemsService(store); using var cancel = new CancellationTokenSource();
        var loading = service.PrepareAsync(Context, cancellationToken: cancel.Token);
        await entered.Task.WaitAsync(Deadline); cancel.Cancel(); await cleanupStarted.Task.WaitAsync(Deadline);
        Assert.False(loading.IsCompleted);
        Assert.Equal("ITEMS_BUSY", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareAsync(Context))).Code);
        cleanup.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading.WaitAsync(Deadline));
        store.Prepare = (_, _, _) => Task.FromResult(Saved);
        Assert.Same(Saved, await service.PrepareAsync(Context)); Assert.Equal(2, store.Prepares);
    }

    [Fact]
    public async Task A_committed_feature_can_win_last_consumer_cancellation()
    {
        var entered = Signal(); var canceled = Signal(); var commit = Signal();
        var store = new Store { Prepare = async (_, _, token) =>
        {
            using var callback = token.Register(() => canceled.TrySetResult());
            entered.SetResult(); await commit.Task; return Saved;
        } };
        await using var service = new ItemsService(store); using var cancel = new CancellationTokenSource();
        var loading = service.PrepareAsync(Context, cancellationToken: cancel.Token);
        await entered.Task.WaitAsync(Deadline); cancel.Cancel(); await canceled.Task.WaitAsync(Deadline);
        Assert.False(loading.IsCompleted); commit.SetResult(); Assert.Same(Saved, await loading.WaitAsync(Deadline));
    }

    public static IEnumerable<object[]> InvalidProgress()
    {
        yield return [null!];
        yield return [new ItemsPreparationProgress("", "rows")];
        yield return [new ItemsPreparationProgress("bad\nstage", "rows")];
        yield return [new ItemsPreparationProgress(new string('x', 129), "rows")];
        yield return [new ItemsPreparationProgress("Joining", null!)];
        yield return [new ItemsPreparationProgress("Joining", "rows", -1)];
        yield return [new ItemsPreparationProgress("Joining", "rows", 0, -1)];
        yield return [new ItemsPreparationProgress("Joining", "rows", 2, 1)];
        yield return [new ItemsPreparationProgress("Joining", "rows", TableId: "Item")];
    }

    [Theory, MemberData(nameof(InvalidProgress))]
    public async Task Invalid_progress_fails_before_observers_and_releases_preparation(ItemsPreparationProgress progress)
    {
        var calls = 0; var store = new Store { Prepare = async (_, report, _) => { await report(progress); return Saved; } };
        await using var service = new ItemsService(store);
        Assert.Equal("ITEMS_PROGRESS_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() =>
            service.PrepareAsync(Context, _ => { calls++; return Task.CompletedTask; }))).Code);
        Assert.Equal(0, calls); store.Prepare = (_, _, _) => Task.FromResult(Saved);
        Assert.Same(Saved, await service.PrepareAsync(Context));
    }

    [Fact]
    public async Task Concurrent_progress_coalesces_and_late_callbacks_do_not_repaint_terminal_results()
    {
        var callbackEntered = Signal(); var coalesced = Signal(); var release = Signal(); var calls = 0;
        Func<ItemsPreparationProgress, Task>? report = null;
        var store = new Store { Prepare = async (_, progress, _) =>
        {
            report = progress;
            var reporting = progress(new("Joining", "rows", 0, 2));
            await callbackEntered.Task;
            await progress(new("Joining", "rows", 1, 2));
            coalesced.SetResult();
            await reporting; return Saved;
        } };
        await using var service = new ItemsService(store);
        var loading = service.PrepareAsync(Context, async _ => { calls++; callbackEntered.SetResult(); await release.Task; });
        await callbackEntered.Task.WaitAsync(Deadline); Assert.False(loading.IsCompleted);
        await coalesced.Task.WaitAsync(Deadline);
        release.SetResult(); Assert.Same(Saved, await loading.WaitAsync(Deadline));
        await report!(new("Joining", "rows", 2, 2)); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Typed_failure_releases_feature_and_shutdown_waits_actual_cleanup()
    {
        var failure = new DiscoveryException("ITEMS_PREREQUISITE_BUSY", "Authored bounded contention.", true);
        var store = new Store { Prepare = (_, _, _) => Task.FromException<ItemsSnapshot>(failure) };
        var service = new ItemsService(store);
        Assert.Same(failure, await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareAsync(Context)));
        var entered = Signal(); var cleanupStarted = Signal(); var cleanup = Signal(); var never = Signal();
        store.Prepare = async (_, _, token) =>
        {
            entered.SetResult();
            try { await never.Task.WaitAsync(token); }
            finally { cleanupStarted.SetResult(); await cleanup.Task; }
            return Saved;
        };
        var loading = service.PrepareAsync(Context); await entered.Task.WaitAsync(Deadline);
        var shutdown = service.DisposeAsync().AsTask(); await cleanupStarted.Task.WaitAsync(Deadline);
        Assert.False(shutdown.IsCompleted); Assert.False(loading.IsCompleted);
        Assert.Equal("ENGINE_STOPPING", (await Assert.ThrowsAsync<DiscoveryException>(() => service.PrepareAsync(Context))).Code);
        Assert.Equal("ENGINE_STOPPING", (await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadAsync(Context))).Code);
        Assert.Equal("ENGINE_STOPPING", (await Assert.ThrowsAsync<DiscoveryException>(() => service.ReadPageAsync(Page))).Code);
        Assert.Equal("ENGINE_STOPPING", (await Assert.ThrowsAsync<DiscoveryException>(() => service.InspectAsync(Inspect))).Code);
        cleanup.SetResult(); await shutdown.WaitAsync(Deadline);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading); await service.DisposeAsync();
    }

    private static ItemsSnapshot Snapshot()
    {
        const string time = "2026-10-19T00:00:00Z";
        var metadata = new MetadataReceipt("encoding", new string('c', 32), new string('d', 32), new string('e', 64),
            "10", "10", "https://us.cdn.blizzard.com/tpr/wow/data/authored", time, time, "authored-v1");
        var item = new TableDataSnapshot(Context.ProductId, Context.BuildIdentity, Context.SourceRegion, "50238ec2", Context.ResearchLocale,
            new string('c', 64), time, "996192AA", new string('f', 64), new string('a', 40), "authored-db2-v1",
            new(new string('c', 32), new string('d', 32), new string('e', 64), "10", "10", metadata.SourceUrl,
                time, time, "authored-v1", new("loose", "0", "10", null)),
            new("841626", "enUS", "authored-v1", metadata, metadata with { Role = "root" }, []), [],
            new("complete", "0", "0", "0", "0", "0", "0", "0", "0"), "0");
        return new(Context, new string('b', 64), time, "items-v1", "ordinal-ignorecase-contains-v1", item,
            item with { TableId = "919be54e", ArtifactId = new string('d', 64) }, new("0", "0", "0", "0", "0", "0", "0", "0"), []);
    }

    private sealed class Store : IItemsStore
    {
        public Func<ItemsContext, Func<ItemsPreparationProgress, Task>, CancellationToken, Task<ItemsSnapshot>> Prepare =
            (_, _, _) => Task.FromResult(Saved);
        public int Prepares, Reads, Pages, Inspects;
        public int TotalCalls => Prepares + Reads + Pages + Inspects;
        public ItemsPageRequest? LastPage;
        public ItemInspectRequest? LastInspect;
        public CancellationToken ReadToken, PageToken, InspectToken;
        public Task<ItemsSnapshot> PrepareAsync(ItemsContext context, Func<ItemsPreparationProgress, Task> progress, CancellationToken token)
        { Interlocked.Increment(ref Prepares); return Prepare(context, progress, token); }
        public Task<ItemsReadiness> ReadAsync(ItemsContext context, CancellationToken token)
        { Reads++; ReadToken = token; return Task.FromResult(new ItemsReadiness(null, [])); }
        public Task<ItemsPage> ReadPageAsync(ItemsPageRequest request, CancellationToken token)
        {
            Pages++; LastPage = request; PageToken = token;
            return Task.FromResult(new ItemsPage(request.Context, request.ArtifactId, request.Query, request.ExactItemId, [], null));
        }
        public Task<bool> SparseRowExistsAsync(ItemInspectRequest request, CancellationToken token) => Task.FromResult(false);
        public Task<ItemInspectResult> InspectAsync(ItemInspectRequest request, CancellationToken token)
        {
            Inspects++; LastInspect = request; InspectToken = token;
            return Task.FromResult(new ItemInspectResult(request.Context, request.ArtifactId, request.ItemId, null));
        }
    }
}
