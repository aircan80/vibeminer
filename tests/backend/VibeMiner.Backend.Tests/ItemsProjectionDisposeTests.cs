using VibeMiner.Infrastructure;
using Xunit;
using Harness = VibeMiner.Backend.Tests.ItemsStoreTests.Harness;

namespace VibeMiner.Backend.Tests;

[Collection("Items projection builder")]
public sealed class ItemsProjectionDisposeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task AllThreeOwnedClosesAreAttemptedEvenAfterAnEarlierFailure(int failing)
    {
        var owners = Enumerable.Range(0, 3).Select(i => new CloseOwner(i == failing || failing == 3)).ToArray();
        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            ItemsProjection.Session.DisposeOwnedResourcesAsync(owners[0], owners[1], owners[2]));
        Assert.Equal(failing == 3 ? 3 : 1, error.InnerExceptions.Count);
        Assert.All(owners, owner => Assert.Equal(1, owner.Attempts));
    }

    [Fact]
    public async Task ConcurrentAndRepeatedSessionDisposalJoinTheOriginalRetirementAndReplayItsFailure()
    {
        await using var h = await Harness.Create();
        await h.Store.PrepareAsync(h.Request, _ => Task.CompletedTask, default);
        var saved = (await h.Catalog.ReadItemsAdmissionAsync(h.Opening, "enUS", default))!;
        var path = Path.Combine(h.DirectoryPath, "authored-disposal-test.bin");
        await using var input = new ControlledInput(path);
        var handle = input.SafeFileHandle;
        var session = new ItemsProjection.Session(input, h.Objects, h.Resources, null, saved.Artifact, Harness.Time, 0, 0, 0);
        try
        {
            var first = session.DisposeAsync().AsTask();
            await input.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var second = session.DisposeAsync().AsTask();
            Assert.False(first.IsCompleted); Assert.False(second.IsCompleted); Assert.Equal(1, input.Attempts);
            input.Release.TrySetResult();
            var firstFailure = await Assert.ThrowsAsync<AggregateException>(() => first);
            var secondFailure = await Assert.ThrowsAsync<AggregateException>(() => second);
            var repeatedFailure = await Assert.ThrowsAsync<AggregateException>(() => session.DisposeAsync().AsTask());
            Assert.Same(firstFailure, secondFailure); Assert.Same(firstFailure, repeatedFailure); Assert.Equal(1, input.Attempts);
            // The authored input really closed its OS handle before reporting the fault.
            Assert.True(handle.IsClosed);
        }
        finally
        {
            input.Release.TrySetResult();
            try { await session.DisposeAsync(); } catch (AggregateException) { }
            input.SuppressAuthoredFault = true;
        }
    }

    private sealed class CloseOwner(bool fail) : IAsyncDisposable
    {
        internal int Attempts;
        public ValueTask DisposeAsync()
        { Attempts++; return fail ? ValueTask.FromException(new IOException("Authored close failure.")) : ValueTask.CompletedTask; }
    }

    private sealed class ControlledInput(string path) : FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None)
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Attempts;
        internal bool SuppressAuthoredFault;
        public override async ValueTask DisposeAsync()
        {
            if (SuppressAuthoredFault) { await base.DisposeAsync(); return; }
            Attempts++; Entered.TrySetResult(); await Release.Task; await base.DisposeAsync();
            throw new IOException("Authored reported retirement failure.");
        }
    }
}
