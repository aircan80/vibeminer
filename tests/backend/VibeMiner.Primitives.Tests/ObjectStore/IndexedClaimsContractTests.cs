using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

public abstract class IndexedClaimsContractTests : IAsyncLifetime
{
    protected abstract Task OpenAsync();
    protected abstract Task CloseAsync();
    protected abstract Task SeedAsync(PutObject item);
    protected abstract Task<IReadOnlyList<bool>> ClaimAsync(IReadOnlyList<ObjectKey> keys, string owner = "a", LibraryOwnerScope scope = LibraryOwnerScope.Build, string producer = "source", LibraryObjectRole role = LibraryObjectRole.Original, CancellationToken ct = default);
    protected abstract ValueTask<bool> OwnsAsync(ObjectKey key, string owner = "a", LibraryOwnerScope scope = LibraryOwnerScope.Build, string producer = "source", LibraryObjectRole role = LibraryObjectRole.Original);
    public Task InitializeAsync() => OpenAsync(); public Task DisposeAsync() => CloseAsync();
    [Fact]
    public async Task OrderedDuplicatesCreditOnlyIndexedRowsAndNeverFabricateAbsentMembership()
    {
        var a = Synthetic.Headerless(2261, 1000); var b = Synthetic.Headerless(2262, 2000); var absent = Synthetic.Headerless(2263, 1000).Key;
        await SeedAsync(a); await SeedAsync(b);
        Assert.Equal([true, false, true, true], await ClaimAsync([b.Key, absent, a.Key, b.Key]));
        Assert.True(await OwnsAsync(a.Key)); Assert.True(await OwnsAsync(b.Key)); Assert.False(await OwnsAsync(absent));
        Assert.Equal([true, true], await ClaimAsync([a.Key, b.Key])); Assert.Empty(await ClaimAsync([]));
    }
    [Fact]
    public async Task FullTypedKeysAndEveryCapturedIdentityComponentRemainIndependent()
    {
        var original = Synthetic.Headerless(2264, 1000); var derived = new PutObject(new ObjectKey(ObjectKeyType.Generated, original.Key.KeyBytes()), 11, new byte[11], ObjectOrigin.Unknown);
        await SeedAsync(original); await SeedAsync(derived); Assert.Equal([true, true], await ClaimAsync([original.Key, derived.Key]));
        Assert.False(await OwnsAsync(original.Key, owner: "b")); Assert.False(await OwnsAsync(original.Key, scope: LibraryOwnerScope.Global));
        Assert.False(await OwnsAsync(original.Key, producer: "other")); Assert.False(await OwnsAsync(original.Key, role: LibraryObjectRole.Derived));
        Assert.Equal([true], await ClaimAsync([original.Key], owner: "b")); Assert.True(await OwnsAsync(original.Key, owner: "b")); Assert.False(await OwnsAsync(derived.Key, owner: "b"));
    }
    [Fact]
    public async Task EntireBatchAndFixedBoundAreValidatedBeforeAnyNewClaim()
    {
        var item = Synthetic.Headerless(2265, 1000); await SeedAsync(item);
        foreach (var invalid in new[] { new ObjectKey((ObjectKeyType)0, new byte[16]), new ObjectKey(ObjectKeyType.Encoded, new byte[16], 1) })
        {
            await Assert.ThrowsAnyAsync<ArgumentException>(() => ClaimAsync([item.Key, invalid])); Assert.False(await OwnsAsync(item.Key));
        }
        Assert.Equal(ObjectStoreCodes.Limit, (await Assert.ThrowsAsync<ObjectStoreException>(() => ClaimAsync(Enumerable.Repeat(item.Key, 1025).ToArray()))).Code);
        Assert.False(await OwnsAsync(item.Key)); Assert.Equal(1024, (await ClaimAsync(Enumerable.Repeat(item.Key, 1024).ToArray())).Count);
    }
    [Fact]
    public async Task PreCanceledClaimLeavesEveryMemberUnchangedAndConcurrentOwnersRemainExact()
    {
        var item = Synthetic.Headerless(2266, 1000); await SeedAsync(item); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ClaimAsync([item.Key], ct: canceled.Token)); Assert.False(await OwnsAsync(item.Key));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => ClaimAsync([item.Key], owner: $"owner-{i}")));
        for (var i = 0; i < 12; i++) Assert.True(await OwnsAsync(item.Key, owner: $"owner-{i}")); Assert.False(await OwnsAsync(item.Key));
    }
}
public sealed class IndexedClaimsContractOnRealStore : IndexedClaimsContractTests
{
    private readonly TempDirectory temp = new(); private ObjectStore library = null!;
    protected override async Task OpenAsync() => library = await ObjectStore.OpenLibraryAsync(Path.Combine(temp.Path, "library"), Quick);
    protected override async Task CloseAsync() { await library.DisposeAsync(); temp.Dispose(); }
    protected override async Task SeedAsync(PutObject item) => Assert.Equal(PutOutcome.Stored, Assert.Single(await library.ForOwner("publisher", LibraryOwnerScope.Global, "source", LibraryObjectRole.Original).PutAsync([item], true)).Outcome);
    protected override Task<IReadOnlyList<bool>> ClaimAsync(IReadOnlyList<ObjectKey> keys, string owner = "a", LibraryOwnerScope scope = LibraryOwnerScope.Build, string producer = "source", LibraryObjectRole role = LibraryObjectRole.Original, CancellationToken ct = default) => library.ForOwner(owner, scope, producer, role).ClaimIndexedAsync(keys, ct);
    protected override ValueTask<bool> OwnsAsync(ObjectKey key, string owner = "a", LibraryOwnerScope scope = LibraryOwnerScope.Build, string producer = "source", LibraryObjectRole role = LibraryObjectRole.Original) => library.ForOwner(owner, scope, producer, role).OwnsAsync(key);
}
public sealed class IndexedClaimsContractOnManagedModel : IndexedClaimsContractTests
{
    private readonly InMemoryIndexedOwnership library = new();
    protected override Task OpenAsync() => Task.CompletedTask; protected override async Task CloseAsync() => await library.DisposeAsync();
    protected override Task SeedAsync(PutObject item) { library.SetIndexedObject(item.Key); return Task.CompletedTask; }
    protected override Task<IReadOnlyList<bool>> ClaimAsync(IReadOnlyList<ObjectKey> keys, string owner = "a", LibraryOwnerScope scope = LibraryOwnerScope.Build, string producer = "source", LibraryObjectRole role = LibraryObjectRole.Original, CancellationToken ct = default) => library.ForOwner(owner, scope, producer, role).ClaimIndexedAsync(keys, ct);
    protected override ValueTask<bool> OwnsAsync(ObjectKey key, string owner = "a", LibraryOwnerScope scope = LibraryOwnerScope.Build, string producer = "source", LibraryObjectRole role = LibraryObjectRole.Original) => library.ForOwner(owner, scope, producer, role).OwnsAsync(key);
}
