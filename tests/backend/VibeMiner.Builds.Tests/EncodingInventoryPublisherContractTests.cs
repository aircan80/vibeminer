using VibeMiner.Builds.Testing;
using Xunit;
using static VibeMiner.Builds.Tests.EncodingInventoryTestKit;

namespace VibeMiner.Builds.Tests;

/// <summary>Publisher policy is shared by production and the managed upper-layer fake; storage semantics have an independent contract.</summary>
public abstract class EncodingInventoryPublisherContractTests
{
    protected abstract IEncodingInventoryPublisher Publisher(InMemoryEncodingInventoryStore store, EncodingInventoryPublisherOptions? options = null);

    [Fact]
    public async Task MultiBatchPublicationUsesFreshGenerationsAndKeepsExactSourceContextWithoutLedgerOrAcquisition()
    {
        var store = new InMemoryEncodingInventoryStore(); await using var publisher = Publisher(store);
        var identity = Identity(); var source = Source(identity); var build = new EncodingSourceBuild(source);
        var publication = publisher.Start(identity, build); var result = await publication.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(EncodingInventoryState.Published, result.State); Assert.Equal(3507, result.Processed); Assert.NotNull(result.Generation);
        Assert.Equal(identity.Id, result.Context.Identity.Id); Assert.Equal(build.Build, result.Context.Build); Assert.Equal(build.Metadata.EncodingCKey, result.Context.EncodingCKey);
        Assert.Equal(new EncodingInventoryProgress(EncodingInventoryState.Published, 3507, 3507), publication.Progress);
        Assert.Equal([1024, 1024, 1024, 435], store.AppendSizes); Assert.Equal(Rows(3507).Select(row => row.EKey), store.ReadActive(identity.Id)!.Keys);
        var again = await publisher.Start(identity, build).Completion; Assert.Equal(EncodingInventoryState.Published, again.State); Assert.NotEqual(result.Generation, again.Generation);
        Assert.Equal(2, store.BeginCalls); Assert.Equal(0, source.ActiveLeases); Assert.Equal(source.OpenedLeases, source.DisposedLeases); Assert.Equal(1024, source.MaxRequestedPage);
    }
    [Fact]
    public async Task SupportedEmptyPublishesZerosAndUnsupportedOrMismatchedSourcesNeverStartAnInventory()
    {
        var store = new InMemoryEncodingInventoryStore(); await using var publisher = Publisher(store); var identity = Identity();
        var source = Source(identity, 0); var build = new EncodingSourceBuild(source);
        var empty = await publisher.Start(identity, build).Completion; Assert.Equal(EncodingInventoryState.Published, empty.State); Assert.Equal(0, empty.Processed); Assert.Empty(store.ReadActive(identity.Id)!.Keys);
        Assert.Equal(EncodingInventoryCodes.Unsupported, Assert.Throws<EncodingInventoryException>(() => publisher.Start(identity, new WithoutRows(build))).Code);
        Assert.Equal(EncodingInventoryCodes.Validation, Assert.Throws<EncodingInventoryException>(() => publisher.Start(identity with { Id = new string('0', 64) }, build)).Code);
        Assert.Equal(EncodingInventoryCodes.Validation, Assert.Throws<EncodingInventoryException>(() => publisher.Start(Identity(2), build)).Code);
        build.Metadata = build.Metadata with { EncodingCKey = Key(123456) };
        Assert.Equal(EncodingInventoryCodes.Validation, Assert.Throws<EncodingInventoryException>(() => publisher.Start(identity, build)).Code);
        foreach (var count in new[] { -1L, 50_000_001L })
        {
            var invalidCount = Source(identity, 0); invalidCount.ReportedCount = count;
            Assert.Equal(EncodingInventoryCodes.Validation, Assert.Throws<EncodingInventoryException>(() => publisher.Start(identity, new EncodingSourceBuild(invalidCount))).Code);
        }
        Assert.Equal(1, store.BeginCalls);
    }
    [Theory]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("duplicate")]
    [InlineData("unordered")]
    [InlineData("empty-more")]
    [InlineData("oversize")]
    [InlineData("negative-size")]
    [InlineData("zero-key")]
    public async Task InvalidEnumerationNeverPublishesAPartialReplacement(string fault)
    {
        var store = new InMemoryEncodingInventoryStore(); await using var publisher = Publisher(store); var identity = Identity();
        var previous = await publisher.Start(identity, new EncodingSourceBuild(Source(identity, 2))).Completion;
        var source = Source(identity, 3); source.ReadOverride = (_, _, _) => ValueTask.FromResult(fault switch
        {
            "short" => new EncodingRowPage(Rows(2), false), "long" => new EncodingRowPage(Rows(4), false),
            "duplicate" => new EncodingRowPage([new(Key(1), 10), new(Key(1), 10), new(Key(3), 10)], false),
            "unordered" => new EncodingRowPage([new(Key(2), 10), new(Key(1), 10), new(Key(3), 10)], false),
            "empty-more" => new EncodingRowPage([], true), "oversize" => new EncodingRowPage(Rows(1025), false),
            "zero-key" => new EncodingRowPage([new(default, 10)], false),
            _ => new EncodingRowPage([new(Key(1), -1)], false),
        });
        var result = await publisher.Start(identity, new EncodingSourceBuild(source)).Completion;
        Assert.Equal(EncodingInventoryState.Failed, result.State); Assert.Equal(EncodingInventoryCodes.Corrupt, result.Code); Assert.Null(result.Generation);
        Assert.Equal(previous.Generation, store.ReadActive(identity.Id)!.Generation); Assert.Equal(Rows(2).Select(row => row.EKey), store.ReadActive(identity.Id)!.Keys);
        Assert.Equal(0, source.ActiveLeases);
    }
    [Fact]
    public async Task AppendFailureAndUnavailableSourcePreservePriorActiveGenerationAndDrainTheirLeases()
    {
        var store = new InMemoryEncodingInventoryStore(); await using var publisher = Publisher(store); var identity = Identity();
        var previous = await publisher.Start(identity, new EncodingSourceBuild(Source(identity, 1))).Completion;
        var broken = Source(identity, 2000); var appends = 0;
        store.BeforeAppend = (_, _, _) => ++appends == 2 ? throw new EncodingInventoryException(EncodingInventoryCodes.Storage, "authored append failure") : Task.CompletedTask;
        var failed = await publisher.Start(identity, new EncodingSourceBuild(broken)).Completion;
        Assert.Equal(EncodingInventoryState.Failed, failed.State); Assert.Equal(EncodingInventoryCodes.Storage, failed.Code); Assert.Equal(0, broken.ActiveLeases);
        store.BeforeAppend = null; var unavailable = Source(identity, 1);
        unavailable.BeforeRead = (_, _, _) => throw new EncodingInventoryException(EncodingInventoryCodes.Unavailable, "authored source unavailable");
        var missing = await publisher.Start(identity, new EncodingSourceBuild(unavailable)).Completion;
        Assert.Equal(EncodingInventoryState.Failed, missing.State); Assert.Equal(EncodingInventoryCodes.Unavailable, missing.Code);
        Assert.Equal(previous.Generation, store.ReadActive(identity.Id)!.Generation); Assert.Equal(0, unavailable.ActiveLeases);
    }
    [Fact]
    public async Task SupersessionWaitsForCanceledProducerDrainAndOnlyTheNewRequestCanPublish()
    {
        var store = new InMemoryEncodingInventoryStore(); await using var publisher = Publisher(store); var identity = Identity();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = Source(identity, 2000); old.BeforeRead = async (_, _, ct) =>
        {
            entered.TrySetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } finally { drained.TrySetResult(); }
        };
        var first = publisher.Start(identity, new EncodingSourceBuild(old)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var latest = publisher.Start(identity, new EncodingSourceBuild(Source(identity, 3, 4000)));
        var superseded = await first.Completion.WaitAsync(TimeSpan.FromSeconds(10)); Assert.True(drained.Task.IsCompleted);
        Assert.Equal(EncodingInventoryState.Superseded, superseded.State); Assert.Equal(0, old.ActiveLeases);
        var published = await latest.Completion; Assert.Equal(EncodingInventoryState.Published, published.State); Assert.Equal(Rows(3, 4000).Select(row => row.EKey), store.ReadActive(identity.Id)!.Keys);
    }
    [Fact]
    public async Task ThrowingSourceCancellationCallbackCannotOrphanAnAlreadyAdmittedReplacement()
    {
        var store = new InMemoryEncodingInventoryStore(); await using var publisher = Publisher(store); var identity = Identity();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var old = Source(identity, 2);
        old.BeforeRead = async (_, _, ct) =>
        {
            using var callback = ct.Register(() => throw new InvalidOperationException("authored source cancellation callback"));
            entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        };
        var previous = publisher.Start(identity, new EncodingSourceBuild(old)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var replacement = publisher.Start(identity, new EncodingSourceBuild(Source(identity, 3, 50)));
        var retired = await previous.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(EncodingInventoryState.Superseded, retired.State); Assert.Equal(EncodingInventoryCodes.Storage, retired.Code);
        Assert.Contains("cancellation observer failed", retired.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(EncodingInventoryState.Published, (await replacement.Completion.WaitAsync(TimeSpan.FromSeconds(10))).State);
        Assert.Equal(0, old.ActiveLeases); Assert.Equal(Rows(3, 50).Select(row => row.EKey), store.ReadActive(identity.Id)!.Keys);
    }

    [Fact]
    public async Task SupersessionAfterActualCommitReportsTheOldRealPublicationAndThenTheNewGeneration()
    {
        var store = new InMemoryEncodingInventoryStore(); await using var publisher = Publisher(store); var identity = Identity();
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var once = 0;
        using var unblock = new ReleaseSignal(release);
        store.AfterPublish = async (_, _) => { if (Interlocked.Increment(ref once) != 1) return; committed.TrySetResult(); await release.Task; };
        var first = publisher.Start(identity, new EncodingSourceBuild(Source(identity, 1))); await committed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var latest = publisher.Start(identity, new EncodingSourceBuild(Source(identity, 2, 50)));
        release.TrySetResult(); var oldResult = await first.Completion; var newResult = await latest.Completion;
        Assert.Equal(EncodingInventoryState.Published, oldResult.State); Assert.Equal(EncodingInventoryState.Published, newResult.State); Assert.NotEqual(oldResult.Generation, newResult.Generation);
        Assert.Equal(newResult.Generation, store.ReadActive(identity.Id)!.Generation); Assert.Equal(Rows(2, 50).Select(row => row.EKey), store.ReadActive(identity.Id)!.Keys);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationBeforePublicationOrAfterRealCommitHasAnHonestTerminalOutcome(bool afterCommit)
    {
        var store = new InMemoryEncodingInventoryStore(); using var canceled = new CancellationTokenSource();
        var options = new EncodingInventoryPublisherOptions { Fault = point => { if (point == (afterCommit ? EncodingInventoryPoint.AfterPublish : EncodingInventoryPoint.AfterAppend)) canceled.Cancel(); } };
        await using var publisher = Publisher(store, options); var identity = Identity();
        var source = Source(identity, 2000); var result = await publisher.Start(identity, new EncodingSourceBuild(source), canceled.Token).Completion;
        Assert.Equal(afterCommit ? EncodingInventoryState.Published : EncodingInventoryState.Canceled, result.State); Assert.Equal(0, source.ActiveLeases);
        Assert.Equal(afterCommit, store.ReadActive(identity.Id) is not null);
    }
    [Fact]
    public async Task AdmissionIsBoundedAndClosingCancelsAndDrainsAcceptedSourcesWithoutClosingTheirStore()
    {
        var store = new InMemoryEncodingInventoryStore(); await using var publisher = Publisher(store, new() { MaxConcurrent = 1, MaxAdmitted = 2 });
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var draining = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var unblock = new ReleaseSignal(release);
        var firstSource = Source(Identity(), 2);
        firstSource.BeforeRead = async (_, _, ct) =>
        {
            entered.TrySetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); } finally { draining.TrySetResult(); await release.Task; }
        };
        var first = publisher.Start(Identity(), new EncodingSourceBuild(firstSource)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var queued = publisher.Start(Identity(2), new EncodingSourceBuild(Source(Identity(2), 2)));
        Assert.Equal(EncodingInventoryCodes.Busy, Assert.Throws<EncodingInventoryException>(() => publisher.Start(Identity(3), new EncodingSourceBuild(Source(Identity(3), 2)))).Code);
        var closing = publisher.DisposeAsync().AsTask(); await draining.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(closing.IsCompleted);
        Assert.Equal(EncodingInventoryCodes.Closed, Assert.Throws<EncodingInventoryException>(() => publisher.Start(Identity(3), new EncodingSourceBuild(Source(Identity(3), 2)))).Code);
        release.TrySetResult(); await closing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(EncodingInventoryState.Canceled, (await first.Completion).State); Assert.Equal(EncodingInventoryState.Canceled, (await queued.Completion).State); Assert.Equal(0, firstSource.ActiveLeases);
        await using var independent = new EncodingInventoryPublisher(store); Assert.Equal(EncodingInventoryState.Published, (await independent.Start(Identity(3), new EncodingSourceBuild(Source(Identity(3), 2))).Completion).State);
    }
    [Fact]
    public async Task ProgressReportsCommittedAppendCountsAndRequestIdentityIsDefensivelyFrozen()
    {
        var store = new InMemoryEncodingInventoryStore(); await using var publisher = Publisher(store); var original = Identity();
        var fields = original.Fields.ToDictionary(pair => pair.Key, pair => pair.Value); var supplied = original with { Fields = fields };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var unblock = new ReleaseSignal(release); var source = Source(original, 2000);
        source.BeforeRead = async (after, _, ct) => { if (after is null) return; entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        var publication = publisher.Start(supplied, new EncodingSourceBuild(source));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        fields["BuildConfig"] = new string('f', 32); fields["VersionsName"] = "caller mutation";
        Assert.False(publication.Completion.IsCompleted); Assert.Equal(new EncodingInventoryProgress(EncodingInventoryState.Running, 1024, 2000), publication.Progress);
        Assert.Equal(original.Fields, publication.Context.Identity.Fields); release.TrySetResult();
        var result = await publication.Completion; Assert.Equal(EncodingInventoryState.Published, result.State);
        Assert.Equal(original.Fields, result.Context.Identity.Fields); Assert.Equal(2000, publication.Progress.Processed);
    }

    [Fact]
    public async Task PreCanceledStartDoesNotOpenOrRetainASource()
    {
        var store = new InMemoryEncodingInventoryStore(); await using var publisher = Publisher(store); var identity = Identity(); var source = Source(identity, 1);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => publisher.Start(identity, new EncodingSourceBuild(source), canceled.Token)); Assert.Equal(0, source.OpenedLeases); Assert.Equal(0, store.BeginCalls);
    }
    [Fact]
    public async Task ExplicitReuseHitNeedsNoLeaseOrReadAndKeepsRequestedRouteLocaleAndSourceProvenance()
    {
        var store = new InMemoryEncodingInventoryStore(); await using var publisher = Publisher(store); var identity = Identity();
        var firstSource = Source(identity, 3507); var completed = await publisher.StartReuse(identity, new EncodingSourceBuild(firstSource)).Completion;
        Assert.Equal(EncodingInventoryState.Published, completed.State); Assert.NotNull(completed.SourceDescriptor);
        var reopenedSource = new InMemoryEncodingRows(Route(identity, "eu", 'd'), firstSource.EncodingCKey, Rows(3507));
        reopenedSource.BeforeRead = (_, _, _) => throw new InvalidOperationException("A reuse hit must not enumerate ENCODING.");
        var routed = new EncodingSourceBuild(reopenedSource); routed.Metadata = routed.Metadata with { Locales = ["koKR", "deDE"] };
        var publication = publisher.StartReuse(identity, routed); var reused = await publication.Completion;
        Assert.Equal(EncodingInventoryState.Reused, reused.State); Assert.Equal(completed.Generation, reused.Generation);
        Assert.Equal(3507, reused.Processed); Assert.Equal("eu", reused.Context.Build.Region); Assert.Equal(routed.Build, reused.Context.Build);
        Assert.Equal(completed.SourceDescriptor, reused.SourceDescriptor); Assert.Equal(firstSource.EncodingCKey, reused.Context.EncodingCKey);
        Assert.Equal(new EncodingInventoryProgress(EncodingInventoryState.Reused, 3507, 3507), publication.Progress);
        Assert.Equal(0, reopenedSource.OpenedLeases); Assert.Equal(0, reopenedSource.ReadCalls); Assert.Equal(1, store.BeginCalls);
    }
    [Fact]
    public async Task FreshRequestsStayFreshAndTheirUnversionedPublicationInvalidatesOldSourceProof()
    {
        var store = new InMemoryEncodingInventoryStore(); await using var publisher = Publisher(store); var identity = Identity(); var source = Source(identity, 2);
        var first = await publisher.StartReuse(identity, new EncodingSourceBuild(source)).Completion;
        Assert.Equal(EncodingInventoryState.Published, first.State); Assert.NotNull(store.ReadActive(identity.Id)!.Descriptor);
        var fresh = await publisher.Start(identity, new EncodingSourceBuild(source)).Completion;
        Assert.Equal(EncodingInventoryState.Published, fresh.State); Assert.Null(fresh.SourceDescriptor); Assert.Null(store.ReadActive(identity.Id)!.Descriptor);
        Assert.NotEqual(first.Generation, fresh.Generation);
        var reuseMiss = await publisher.StartReuse(identity, new EncodingSourceBuild(source)).Completion;
        Assert.Equal(EncodingInventoryState.Published, reuseMiss.State); Assert.NotEqual(fresh.Generation, reuseMiss.Generation);
        Assert.Equal(3, store.BeginCalls);
    }
    [Theory]
    [InlineData("format")]
    [InlineData("revision")]
    [InlineData("ckey")]
    [InlineData("count")]
    [InlineData("missing")]
    public async Task EachMissingOrChangedSourceDescriptorTakesTheCompleteFreshPath(string mismatch)
    {
        var store = new InMemoryEncodingInventoryStore(); await using var publisher = Publisher(store); var identity = Identity();
        var context = Context(count: 2); await using var seeded = await store.BeginAsync(context); await seeded.AppendAsync([Key(1), Key(2)]);
        if (mismatch == "missing") await seeded.PublishAsync();
        else await seeded.PublishSourceAsync(new(mismatch == "format" ? "older-format" : EncodingInventoryPublisher.SourceFormat,
            mismatch == "revision" ? "older-revision" : EncodingInventoryPublisher.SourceRevision, context.EncodingCKey, 2));
        var source = mismatch == "ckey" ? new InMemoryEncodingRows(Route(identity), Key(999_999), Rows(2)) : Source(identity, mismatch == "count" ? 3 : 2);
        var old = store.ReadActive(identity.Id)!.Generation; var result = await publisher.StartReuse(identity, new EncodingSourceBuild(source)).Completion;
        Assert.Equal(EncodingInventoryState.Published, result.State); Assert.NotEqual(old, result.Generation); Assert.Equal(1, source.OpenedLeases);
        Assert.Equal(source.EncodedCount, result.Processed); Assert.Equal(source.EncodingCKey, result.SourceDescriptor!.EncodingCKey);
    }
    [Fact]
    public async Task FailedRefreshKeepsPriorSourceProofAndTheNextMatchingRequestCanReuseIt()
    {
        var store = new InMemoryEncodingInventoryStore(); await using var publisher = Publisher(store); var identity = Identity(); var source = Source(identity, 2);
        var previous = await publisher.StartReuse(identity, new EncodingSourceBuild(source)).Completion;
        var changed = new InMemoryEncodingRows(Route(identity), Key(999_999), Rows(2000));
        changed.BeforeRead = (after, _, _) => after is null ? ValueTask.CompletedTask : throw new EncodingInventoryException(EncodingInventoryCodes.Corrupt, "authored refresh corruption");
        var failed = await publisher.StartReuse(identity, new EncodingSourceBuild(changed)).Completion; Assert.Equal(EncodingInventoryState.Failed, failed.State);
        Assert.Equal(previous.Generation, store.ReadActive(identity.Id)!.Generation); Assert.Equal(previous.SourceDescriptor, store.ReadActive(identity.Id)!.Descriptor);
        var noRead = Source(identity, 2); noRead.BeforeRead = (_, _, _) => throw new InvalidOperationException("Expected reuse.");
        Assert.Equal(EncodingInventoryState.Reused, (await publisher.StartReuse(identity, new EncodingSourceBuild(noRead)).Completion).State); Assert.Equal(0, noRead.OpenedLeases);
    }
    [Fact]
    public async Task SupersessionAfterMatchButBeforeAcceptanceCannotCompleteAnOlderHitAsCurrentWork()
    {
        var store = new InMemoryEncodingInventoryStore(); await using var publisher = Publisher(store); var identity = Identity();
        await publisher.StartReuse(identity, new EncodingSourceBuild(Source(identity, 2))).Completion;
        var matched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var unblock = new ReleaseSignal(release); store.AfterReuse = async (_, generation) => { if (generation is not null) { matched.TrySetResult(); await release.Task; } };
        var oldSource = Source(identity, 2); var older = publisher.StartReuse(identity, new EncodingSourceBuild(oldSource));
        await matched.Task.WaitAsync(TimeSpan.FromSeconds(10)); var newer = publisher.Start(identity, new EncodingSourceBuild(Source(identity, 1, 5000)));
        release.TrySetResult(); var old = await older.Completion.WaitAsync(TimeSpan.FromSeconds(10)); var current = await newer.Completion;
        Assert.Equal(EncodingInventoryState.Superseded, old.State); Assert.Null(old.Generation); Assert.Equal(0, oldSource.OpenedLeases);
        Assert.Equal(EncodingInventoryState.Published, current.State); Assert.Equal(current.Generation, store.ReadActive(identity.Id)!.Generation); Assert.Null(store.ReadActive(identity.Id)!.Descriptor);
    }
    [Fact]
    public async Task CancellationAndCloseWhileMatchingDrainAcceptedReuseWithoutOpeningASource()
    {
        var store = new InMemoryEncodingInventoryStore(); var publisher = Publisher(store); var identity = Identity();
        await publisher.StartReuse(identity, new EncodingSourceBuild(Source(identity, 2))).Completion;
        var matched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.AfterReuse = async (_, _) => { matched.TrySetResult(); await release.Task; };
        var source = Source(identity, 2); var reuse = publisher.StartReuse(identity, new EncodingSourceBuild(source));
        try
        {
            await matched.Task.WaitAsync(TimeSpan.FromSeconds(10)); var closing = publisher.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted);
            Assert.Equal(EncodingInventoryCodes.Closed, Assert.Throws<EncodingInventoryException>(() => publisher.StartReuse(identity, new EncodingSourceBuild(source))).Code);
            release.TrySetResult(); await closing.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(EncodingInventoryState.Canceled, (await reuse.Completion).State); Assert.Equal(0, source.OpenedLeases);
        }
        finally { release.TrySetResult(); await publisher.DisposeAsync(); }
    }

    [Fact]
    public async Task EmptyCompletedInventoryCanBeReusedAndPreCanceledReuseDoesNoLookupOrLease()
    {
        var store = new InMemoryEncodingInventoryStore(); await using var publisher = Publisher(store); var identity = Identity(); var empty = Source(identity, 0);
        var first = await publisher.StartReuse(identity, new EncodingSourceBuild(empty)).Completion; Assert.Equal(EncodingInventoryState.Published, first.State);
        var noRead = Source(identity, 0); var reused = await publisher.StartReuse(identity, new EncodingSourceBuild(noRead)).Completion;
        Assert.Equal(EncodingInventoryState.Reused, reused.State); Assert.Equal(first.Generation, reused.Generation); Assert.Equal(0, reused.Processed); Assert.Equal(0, noRead.OpenedLeases);
        using var canceled = new CancellationTokenSource(); canceled.Cancel(); var lookups = store.ReuseLookups;
        Assert.ThrowsAny<OperationCanceledException>(() => publisher.StartReuse(identity, new EncodingSourceBuild(noRead), canceled.Token));
        Assert.Equal(lookups, store.ReuseLookups); Assert.Equal(0, noRead.OpenedLeases);
    }

    [Fact]
    public void NewSourceDescriptorTextRequiresExactUtf8WithoutLoneSurrogates()
    {
        foreach (var invalid in new[] { "\ud800", "\udc00" })
        {
            Assert.Equal(EncodingInventoryCodes.Validation, Assert.Throws<EncodingInventoryException>(() => new EncodingInventorySourceDescriptor(invalid, "revision", Key(1), 1)).Code);
            Assert.Equal(EncodingInventoryCodes.Validation, Assert.Throws<EncodingInventoryException>(() => new EncodingInventorySourceDescriptor("format", invalid, Key(1), 1)).Code);
        }
    }

    [Fact]
    public async Task NewestReuseWaitsForAnOlderEnteredPublicationAndThenRestoresItsOwnSource()
    {
        var store = new InMemoryEncodingInventoryStore(); await using var publisher = Publisher(store, new() { MaxConcurrent = 2, MaxAdmitted = 16 });
        var identity = Identity(); var a = Source(identity, 2);
        var seed = await publisher.StartReuse(identity, new EncodingSourceBuild(a)).Completion; Assert.Equal(EncodingInventoryState.Published, seed.State);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var unblock = new ReleaseSignal(release); var bKey = Key(999_999);
        store.InsidePublication = async context =>
        {
            if (context.EncodingCKey != bKey) return;
            entered.TrySetResult(); await release.Task;
        };
        var b = new InMemoryEncodingRows(Route(identity), bKey, Rows(3, 100));
        var older = publisher.StartReuse(identity, new EncodingSourceBuild(b)); await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var newestSource = Source(identity, 2); var newest = publisher.StartReuse(identity, new EncodingSourceBuild(newestSource));
        var unrelatedIdentity = Identity(2); var unrelated = publisher.StartReuse(unrelatedIdentity, new EncodingSourceBuild(Source(unrelatedIdentity, 1, 5000)));
        Assert.Equal(EncodingInventoryState.Published, (await unrelated.Completion.WaitAsync(TimeSpan.FromSeconds(10))).State);
        Assert.False(newest.Completion.IsCompleted);
        Assert.Equal(seed.Generation, store.ReadActive(identity.Id)!.Generation);
        release.TrySetResult(); var retired = await older.Completion.WaitAsync(TimeSpan.FromSeconds(10)); var restored = await newest.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(EncodingInventoryState.Published, retired.State); Assert.NotNull(retired.Generation);
        Assert.Equal(EncodingInventoryState.Published, restored.State); Assert.NotEqual(retired.Generation, restored.Generation);
        Assert.Equal(1, newestSource.OpenedLeases); Assert.Equal(0, newestSource.ActiveLeases); Assert.Equal(0, b.ActiveLeases);
        var active = store.ReadActive(identity.Id)!; Assert.Equal(restored.Generation, active.Generation); Assert.Equal(a.EncodingCKey, active.Descriptor!.EncodingCKey);
        Assert.Equal(Rows(2).Select(row => row.EKey), active.Keys);
    }

    private sealed class ReleaseSignal(TaskCompletionSource signal) : IDisposable
    { public void Dispose() => signal.TrySetResult(); }
    private sealed class WithoutRows(EncodingSourceBuild build) : IBuild
    {
        public BuildRef Build => build.Build; public BuildMetadata Metadata => build.Metadata; public VibeMiner.ObjectAccess.CdnContext? Cdn => throw new InvalidOperationException();
        public FileResolution Resolve(uint id) => throw new InvalidOperationException(); public FileResolution Resolve(string path) => throw new InvalidOperationException();
        public IEnumerable<uint> FileDataIds(string locale, uint fromId = 0) => throw new InvalidOperationException();
    }
}
public sealed class EncodingInventoryPublisherContractOnService : EncodingInventoryPublisherContractTests
{ protected override IEncodingInventoryPublisher Publisher(InMemoryEncodingInventoryStore store, EncodingInventoryPublisherOptions? options = null) => new EncodingInventoryPublisher(store, options); }
public sealed class EncodingInventoryPublisherContractOnManagedFake : EncodingInventoryPublisherContractTests
{ protected override IEncodingInventoryPublisher Publisher(InMemoryEncodingInventoryStore store, EncodingInventoryPublisherOptions? options = null) => new InMemoryEncodingInventoryPublisher(store, options); }
public sealed class EncodingInventoryStoreContractOnManagedFake : EncodingInventoryStoreContract
{
    private readonly InMemoryEncodingInventoryStore store = new();
    protected override Task<IEncodingInventoryStore> CreateAsync() => Task.FromResult<IEncodingInventoryStore>(store);
    protected override Task<(long? Generation, IReadOnlyList<VibeMiner.Cdn.CdnKey> Keys)> ReadActiveAsync(EncodingInventoryContext context)
    { var active = store.ReadActive(context.Identity.Id); return Task.FromResult<(long?, IReadOnlyList<VibeMiner.Cdn.CdnKey>)>((active?.Generation, active?.Keys ?? [])); }
    protected override Task CloseAsync() => Task.CompletedTask;
}
