using System.Security.Cryptography;
using VibeMiner.Builds;
using VibeMiner.Builds.Testing;
using VibeMiner.Builds.Tests;
using VibeMiner.Cdn;
using VibeMiner.Composition;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Builds.Tests.EncodingInventoryTestKit;

namespace VibeMiner.Storage.IntegrationTests;

public sealed class EncodingInventoryStoreContractOnComposition : EncodingInventoryStoreContract
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "vibeminer-encoding-port-" + Guid.NewGuid().ToString("N"));
    private ObjectStore library = null!;
    protected override async Task<IEncodingInventoryStore> CreateAsync()
    { library = await ObjectStore.OpenLibraryAsync(directory, new() { CommitDelay = TimeSpan.FromMilliseconds(5) }); return new LibraryEncodingInventoryStore(library); }
    protected override async Task<(long? Generation, IReadOnlyList<CdnKey> Keys)> ReadActiveAsync(EncodingInventoryContext context)
    {
        var inventory = library.ForOwner(context.Identity.Id, LibraryOwnerScope.Build, EncodingInventoryPublisher.Producer, LibraryObjectRole.Original).Inventory;
        var keys = new List<CdnKey>(); LibraryInventoryCursor? after = null; long? generation = null;
        do
        {
            var page = await inventory.ReadPageAsync(1024, after); generation = page.Generation;
            foreach (var key in page.Keys) { Assert.Equal(ObjectKeyType.Encoded, key.Type); Assert.Equal(0, key.Namespace); keys.Add(new(key.KeyBytes())); }
            after = page.Next;
        } while (after is not null);
        return (generation, keys);
    }
    protected override async Task CloseAsync() { await library.DisposeAsync(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }
}

