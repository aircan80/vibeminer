using System.Buffers.Binary;
using System.Text;
using VibeMiner.Builds.Testing;
using VibeMiner.Cdn;
using VibeMiner.ObjectAccess;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;
using Xunit;

namespace VibeMiner.Builds.Tests;

/// <summary>Layer 3 Download all over the in-memory layer 2 (D137): real opened builds and small hand-made ledgers.</summary>
public sealed class DownloadAllTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(10);
    private static CancellationToken Ct => CancellationToken.None;
    private static readonly CdnRoute Route = new(["us.cdn.blizzard.com"], "tpr/wow");
    private readonly InMemoryObjectSource inner = new();
    private readonly Recording source;

    public DownloadAllTests() => source = new Recording(inner);

    private static ValueTask<long> Plenty(CancellationToken _) => ValueTask.FromResult(long.MaxValue);
    private static ObjectKey Encoded(CdnKey key) => new(ObjectKeyType.Encoded, key.ToBytes());

    private static CdnKey Key(int i)
    {
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)i + 1);
        return new CdnKey(bytes);
    }

    /// <summary>A build of <paramref name="count"/> ledger rows sized 10 + i, each fetchable from the fake; <paramref name="stored"/> are already stored.</summary>
    private LedgerBuild Ledger(int count, IEnumerable<int>? stored = null, bool withCdn = true, Func<int, long>? size = null,
        Dictionary<int, (ArchiveLocation Location, int Archive)>? archived = null)
    {
        var sizes = Enumerable.Range(0, count).Select(i => size?.Invoke(i) ?? 10 + i).ToArray();
        var keys = Enumerable.Range(0, count).Select(Key).ToArray();
        var storedSet = (stored ?? []).ToHashSet();
        for (var i = 0; i < count; i++)
        {
            if (sizes[i] > 1 << 20) continue;
            var bytes = new byte[sizes[i]];
            if (storedSet.Contains(i)) inner.AddStored(Encoded(keys[i]), bytes); else inner.Add(Encoded(keys[i]), bytes);
        }
        var archives = archived?.Values.Select(v => (v.Archive, v.Location.Archive)).Distinct().OrderBy(a => a.Item1).Select(a => a.Item2).ToArray();
        return new LedgerBuild(new BuildRef("wow", new string('a', 32), new string('b', 32)), new ObjectLedger(keys, sizes),
            withCdn ? new CdnContext(Route, archives) : null, (archived ?? []).ToDictionary(p => keys[p.Key], p => p.Value));
    }

    private async Task<IBuild> OpenSyntheticAsync(SyntheticBuild b)
    {
        inner.AddReply("wow", "us", ObservationKind.Cdns,
            Encoding.UTF8.GetBytes("Name!STRING:0|Path!STRING:0|Hosts!STRING:0|Servers!STRING:0|ConfigPath!STRING:0\n## seqn = 1\nus|tpr/wow|us.cdn.blizzard.com|x|tpr/configs/data\n"));
        Assert.Equal(ObjectState.Present, (await inner.ObserveAsync(new(ObservationKind.Cdns, "wow", "us"), Ct)).State);
        var result = await new BuildOpener(inner).OpenAsync(b.Ref, Ct);
        Assert.True(result.State == BuildOpenState.Open, $"{result.State} {result.Code}");
        Assert.NotNull(result.Opened!.Cdn);
        return result.Opened;
    }

    private async Task<bool> StoredAsync(CdnKey key) => (await inner.HasKeysAsync([Encoded(key)], Ct))[0];

    private static Task<DownloadAllResult> Done(DownloadAllJob job) => job.Completion.WaitAsync(Guard);

    [Fact]
    public async Task FetchesEveryLedgerObjectOfAnOpenedBuildAtBackgroundInArchiveOrder()
    {
        var b = new SyntheticBuild().LoadInto(inner);
        var build = await OpenSyntheticAsync(b);
        var ledger = ((OpenedBuild)build).Ledger;
        for (var i = 0; i < ledger.Count; i++)
            if (!await StoredAsync(ledger.Key(i))) inner.Add(Encoded(ledger.Key(i)), new byte[ledger.Size(i)]);
        await using var jobs = new DownloadAllJobs(source, Plenty, new() { ScanBatchKeys = 2, FetchBatch = 2 });

        var result = await Done(jobs.Start(build));

        Assert.Equal(DownloadAllOutcome.Completed, result.Outcome);
        for (var i = 0; i < ledger.Count; i++) Assert.True(await StoredAsync(ledger.Key(i)));
        Assert.Equal(new DownloadAllProgress(b.Ref, DownloadAllStage.Fetching, ledger.Count, ledger.Count, ledger.TotalBytes, ledger.TotalBytes, 0,
            ledger.TotalBytes - b.Objects[b.RootKey].Length), result.Final);
        Assert.All(source.Requests, r => Assert.Equal(FetchPriority.Background, r.Priority));
        // ROOT was stored by the open, so it's never asked. 100 enUS sits in the first archive, 200 in the second; the rest are loose.
        var asked = source.Requests.Select(r => r.Request).ToArray();
        Assert.Equal(ledger.Count - 1, asked.Length);
        Assert.DoesNotContain(asked, r => r.Key == b.RootKey);
        var file100 = b.Variants(100).Single(v => v.LocaleFlags == SyntheticBuild.EnUs);
        var file200 = b.Variants(200).Single();
        Assert.Equal([Encoded(file100.EKey!.Value), Encoded(file200.EKey!.Value)], asked.Take(2).Select(r => r.Key));
        Assert.Equal([file100.Location!.Value], asked[0].Locations);
        Assert.Equal([file200.Location!.Value], asked[1].Locations);
        Assert.All(asked.Skip(2), r => Assert.Null(r.Locations));
        Assert.All(asked, r => Assert.Equal(ledger.Size(ledger.IndexOf(new CdnKey(r.Key.KeyBytes()))), r.DeclaredLength));
        Assert.Null(jobs.Find(b.Ref));
    }

    [Fact]
    public async Task NeverAsksForWhatIsAlreadyStored()
    {
        var build = Ledger(10, stored: [0, 3, 4, 9]);
        await using var jobs = new DownloadAllJobs(source, Plenty, new() { ScanBatchKeys = 3, FetchBatch = 4 });
        var result = await Done(jobs.Start(build));
        Assert.Equal(DownloadAllOutcome.Completed, result.Outcome);
        Assert.Equal([1, 2, 5, 6, 7, 8], source.Requests.Select(r => Row(r.Request)));
        Assert.Equal(10, result.Final.StoredObjects);
        Assert.Equal(build.Ledger.TotalBytes, result.Final.StoredBytes);
        Assert.Equal(new[] { 4, 2 }, source.Batches);
    }

    [Fact]
    public async Task AnEverythingStoredBuildCompletesWithoutAskingOrProbingSpace()
    {
        var build = Ledger(5, stored: Enumerable.Range(0, 5));
        var probed = false;
        await using var jobs = new DownloadAllJobs(source, _ => { probed = true; return ValueTask.FromResult(0L); });
        var result = await Done(jobs.Start(build));
        Assert.Equal(DownloadAllOutcome.Completed, result.Outcome);
        Assert.Empty(source.Requests);
        Assert.False(probed);
    }

    [Fact]
    public async Task AKilledJobResumesFromWhatTheStoreHolds()
    {
        var build = Ledger(10);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.BeforeAnswer = async (key, token) =>
        {
            if (key != Encoded(Key(4))) return;
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        var jobs = new DownloadAllJobs(source, Plenty, new() { FetchBatch = 2 });
        var job = jobs.Start(build);
        await reached.Task.WaitAsync(Guard);
        // Process exit: the owner goes away mid-batch.
        await jobs.DisposeAsync();
        var killed = await Done(job);
        Assert.Equal(DownloadAllOutcome.Canceled, killed.Outcome);
        Assert.Equal(4, killed.Final.StoredObjects);

        inner.BeforeAnswer = null;
        source.Clear();
        await using var again = new DownloadAllJobs(source, Plenty, new() { FetchBatch = 2 });
        var resumed = await Done(again.Start(build));
        Assert.Equal(DownloadAllOutcome.Completed, resumed.Outcome);
        Assert.Equal([4, 5, 6, 7, 8, 9], source.Requests.Select(r => Row(r.Request)));
    }

    [Fact]
    public async Task LargeObjectsAreAskedWithTheirDeclaredLength()
    {
        const long large = 5L << 30;
        var build = Ledger(3, size: i => i == 1 ? large : 10 + i);
        source.Answer = r => r.DeclaredLength == large ? new ObjectResult(r.Key, ObjectState.Present, ServedFrom.Cdn, Length: large) : null;
        await using var jobs = new DownloadAllJobs(source, Plenty, new() { FreeReserveBytes = 0 });
        var result = await Done(jobs.Start(build));
        Assert.Equal(DownloadAllOutcome.Completed, result.Outcome);
        Assert.Equal([10L, large, 12L], source.Requests.Select(r => r.Request.DeclaredLength!.Value));
        Assert.Equal(large + 22, result.Final.StoredBytes);
    }

    [Fact]
    public async Task NotEnoughFreeSpaceEndsAsDiskFullBeforeAnyFetch()
    {
        var build = Ledger(4, stored: [0]);
        var missing = 11 + 12 + 13;
        await using var jobs = new DownloadAllJobs(source, _ => ValueTask.FromResult(missing + 100 - 1L), new() { FreeReserveBytes = 100 });
        var result = await Done(jobs.Start(build));
        Assert.Equal((DownloadAllOutcome.DiskFull, "DOWNLOAD_ALL_NOT_ENOUGH_SPACE"), (result.Outcome, result.Code));
        Assert.Empty(source.Requests);
        Assert.False(await StoredAsync(Key(1)));

        await using var enough = new DownloadAllJobs(source, _ => ValueTask.FromResult(missing + 100L), new() { FreeReserveBytes = 100 });
        Assert.Equal(DownloadAllOutcome.Completed, (await Done(enough.Start(build))).Outcome);
    }

    [Fact]
    public async Task AFreeSpaceProbeThatThrowsIsAStoreFailure()
    {
        var build = Ledger(2);
        await using var jobs = new DownloadAllJobs(source, _ => throw new IOException("no statfs"));
        var result = await Done(jobs.Start(build));
        Assert.Equal((DownloadAllOutcome.StoreFailed, "DOWNLOAD_ALL_FREE_SPACE_UNKNOWN"), (result.Outcome, result.Code));
        Assert.Empty(source.Requests);
    }

    [Fact]
    public async Task ADiskFullStoreFailureStopsTheJob()
    {
        var build = Ledger(10);
        inner.StoreFailed(Encoded(Key(2)));
        await using var jobs = new DownloadAllJobs(source, Plenty, new() { FetchBatch = 2 });
        var result = await Done(jobs.Start(build));
        Assert.Equal((DownloadAllOutcome.DiskFull, ObjectStoreCodes.DiskFull), (result.Outcome, result.Code));
        Assert.Equal([0, 1, 2, 3], source.Requests.Select(r => Row(r.Request)));
        // The batch's other answers still count.
        Assert.Equal(3, result.Final.StoredObjects);
    }

    [Fact]
    public async Task AnotherStoreFailureStopsTheJobAsStoreFailed()
    {
        var build = Ledger(6);
        inner.StoreFailed(Encoded(Key(1)), ObjectStoreCodes.WriteFailed);
        await using var jobs = new DownloadAllJobs(source, Plenty, new() { FetchBatch = 2 });
        var result = await Done(jobs.Start(build));
        Assert.Equal((DownloadAllOutcome.StoreFailed, ObjectStoreCodes.WriteFailed), (result.Outcome, result.Code));
        Assert.Equal(2, source.Requests.Count);
    }

    [Fact]
    public async Task FailedObjectsAreCountedAndTheJobRunsOnAsIncomplete()
    {
        var build = Ledger(8);
        inner.NotFound(Encoded(Key(1))).Refuse(Encoded(Key(4)), "OBJECT_KEY_MISMATCH").Unavailable(Encoded(Key(5)), "CDN_TIMEOUT");
        await using var jobs = new DownloadAllJobs(source, Plenty, new() { FetchBatch = 3 });
        var result = await Done(jobs.Start(build));
        Assert.Equal(DownloadAllOutcome.Incomplete, result.Outcome);
        Assert.Equal((1L, 1L, 1L, 0L, "CDN_NOT_FOUND"), (result.NotFound, result.Unavailable, result.Refused, result.Unsupported, result.Code));
        Assert.DoesNotContain("unsupported", result.Message);
        Assert.Equal(8, source.Requests.Count);
        Assert.Equal((5L, 3L), (result.Final.StoredObjects, result.Final.FailedObjects));
        foreach (var i in new[] { 0, 2, 3, 6, 7 }) Assert.True(await StoredAsync(Key(i)));
    }

    [Fact]
    public async Task AnArchiveEntryOver64MiBIsCountedAsUnsupportedNotRefused()
    {
        const long big = (64L << 20) + 1;
        var build = Ledger(3, size: i => i == 1 ? big : 10 + i, archived: new() { [1] = (new(Key(1000), 0, big), 0) });
        await using var jobs = new DownloadAllJobs(source, Plenty);
        var result = await Done(jobs.Start(build));
        Assert.Equal(DownloadAllOutcome.Incomplete, result.Outcome);
        Assert.Equal((0L, 0L, 0L, 1L, "OBJECT_LOCATION_TOO_LARGE"), (result.NotFound, result.Unavailable, result.Refused, result.Unsupported, result.Code));
        Assert.Equal((2L, 1L), (result.Final.StoredObjects, result.Final.FailedObjects));
        Assert.Contains("1 of them unsupported", result.Message);
    }

    [Fact]
    public async Task NoCdnContextNeedsTheNetworkAndAsksNothing()
    {
        var build = Ledger(3, withCdn: false);
        await using var jobs = new DownloadAllJobs(source, Plenty);
        var result = await Done(jobs.Start(build));
        Assert.Equal((DownloadAllOutcome.NeedsNetwork, "OBJECT_ACCESS_NO_CDN_CONTEXT"), (result.Outcome, result.Code));
        Assert.Empty(source.Requests);
        Assert.Equal(0, inner.HasKeysCalls);
    }

    [Fact]
    public async Task ANeedsNetworkAnswerStopsTheJob()
    {
        var build = Ledger(6);
        inner.NeedsNetwork(Encoded(Key(0)));
        await using var jobs = new DownloadAllJobs(source, Plenty, new() { FetchBatch = 2 });
        var result = await Done(jobs.Start(build));
        Assert.Equal(DownloadAllOutcome.NeedsNetwork, result.Outcome);
        Assert.Equal(2, source.Requests.Count);
    }

    [Fact]
    public async Task AnOfflineCdnStopsTheJobAsNeedsNetwork()
    {
        var build = Ledger(6);
        inner.Unavailable(Encoded(Key(2)));
        await using var jobs = new DownloadAllJobs(source, Plenty, new() { FetchBatch = 2 });
        var result = await Done(jobs.Start(build));
        Assert.Equal((DownloadAllOutcome.NeedsNetwork, "CDN_OFFLINE"), (result.Outcome, result.Code));
        Assert.Equal(4, source.Requests.Count);
    }

    [Fact]
    public async Task TurningItOffKeepsWhatWasStoredAndLeavesNothingRunning()
    {
        var build = Ledger(6);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.BeforeAnswer = async (key, token) =>
        {
            if (key != Encoded(Key(2))) return;
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        await using var jobs = new DownloadAllJobs(source, Plenty, new() { FetchBatch = 2 });
        Assert.Null(await jobs.StopAsync(build.Build));
        var job = jobs.Start(build);
        await reached.Task.WaitAsync(Guard);

        var result = await jobs.StopAsync(build.Build);

        Assert.NotNull(result);
        Assert.Equal(DownloadAllOutcome.Canceled, result.Outcome);
        Assert.True(job.Completion.IsCompletedSuccessfully);
        Assert.Same(result, await job.Completion);
        Assert.Null(jobs.Find(build.Build));
        Assert.True(await StoredAsync(Key(0)));
        Assert.True(await StoredAsync(Key(1)));
        // The held batch is rows 2 and 3; nothing after it was asked.
        var asked = source.Requests.Count;
        Assert.Equal(4, asked);
        Assert.Same(result, await jobs.StopAsync(build.Build));
        Assert.Equal(result.Final, job.Latest);
        Assert.Equal(asked, source.Requests.Count);
    }

    [Fact]
    public async Task InteractiveFetchesFinishWhileTheJobIsHeld()
    {
        var build = Ledger(4);
        var interactive = new ObjectKey(ObjectKeyType.Encoded, Key(100).ToBytes());
        inner.Add(interactive, [1, 2, 3]);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.BeforeAnswer = async (key, token) =>
        {
            if (key == interactive) return;
            reached.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        };
        await using var jobs = new DownloadAllJobs(source, Plenty);
        var job = jobs.Start(build);
        await reached.Task.WaitAsync(Guard);
        var got = await source.GetKeyAsync(new(interactive), new CdnContext(Route), FetchPriority.Interactive, Ct).WaitAsync(Guard);
        Assert.True(got.IsPresent);
        Assert.False(job.Completion.IsCompleted);
        Assert.Equal(DownloadAllOutcome.Canceled, (await jobs.StopAsync(build.Build))!.Outcome);
    }

    [Fact]
    public async Task ProgressIsCoalescedAndEndsWithTheFinalSnapshot()
    {
        var build = Ledger(6);
        var clock = new ManualClock();
        var interval = TimeSpan.FromMilliseconds(100);
        // Batches of one: time passes only while row 3 is answered.
        inner.BeforeAnswer = (key, _) => { if (key == Encoded(Key(3))) clock.Advance(interval); return Task.CompletedTask; };
        var reports = new Reports();
        await using var jobs = new DownloadAllJobs(source, Plenty, new() { ScanBatchKeys = 1, FetchBatch = 1, ProgressInterval = interval, Time = clock });
        var result = await Done(jobs.Start(build, reports));

        var seen = reports.All;
        Assert.Equal([DownloadAllStage.Scanning, DownloadAllStage.Fetching, DownloadAllStage.Fetching, DownloadAllStage.Fetching], seen.Select(p => p.Stage));
        Assert.Equal(0, seen[0].StoredObjects);
        Assert.Equal(6, seen[0].TotalObjects);
        // Layer 2's report during row 3's batch: three rows stored before it plus row 3 finished, and the CDN bytes so far.
        Assert.Equal(4, seen[2].StoredObjects);
        Assert.Equal(10 + 11 + 12, seen[2].StoredBytes);
        Assert.Equal(10 + 11 + 12 + 13, seen[2].CdnBytes);
        Assert.Equal(result.Final, seen[^1]);
        Assert.Equal(result.Final, (await jobs.StopAsync(build.Build))!.Final);
        Assert.Equal(build.Ledger.TotalBytes, result.Final.CdnBytes);
    }

    [Fact]
    public async Task ASlowSubscriberNeverHoldsUpLayerTwosProgressCallback()
    {
        var build = Ledger(4);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var reports = new Reports(first =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(Guard));
        });
        var sourceReports = 0;
        var allReported = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.AfterReport = _ => { if (Interlocked.Increment(ref sourceReports) == 4) allReported.TrySetResult(); };
        await using var jobs = new DownloadAllJobs(source, Plenty, new() { FetchBatch = 2, ProgressInterval = TimeSpan.Zero });
        var job = jobs.Start(build, reports);
        await entered.Task.WaitAsync(Guard);

        // The subscriber is stuck in its first report, yet every layer 2 report returned and the job fetched everything.
        await allReported.Task.WaitAsync(Guard);
        Assert.Equal(4, source.Requests.Count);
        Assert.False(job.Completion.IsCompleted);

        release.Set();
        var result = await Done(job);
        var seen = reports.All;
        Assert.Equal(DownloadAllStage.Scanning, seen[0].Stage);
        Assert.Contains(seen, p => p.Stage == DownloadAllStage.Fetching);
        Assert.Equal(result.Final, seen[^1]);
        // Never an older snapshot after a newer one.
        for (var i = 1; i < seen.Count; i++)
        {
            Assert.True(seen[i].StoredObjects >= seen[i - 1].StoredObjects);
            Assert.True(seen[i].CdnBytes >= seen[i - 1].CdnBytes);
            Assert.True(seen[i].Stage >= seen[i - 1].Stage);
        }
    }

    [Fact]
    public async Task ProgressMovesWithinABatch()
    {
        var build = Ledger(4);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.BeforeAnswer = async (key, token) =>
        {
            if (key != Encoded(Key(2))) return;
            reached.TrySetResult();
            await hold.Task.WaitAsync(token);
        };
        var reports = new Reports();
        var moved = reports.When(p => p.Stage == DownloadAllStage.Fetching && p.StoredObjects == 2);
        await using var jobs = new DownloadAllJobs(source, Plenty, new() { FetchBatch = 4, ProgressInterval = TimeSpan.Zero });
        var job = jobs.Start(build, reports);
        await reached.Task.WaitAsync(Guard);

        // One batch of four, held at its third object: the two finished ones already show, their bytes don't yet.
        var during = await moved.WaitAsync(Guard);
        Assert.Equal((0L, 10L + 11), (during.StoredBytes, during.CdnBytes));
        Assert.Equal(2, job.Latest.StoredObjects);

        hold.SetResult();
        var result = await Done(job);
        Assert.Equal((4L, build.Ledger.TotalBytes), (result.Final.StoredObjects, result.Final.StoredBytes));
        Assert.Equal(result.Final, reports.All[^1]);
    }

    [Fact]
    public async Task InBatchProgressNeverGoesBackOrCountsAFailureTwice()
    {
        var build = Ledger(4);
        inner.NotFound(Encoded(Key(1)));
        var reports = new Reports();
        await using var jobs = new DownloadAllJobs(source, Plenty, new() { FetchBatch = 2, ProgressInterval = TimeSpan.Zero });
        var result = await Done(jobs.Start(build, reports));

        Assert.Equal(DownloadAllOutcome.Incomplete, result.Outcome);
        Assert.Equal((3L, 1L), (result.Final.StoredObjects, result.Final.FailedObjects));
        var seen = reports.All.Where(p => p.Stage == DownloadAllStage.Fetching).ToArray();
        for (var i = 1; i < seen.Length; i++)
        {
            Assert.True(seen[i].StoredObjects >= seen[i - 1].StoredObjects);
            Assert.True(seen[i].FailedObjects >= seen[i - 1].FailedObjects);
            Assert.True(seen[i].StoredObjects + seen[i].FailedObjects >= seen[i - 1].StoredObjects + seen[i - 1].FailedObjects);
        }
        Assert.All(seen, p => Assert.InRange(p.StoredObjects + p.FailedObjects, 0, 4));
    }

    [Fact]
    public async Task ADiskFullStoreExceptionFromTheSourceEndsDiskFull()
    {
        var build = Ledger(4);
        source.Answer = r => Row(r) == 2 ? throw new ObjectStoreException(ObjectStoreCodes.DiskFull, "The volume is full.") : null;
        await using var jobs = new DownloadAllJobs(source, Plenty, new() { FetchBatch = 2 });
        var result = await Done(jobs.Start(build));
        Assert.Equal((DownloadAllOutcome.DiskFull, ObjectStoreCodes.DiskFull, "The volume is full."), (result.Outcome, result.Code, result.Message));
        Assert.Equal(2, result.Final.StoredObjects);
        Assert.Equal(4, source.Requests.Count);
    }

    [Fact]
    public async Task AStoreFailureAfterTurningItOffIsReportedAsCanceled()
    {
        var build = Ledger(4);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Row 3, the last of its batch, is interrupted by the turn-off and comes back StoreFailed instead of throwing.
        source.Hold = async (r, token) =>
        {
            if (Row(r) != 3) return;
            reached.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, token); } catch (OperationCanceledException) { }
        };
        source.Answer = r => Row(r) == 3 ? new ObjectResult(r.Key, ObjectState.StoreFailed, Code: ObjectStoreCodes.WriteFailed, Message: "interrupted") : null;
        await using var jobs = new DownloadAllJobs(source, Plenty, new() { FetchBatch = 2 });
        var job = jobs.Start(build);
        await reached.Task.WaitAsync(Guard);

        var result = await jobs.StopAsync(build.Build).WaitAsync(Guard);

        Assert.NotNull(result);
        Assert.Equal((DownloadAllOutcome.Canceled, (string?)null), (result.Outcome, result.Code));
        Assert.Equal(3, result.Final.StoredObjects);
        Assert.Same(result, await job.Completion);
    }

    [Fact]
    public async Task RequestsFollowTheArchiveListThenOffsetWithLooseLast()
    {
        var a0 = Key(1000); var a1 = Key(1001);
        var build = Ledger(6, archived: new()
        {
            [0] = (new(a1, 10, 10), 1),
            [2] = (new(a0, 50, 12), 0),
            [3] = (new(a0, 5, 13), 0),
            [5] = (new(a1, 0, 15), 1),
        });
        await using var jobs = new DownloadAllJobs(source, Plenty);
        Assert.Equal(DownloadAllOutcome.Completed, (await Done(jobs.Start(build))).Outcome);
        var asked = source.Requests.Select(r => r.Request).ToArray();
        Assert.Equal([3, 2, 5, 0, 1, 4], asked.Select(Row));
        Assert.Equal([new ArchiveLocation(a0, 5, 13)], asked[0].Locations);
        Assert.Null(asked[4].Locations);
        Assert.All(asked, r => Assert.Equal(0, r.Order));
    }

    [Fact]
    public async Task OneJobPerBuildUpToTheLimit()
    {
        var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.BeforeAnswer = (_, token) => hold.Task.WaitAsync(token);
        await using var jobs = new DownloadAllJobs(source, Plenty, new() { MaxJobs = 1 });
        var build = Ledger(2);
        var job = jobs.Start(build);
        Assert.Same(job, jobs.Start(build));
        Assert.Same(job, jobs.Find(build.Build));
        var other = build with { Build = new BuildRef("wow", new string('c', 32), new string('d', 32)) };
        Assert.Throws<InvalidOperationException>(() => jobs.Start(other));
        Assert.Throws<ArgumentException>(() => jobs.Start(new NoLedgerBuild(other.Build)));

        hold.SetResult();
        Assert.Equal(DownloadAllOutcome.Completed, (await Done(job)).Outcome);
        Assert.Null(jobs.Find(build.Build));
        // Finished jobs leave the table, so the build runs again.
        var second = jobs.Start(build);
        Assert.NotSame(job, second);
        Assert.Equal(DownloadAllOutcome.Completed, (await Done(second)).Outcome);
    }

    [Fact]
    public async Task DisposingStopsEveryJob()
    {
        inner.BeforeAnswer = (_, token) => Task.Delay(Timeout.Infinite, token);
        var jobs = new DownloadAllJobs(source, Plenty);
        var first = jobs.Start(Ledger(2));
        var second = jobs.Start(Ledger(2) with { Build = new BuildRef("wow", new string('c', 32), new string('d', 32)) });
        await jobs.DisposeAsync().AsTask().WaitAsync(Guard);
        Assert.True(first.Completion.IsCompleted && second.Completion.IsCompleted);
        Assert.Equal(DownloadAllOutcome.Canceled, (await first.Completion).Outcome);
        Assert.Equal(DownloadAllOutcome.Canceled, (await second.Completion).Outcome);
        Assert.Throws<ObjectDisposedException>(() => jobs.Start(Ledger(1)));
    }

    [Fact]
    public void OptionsAreValidated()
    {
        Assert.Throws<ArgumentException>(() => new DownloadAllJobs(source, Plenty, new() { FetchBatch = 0 }));
        Assert.Throws<ArgumentException>(() => new DownloadAllJobs(source, Plenty, new() { FreeReserveBytes = -1 }));
        Assert.Throws<ArgumentException>(() => new DownloadAllJobs(source, Plenty, new() { MaxJobs = 0 }));
    }

    private static int Row(ObjectRequest r) => (int)BinaryPrimitives.ReadUInt32BigEndian(r.Key.KeyBytes()) - 1;

    private sealed record LedgerBuild(BuildRef Build, ObjectLedger Ledger, CdnContext? Cdn, Dictionary<CdnKey, (ArchiveLocation Location, int Archive)> Archived)
        : IBuild, ILedgerSource
    {
        public BuildMetadata Metadata => throw new NotSupportedException();
        public FileResolution Resolve(uint fileDataId) => throw new NotSupportedException();
        public FileResolution Resolve(string path) => throw new NotSupportedException();
        public IEnumerable<uint> FileDataIds(string locale, uint fromId = 0) => throw new NotSupportedException();

        public bool TryLocate(CdnKey eKey, out ArchiveLocation location, out int archive)
        {
            var found = Archived.TryGetValue(eKey, out var at);
            (location, archive) = found ? at : (default, -1);
            return found;
        }
    }

    /// <summary>A build with no object ledger, as one not opened by <see cref="BuildOpener"/>.</summary>
    private sealed record NoLedgerBuild(BuildRef Build) : IBuild
    {
        public CdnContext? Cdn => new(Route);
        public BuildMetadata Metadata => throw new NotSupportedException();
        public FileResolution Resolve(uint fileDataId) => throw new NotSupportedException();
        public FileResolution Resolve(string path) => throw new NotSupportedException();
        public IEnumerable<uint> FileDataIds(string locale, uint fromId = 0) => throw new NotSupportedException();
    }

    /// <summary>
    /// Records every GetKeysAsync request with its priority and reports the batch's declared bytes as its CDN bytes. Answers
    /// one request at a time through the in-memory source, unless <see cref="Answer"/> answers it.
    /// </summary>
    private sealed class Recording(InMemoryObjectSource inner) : IObjectSource
    {
        private readonly Lock gate = new();
        private readonly List<(ObjectRequest, FetchPriority)> requests = [];
        private readonly List<int> batches = [];

        public Func<ObjectRequest, ObjectResult?>? Answer { get; set; }
        /// <summary>Awaited before each request is answered.</summary>
        public Func<ObjectRequest, CancellationToken, Task>? Hold { get; set; }
        /// <summary>Called after each progress report to the caller has returned.</summary>
        public Action<ObjectProgress>? AfterReport { get; set; }
        public IReadOnlyList<(ObjectRequest Request, FetchPriority Priority)> Requests { get { lock (gate) return requests.ToArray(); } }
        public IReadOnlyList<int> Batches { get { lock (gate) return batches.ToArray(); } }
        public void Clear() { lock (gate) { requests.Clear(); batches.Clear(); } }

        public Task<ObjectResult> GetKeyAsync(ObjectRequest request, CdnContext? cdn, FetchPriority priority, CancellationToken cancellationToken) =>
            inner.GetKeyAsync(request, cdn, priority, cancellationToken);

        public async Task<IReadOnlyList<ObjectResult>> GetKeysAsync(IReadOnlyList<ObjectRequest> requests, CdnContext? cdn, FetchPriority priority,
            IProgress<ObjectProgress>? progress, CancellationToken cancellationToken)
        {
            lock (gate) { this.requests.AddRange(requests.Select(r => (r, priority))); batches.Add(requests.Count); }
            var results = new ObjectResult[requests.Count];
            long bytes = 0;
            for (var i = 0; i < requests.Count; i++)
            {
                if (Hold is { } hold) await hold(requests[i], cancellationToken);
                results[i] = Answer?.Invoke(requests[i]) ?? (await inner.GetKeysAsync([requests[i]], cdn, priority, null, cancellationToken))[0];
                bytes += requests[i].DeclaredLength ?? 0;
                var report = new ObjectProgress(i + 1, requests.Count, bytes);
                progress?.Report(report);
                AfterReport?.Invoke(report);
            }
            return results;
        }

        public Task<ObjectResult> GetRangeAsync(ObjectRequest request, long offset, Memory<byte> destination, CdnContext? cdn, FetchPriority priority,
            CancellationToken cancellationToken) => inner.GetRangeAsync(request, offset, destination, cdn, priority, cancellationToken);
        public Task<ObservationResult> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken) => inner.ObserveAsync(request, cancellationToken);
        public Task<ObservationResult> RecordAsync(Observation observation, CancellationToken cancellationToken) => inner.RecordAsync(observation, cancellationToken);
        public Task<long> CountPresentAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken) => inner.CountPresentAsync(keys, cancellationToken);
        public Task<IReadOnlyList<bool>> HasKeysAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken) => inner.HasKeysAsync(keys, cancellationToken);
        public Task<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) => inner.ReadHistoryAsync(query, cancellationToken);
    }

    /// <summary>
    /// Collects reports synchronously, unlike <see cref="Progress{T}"/>, which posts them. <paramref name="onFirst"/> runs inside
    /// the first report; <see cref="When"/> completes with the first report that matches.
    /// </summary>
    private sealed class Reports(Action<DownloadAllProgress>? onFirst = null) : IProgress<DownloadAllProgress>
    {
        private readonly Lock gate = new();
        private readonly List<DownloadAllProgress> all = [];
        private readonly List<(Func<DownloadAllProgress, bool> Match, TaskCompletionSource<DownloadAllProgress> Seen)> waits = [];
        public IReadOnlyList<DownloadAllProgress> All { get { lock (gate) return all.ToArray(); } }

        public Task<DownloadAllProgress> When(Func<DownloadAllProgress, bool> match)
        {
            var seen = new TaskCompletionSource<DownloadAllProgress>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (gate)
            {
                if (all.FirstOrDefault(match) is { } already) seen.SetResult(already);
                else waits.Add((match, seen));
            }
            return seen.Task;
        }

        public void Report(DownloadAllProgress value)
        {
            bool first;
            lock (gate)
            {
                first = all.Count == 0;
                all.Add(value);
                foreach (var w in waits) if (w.Match(value)) w.Seen.TrySetResult(value);
            }
            if (first) onFirst?.Invoke(value);
        }
    }

    private sealed class ManualClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public void Advance(TimeSpan by) => Interlocked.Add(ref ticks, by.Ticks);
    }
}
