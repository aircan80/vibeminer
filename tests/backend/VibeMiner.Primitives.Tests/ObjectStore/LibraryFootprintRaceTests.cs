using System.Runtime.InteropServices;
using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Primitives.Tests.LibraryFootprintTestKit;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

public sealed class LibraryFootprintRaceTests
{
    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    [Fact]
    public async Task UnsupportedPipeIsReportedWithoutOpeningOrWaitingForAPayloadProducer()
    {
        if (!Supported) return;
        using var temp = new TempDirectory(); var root = DirectoryPath(temp); await using var library = await ObjectStore.OpenLibraryAsync(root, Quick); await using var view = library.ForFootprint();
        var before = await view.MeasureAsync(); Assert.Equal(0, MakeFifo(Path.Combine(root, "no-producer.pipe"), 0x180));
        var sample = await view.MeasureAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(sample.Complete); AssertReconciled(sample); Assert.Equal(before.Total, sample.Total);
        Assert.Contains(sample.Problems, problem => problem.RelativePath == "no-producer.pipe" && problem.Code == LibraryFootprintProblemCode.UnsupportedEntry);
    }
    [Fact]
    public async Task DescendantReplacementWithOutsideLinkBeforeDirectoryOpenCannotEscapeTheRoot()
    {
        if (!Supported) return;
        using var temp = new TempDirectory(); var root = DirectoryPath(temp); var held = Path.Combine(PhysicalPath(temp.Path), "held-packs"); var outside = Path.Combine(PhysicalPath(temp.Path), "outside"); WriteSized(outside, "pack-000099.vmp", 12345);
        await using var library = await ObjectStore.OpenLibraryAsync(root, Quick); await SeedMixedAsync(library); var swapped = false;
        await using var view = library.ForFootprint(new() { Fault = (point, path) =>
        {
            if (point != LibraryFootprintPoint.BeforeDirectoryOpen || path != "packs" || swapped) return;
            swapped = true; Directory.Move(Path.Combine(root, "packs"), held); Directory.CreateSymbolicLink(Path.Combine(root, "packs"), outside);
        } });
        try
        {
            var sample = await view.MeasureAsync(); Assert.True(swapped); Assert.False(sample.Complete); Assert.Equal(new LibraryFootprintBucket(0, 0), sample.Packs); AssertReconciled(sample);
            Assert.Contains(sample.Problems, problem => problem.RelativePath == "packs" && problem.Code is LibraryFootprintProblemCode.Link or LibraryFootprintProblemCode.ChangedDirectory);
            Assert.Equal(12345, new FileInfo(Path.Combine(outside, "pack-000099.vmp")).Length);
        }
        finally { if (swapped) { Directory.Delete(Path.Combine(root, "packs")); Directory.Move(held, Path.Combine(root, "packs")); } }
    }
    [Fact]
    public async Task RegularFileReplacedWithALinkBeforeLengthReadDoesNotReuseTheEarlierRegularMetadata()
    {
        if (!Supported) return;
        using var temp = new TempDirectory(); var root = DirectoryPath(temp); var outside = Path.Combine(PhysicalPath(temp.Path), "outside"); WriteSized(outside, "secret.bin", 12345);
        await using var library = await ObjectStore.OpenLibraryAsync(root, Quick); WriteSized(root, "swap.bin", 11); var held = Path.Combine(PhysicalPath(temp.Path), "held.bin"); var swapped = false;
        await using var view = library.ForFootprint(new() { Fault = (point, path) =>
        {
            if (point != LibraryFootprintPoint.BeforeFileLength || path != "swap.bin" || swapped) return;
            swapped = true; File.Move(Path.Combine(root, path), held); File.CreateSymbolicLink(Path.Combine(root, path), Path.Combine(outside, "secret.bin"));
        } });
        try
        {
            var sample = await view.MeasureAsync(); Assert.False(sample.Complete); AssertReconciled(sample); Assert.Contains(sample.Problems, problem => problem.RelativePath == "swap.bin" && problem.Code == LibraryFootprintProblemCode.Link);
            Assert.Equal(12345, new FileInfo(Path.Combine(outside, "secret.bin")).Length);
        }
        finally { if (swapped) { File.Delete(Path.Combine(root, "swap.bin")); File.Move(held, Path.Combine(root, "swap.bin")); } }
    }
    [Fact]
    public async Task ReplacementRootAfterTraversalStartsCannotProduceACompleteSampleOfEitherDirectory()
    {
        if (!Supported) return;
        using var temp = new TempDirectory(); var root = DirectoryPath(temp); var held = Path.Combine(PhysicalPath(temp.Path), "held-root");
        await using var library = await ObjectStore.OpenLibraryAsync(root, Quick); WriteSized(root, "trigger.bin", 11); var swapped = false;
        await using var view = library.ForFootprint(new() { Fault = (point, path) =>
        {
            if (point != LibraryFootprintPoint.BeforeEntryMetadata || path != "trigger.bin" || swapped) return;
            swapped = true; Directory.Move(root, held); WriteSized(root, "replacement-only.bin", 1234567);
        } });
        try
        {
            var sample = await view.MeasureAsync(); Assert.True(swapped); Assert.False(sample.Complete); AssertReconciled(sample); Assert.True(sample.Total.Bytes < 1234567);
            Assert.Contains(sample.Problems, problem => problem.RelativePath == "" && problem.Code == LibraryFootprintProblemCode.ChangedDirectory);
            Assert.True(File.Exists(Path.Combine(root, "replacement-only.bin"))); Assert.True(File.Exists(Path.Combine(held, "trigger.bin")));
        }
        finally { if (swapped) { Directory.Delete(root, true); Directory.Move(held, root); } }
    }
    private sealed class ManualClock(DateTimeOffset initial) : TimeProvider
    {
        private long ticks = initial.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }
    [Fact]
    public async Task ConcurrentPackAppendIsAnIntervalObservationWithoutCheckpointReadClaimsOrAtomicSnapshotFiction()
    {
        if (!Supported) return;
        using var temp = new TempDirectory(); var root = DirectoryPath(temp); await using var library = await ObjectStore.OpenLibraryAsync(root, Quick); await SeedMixedAsync(library);
        var pack = Assert.Single(PackFiles(root)); var relative = Path.GetRelativePath(root, pack).Replace(Path.DirectorySeparatorChar, '/'); var oldLength = new FileInfo(pack).Length;
        var clock = new ManualClock(new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero)); var start = clock.GetUtcNow(); using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var view = library.ForFootprint(new() { Time = clock, Fault = (point, path) =>
        {
            if (point != LibraryFootprintPoint.BeforeFileLength || path != relative) return;
            entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Footprint append gate was not released.");
        } });
        var sampleTask = view.MeasureAsync().AsTask();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var item = Synthetic.Headerless(228020, 3000); var publisher = library.ForOwner("concurrent", LibraryOwnerScope.Build, "source", LibraryObjectRole.Original);
            Assert.Equal(PutOutcome.Stored, Assert.Single(await publisher.PutAsync([item], true)).Outcome); clock.Advance(TimeSpan.FromSeconds(5)); var newLength = new FileInfo(pack).Length; Assert.True(newLength > oldLength);
            var members = Membership(root); var rows = Rows(root); var lastRead = LastRead(root, item.Key); release.Set(); var sample = await sampleTask.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(sample.Complete); AssertReconciled(sample); Assert.Equal(start, sample.StartedAt); Assert.Equal(start.AddSeconds(5), sample.CompletedAt); Assert.InRange(sample.Packs.Bytes, oldLength, newLength);
            Assert.Equal(members, Membership(root)); Assert.Equal(rows, Rows(root)); Assert.Equal(lastRead, LastRead(root, item.Key)); Assert.True(await publisher.OwnsAsync(item.Key));
        }
        finally { release.Set(); }
    }
}