public sealed class EncodingInventoryCompositionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vibeminer-encoding-composition-" + Guid.NewGuid().ToString("N"));
    private string LibraryPath => Path.Combine(root, "library");
    private static ObjectStoreOptions Quick => new() { CommitDelay = TimeSpan.FromMilliseconds(5) };
    private static LibraryObjectOwner Owner(ObjectStore library, BuildIdentity identity, string producer = EncodingInventoryPublisher.Producer) =>
        library.ForOwner(identity.Id, LibraryOwnerScope.Build, producer, LibraryObjectRole.Original);
    private static async Task<IReadOnlyList<ObjectKey>> ActiveKeysAsync(ILibraryInventory inventory)
    {
        var keys = new List<ObjectKey>(); LibraryInventoryCursor? after = null;
        do { var page = await inventory.ReadPageAsync(1024, after); keys.AddRange(page.Keys); after = page.Next; } while (after is not null);
        return keys;
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public async Task ExplicitPublicationUsesCanonicalOwnerAndPreservesAbsentSharedObjectsOtherProducersAndRegistryStateAfterReopen()
    {
        var identity = Identity(); var shared = Synthetic.Headerless(2241, 2000); long generation;
        await using (var library = await ObjectStore.OpenLibraryAsync(LibraryPath, Quick))
        {
            await library.ForOwner("payload-publisher", LibraryOwnerScope.Global, "synthetic", LibraryObjectRole.Original).PutAsync([shared], true);
            var independent = Owner(library, identity, "manual").Inventory; var independentStage = await independent.BeginAsync();
            var independentKey = new ObjectKey(ObjectKeyType.Encoded, Key(600_001).ToBytes()); await independent.AppendAsync(independentStage, [independentKey]); await independent.PublishAsync(independentStage);
            var other = Owner(library, Identity(2)).Inventory; var otherStage = await other.BeginAsync(); await other.AppendAsync(otherStage, [independentKey]); await other.PublishAsync(otherStage);
            await using var registry = await BuildRegistry.OpenAsync(Path.Combine(root, "registry"));
            var observation = new BuildRegistryObservation(identity, Route(identity), "enUS", DateTimeOffset.UnixEpoch);
            await registry.ObserveAsync(observation); var load = await registry.BeginLoadAsync(identity.Id, observation.Selection, "enUS");
            var loaded = await registry.FinishAsync(load, BuildLoadState.Loaded);
            var rows = Rows(3506).Append(new EncodingRow(new(shared.Key.KeyBytes()), 2000)).OrderBy(row => row.EKey).ToArray();
            var source = new InMemoryEncodingRows(Route(identity), Key(900_001), rows);
            await using (var publisher = new EncodingInventoryPublisher(new LibraryEncodingInventoryStore(library)))
            {
                var publication = publisher.Start(identity, new EncodingSourceBuild(source)); var result = await publication.Completion.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(EncodingInventoryState.Published, result.State); generation = Assert.IsType<long>(result.Generation); Assert.Equal(3507, result.Processed);
                Assert.Equal(Key(900_001), result.Context.EncodingCKey); Assert.Equal(Route(identity), result.Context.Build);
                var inventory = Owner(library, identity).Inventory;
                var keys = await ActiveKeysAsync(inventory); Assert.Equal(rows.Select(row => new ObjectKey(ObjectKeyType.Encoded, row.EKey.ToBytes())), keys);
                Assert.All(keys, key => { Assert.Equal(ObjectKeyType.Encoded, key.Type); Assert.Equal(0, key.Namespace); });
                Assert.False(await Owner(library, identity).OwnsAsync(shared.Key)); Assert.True(await library.HasAsync(shared.Key));
                Assert.False(await library.HasAsync(new ObjectKey(ObjectKeyType.Encoded, Key(1).ToBytes()))); Assert.Equal(1, (await library.StatsAsync()).Objects);
                var globalLookup = await library.ForOwner(identity.Id, LibraryOwnerScope.Global, EncodingInventoryPublisher.Producer, LibraryObjectRole.Original).Inventory.LookupAsync(shared.Key);
                Assert.False(globalLookup.Contains);
                var routed = new InMemoryEncodingRows(Route(identity, "eu", 'd'), Key(900_002), Rows(1, 5000));
                var refreshed = await publisher.Start(identity, new EncodingSourceBuild(routed)).Completion;
                Assert.Equal(EncodingInventoryState.Published, refreshed.State); Assert.NotEqual(generation, refreshed.Generation); generation = Assert.IsType<long>(refreshed.Generation);
                Assert.Equal(Key(900_002), refreshed.Context.EncodingCKey); Assert.Equal("eu", refreshed.Context.Build.Region);
            }
            Assert.Equal([independentKey], await ActiveKeysAsync(independent)); Assert.Equal([independentKey], await ActiveKeysAsync(other));
            var retained = Assert.IsType<BuildRegistryEntry>(await registry.GetAsync(identity.Id));
            Assert.Equal(BuildLoadState.Loaded, retained.State); Assert.Equal(loaded.Generation, retained.Generation); Assert.Equal(loaded.UpdatedAt, retained.UpdatedAt); Assert.Equal(loaded.FinishedAt, retained.FinishedAt);
            Assert.Equal(shared.Bytes.ToArray(), (await library.ReadAsync(shared.Key)).Bytes); Assert.Equal(0, source.ActiveLeases);
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(LibraryPath, Quick);
        var active = await Owner(reopened, identity).Inventory.ReadPageAsync(); Assert.Equal(generation, active.Generation);
        Assert.Equal([new ObjectKey(ObjectKeyType.Encoded, Key(5000).ToBytes())], active.Keys); Assert.False(await Owner(reopened, identity).OwnsAsync(shared.Key));
        Assert.Equal(1, (await reopened.StatsAsync()).Objects);
    }

    [Fact]
    public async Task FailedRefreshKeepsThePreviousGenerationAndDisposingPublisherLeavesLibraryUsable()
    {
        await using var library = await ObjectStore.OpenLibraryAsync(LibraryPath, Quick); var identity = Identity();
        var publisher = new EncodingInventoryPublisher(new LibraryEncodingInventoryStore(library));
        var first = await publisher.Start(identity, new EncodingSourceBuild(Source(identity, 3))).Completion;
        var broken = Source(identity, 2000); broken.BeforeRead = (after, _, _) => after is null ? ValueTask.CompletedTask : throw new EncodingInventoryException(EncodingInventoryCodes.Corrupt, "authored next-page corruption");
        var failed = await publisher.Start(identity, new EncodingSourceBuild(broken)).Completion;
        Assert.Equal(EncodingInventoryState.Failed, failed.State); Assert.Equal(EncodingInventoryCodes.Corrupt, failed.Code);
        var active = await Owner(library, identity).Inventory.ReadPageAsync(); Assert.Equal(first.Generation, active.Generation); Assert.Equal(3, active.Keys.Count);
        await publisher.DisposeAsync(); Assert.Equal(0, broken.ActiveLeases); Assert.Equal(0, (await library.StatsAsync()).Objects);
        await library.ForOwner("other", LibraryOwnerScope.Global, "source", LibraryObjectRole.Original).PutAsync([Synthetic.Headerless(2242, 100)], true);
        Assert.Equal(1, (await library.StatsAsync()).Objects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupersessionOrCancellationAfterTheRealCompositionCommitCannotReportRollback(bool supersede)
    {
        await using var library = await ObjectStore.OpenLibraryAsync(LibraryPath, Quick); var identity = Identity();
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new HeldCommittedStore(new LibraryEncodingInventoryStore(library), committed, release);
        using var canceled = new CancellationTokenSource(); await using var publisher = new EncodingInventoryPublisher(store);
        var first = publisher.Start(identity, new EncodingSourceBuild(Source(identity, 1)), canceled.Token);
        try
        {
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(10)); EncodingInventoryPublication? latest = null;
            if (supersede) latest = publisher.Start(identity, new EncodingSourceBuild(Source(identity, 2, 5000))); else canceled.Cancel();
            release.TrySetResult(); var result = await first.Completion.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(EncodingInventoryState.Published, result.State); Assert.NotNull(result.Generation);
            if (latest is not null)
            {
                var next = await latest.Completion; Assert.Equal(EncodingInventoryState.Published, next.State);
                Assert.NotEqual(result.Generation, next.Generation); Assert.Equal(next.Generation, (await Owner(library, identity).Inventory.ReadPageAsync()).Generation);
            }
            else Assert.Equal(result.Generation, (await Owner(library, identity).Inventory.ReadPageAsync()).Generation);
            Assert.Equal(0, (await library.StatsAsync()).Objects);
        }
        finally { release.TrySetResult(); }
    }

    private sealed class HeldCommittedStore(IEncodingInventoryStore inner, TaskCompletionSource committed, TaskCompletionSource release) : IEncodingInventoryStore
    {
        private int commits;
        private readonly TaskCompletionSource committedSignal = committed;
        private readonly TaskCompletionSource releaseSignal = release;
        public async Task<IEncodingInventoryWrite> BeginAsync(EncodingInventoryContext context, CancellationToken ct = default) => new Write(this, await inner.BeginAsync(context, ct));
        private sealed class Write(HeldCommittedStore owner, IEncodingInventoryWrite inner) : IEncodingInventoryWrite
        {
            public Task AppendAsync(IReadOnlyList<CdnKey> keys, CancellationToken ct = default) => inner.AppendAsync(keys, ct);
            public async Task<long> PublishAsync(CancellationToken ct = default)
            {
                var generation = await inner.PublishAsync(ct);
                if (Interlocked.Increment(ref owner.commits) == 1) { owner.committedSignal.TrySetResult(); await owner.releaseSignal.Task; }
                return generation;
            }
            public Task AbandonAsync(CancellationToken ct = default) => inner.AbandonAsync(ct);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
