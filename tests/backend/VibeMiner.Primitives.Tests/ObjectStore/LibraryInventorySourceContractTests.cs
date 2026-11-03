using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Primitives.Tests.LibraryInventoryTestKit;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

public abstract class LibraryInventorySourceContractTests : IAsyncLifetime
{
    protected abstract Task OpenAsync();
    protected abstract Task CloseAsync();
    protected abstract ILibraryInventory Inventory(string owner = "a", LibraryOwnerScope scope = LibraryOwnerScope.Build, string producer = "encoding", LibraryObjectRole role = LibraryObjectRole.Original);
    public Task InitializeAsync() => OpenAsync();
    public Task DisposeAsync() => CloseAsync();
    internal static LibraryInventorySourceDescriptor Descriptor(long count = 2, string source = "authored-source-a", string revision = "revision-a", string format = "format-a") => new(format, revision, source, count);
    private static async Task<LibraryInventoryGeneration> PublishAsync(ILibraryInventory inventory, LibraryInventorySourceDescriptor? proof, params ObjectKey[] keys)
    {
        var stage = await inventory.BeginAsync(); await inventory.AppendAsync(stage, keys);
        if (proof is null) await inventory.PublishAsync(stage); else await inventory.PublishSourceAsync(stage, proof);
        return stage;
    }

