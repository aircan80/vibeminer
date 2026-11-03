using System.Collections.Concurrent;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

public sealed class LibraryObjectStagingTests
{
    private static string Folder(TempDirectory temp) => Path.Combine(temp.Path, "library");
    private static LibraryObjectOwner Owner(ObjectStore library, string id = "build-a") => library.ForOwner(id, LibraryOwnerScope.Build, "download", LibraryObjectRole.Original);
    private static long PackBytes(string folder) => PackFiles(folder).Sum(path => new FileInfo(path).Length);

    [Fact]
    public async Task LibraryOwnerRunsTheSameAuthoredStagingContractAsLegacyCdnAndFake()
    {
        using var temp = new TempDirectory();
        await using var library = await ObjectStore.OpenLibraryAsync(Folder(temp), Quick);
        var owner = Owner(library);
        await ObjectStagingContract.AssertChunkResumeAsync(library, owner);
        Assert.True(await owner.OwnsAsync(Synthetic.Chunked(1, Enumerable.Repeat(65_536, 6).ToArray()).Key));
        Assert.True(await owner.OwnsAsync(Synthetic.Headerless(9, 1000).Key));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task FinishedOwnerStagingCannotDeleteAnotherOwnersSessionOrPublishedPayload(int terminal)
    {
        using var temp = new TempDirectory();
        await using var library = await ObjectStore.OpenLibraryAsync(Folder(temp), Quick);
        var a = Owner(library); var b = Owner(library, "build-b"); var c = Owner(library, "build-c");
        var key = await ObjectStagingContract.AssertFinishedSessionCannotAbandonNewerAsync(library, a, b, c, terminal);
        Assert.Equal(terminal == 2, await a.OwnsAsync(key));
        Assert.False(await b.OwnsAsync(key)); Assert.True(await c.OwnsAsync(key));
    }

    [Fact]
    public async Task AnotherOwnerResumesOnlyProvenChunksAndOnlyTheCommittingOwnerGetsMembership()
    {
        using var temp = new TempDirectory(); var directory = Folder(temp);
        var item = Synthetic.Chunked(2211, Enumerable.Repeat(65_536, 6).ToArray());
        const int prefix = 12 + 24 * 6 + 2 * 65_536;
        await using (var library = await ObjectStore.OpenLibraryAsync(directory, Quick))
        {
            await using (var staging = await Owner(library).StageAsync(item.Key, item.DeclaredLength))
            {
                await staging.AppendAsync(item.Bytes[..(prefix + 91)]);
                await Assert.ThrowsAsync<InvalidOperationException>(() => staging.CommitAsync());
                Assert.False(await library.HasAsync(item.Key)); Assert.False(await Owner(library).OwnsAsync(item.Key));
            }
        }
        Append(Assert.Single(Directory.GetFiles(Path.Combine(directory, "staging"))), new byte[100]);
        await using (var library = await ObjectStore.OpenLibraryAsync(directory, Quick))
        {
            var second = Owner(library, "build-b");
            await using var staging = await second.StageAsync(item.Key, item.DeclaredLength);
            Assert.Equal(prefix, staging.Length);
            await staging.AppendAsync(item.Bytes[(int)staging.Length..]);
            Assert.Equal(PutOutcome.Stored, (await staging.CommitAsync()).Outcome);
            Assert.True(await second.OwnsAsync(item.Key)); Assert.False(await Owner(library).OwnsAsync(item.Key));
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick);
        Assert.True(await Owner(reopened, "build-b").OwnsAsync(item.Key)); Assert.False(await Owner(reopened).OwnsAsync(item.Key));
        Assert.Equal(item.Bytes.ToArray(), (await reopened.ReadAsync(item.Key)).Bytes);
    }

    [Fact]
    public async Task HeaderlessPartialStartsOverAndWrongCompleteKeyIsRefusedWithoutOwnership()
    {
        using var temp = new TempDirectory();
        await using var library = await ObjectStore.OpenLibraryAsync(Folder(temp), Quick);
        var item = Synthetic.Headerless(2212, 2000);
        await using (var staging = await Owner(library).StageAsync(item.Key, item.DeclaredLength)) await staging.AppendAsync(item.Bytes[..1000]);
        await using (var staging = await Owner(library, "build-b").StageAsync(item.Key, item.DeclaredLength))
        {
            Assert.Equal(0, staging.Length); await staging.AppendAsync(item.Bytes);
            Assert.Equal(PutOutcome.Stored, (await staging.CommitAsync()).Outcome);
        }
        Assert.False(await Owner(library).OwnsAsync(item.Key));
        var wrong = Synthetic.Headerless(2213, 2000).Key;
        await using var rejected = await Owner(library).StageAsync(wrong, item.DeclaredLength);
        await rejected.AppendAsync(item.Bytes);
        var refusal = await rejected.CommitAsync();
        Assert.Equal((PutOutcome.Refused, ObjectRefusalCodes.KeyMismatch), (refusal.Outcome, refusal.Code));
        Assert.False(await library.HasAsync(wrong)); Assert.False(await Owner(library).OwnsAsync(wrong));
    }

    [Fact]
    public async Task StagingBudgetsAndOneActiveKeyAreSharedAcrossOwnersButDisposingNeverClosesTheLibrary()
    {
        using var temp = new TempDirectory();
        await using var library = await ObjectStore.OpenLibraryAsync(Folder(temp), Quick with { MaxStagings = 2, MaxStagingBytes = 4000 });
        var a = Synthetic.Headerless(2214, 2000); var b = Synthetic.Headerless(2215, 2000); var c = Synthetic.Headerless(2216, 2000);
        var first = Owner(library); var second = Owner(library, "build-b");
        await using (var staging = await first.StageAsync(a.Key, a.DeclaredLength))
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => second.StageAsync(a.Key, a.DeclaredLength));
            Assert.Equal(ObjectStoreCodes.Limit, (await Assert.ThrowsAsync<ObjectStoreException>(() => second.StageAsync(b.Key, 3000))).Code);
            await using var other = await second.StageAsync(b.Key, b.DeclaredLength);
            Assert.Equal(ObjectStoreCodes.Limit, (await Assert.ThrowsAsync<ObjectStoreException>(() => first.StageAsync(c.Key, c.DeclaredLength))).Code);
            await staging.AbandonAsync();
            await using var freed = await first.StageAsync(c.Key, c.DeclaredLength);
        }
        await first.PutAsync([a], true); Assert.True(await first.OwnsAsync(a.Key));
        await using var resumed = await second.StageAsync(b.Key, b.DeclaredLength);
        await resumed.AppendAsync(b.Bytes); Assert.Equal(PutOutcome.Stored, (await resumed.CommitAsync()).Outcome);
        Assert.True(await second.OwnsAsync(b.Key));
        Assert.Equal(ObjectStoreCodes.LibraryOperationUnsupported, (await Assert.ThrowsAsync<ObjectStoreException>(() => library.StageAsync(c.Key, c.DeclaredLength))).Code);
        await Assert.ThrowsAsync<ArgumentException>(() => first.StageAsync(Synthetic.Config(1).Key, 100));
        await Assert.ThrowsAsync<ArgumentException>(() => first.StageAsync(new ObjectKey(ObjectKeyType.Generated, a.Key.KeyBytes()), 100));
    }

    [Fact]
    public async Task DuplicateStagedCommitClaimsExistingBytesDurablyWithoutAppendingAnotherPayload()
    {
        using var temp = new TempDirectory(); var directory = Folder(temp); var item = Synthetic.Headerless(2217, 3000);
        await using (var library = await ObjectStore.OpenLibraryAsync(directory, Quick))
        {
            await Owner(library).PutAsync([item], true); var before = PackBytes(directory);
            var second = Owner(library, "build-b");
            await using var staging = await second.StageAsync(item.Key, item.DeclaredLength);
            await staging.AppendAsync(item.Bytes); Assert.Equal(PutOutcome.AlreadyPresent, (await staging.CommitAsync()).Outcome);
            Assert.True(await second.OwnsAsync(item.Key)); Assert.Equal(before, PackBytes(directory));
            Assert.Equal(1, (await library.StatsAsync()).Objects);
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick);
        Assert.True(await Owner(reopened).OwnsAsync(item.Key)); Assert.True(await Owner(reopened, "build-b").OwnsAsync(item.Key));
    }

    [Fact]
    public async Task DuplicateStagingBehindAPendingWinnerCommitsBothOwnersWithoutDuplicatingBytes()
    {
        using var temp = new TempDirectory(); var directory = Folder(temp); var item = Synthetic.Headerless(2218, 3000);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim();
        var armed = 0;
        var options = Quick with { CommitDelay = TimeSpan.FromSeconds(10), Fault = point =>
        {
            if (point != StorePoint.EntryWritten || Interlocked.Exchange(ref armed, 0) != 1) return;
            entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Pending winner test was not released.");
        } };
        await using var library = await ObjectStore.OpenLibraryAsync(directory, options);
        await using var staging = await Owner(library, "build-b").StageAsync(item.Key, item.DeclaredLength);
        await staging.AppendAsync(item.Bytes); Volatile.Write(ref armed, 1);
        var putting = Owner(library).PutAsync([item]);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var committing = staging.CommitAsync(); release.Set();
            Assert.Equal(PutOutcome.AlreadyPresent, (await committing.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
            Assert.Equal(PutOutcome.Stored, Assert.Single(await putting).Outcome);
            Assert.True(await Owner(library).OwnsAsync(item.Key)); Assert.True(await Owner(library, "build-b").OwnsAsync(item.Key));
            Assert.Equal(PackFormat.PackHeaderBytes + PackFormat.EntryBytes(item.DeclaredLength), PackBytes(directory));
        }
        finally { release.Set(); await putting; }
    }

    [Fact]
    public async Task PreCanceledAdmissionAndCommitDoNotPublishAndTheCompleteStagingCanRetry()
    {
        using var temp = new TempDirectory();
        await using var library = await ObjectStore.OpenLibraryAsync(Folder(temp), Quick);
        var owner = Owner(library); var item = Synthetic.Headerless(2219, 2000);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => owner.StageAsync(item.Key, item.DeclaredLength, ct: canceled.Token));
        Assert.False(await library.HasAsync(item.Key)); Assert.False(await owner.OwnsAsync(item.Key));
        await using var staging = await owner.StageAsync(item.Key, item.DeclaredLength); await staging.AppendAsync(item.Bytes);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => staging.CommitAsync(canceled.Token));
        Assert.False(await library.HasAsync(item.Key)); Assert.False(await owner.OwnsAsync(item.Key));
        Assert.Equal(PutOutcome.Stored, (await staging.CommitAsync()).Outcome); Assert.True(await owner.OwnsAsync(item.Key));
    }

    [Theory]
    [InlineData(nameof(StorePoint.EntryHalfWritten), false)]
    [InlineData(nameof(StorePoint.StagedBeforePublish), false)]
    [InlineData(nameof(StorePoint.InsideIndexCommit), true)]
    [InlineData(nameof(StorePoint.AfterIndexCommit), true)]
    public async Task CancellationReturnsTheRealPublicationOutcomeAndRetainsOnlyCommittedOwners(string at, bool committed)
    {
        using var temp = new TempDirectory(); var directory = Folder(temp); using var canceled = new CancellationTokenSource(); var armed = false;
        var point = Enum.Parse<StorePoint>(at);
        var options = Quick with { Fault = current => { if (armed && current == point) canceled.Cancel(); } };
        var item = Synthetic.Chunked(2220, 1 << 20, 1 << 20, 1 << 20); var existing = Synthetic.Headerless(2221, 1000);
        await using (var library = await ObjectStore.OpenLibraryAsync(directory, options))
        {
            var owner = Owner(library); await owner.PutAsync([existing], true);
            await using var staging = await owner.StageAsync(item.Key, item.DeclaredLength); await staging.AppendAsync(item.Bytes); armed = true;
            Assert.Equal(committed ? PutOutcome.Stored : PutOutcome.Canceled, (await staging.CommitAsync(canceled.Token)).Outcome);
            Assert.Equal(committed, await library.HasAsync(item.Key)); Assert.Equal(committed, await owner.OwnsAsync(item.Key));
            Assert.True(await owner.OwnsAsync(existing.Key)); Assert.Equal(existing.Bytes.ToArray(), (await library.ReadAsync(existing.Key)).Bytes);
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick);
        Assert.Equal(committed, await Owner(reopened).OwnsAsync(item.Key)); Assert.True(await Owner(reopened).OwnsAsync(existing.Key));
        if (!committed)
        {
            var retryOwner = Owner(reopened, "retry"); await using var retry = await retryOwner.StageAsync(item.Key, item.DeclaredLength);
            await retry.AppendAsync(item.Bytes[(int)retry.Length..]); Assert.Equal(PutOutcome.Stored, (await retry.CommitAsync()).Outcome);
            Assert.True(await retryOwner.OwnsAsync(item.Key)); Assert.False(await Owner(reopened).OwnsAsync(item.Key));
        }
    }

    [Fact]
    public async Task CancelingANewStagedCopyPreservesAnEarlierPendingObjectAndItsOwner()
    {
        using var temp = new TempDirectory(); using var canceled = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim();
        var gateNextEntry = 0;
        var options = Quick with { Fault = point =>
        {
            if (point == StorePoint.StagedBeforePublish) canceled.Cancel();
            if (point != StorePoint.EntryWritten || Interlocked.Exchange(ref gateNextEntry, 0) != 1) return;
            entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Pending preservation test was not released.");
        } };
        await using var library = await ObjectStore.OpenLibraryAsync(Folder(temp), options);
        var earlier = Synthetic.Headerless(2224, 4000); var interrupted = Synthetic.Chunked(2225, 100_000, 100_000);
        await using var staging = await Owner(library, "interrupted").StageAsync(interrupted.Key, interrupted.DeclaredLength);
        await staging.AppendAsync(interrupted.Bytes); Volatile.Write(ref gateNextEntry, 1);
        var putting = Owner(library).PutAsync([earlier]);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var committing = staging.CommitAsync(canceled.Token); release.Set();
            Assert.Equal(PutOutcome.Canceled, (await committing.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
            Assert.Equal(PutOutcome.Stored, Assert.Single(await putting.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
            Assert.Equal(earlier.Bytes.ToArray(), (await library.ReadAsync(earlier.Key)).Bytes);
            Assert.True(await Owner(library).OwnsAsync(earlier.Key));
            Assert.False(await library.HasAsync(interrupted.Key)); Assert.False(await Owner(library, "interrupted").OwnsAsync(interrupted.Key));
        }
        finally { release.Set(); await putting; }
    }

    [Fact]
    public async Task LibraryCloseDrainsAcceptedStagedPublicationAndRejectsNewOwnerAdmissions()
    {
        using var temp = new TempDirectory(); var directory = Folder(temp);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); using var release = new ManualResetEventSlim();
        var armed = false;
        var options = Quick with { Fault = point =>
        {
            if (!armed || point != StorePoint.InsideIndexCommit) return;
            entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Close drain test was not released.");
        } };
        var library = await ObjectStore.OpenLibraryAsync(directory, options); var owner = Owner(library);
        var item = Synthetic.Chunked(2226, 100_000, 100_000);
        await using var staging = await owner.StageAsync(item.Key, item.DeclaredLength); await staging.AppendAsync(item.Bytes); armed = true;
        var committing = staging.CommitAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var closing = library.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted);
            Assert.Equal(ObjectStoreCodes.Closed, (await Assert.ThrowsAsync<ObjectStoreException>(() => owner.StageAsync(item.Key, item.DeclaredLength))).Code);
            release.Set();
            Assert.Equal(PutOutcome.Stored, (await committing.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
            await closing.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { release.Set(); await committing; await library.DisposeAsync(); }
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick);
        Assert.True(await Owner(reopened).OwnsAsync(item.Key)); Assert.Equal(item.Bytes.ToArray(), (await reopened.ReadAsync(item.Key)).Bytes);
    }

    [Fact]
    public async Task LateCommittedStagingCleanupAfterLibraryCloseCannotDeleteANewStoresSession()
    {
        using var temp = new TempDirectory(); var directory = Folder(temp);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var armed = false;
        var options = Quick with { Fault = point =>
        {
            if (!armed || point != StorePoint.InsideIndexCommit) return;
            entered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Late cleanup test was not released.");
        } };
        var item = Synthetic.Chunked(2299, 65_536, 65_536, 65_536);
        var oldStore = await ObjectStore.OpenLibraryAsync(directory, options);
        var staging = await Owner(oldStore).StageAsync(item.Key, item.DeclaredLength);
        await staging.AppendAsync(item.Bytes); armed = true;
        var context = new QueuedCommitContext();
        Task<PutResult> committing;
        var previous = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            committing = staging.CommitAsync();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var closing = oldStore.DisposeAsync().AsTask();
            release.Set();
            await context.Posted.WaitAsync(TimeSpan.FromSeconds(10));
            await closing.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(committing.IsCompleted);
            await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick);
            var b = Owner(reopened, "build-b");
            // A's physically committed staging file may remain until its held continuation runs.
            // B owns this current session and may abandon it before starting fresh bytes.
            await using (var inherited = await b.StageAsync(item.Key, item.DeclaredLength)) await inherited.AbandonAsync();
            const int header = 12 + 24 * 3;
            await using (var fresh = await b.StageAsync(item.Key, item.DeclaredLength))
            {
                await fresh.AppendAsync(item.Bytes[..(header + 2 * 65_536 + 73)]);
                await context.PumpUntilAsync(committing);
                Assert.Equal(PutOutcome.Stored, (await committing).Outcome);
            }
            var c = Owner(reopened, "build-c");
            await using var resumed = await c.StageAsync(item.Key, item.DeclaredLength);
            Assert.Equal(header + 2 * 65_536, resumed.Length);
            await resumed.AppendAsync(item.Bytes[(int)resumed.Length..]);
            Assert.Equal(PutOutcome.AlreadyPresent, (await resumed.CommitAsync()).Outcome);
            Assert.True(await Owner(reopened).OwnsAsync(item.Key)); Assert.False(await b.OwnsAsync(item.Key)); Assert.True(await c.OwnsAsync(item.Key));
            Assert.Equal(item.Bytes.ToArray(), (await reopened.ReadAsync(item.Key)).Bytes);
        }
        finally
        {
            release.Set(); await oldStore.DisposeAsync();
            await context.PumpUntilAsync(committing);
        }
    }

    private sealed class QueuedCommitContext : SynchronizationContext
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> callbacks = new();
        private readonly SemaphoreSlim available = new(0);
        private readonly TaskCompletionSource posted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Posted => posted.Task;
        public override void Post(SendOrPostCallback callback, object? state)
        {
            callbacks.Enqueue((callback, state)); available.Release(); posted.TrySetResult();
        }
        public async Task PumpUntilAsync(Task task)
        {
            while (!task.IsCompleted)
            {
                if (!await available.WaitAsync(TimeSpan.FromSeconds(10))) throw new TimeoutException("Held commit continuation did not complete.");
                if (!callbacks.TryDequeue(out var next)) throw new InvalidOperationException("Queued context lost a callback.");
                var previous = Current;
                try { SetSynchronizationContext(this); next.Callback(next.State); }
                finally { SetSynchronizationContext(previous); }
            }
        }
    }

    [Theory]
    [InlineData(nameof(StorePoint.EntryWritten), false)]
    [InlineData(nameof(StorePoint.InsideIndexCommit), false)]
    [InlineData(nameof(StorePoint.AfterIndexCommit), true)]
    public async Task CrashNeverAdoptsAnOwnerlessTailAndKeepsPayloadAndClaimAtomic(string at, bool committed)
    {
        using var temp = new TempDirectory(); var directory = Folder(temp); var point = Enum.Parse<StorePoint>(at); var armed = false;
        var item = Synthetic.Chunked(2222, 100_000, 100_000); var existing = Synthetic.Headerless(2223, 1000);
        var options = Quick with { Fault = current => { if (armed && current == point) throw new SimulatedCrashException(current); } };
        await using (var library = await ObjectStore.OpenLibraryAsync(directory, options))
        {
            await Owner(library).PutAsync([existing], true);
            await using var staging = await Owner(library, "interrupted").StageAsync(item.Key, item.DeclaredLength); await staging.AppendAsync(item.Bytes); armed = true;
            await Assert.ThrowsAsync<ObjectStoreException>(() => staging.CommitAsync());
        }
        await using var reopened = await ObjectStore.OpenLibraryAsync(directory, Quick);
        Assert.Equal(committed, await reopened.HasAsync(item.Key)); Assert.Equal(committed, await Owner(reopened, "interrupted").OwnsAsync(item.Key));
        Assert.True(await Owner(reopened).OwnsAsync(existing.Key)); Assert.False(reopened.OpenReport.IndexRebuilt); Assert.Equal(0, reopened.OpenReport.RecoveredEntries);
        if (committed)
        {
            var before = PackBytes(directory); await using var retry = await Owner(reopened, "retry").StageAsync(item.Key, item.DeclaredLength);
            await retry.AppendAsync(item.Bytes[(int)retry.Length..]); Assert.Equal(PutOutcome.AlreadyPresent, (await retry.CommitAsync()).Outcome);
            Assert.True(await Owner(reopened, "retry").OwnsAsync(item.Key)); Assert.Equal(before, PackBytes(directory));
        }
    }
}
