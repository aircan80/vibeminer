using Microsoft.Data.Sqlite;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

/// <summary>Write errors, cancellation, and index commits lost the way power loss loses them.</summary>
public sealed class ObjectStoreFailureTests
{
    [Theory]
    [InlineData(nameof(StorePoint.EntryWritten), 28, ObjectStoreCodes.DiskFull)]
    [InlineData(nameof(StorePoint.BeforePackSync), 28, ObjectStoreCodes.DiskFull)]
    [InlineData(nameof(StorePoint.InsideIndexCommit), 5, ObjectStoreCodes.WriteFailed)]
    public async Task AWriteErrorFailsOnlyWhatWasPendingCutsThePackBackAndTheStoreGoesOn(string point, int errno, string code)
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var target = Enum.Parse<StorePoint>(point);
        var armed = 0;
        var options = Quick with { Fault = p => { if (p == target && Interlocked.Exchange(ref armed, 0) == 1) throw new IOException("injected", errno); } };
        var a = Synthetic.Headerless(1, 3000); var b = Synthetic.Headerless(2, 3000); var c = Synthetic.Headerless(3, 3000);
        await using (var store = await ObjectStore.OpenAsync(dir, options))
        {
            await store.PutAsync([a]);
            var length = new FileInfo(PackFiles(dir).Single()).Length;
            Volatile.Write(ref armed, 1);
            var error = await Assert.ThrowsAsync<ObjectStoreException>(() => store.PutAsync([b]));
            Assert.Equal(code, error.Code);
            Assert.Equal(length, new FileInfo(PackFiles(dir).Single()).Length);  // cut back to the last commit
            Assert.False(await store.HasAsync(b.Key));
            Assert.Equal(PutOutcome.Stored, Assert.Single(await store.PutAsync([c])).Outcome);
        }
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            Assert.False(store.OpenReport.IndexRebuilt, string.Join("; ", store.OpenReport.Problems));
            Assert.Equal(0, store.OpenReport.RecoveredEntries);
            Assert.True(await store.HasAsync(a.Key)); Assert.True(await store.HasAsync(c.Key)); Assert.False(await store.HasAsync(b.Key));
            Assert.Equal(0, (await store.VerifyAsync(full: true)).CorruptCount);
        }
    }

    [Fact]
    public async Task AFailedPutIsToldOnlyAfterThePackIsCutBack()
    {
        // Regression (#184): the caller was failed before the cut, so on a slow runner it saw the uncut pack. The hook holds the
        // writer where the cut starts until the caller has seen its error and measured the pack. Told first, the caller measures
        // the uncut pack; told after the cut, it can't be told while the writer is held, so the hold ends at its bound.
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var armed = 0;
        using var measured = new ManualResetEventSlim(false);
        var options = Quick with
        {
            Fault = p =>
            {
                if (p == StorePoint.InsideIndexCommit && Interlocked.Exchange(ref armed, 0) == 1) throw new IOException("injected", 5);
                if (p == StorePoint.WriteErrorCutting) measured.Wait(TimeSpan.FromSeconds(1));
            },
        };
        await using var store = await ObjectStore.OpenAsync(dir, options);
        await store.PutAsync([Synthetic.Headerless(1, 3000)]);
        var length = new FileInfo(PackFiles(dir).Single()).Length;
        Volatile.Write(ref armed, 1);
        await Assert.ThrowsAsync<ObjectStoreException>(() => store.PutAsync([Synthetic.Headerless(2, 3000)]));
        var seen = new FileInfo(PackFiles(dir).Single()).Length;
        measured.Set();
        Assert.Equal(length, seen);
    }

    [Fact]
    public async Task WhenTheCutBackFailsTooTheWriterStopsAndTheNextOpenRecoversTheTail()
    {
        // #184: the cut failing has its own path (the writer stops). The hook holds the failing cut until a Delete is queued behind
        // it, so the failed Put, the queued Delete and every later call must all hear the same stop, and none of them before it.
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var armed = 0;
        using var reached = new SemaphoreSlim(0);
        using var release = new ManualResetEventSlim(false);
        var options = Quick with
        {
            Fault = p =>
            {
                if (Volatile.Read(ref armed) == 0) return;
                if (p == StorePoint.InsideIndexCommit) throw new IOException("injected", 5);
                if (p == StorePoint.WriteErrorCut && Interlocked.Exchange(ref armed, 0) == 1)
                {
                    reached.Release();
                    release.Wait(TimeSpan.FromSeconds(30));
                    throw new IOException("cut failed", 5);
                }
            },
        };
        var a = Synthetic.Headerless(1, 3000); var b = Synthetic.Headerless(2, 3000); var c = Synthetic.Headerless(3, 3000);
        long committed;
        await using (var store = await ObjectStore.OpenAsync(dir, options))
        {
            await store.PutAsync([a]);
            committed = new FileInfo(PackFiles(dir).Single()).Length;
            Volatile.Write(ref armed, 1);
            var put = store.PutAsync([b]);
            Assert.True(await reached.WaitAsync(TimeSpan.FromSeconds(30)));
            var queued = store.DeleteAsync([a.Key]);   // in the channel when DeleteAsync returns
            await Task.Delay(50);
            Assert.False(put.IsCompleted);             // not told before the stop
            Assert.False(queued.IsCompleted);
            release.Set();

            // Everyone gets the one error, and it doesn't claim the pack was cut back.
            var error = await Assert.ThrowsAsync<ObjectStoreException>(() => put);
            Assert.Equal(ObjectStoreCodes.WriteFailed, error.Code);
            Assert.Contains("couldn't be cut back", error.Message, StringComparison.Ordinal);
            Assert.Same(error, await Assert.ThrowsAsync<ObjectStoreException>(() => queued));
            Assert.Same(error, await Assert.ThrowsAsync<ObjectStoreException>(() => store.PutAsync([c])));
            Assert.Same(error, await Assert.ThrowsAsync<ObjectStoreException>(() => store.DeleteAsync([a.Key])));

            // The cut didn't happen: b's bytes are still in the pack, but nothing past the last commit is served.
            Assert.True(new FileInfo(PackFiles(dir).Single()).Length > committed);
            Assert.False(await store.HasAsync(b.Key));
            Assert.Equal(ReadState.Absent, (await store.ReadAsync(b.Key)).State);
            Assert.True(await store.HasAsync(a.Key));
        }
        await using (var store = await ObjectStore.OpenAsync(dir, Quick))
        {
            // The uncut tail is recovered as after a crash: b's whole, synced entry is adopted; the queued Delete never ran.
            Assert.False(store.OpenReport.IndexRebuilt, string.Join("; ", store.OpenReport.Problems));
            Assert.True(store.OpenReport.FullIndexCheck);   // the stopped writer never set clean_close
            Assert.Equal(1, store.OpenReport.RecoveredEntries);
            Assert.Equal(0, store.OpenReport.TruncatedBytes);
            Assert.True(await store.HasAsync(a.Key)); Assert.True(await store.HasAsync(b.Key));
            Assert.Equal(PutOutcome.Stored, Assert.Single(await store.PutAsync([c])).Outcome);
            Assert.Equal(0, (await store.VerifyAsync(full: true)).CorruptCount);
        }
    }

    /// <summary>Holds the writer at its next EntryStarted until released, so the test can queue work behind it.</summary>
    private sealed class Gate : IDisposable
    {
        public readonly SemaphoreSlim Reached = new(0);
        public readonly ManualResetEventSlim Release = new(true);
        public void Hook(StorePoint p) { if (p == StorePoint.EntryStarted && !Release.IsSet) { Reached.Release(); Release.Wait(); } }
        public Task CloseAsync() { Release.Reset(); return Task.CompletedTask; }
        public void Dispose() { Release.Set(); Reached.Dispose(); Release.Dispose(); }
    }

    [Fact]
    public async Task CancelingQueuedWorkStoresNothingAndLeavesTheStoreAsItWas()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        using var gate = new Gate();
        await using var store = await ObjectStore.OpenAsync(dir, Quick with { MaxPackBytes = 20_000, Fault = gate.Hook });
        var objects = Enumerable.Range(0, 12).Select(i => Synthetic.Headerless(i, 3000)).ToList();
        foreach (var o in objects) await store.PutAsync([o]);
        await store.DeleteAsync(objects.Take(8).Select(o => o.Key).ToList());
        var rows = Rows(dir);
        var files = HashFolder(Path.Combine(dir, "packs"));

        await gate.CloseAsync();
        var holder = store.PutAsync([Synthetic.Headerless(50)]);
        Assert.True(await gate.Reached.WaitAsync(TimeSpan.FromSeconds(30)));
        using var cancel = new CancellationTokenSource();
        var put = store.PutAsync([Synthetic.Headerless(51)], ct: cancel.Token);   // queued when PutAsync returns
        var scan = store.ScanAsync(ct: cancel.Token);
        var compact = store.CompactAsync(new CompactOptions(0.01, 10), cancel.Token);
        await cancel.CancelAsync();
        gate.Release.Set();
        Assert.Equal(PutOutcome.Stored, Assert.Single(await holder).Outcome);
        Assert.Equal(PutOutcome.Canceled, Assert.Single(await put).Outcome);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scan);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compact);
        Assert.False(await store.HasAsync(Synthetic.Headerless(51).Key));
        // The index holds what it held plus the one Put that went through; no compaction target was left behind.
        Assert.Equal(rows.Count + 1, Rows(dir).Count);
        Assert.Equal(files.Keys.Order(), HashFolder(Path.Combine(dir, "packs")).Keys.Where(files.ContainsKey).Order());
        Assert.Equal(0, PackRows(dir, 2));
        Assert.True(PackRows(dir, 0) <= 1);
        Assert.Equal(PutOutcome.Stored, Assert.Single(await store.PutAsync([Synthetic.Headerless(52)])).Outcome);
        Assert.True(PackRows(dir, 0) <= 1);  // a Scan that didn't finish left no second active pack
    }

    [Fact]
    public async Task ACompactionCanceledWhileCopyingRemovesItsTargetAndCanRunAgain()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        using var cancel = new CancellationTokenSource();
        var armed = 0;
        // Cancel once the target pack is listed, so the copy itself sees the cancellation.
        var options = Quick with { MaxPackBytes = 20_000, Fault = p => { if (p == StorePoint.CompactTargetListed && Volatile.Read(ref armed) == 1) cancel.Cancel(); } };
        await using var store = await ObjectStore.OpenAsync(dir, options);
        var objects = Enumerable.Range(0, 12).Select(i => Synthetic.Headerless(i, 3000)).ToList();
        foreach (var o in objects) await store.PutAsync([o]);
        await store.DeleteAsync(objects.Take(8).Select(o => o.Key).ToList());
        var rows = Rows(dir);
        var packs = PackFiles(dir);
        Volatile.Write(ref armed, 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CompactAsync(new CompactOptions(0.01, 10), cancel.Token));
        Assert.Equal(0, PackRows(dir, 2));
        Assert.Equal(packs, PackFiles(dir));
        Assert.Equal(rows, Rows(dir));
        Volatile.Write(ref armed, 0);
        Assert.True((await store.CompactAsync(new CompactOptions(0.01, 10))).PacksCompacted > 0);
        foreach (var o in objects.Skip(8)) Assert.Equal(o.Bytes.ToArray(), (await store.ReadAsync(o.Key)).Bytes);
    }

    [Fact]
    public async Task AFailedCompactionTargetsNumberIsReusedAfterReopenAndServesOnlyTheNewPack()
    {
        // D373: pack numbers aren't persisted, so a failed target above every other pack is handed out again by the next session.
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        using var cancel = new CancellationTokenSource();
        var armed = 0;
        var options = Quick with { MaxPackBytes = 400_000, Fault = p => { if (p == StorePoint.CompactTargetListed && Volatile.Read(ref armed) == 1) cancel.Cancel(); } };
        var dead = Synthetic.Headerless(1, 300_000); var keep = Synthetic.Headerless(2, 1000); var other = Synthetic.Headerless(3, 300_000);
        await using (var store = await ObjectStore.OpenAsync(dir, options))
        {
            await store.PutAsync([dead]); await store.PutAsync([keep]); await store.PutAsync([other]);  // 1 sealed (dead, keep), 2 active
            await store.DeleteAsync([dead.Key]);
        }
        Assert.Equal(1, Row(dir, keep.Key).pack);
        // A read-only store held open across both sessions, with pack 1's handle cached and its entry hashed.
        await using var reader = await ObjectStore.OpenReadOnlyAsync(dir);
        Assert.Equal(keep.Bytes.ToArray(), (await reader.ReadAsync(keep.Key)).Bytes);
        await using (var store = await ObjectStore.OpenAsync(dir, options))
        {
            Volatile.Write(ref armed, 1);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CompactAsync(new CompactOptions(0.01, 10), cancel.Token));
            Assert.False(File.Exists(PackPath(dir, 3)));
            Assert.Equal(0, PackRows(dir, 2));
        }
        await using (var store = await ObjectStore.OpenAsync(dir, options with { Fault = null }))
        {
            Assert.Empty(store.OpenReport.Problems);
            Assert.Equal(1, (await store.CompactAsync(new CompactOptions(0.01, 10))).PacksCompacted);
            Assert.Equal(3, Row(dir, keep.Key).pack);                              // the failed target's number, taken again
            Assert.Equal(new[] { PackPath(dir, 2), PackPath(dir, 3) }, PackFiles(dir));
            Assert.Equal(keep.Bytes.ToArray(), (await store.ReadAsync(keep.Key)).Bytes);
            Assert.Equal(ReadState.Absent, (await store.ReadAsync(dead.Key)).State);
            Assert.Equal(0, (await store.VerifyAsync(full: true)).CorruptCount);
        }
        // The long-lived reader follows the switch into the new pack 3; nothing it cached from before points there.
        Assert.Equal(keep.Bytes.ToArray(), (await reader.ReadAsync(keep.Key)).Bytes);
        Assert.Equal(other.Bytes.ToArray(), (await reader.ReadAsync(other.Key)).Bytes);
        Assert.Equal(ReadState.Absent, (await reader.ReadAsync(dead.Key)).State);
        var verify = await reader.VerifyAsync(full: true);
        Assert.Equal((2, 0, 0), (verify.Checked, verify.CorruptCount, verify.LostCount));
    }

    private static long PackRows(string dir, int state)
    {
        using var c = new SqliteConnection($"Data Source={Path.Combine(dir, "objects.sqlite3")};Mode=ReadOnly;Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = "SELECT count(*) FROM packs WHERE state = $s"; cmd.Parameters.AddWithValue("$s", state);
        return (long)cmd.ExecuteScalar()!;
    }

    [Fact]
    public async Task IndexCommitsLostAfterTheLastDurableOneComeBackFromThePack()
    {
        // Power loss under synchronous=NORMAL can drop any index commit after the last FULL one (a pack roll). Restoring the index
        // as it was right after the roll stands in for that: everything Put confirmed since must come back through tail recovery.
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var snapshot = Path.Combine(t.Path, "index-after-roll");
        var taken = 0;
        var options = Quick with
        {
            Fault = p =>
            {
                if (p != StorePoint.PackListed || Interlocked.Exchange(ref taken, 1) != 0) return;
                Directory.CreateDirectory(snapshot);
                foreach (var f in Directory.GetFiles(dir, "objects.sqlite3*")) File.Copy(f, Path.Combine(snapshot, Path.GetFileName(f)));
            },
        };
        var objects = Enumerable.Range(0, 30).Select(i => i % 2 == 0 ? Synthetic.Headerless(i, 2000) : Synthetic.Config(i, 100 + i)).ToList();
        var store = await ObjectStore.OpenAsync(dir, options);
        foreach (var o in objects) Assert.Equal(PutOutcome.Stored, Assert.Single(await store.PutAsync([o])).Outcome);
        await store.PutAsync(HistoryRecordFormat.Objects(Synthetic.Observation(1)));
        await store.DeleteAsync([objects[0].Key]);
        store.Kill();
        foreach (var f in Directory.GetFiles(dir, "objects.sqlite3*")) File.Delete(f);
        foreach (var f in Directory.GetFiles(snapshot).Where(f => !f.EndsWith("-shm", StringComparison.Ordinal))) File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
        await using (var reopened = await ObjectStore.OpenAsync(dir, Quick))
        {
            Assert.False(reopened.OpenReport.IndexRebuilt, string.Join("; ", reopened.OpenReport.Problems));
            Assert.True(reopened.OpenReport.RecoveredEntries >= objects.Count);
            Assert.False(await reopened.HasAsync(objects[0].Key));
            foreach (var o in objects.Skip(1)) Assert.Equal(o.Bytes.ToArray(), (await reopened.ReadAsync(o.Key)).Bytes);
            Assert.Single(await reopened.ReadHistoryAsync(new HistoryQuery()));
        }
        var rows = Rows(dir);
        await using (var store2 = await ObjectStore.OpenAsync(dir, Quick)) await store2.ScanAsync();
        Assert.Equal(rows, Rows(dir));
    }

    [Fact]
    public async Task AStagedFileThatCanNotBeReadRefusesOnlyItsOwnObject()
    {
        using var t = new TempDirectory();
        var dir = StoreDir(t);
        var big = Synthetic.Chunked(1, 70_000, 70_000);
        await using var store = await ObjectStore.OpenAsync(dir, Quick);
        await using (var s = await store.StageAsync(big.Key, big.DeclaredLength))
        {
            await s.AppendAsync(big.Bytes);
            File.Delete(Directory.GetFiles(Path.Combine(dir, "staging")).Single());
            var r = await s.CommitAsync();
            Assert.Equal((PutOutcome.Refused, ObjectStoreCodes.StagingRejected), (r.Outcome, r.Code));
        }
        Assert.False(await store.HasAsync(big.Key));
        Assert.Equal(PutOutcome.Stored, Assert.Single(await store.PutAsync([Synthetic.Headerless(2)])).Outcome);
    }
}
