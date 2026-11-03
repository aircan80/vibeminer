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

public sealed class EncodingInventoryReuseCompositionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "vibeminer-encoding-reuse-" + Guid.NewGuid().ToString("N"));
    private string DirectoryPath => Path.Combine(root, "library");
    private static ObjectStoreOptions Quick => new() { CommitDelay = TimeSpan.FromMilliseconds(5) };
    private static LibraryObjectOwner Owner(ObjectStore library, BuildIdentity identity) => library.ForOwner(identity.Id, LibraryOwnerScope.Build, EncodingInventoryPublisher.Producer, LibraryObjectRole.Original);
    private static LibraryInventorySourceDescriptor Proof(CdnKey ckey, long count) => new(EncodingInventoryPublisher.SourceFormat, EncodingInventoryPublisher.SourceRevision, Convert.ToHexStringLower(ckey.ToBytes()), count);
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }

    [Fact]
    public async Task ReopenedExactProofReusesWithoutLeaseOrPayloadVerificationDespiteChangedRouteAndLocales()
    {
        var identity = Identity(); var item = Synthetic.Headerless(2251, 2000); var key = new CdnKey(item.Key.KeyBytes()); long generation;
        await using (var library = await ObjectStore.OpenLibraryAsync(DirectoryPath, Quick))
        {
            await library.ForOwner("payload-owner", LibraryOwnerScope.Global, "source", LibraryObjectRole.Original).PutAsync([item], true);
            var source = new InMemoryEncodingRows(Route(identity), Key(900_001), [new(key, 2000)]);
            await using var publisher = new EncodingInventoryPublisher(new LibraryEncodingInventoryStore(library));
            var published = await publisher.StartReuse(identity, new EncodingSourceBuild(source)).Completion;
            Assert.Equal(EncodingInventoryState.Published, published.State); generation = Assert.IsType<long>(published.Generation);
        }
        // Authored fixture damage proves index source reuse is not payload verification.
        var pack = Assert.Single(System.IO.Directory.GetFiles(Path.Combine(DirectoryPath, "packs"), "*.vmp"));
        var bytes = File.ReadAllBytes(pack); var payload = bytes.AsSpan().IndexOf(item.Bytes.Span[..32]); Assert.True(payload > 0); bytes[payload + 40] ^= 0x40; File.WriteAllBytes(pack, bytes);
        await using var reopened = await ObjectStore.OpenLibraryAsync(DirectoryPath, Quick);
        var noRead = new InMemoryEncodingRows(Route(identity, "eu", 'd'), Key(900_001), [new(key, 2000)]);
        noRead.BeforeRead = (_, _, _) => throw new InvalidOperationException("Reopened reuse must not read ENCODING.");
        var opened = new EncodingSourceBuild(noRead); opened.Metadata = opened.Metadata with { Locales = ["koKR", "deDE"] };
        await using var again = new EncodingInventoryPublisher(new LibraryEncodingInventoryStore(reopened));
        var reused = await again.StartReuse(identity, opened).Completion;
        Assert.Equal(EncodingInventoryState.Reused, reused.State); Assert.Equal(generation, reused.Generation); Assert.Equal("eu", reused.Context.Build.Region);
        Assert.Equal(0, noRead.OpenedLeases); Assert.Equal(0, noRead.ReadCalls); Assert.False(await Owner(reopened, identity).OwnsAsync(item.Key));
        Assert.True(await reopened.ForOwner("payload-owner", LibraryOwnerScope.Global, "source", LibraryObjectRole.Original).OwnsAsync(item.Key));
        Assert.Equal(1, (await reopened.StatsAsync()).Objects); Assert.Equal(ReadState.Corrupt, (await reopened.ReadAsync(item.Key)).State);
    }

    [Fact]
    public async Task V2FreshPublicationStillWorksAndSourceReuseRequiresExplicitAdditiveUpgrade()
    {
        var identity = Identity();
        await using (var library = await ObjectStore.OpenLibraryAsync(DirectoryPath, Quick))
        {
            await using var publisher = new EncodingInventoryPublisher(new LibraryEncodingInventoryStore(library));
            Assert.Equal(EncodingInventoryState.Published, (await publisher.Start(identity, new EncodingSourceBuild(Source(identity, 2))).Completion).State);
        }
        Mutate("DROP TABLE library_inventory_source; UPDATE library_format SET version=2");
        await using (var old = await ObjectStore.OpenLibraryAsync(DirectoryPath, Quick))
        {
            await using var publisher = new EncodingInventoryPublisher(new LibraryEncodingInventoryStore(old));
            var source = Source(identity, 2); var unsupported = await publisher.StartReuse(identity, new EncodingSourceBuild(source)).Completion;
            Assert.Equal(EncodingInventoryState.Failed, unsupported.State); Assert.Equal(EncodingInventoryCodes.Unsupported, unsupported.Code); Assert.Equal(0, source.OpenedLeases);
            Assert.Equal(EncodingInventoryState.Published, (await publisher.Start(identity, new EncodingSourceBuild(Source(identity, 3))).Completion).State);
        }
        await using var upgraded = await ObjectStore.OpenLibraryAsync(DirectoryPath, Quick with { UpgradeLibraryInventorySources = true });
        await using var reusable = new EncodingInventoryPublisher(new LibraryEncodingInventoryStore(upgraded));
        var first = await reusable.StartReuse(identity, new EncodingSourceBuild(Source(identity, 3))).Completion; Assert.Equal(EncodingInventoryState.Published, first.State);
        var noRead = Source(identity, 3); var hit = await reusable.StartReuse(identity, new EncodingSourceBuild(noRead)).Completion;
        Assert.Equal(EncodingInventoryState.Reused, hit.State); Assert.Equal(first.Generation, hit.Generation); Assert.Equal(0, noRead.OpenedLeases); Assert.Equal(0, (await upgraded.StatsAsync()).Objects);
    }

    [Fact]
    public async Task CanceledRefreshKeepsTheOldGenerationAndProofTogetherAndFreshStartInvalidatesProof()
    {
        await using var library = await ObjectStore.OpenLibraryAsync(DirectoryPath, Quick); var identity = Identity();
        await using var publisher = new EncodingInventoryPublisher(new LibraryEncodingInventoryStore(library));
        var old = await publisher.StartReuse(identity, new EncodingSourceBuild(Source(identity, 2))).Completion;
        using var canceled = new CancellationTokenSource(); var changed = new InMemoryEncodingRows(Route(identity), Key(999_999), Rows(2000));
        changed.BeforeRead = (after, _, _) => { if (after is not null) canceled.Cancel(); return ValueTask.CompletedTask; };
        var interrupted = await publisher.StartReuse(identity, new EncodingSourceBuild(changed), canceled.Token).Completion;
        Assert.Equal(EncodingInventoryState.Canceled, interrupted.State); Assert.Equal(0, changed.ActiveLeases);
        Assert.Equal(old.Generation, await Owner(library, identity).Inventory.MatchSourceAsync(Proof(Key(900_001), 2)));
        var noRead = Source(identity, 2); var reused = await publisher.StartReuse(identity, new EncodingSourceBuild(noRead)).Completion;
        Assert.Equal(EncodingInventoryState.Reused, reused.State); Assert.Equal(0, noRead.OpenedLeases);
        var fresh = await publisher.Start(identity, new EncodingSourceBuild(Source(identity, 2))).Completion;
        Assert.Equal(EncodingInventoryState.Published, fresh.State); Assert.NotEqual(old.Generation, fresh.Generation);
        Assert.Null(await Owner(library, identity).Inventory.MatchSourceAsync(Proof(Key(900_001), 2)));
        Assert.Equal(0, (await library.StatsAsync()).Objects);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostAcknowledgementUsesExactReadbackOrRemainsUncertainUntilReopen(bool failReadback)
    {
        var identity = Identity(); var fail = 1; long? confirmed = null;
        var options = InventorySourceFaults.AfterPublicationFailure(Quick, () => Interlocked.Exchange(ref fail, 0) == 1, failReadback);
        await using (var library = await ObjectStore.OpenLibraryAsync(DirectoryPath, options))
        {
            await using var publisher = new EncodingInventoryPublisher(new LibraryEncodingInventoryStore(library));
            var result = await publisher.StartReuse(identity, new EncodingSourceBuild(Source(identity, 2))).Completion;
            if (failReadback)
            {
                Assert.Equal(EncodingInventoryState.Failed, result.State); Assert.Equal(EncodingInventoryCodes.Storage, result.Code); Assert.Null(result.Generation);
                Assert.Contains("may be committed", result.Message, StringComparison.OrdinalIgnoreCase);
            }
            else { Assert.Equal(EncodingInventoryState.Published, result.State); confirmed = result.Generation; Assert.NotNull(confirmed); }
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(DirectoryPath, Quick); await using var retry = new EncodingInventoryPublisher(new LibraryEncodingInventoryStore(reopened));
        var noRead = Source(identity, 2); noRead.BeforeRead = (_, _, _) => throw new InvalidOperationException("The durable uncertain result must be reusable after readback.");
        var actual = await retry.StartReuse(identity, new EncodingSourceBuild(noRead)).Completion;
        Assert.Equal(EncodingInventoryState.Reused, actual.State); Assert.NotNull(actual.Generation); if (confirmed is not null) Assert.Equal(confirmed, actual.Generation);
        Assert.Equal(0, noRead.OpenedLeases); Assert.Equal(2, (await Owner(reopened, identity).Inventory.ReadPageAsync()).Keys.Count);
    }

    [Fact]
    public async Task ANewerMatchingGenerationCannotAcknowledgeAnOlderUncertainPublication()
    {
        var identity = Identity(); var fail = 1; long replacement = 0;
        var options = InventorySourceFaults.AfterPublicationFailure(Quick, () =>
        {
            if (Interlocked.Exchange(ref fail, 0) != 1) return false;
            replacement = InventorySourceFaults.PublishMatchingReplacement(DirectoryPath, identity.Id, LibraryOwnerScope.Build, EncodingInventoryPublisher.Producer,
                LibraryObjectRole.Original, Proof(Key(900_001), 2)); return true;
        });
        await using (var library = await ObjectStore.OpenLibraryAsync(DirectoryPath, options))
        {
            var captured = Owner(library, identity).Inventory;
            await using var publisher = new EncodingInventoryPublisher(new LibraryEncodingInventoryStore(library));
            var result = await publisher.StartReuse(identity, new EncodingSourceBuild(Source(identity, 2))).Completion;
            Assert.Equal(EncodingInventoryState.Failed, result.State); Assert.Equal(EncodingInventoryCodes.Storage, result.Code); Assert.Null(result.Generation);
            Assert.Contains("may be committed", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(replacement > 0); Assert.Equal(replacement, await captured.MatchSourceAsync(Proof(Key(900_001), 2)));
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(DirectoryPath, Quick);
        await using var retry = new EncodingInventoryPublisher(new LibraryEncodingInventoryStore(reopened));
        var noRead = Source(identity, 2); var hit = await retry.StartReuse(identity, new EncodingSourceBuild(noRead)).Completion;
        Assert.Equal(EncodingInventoryState.Reused, hit.State); Assert.Equal(replacement, hit.Generation); Assert.Equal(0, noRead.OpenedLeases);
    }

    [Fact]
    public async Task NewerRequestFencesARealMatchBeforeAcceptanceAndPublishedEmptyProofCanBeReused()
    {
        await using var library = await ObjectStore.OpenLibraryAsync(DirectoryPath, Quick); var identity = Identity(); var inner = new LibraryEncodingInventoryStore(library);
        await using (var seed = new EncodingInventoryPublisher(inner)) await seed.StartReuse(identity, new EncodingSourceBuild(Source(identity, 0))).Completion;
        var matched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new HeldMatchStore(inner, matched, release); await using var publisher = new EncodingInventoryPublisher(store);
        var oldSource = Source(identity, 0); var older = publisher.StartReuse(identity, new EncodingSourceBuild(oldSource));
        try
        {
            await matched.Task.WaitAsync(TimeSpan.FromSeconds(10)); var newer = publisher.Start(identity, new EncodingSourceBuild(Source(identity, 1, 5000)));
            release.TrySetResult(); var superseded = await older.Completion; var actual = await newer.Completion;
            Assert.Equal(EncodingInventoryState.Superseded, superseded.State); Assert.Null(superseded.Generation); Assert.Equal(0, oldSource.OpenedLeases);
            Assert.Equal(EncodingInventoryState.Published, actual.State); Assert.Null(await Owner(library, identity).Inventory.MatchSourceAsync(Proof(Key(900_001), 0)));
        }
        finally { release.TrySetResult(); }
    }

    private void Mutate(string sql)
    {
        using var c = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(DirectoryPath, "objects.sqlite3"), Pooling = false }.ToString());
        c.Open(); using var command = c.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private sealed class HeldMatchStore(IEncodingInventoryStore inner, TaskCompletionSource matched, TaskCompletionSource release) : IEncodingInventoryStore
    {
        public Task<IEncodingInventoryWrite> BeginAsync(EncodingInventoryContext context, CancellationToken ct = default) => inner.BeginAsync(context, ct);
        public async Task<long?> TryReuseAsync(EncodingInventoryContext context, EncodingInventorySourceDescriptor descriptor, CancellationToken ct = default)
        {
            var generation = await inner.TryReuseAsync(context, descriptor, ct);
            if (generation is not null) { matched.TrySetResult(); await release.Task; }
            return generation;
        }
    }
}
