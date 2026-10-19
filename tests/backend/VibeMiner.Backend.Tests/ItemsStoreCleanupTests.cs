using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Infrastructure;
using Xunit;
using Harness = VibeMiner.Backend.Tests.ItemsStoreTests.Harness;

namespace VibeMiner.Backend.Tests;

[Collection("Items projection builder")]
public sealed class ItemsStoreCleanupTests
{
    [Fact]
    public async Task UncertainRetirementKeepsOwnershipAndFailsClosedAcrossOrdinaryAndEffectsEntryPoints()
    {
        await using var h = await Harness.Create();
        var ready = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        await h.Store.DisposeAsync();
        var attempts = 0; ItemsProjection.Session? debt = null;
        var store = new ItemsStore(h.Catalog, h.Objects, h.Tables, h.Resources, session =>
        {
            attempts++; debt = session; throw new IOException("Authored retirement failure.");
        });
        try
        {
            Assert.True((await store.AuthorizeItemEffectsAsync(new(h.Request, ready.ArtifactId, "1"), default)).Item.Admitted);
            await h.ReplaceItem();
            await CleanupFails(() => store.ReadAsync(h.Request, default));
            await CleanupFails(() => store.AuthorizeItemEffectsAsync(new(h.Request, ready.ArtifactId, "1"), default));
            await CleanupFails(() => store.ReadPageAsync(new(h.Request, ready.ArtifactId), default));
            await CleanupFails(() => store.InspectAsync(new(h.Request, ready.ArtifactId, "1"), default));
            await CleanupFails(() => store.AuthorizeSummariesAsync(h.Request, ready.ArtifactId, ["1"], default));
            await CleanupFails(() => store.AuthorizeIconsAsync(h.Request, ready.ArtifactId, ["1"], default));
            await CleanupFails(() => store.PrepareAsync(h.Request, _ => Task.CompletedTask, default));
            await CleanupFails(() => store.DisposeAsync().AsTask());
            await CleanupFails(() => store.DisposeAsync().AsTask());
            Assert.Equal(1, attempts); Assert.NotNull(debt);
        }
        finally { if (debt is not null) await debt.DisposeAsync(); }
    }

    [Fact]
    public async Task FirstCloseFailureBlocksAConcurrentPreparationBeforeAnotherProjectionCanOpen()
    {
        await using var h = await Harness.Create();
        var ready = await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        await h.Store.DisposeAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0; ItemsProjection.Session? debt = null;
        var store = new ItemsStore(h.Catalog, h.Objects, h.Tables, h.Resources, async session =>
        {
            attempts++; debt = session; entered.TrySetResult(); await release.Task;
            throw new IOException("Authored delayed retirement failure.");
        });
        try
        {
            await store.AuthorizeItemEffectsAsync(new(h.Request, ready.ArtifactId, "1"), default);
            await ItemEffectsItemsProofTests.PoisonValidShapeCounts(h.DirectoryPath, ready.ArtifactId);
            var first = store.AuthorizeItemEffectsAsync(new(h.Request, ready.ArtifactId, "1"), default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var preparation = store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
            Assert.False(first.IsCompleted); Assert.False(preparation.IsCompleted);
            release.TrySetResult();
            await CleanupFails(() => first); await CleanupFails(() => preparation);
            await CleanupFails(() => store.DisposeAsync().AsTask());
            Assert.Equal(1, attempts); Assert.Empty(h.PreparedTables);
        }
        finally { release.TrySetResult(); if (debt is not null) await debt.DisposeAsync(); }
    }

    private static async Task CleanupFails(Func<Task> operation) =>
        Assert.Equal("ITEMS_CLEANUP_FAILED", (await Assert.ThrowsAsync<DiscoveryException>(operation)).Code);
}
