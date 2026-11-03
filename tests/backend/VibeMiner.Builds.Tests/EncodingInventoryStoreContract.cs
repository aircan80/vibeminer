using VibeMiner.Cdn;
using Xunit;
using static VibeMiner.Builds.Tests.EncodingInventoryTestKit;

namespace VibeMiner.Builds.Tests;

/// <summary>Runs against independent managed staging and the Composition adapter over a real mixed library.</summary>
public abstract class EncodingInventoryStoreContract : IAsyncLifetime
{
    protected IEncodingInventoryStore Store { get; private set; } = null!;
    protected abstract Task<IEncodingInventoryStore> CreateAsync();
    protected abstract Task<(long? Generation, IReadOnlyList<CdnKey> Keys)> ReadActiveAsync(EncodingInventoryContext context);
    protected abstract Task CloseAsync();
    public async Task InitializeAsync() => Store = await CreateAsync();
    public Task DisposeAsync() => CloseAsync();

    [Fact]
    public async Task WholeGenerationIsInvisibleUntilPublishAndEveryRequestGetsAFreshGeneration()
    {
        var context = Context(count: 1500); await using var write = await Store.BeginAsync(context);
        await write.AppendAsync(Rows(1024).Select(row => row.EKey).ToArray());
        Assert.Null((await ReadActiveAsync(context)).Generation);
        await write.AppendAsync(Rows(476, 1025).Select(row => row.EKey).ToArray()); var first = await write.PublishAsync();
        var active = await ReadActiveAsync(context); Assert.Equal(first, active.Generation); Assert.Equal(Rows(1500).Select(row => row.EKey), active.Keys);
        await using var next = await Store.BeginAsync(context); await next.AppendAsync([Key(8)]);
        Assert.Equal(first, (await ReadActiveAsync(context)).Generation); var second = await next.PublishAsync(); Assert.NotEqual(first, second);
        Assert.Equal([Key(8)], (await ReadActiveAsync(context)).Keys);
    }
    [Fact]
    public async Task AbandonDisposeAndInvalidAppendNeverReplaceAnExistingActiveGeneration()
    {
        var context = Context(); await using var original = await Store.BeginAsync(context); await original.AppendAsync([Key(1)]); var first = await original.PublishAsync();
        await using (var abandoned = await Store.BeginAsync(context)) { await abandoned.AppendAsync([Key(2)]); await abandoned.AbandonAsync(); }
        await using (var disposed = await Store.BeginAsync(context)) await disposed.AppendAsync([Key(3)]);
        await using (var invalid = await Store.BeginAsync(context))
        {
            Assert.Equal(EncodingInventoryCodes.Validation, (await Assert.ThrowsAsync<EncodingInventoryException>(() => invalid.AppendAsync(Rows(1025).Select(row => row.EKey).ToArray()))).Code);
            await Assert.ThrowsAsync<EncodingInventoryException>(() => invalid.PublishAsync());
        }
        var active = await ReadActiveAsync(context); Assert.Equal(first, active.Generation); Assert.Equal([Key(1)], active.Keys);
    }
    [Fact]
    public async Task LaterCanonicalOwnerGenerationFencesOlderWritesButAnotherBuildIsIndependent()
    {
        var context = Context(); await using var stale = await Store.BeginAsync(context); await stale.AppendAsync([Key(1)]);
        var routed = new EncodingInventoryContext(context.Identity, Route(context.Identity, "eu", 'd'), context.EncodingCKey, 3);
        await using var latest = await Store.BeginAsync(routed); await latest.AppendAsync([Key(2)]);
        Assert.Equal(EncodingInventoryCodes.Superseded, (await Assert.ThrowsAsync<EncodingInventoryException>(() => stale.PublishAsync())).Code);
        await latest.PublishAsync();
        var other = Context(2); await using var independent = await Store.BeginAsync(other); await independent.AppendAsync([Key(3)]); await independent.PublishAsync();
        Assert.Equal([Key(2)], (await ReadActiveAsync(context)).Keys); Assert.Equal([Key(3)], (await ReadActiveAsync(other)).Keys);
    }
    [Fact]
    public async Task EmptyGenerationIsPublishedAndPreCanceledBeginNeverCreatesAnActiveValue()
    {
        var context = Context(count: 0); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store.BeginAsync(context, canceled.Token)); Assert.Null((await ReadActiveAsync(context)).Generation);
        await using var empty = await Store.BeginAsync(context); var generation = await empty.PublishAsync();
        var active = await ReadActiveAsync(context); Assert.Equal(generation, active.Generation); Assert.Empty(active.Keys);
    }    [Fact]
    public async Task SourceProofIsPublishedWithTheGenerationAndGenericPublicationNeverInheritsIt()
    {
        var context = Context(count: 2); var descriptor = new EncodingInventorySourceDescriptor(EncodingInventoryPublisher.SourceFormat, EncodingInventoryPublisher.SourceRevision, context.EncodingCKey, 2);
        Assert.Null(await Store.TryReuseAsync(context, descriptor)); await using var write = await Store.BeginAsync(context); await write.AppendAsync([Key(1), Key(2)]);
        Assert.Null(await Store.TryReuseAsync(context, descriptor)); var generation = await write.PublishSourceAsync(descriptor);
        Assert.Equal(generation, await Store.TryReuseAsync(context, descriptor));
        var routeOnly = new EncodingInventoryContext(context.Identity, Route(context.Identity, "eu", 'd'), context.EncodingCKey, 2);
        Assert.Equal(generation, await Store.TryReuseAsync(routeOnly, descriptor));
        await using (var canceledRefresh = await Store.BeginAsync(context)) { await canceledRefresh.AppendAsync([Key(3)]); Assert.Equal(generation, await Store.TryReuseAsync(context, descriptor)); }
        Assert.Equal(generation, await Store.TryReuseAsync(context, descriptor));
        await using var fresh = await Store.BeginAsync(context); await fresh.AppendAsync([Key(1), Key(2)]); await fresh.PublishAsync();
        Assert.Null(await Store.TryReuseAsync(context, descriptor));
    }
    [Fact]
    public async Task SourceCountProofFailureCannotActivatePartialKeysOrReplaceThePriorProof()
    {
        var context = Context(count: 2); var descriptor = new EncodingInventorySourceDescriptor(EncodingInventoryPublisher.SourceFormat, EncodingInventoryPublisher.SourceRevision, context.EncodingCKey, 2);
        await using var original = await Store.BeginAsync(context); await original.AppendAsync([Key(1), Key(2)]); var generation = await original.PublishSourceAsync(descriptor);
        await using var partial = await Store.BeginAsync(context); await partial.AppendAsync([Key(3)]);
        Assert.Equal(EncodingInventoryCodes.Corrupt, (await Assert.ThrowsAsync<EncodingInventoryException>(() => partial.PublishSourceAsync(descriptor))).Code);
        Assert.Equal(generation, await Store.TryReuseAsync(context, descriptor)); Assert.Equal([Key(1), Key(2)], (await ReadActiveAsync(context)).Keys);
    }
    [Fact]
    public async Task DescriptorCannotContradictTheCapturedCKeyOrCountEvenIfAnotherProofWouldMatch()
    {
        var context = Context(count: 2);
        foreach (var descriptor in new[]
        {
            new EncodingInventorySourceDescriptor(EncodingInventoryPublisher.SourceFormat, EncodingInventoryPublisher.SourceRevision, Key(999_999), 2),
            new EncodingInventorySourceDescriptor(EncodingInventoryPublisher.SourceFormat, EncodingInventoryPublisher.SourceRevision, context.EncodingCKey, 3),
        })
        {
            Assert.Equal(EncodingInventoryCodes.Validation, (await Assert.ThrowsAsync<EncodingInventoryException>(() => Store.TryReuseAsync(context, descriptor))).Code);
            await using var write = await Store.BeginAsync(context); await write.AppendAsync([Key(1), Key(2)]);
            Assert.Equal(EncodingInventoryCodes.Validation, (await Assert.ThrowsAsync<EncodingInventoryException>(() => write.PublishSourceAsync(descriptor))).Code);
            Assert.Null((await ReadActiveAsync(context)).Generation);
        }
    }

}
