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
