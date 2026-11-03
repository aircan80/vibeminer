using VibeMiner.Objects;
using VibeMiner.Objects.Testing;
using Xunit;
using static VibeMiner.Primitives.Tests.LibraryFootprintTestKit;
using static VibeMiner.Primitives.Tests.ObjectStoreTestKit;

namespace VibeMiner.Primitives.Tests;

public abstract class LibraryFootprintContractTests : IAsyncLifetime
{
    protected ILibraryFootprint View { get; private set; } = null!;
    protected virtual bool SupportsSampling => true;
    protected abstract Task<ILibraryFootprint> CreateAsync();
    protected abstract Task AddFileAsync(string path, int length);
    protected abstract Task CloseAsync();
    public async Task InitializeAsync() => View = await CreateAsync();
    public Task DisposeAsync() => CloseAsync();
    [Fact]
    public async Task DisjointCategoryDeltasKeepUnknownNamesOtherAndCountZeroLengthFiles()
    {
        if (!SupportsSampling) return;
        var before = await View.MeasureAsync(); Assert.True(before.Complete); AssertReconciled(before);
        await AddFileAsync("packs/pack-000099.vmp", 31); await AddFileAsync("staging/transfer.part", 0);
        foreach (var path in new[] { "pack-000098.vmp", "packs/pack-000000.vmp", "packs/pack-1234567.vmp", "staging/nested/transfer.part" }) await AddFileAsync(path, 17);
        var after = await View.MeasureAsync(); Assert.True(after.Complete); Assert.Empty(after.Problems); Assert.False(after.TruncatedDiagnostics); AssertReconciled(after);
        Assert.Equal(new LibraryFootprintBucket(before.Packs.Files + 1, before.Packs.Bytes + 31), after.Packs);
        Assert.Equal(new LibraryFootprintBucket(before.Staging.Files + 1, before.Staging.Bytes), after.Staging);
        Assert.Equal(new LibraryFootprintBucket(before.Other.Files + 4, before.Other.Bytes + 68), after.Other);
        Assert.Equal(before.Database, after.Database); Assert.Equal(before.DatabaseWal, after.DatabaseWal); Assert.Equal(before.DatabaseShm, after.DatabaseShm);
        Assert.Equal(new LibraryFootprintBucket(before.Total.Files + 6, before.Total.Bytes + 99), after.Total);
    }
    [Fact]
    public async Task RepeatedSamplesRemainIntervalsAndDoNotAccumulatePreviouslyCountedFiles()
    {
        if (!SupportsSampling) return;
        await AddFileAsync("other.bin", 101);
        var earliest = DateTimeOffset.UtcNow; var first = await View.MeasureAsync(); var second = await View.MeasureAsync(); var latest = DateTimeOffset.UtcNow;
        Assert.True(first.Complete); Assert.True(second.Complete); AssertReconciled(first); AssertReconciled(second); Assert.Equal(Buckets(first), Buckets(second)); Assert.Equal(first.Total, second.Total);
        Assert.InRange(first.StartedAt, earliest, latest); Assert.InRange(second.CompletedAt, first.CompletedAt, latest);
    }
    [Fact]
    public async Task PreCanceledMeasurementDoesNotPoisonTheNextSampleAndDisposedViewRejectsNewWork()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await View.MeasureAsync(cancel.Token));
        var sample = await View.MeasureAsync(); if (SupportsSampling) Assert.True(sample.Complete);
        await View.DisposeAsync(); await View.DisposeAsync();
        Assert.Equal(LibraryFootprintCodes.Closed, (await Assert.ThrowsAsync<ObjectStoreException>(async () => await View.MeasureAsync())).Code);
    }
}

public sealed class LibraryFootprintContractOnReal : LibraryFootprintContractTests
{
    private readonly TempDirectory temp = new(); private ObjectStore library = null!;
    protected override bool SupportsSampling => Supported;
    protected override async Task<ILibraryFootprint> CreateAsync()
    { library = await ObjectStore.OpenLibraryAsync(DirectoryPath(temp), Quick); return library.ForFootprint(); }
    protected override Task AddFileAsync(string path, int length) { WriteSized(DirectoryPath(temp), path, length); return Task.CompletedTask; }
    protected override async Task CloseAsync() { await View.DisposeAsync(); await library.DisposeAsync(); temp.Dispose(); }
}

public sealed class LibraryFootprintContractOnFake : LibraryFootprintContractTests
{
    private InMemoryLibraryFootprint fake = null!;
    protected override Task<ILibraryFootprint> CreateAsync() { fake = new(); return Task.FromResult<ILibraryFootprint>(fake); }
    protected override Task AddFileAsync(string path, int length)
    {
        var parts = path.Split('/'); for (var i = 1; i < parts.Length; i++) fake.SetDirectory(string.Join('/', parts.Take(i)));
        fake.SetFile(path, length); return Task.CompletedTask;
    }
    protected override async Task CloseAsync() => await View.DisposeAsync();
}