    [Fact]
    public async Task ProofAndCompleteUniqueKeySetBecomeVisibleTogetherAndStagedRefreshKeepsPriorPair()
    {
        var inventory = Inventory(); var proof = Descriptor(); Assert.Null(await inventory.MatchSourceAsync(proof));
        var first = await inventory.BeginAsync(); await inventory.AppendAsync(first, [Key(1), Key(2), Key(1)]);
        Assert.Null(await inventory.MatchSourceAsync(proof)); await inventory.PublishSourceAsync(first, proof);
        Assert.Equal(first.Value, await inventory.MatchSourceAsync(proof));
        var refresh = await inventory.BeginAsync(); await inventory.AppendAsync(refresh, [Key(3)]);
        Assert.Equal(first.Value, await inventory.MatchSourceAsync(proof)); Assert.Null(await inventory.MatchSourceAsync(Descriptor(1, "new-source")));
        await inventory.AbandonAsync(refresh); Assert.Equal(first.Value, await inventory.MatchSourceAsync(proof));
    }
    [Fact]
    public async Task EveryDescriptorComponentAndExactCapturedIdentityMustMatchTheActiveProof()
    {
        var inventory = Inventory(); var proof = Descriptor(); var first = await PublishAsync(inventory, proof, Key(1), Key(2));
        foreach (var mismatch in new[] { Descriptor(3), Descriptor(source: "changed"), Descriptor(revision: "changed"), Descriptor(format: "changed") })
            Assert.Null(await inventory.MatchSourceAsync(mismatch));
        foreach (var other in new[] { Inventory("b"), Inventory(scope: LibraryOwnerScope.Global), Inventory(producer: "other"), Inventory(role: LibraryObjectRole.Derived) })
        {
            Assert.Null(await other.MatchSourceAsync(proof)); await PublishAsync(other, Descriptor(1), Key(10));
            Assert.Null(await other.MatchSourceAsync(proof));
        }
        Assert.Equal(first.Value, await inventory.MatchSourceAsync(proof));
    }
    [Fact]
    public async Task GenericPublicationCannotInheritAnInactiveGenerationsProofEvenWhenKeysAreUnchanged()
    {
        var inventory = Inventory(); var proof = Descriptor(); var old = await PublishAsync(inventory, proof, Key(1), Key(2));
        var unversioned = await PublishAsync(inventory, null, Key(1), Key(2)); Assert.NotEqual(old.Value, unversioned.Value);
        Assert.Null(await inventory.MatchSourceAsync(proof));
        var replacement = await PublishAsync(inventory, Descriptor(source: "new"), Key(3), Key(4));
        Assert.Null(await inventory.MatchSourceAsync(proof)); Assert.Equal(replacement.Value, await inventory.MatchSourceAsync(Descriptor(source: "new")));
    }
    [Fact]
    public async Task WrongDeclaredUniqueCountPoisonsTheStageWithoutReplacingThePriorProofPair()
    {
        var inventory = Inventory(); var old = await PublishAsync(inventory, Descriptor(1), Key(1));
        var wrong = await inventory.BeginAsync(); await inventory.AppendAsync(wrong, [Key(2), Key(2)]);
        Assert.Equal(ObjectStoreCodes.InventorySourceMismatch, (await Assert.ThrowsAsync<ObjectStoreException>(() => inventory.PublishSourceAsync(wrong, Descriptor(2)))).Code);
        Assert.Equal(ObjectStoreCodes.InventoryStale, (await Assert.ThrowsAsync<ObjectStoreException>(() => inventory.PublishAsync(wrong))).Code);
        Assert.Equal(old.Value, await inventory.MatchSourceAsync(Descriptor(1))); Assert.Null(await inventory.MatchSourceAsync(Descriptor(2)));
        Assert.Equal([Key(1)], (await inventory.ReadPageAsync()).Keys);
    }
    [Fact]
    public async Task StaleTokensCannotReplaceNewerProofAndAnEmptyCompletedProofIsARealHit()
    {
        var inventory = Inventory(); var stale = await inventory.BeginAsync(); await inventory.AppendAsync(stale, [Key(1), Key(2)]);
        var empty = await PublishAsync(inventory, Descriptor(0));
        Assert.Equal(ObjectStoreCodes.InventoryStale, (await Assert.ThrowsAsync<ObjectStoreException>(() => inventory.PublishSourceAsync(stale, Descriptor()))).Code);
        Assert.Equal(empty.Value, await inventory.MatchSourceAsync(Descriptor(0))); Assert.Empty((await inventory.ReadPageAsync()).Keys);
    }
    [Fact]
    public async Task CancellationAndClosureAreExplicitAndNeverFabricateAMatchingGeneration()
    {
        var inventory = Inventory(); var old = await PublishAsync(inventory, Descriptor(1), Key(1));
        var pending = await inventory.BeginAsync(); await inventory.AppendAsync(pending, [Key(2), Key(3)]);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await inventory.MatchSourceAsync(Descriptor(1), canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => inventory.PublishSourceAsync(pending, Descriptor(2), canceled.Token));
        Assert.Equal(old.Value, await inventory.MatchSourceAsync(Descriptor(1))); await CloseAsync();
        Assert.Equal(ObjectStoreCodes.Closed, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await inventory.MatchSourceAsync(Descriptor(1)))).Code);
    }
    [Fact]
    public void DescriptorBoundsAreUtf8BasedAndInvalidInputCannotBeConstructed()
    {
        _ = new LibraryInventorySourceDescriptor(new string('é', 32), new string('é', 64), new string('é', 512), long.MaxValue);
        foreach (var values in new[] { ("", "r", "s"), ("f", "", "s"), ("f", "r", ""), ("bad\0", "r", "s"),
            (new string('é', 33), "r", "s"), ("f", new string('é', 65), "s"), ("f", "r", new string('é', 513)) })
            Assert.ThrowsAny<ArgumentException>(() => new LibraryInventorySourceDescriptor(values.Item1, values.Item2, values.Item3, 1));
        Assert.ThrowsAny<ArgumentException>(() => Descriptor(-1));
        foreach (var invalid in new[] { "\ud800", "\udc00" })
        {
            Assert.ThrowsAny<ArgumentException>(() => new LibraryInventorySourceDescriptor(invalid, "r", "s", 1));
            Assert.ThrowsAny<ArgumentException>(() => new LibraryInventorySourceDescriptor("f", invalid, "s", 1));
            Assert.ThrowsAny<ArgumentException>(() => new LibraryInventorySourceDescriptor("f", "r", invalid, 1));
        }
    }
}
public sealed class LibraryInventorySourceContractOnRealStore : LibraryInventorySourceContractTests
{
    private readonly TempDirectory temp = new(); private ObjectStore library = null!;
    protected override async Task OpenAsync() => library = await ObjectStore.OpenLibraryAsync(DirectoryPath(temp), Quick);
    protected override async Task CloseAsync() { await library.DisposeAsync(); temp.Dispose(); }
    protected override ILibraryInventory Inventory(string owner = "a", LibraryOwnerScope scope = LibraryOwnerScope.Build, string producer = "encoding", LibraryObjectRole role = LibraryObjectRole.Original) => library.ForOwner(owner, scope, producer, role).Inventory;
}
public sealed class LibraryInventorySourceContractOnFake : LibraryInventorySourceContractTests
{
    private readonly InMemoryLibraryInventories library = new();
    protected override Task OpenAsync() => Task.CompletedTask;
    protected override async Task CloseAsync() => await library.DisposeAsync();
    protected override ILibraryInventory Inventory(string owner = "a", LibraryOwnerScope scope = LibraryOwnerScope.Build, string producer = "encoding", LibraryObjectRole role = LibraryObjectRole.Original) => library.ForOwner(owner, scope, producer, role);
}
