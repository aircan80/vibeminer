using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Primitives.Tests.LibraryFootprintTestKit;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

public sealed class LibraryFootprintBoundsTests
{
    [Theory]
    [InlineData("entries")]
    [InlineData("depth")]
    [InlineData("path")]
    [InlineData("diagnostics")]
    public async Task ResourceLimitsReturnExplicitIncompleteSamplesWithBoundedDiagnostics(string bound)
    {
        if (!Supported) return;
        using var temp = new TempDirectory(); var root = DirectoryPath(temp); await using var library = await ObjectStore.OpenLibraryAsync(root, Quick);
        var options = new LibraryFootprintOptions(); LibraryFootprintProblemCode expected;
        switch (bound)
        {
            case "entries":
                for (var i = 0; i < 20; i++) WriteSized(root, $"extra-{i}.bin", 1);
                options = options with { MaxEntries = 4 }; expected = LibraryFootprintProblemCode.EntryLimit; break;
            case "depth": WriteSized(root, "deep/a/b/payload.bin", 1234); options = options with { MaxDepth = 1 }; expected = LibraryFootprintProblemCode.DepthLimit; break;
            case "path":
                var pathLimit = Math.Max(32, root.Length + 1); WriteSized(root, new string('x', 120) + "/" + new string('y', 120) + "/payload.bin", 1234);
                options = options with { MaxPathLength = pathLimit }; expected = LibraryFootprintProblemCode.PathLimit; break;
            default:
                for (var i = 0; i < 10; i++) WriteSized(root, $"denied-{i}.bin", 1);
                options = options with { MaxProblems = 2, Fault = (point, path) => { if (point == LibraryFootprintPoint.BeforeEntryMetadata && path.StartsWith("denied-", StringComparison.Ordinal)) throw new UnauthorizedAccessException("Authored denied metadata."); } };
                expected = LibraryFootprintProblemCode.Unreadable; break;
        }
        await using var view = library.ForFootprint(options); var sample = await view.MeasureAsync(); Assert.False(sample.Complete); AssertReconciled(sample);
        Assert.Contains(sample.Problems, problem => problem.Code == expected); Assert.InRange(sample.Problems.Count, 1, options.MaxProblems);
        Assert.All(sample.Problems, problem => { Assert.False(Path.IsPathFullyQualified(problem.RelativePath)); Assert.True(problem.RelativePath.Length <= options.MaxPathLength); });
        if (bound == "entries") Assert.InRange(sample.ExaminedEntries, 0, options.MaxEntries);
        if (bound == "diagnostics") { Assert.Equal(2, sample.Problems.Count); Assert.True(sample.TruncatedDiagnostics); }
    }
    [Fact]
    public async Task CheckedLengthOverflowIsIncompleteAndNeverWrapsOrReturnsACompletePartialTotal()
    {
        if (!Supported) return;
        using var temp = new TempDirectory(); var root = DirectoryPath(temp); await using var library = await ObjectStore.OpenLibraryAsync(root, Quick); WriteSized(root, "huge.bin", 1); WriteSized(root, "one.bin", 1);
        await using var view = library.ForFootprint(new() { LengthOverride = (path, length) => path == "huge.bin" ? long.MaxValue : path == "one.bin" ? 1 : length });
        var sample = await view.MeasureAsync(); Assert.False(sample.Complete); AssertReconciled(sample); Assert.Contains(sample.Problems, problem => problem.Code == LibraryFootprintProblemCode.Overflow);
        Assert.True(sample.Total.Bytes >= 0); Assert.True(sample.Total.Files >= 0);
    }
    [Fact]
    public async Task PerViewAndPerLibraryAdmissionFailFastAndReleaseSlotsAfterActualRetirement()
    {
        if (!Supported) return;
        using var temp = new TempDirectory(); var root = DirectoryPath(temp); await using var library = await ObjectStore.OpenLibraryAsync(root, Quick);
        using var release = new ManualResetEventSlim(); var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var starts = 0;
        var options = new LibraryFootprintOptions { Fault = (point, _) =>
        {
            if (point != LibraryFootprintPoint.MeasurementStarted) return;
            if (Interlocked.Increment(ref starts) == 1) firstEntered.TrySetResult(); else secondEntered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Footprint admission gate was not released.");
        } };
        await using var firstView = library.ForFootprint(options); await using var secondView = library.ForFootprint(options); await using var thirdView = library.ForFootprint();
        var first = firstView.MeasureAsync().AsTask(); Task<LibraryFootprintSample>? second = null;
        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(LibraryFootprintCodes.Busy, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await firstView.MeasureAsync())).Code);
            second = secondView.MeasureAsync().AsTask(); await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(LibraryFootprintCodes.Busy, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await thirdView.MeasureAsync())).Code);
            release.Set(); Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(10))).Complete); Assert.True((await second.WaitAsync(TimeSpan.FromSeconds(10))).Complete); Assert.True((await thirdView.MeasureAsync()).Complete);
        }
        finally { release.Set(); await first; if (second is not null) await second; }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrViewCloseDrainsTheBlockedMetadataWorkerAndLeavesTheBorrowedLibraryUsable(bool close)
    {
        if (!Supported) return;
        using var temp = new TempDirectory(); var root = DirectoryPath(temp); await using var library = await ObjectStore.OpenLibraryAsync(root, Quick); WriteSized(root, "slow.bin", 10);
        using var release = new ManualResetEventSlim(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var armed = 1;
        await using var view = library.ForFootprint(new() { Fault = (point, path) =>
        {
            if (point != LibraryFootprintPoint.BeforeFileLength || path != "slow.bin" || Volatile.Read(ref armed) == 0) return;
            entered.TrySetResult(); if (!release.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Footprint metadata gate was not released.");
        } });
        using var cancel = new CancellationTokenSource(); var measuring = view.MeasureAsync(cancel.Token).AsTask(); Task? closing = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (close)
            {
                closing = view.DisposeAsync().AsTask(); Assert.False(closing.IsCompleted);
                Assert.Equal(LibraryFootprintCodes.Closed, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await view.MeasureAsync())).Code);
            }
            else { cancel.Cancel(); Assert.False(measuring.IsCompleted); }
            await using var sibling = library.ForFootprint(); Assert.True((await sibling.MeasureAsync()).Complete);
            var item = Synthetic.Headerless(228010, 1000); Assert.Equal(PutOutcome.Stored, Assert.Single(await library.ForOwner("independent", LibraryOwnerScope.Build, "source", LibraryObjectRole.Original).PutAsync([item], true)).Outcome);
            Interlocked.Exchange(ref armed, 0); release.Set(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => measuring).WaitAsync(TimeSpan.FromSeconds(10)); if (closing is not null) await closing.WaitAsync(TimeSpan.FromSeconds(10));
            if (!close) Assert.True((await view.MeasureAsync()).Complete);
            Assert.True(await library.HasAsync(item.Key)); Assert.True(await library.ForOwner("independent", LibraryOwnerScope.Build, "source", LibraryObjectRole.Original).OwnsAsync(item.Key));
        }
        finally { release.Set(); }
    }
    [Theory]
    [InlineData("entries", 0)]
    [InlineData("entries", 1000001)]
    [InlineData("depth", 0)]
    [InlineData("depth", 33)]
    [InlineData("path", 31)]
    [InlineData("path", 4097)]
    [InlineData("problems", 0)]
    [InlineData("problems", 65)]
    [InlineData("concurrency", 0)]
    [InlineData("concurrency", 3)]
    public async Task InvalidResourceBoundsAreRejectedBeforeAdmission(string limit, int value)
    {
        using var temp = new TempDirectory(); await using var library = await ObjectStore.OpenLibraryAsync(DirectoryPath(temp), Quick);
        var options = limit switch
        {
            "entries" => new LibraryFootprintOptions { MaxEntries = value }, "depth" => new LibraryFootprintOptions { MaxDepth = value },
            "path" => new LibraryFootprintOptions { MaxPathLength = value }, "problems" => new LibraryFootprintOptions { MaxProblems = value }, _ => new LibraryFootprintOptions { MaxConcurrentMeasurements = value }
        };
        Assert.Throws<ArgumentException>(() => library.ForFootprint(options));
    }
    [Fact]
    public async Task StandaloneStoreCannotBeSilentlyTreatedAsABorrowedMixedLibrary()
    {
        using var temp = new TempDirectory(); await using var cdn = await ObjectStore.OpenAsync(Path.Combine(PhysicalPath(temp.Path), "standalone"), Quick);
        Assert.Throws<InvalidOperationException>(() => cdn.ForFootprint());
    }
    [Fact]
    public async Task UnsupportedPlatformsRetainAnExplicitIncompleteOutcome()
    {
        if (Supported) return;
        using var temp = new TempDirectory(); await using var library = await ObjectStore.OpenLibraryAsync(DirectoryPath(temp), Quick); await using var view = library.ForFootprint();
        var sample = await view.MeasureAsync(); Assert.False(sample.Complete); Assert.Contains(sample.Problems, problem => problem.Code == LibraryFootprintProblemCode.UnsupportedPlatform); AssertReconciled(sample);
    }
}
