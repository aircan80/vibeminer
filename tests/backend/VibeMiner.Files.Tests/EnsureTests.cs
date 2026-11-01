using System.Globalization;
using System.Runtime.CompilerServices;
using VibeMiner.Builds;
using VibeMiner.Cdn;
using VibeMiner.Files.Testing;
using VibeMiner.ObjectAccess;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;
using Xunit;
using L3 = VibeMiner.Builds.Testing;

namespace VibeMiner.Files.Tests;

/// <summary>The real <see cref="IBuildFiles.EnsureAsync"/> over the layer 3 and layer 2 fakes (D137): what it asks layer 2.</summary>
public sealed class EnsureTests
{
    private static CancellationToken Ct => CancellationToken.None;
    private static readonly string[] EnUs = ["enUS"];
    private static readonly CdnContext Cdn = new(new CdnRoute(["us.cdn.blizzard.com"], "tpr/wow"));

    private static async Task<IBuildFiles> OpenAsync(IFileSystem files)
    {
        var opened = await files.ForAsync(FilesFixture.Build, "enUS", Ct);
        Assert.Equal(FilesOpenState.Open, opened.State);
        return opened.Files!;
    }

    private static FakeFile[] Distinct(int count, Func<uint, FileStorage>? storage = null) =>
    [
        .. Enumerable.Range(1, count).Select(i => new FakeFile((uint)i, Content: BlteBuilder.Bytes(200, 100 + i), Storage: storage?.Invoke((uint)i) ?? FileStorage.NotStored)),
    ];

    private static CdnKey Key(char prefix, uint n) => CdnKey.Parse(prefix + n.ToString("x31", CultureInfo.InvariantCulture));

    [Fact]
    public async Task Files_sharing_an_encoded_copy_fetch_it_once()
    {
        var same = BlteBuilder.Bytes(300, 9);
        var (fs, _, objects) = FilesFixture.Real(FilesFixture.Build, EnUs, [new(1, Content: same), new(2, Content: same), new(3, Content: BlteBuilder.Bytes(300, 10))]);
        await using var files = await OpenAsync(fs);

        var result = await files.EnsureAsync(EnsureSelection.Many([1, 2, 3]), EnsurePriority.Background, null, Ct);

        Assert.Equal(EnsureOutcome.Completed, result.Outcome);
        Assert.All(result.Files, f => Assert.Equal(FileEnsureState.Stored, f.State));
        Assert.Equal(2, objects.Asked.Count);
        Assert.Equal(2, objects.Fetched.Distinct().Count());
        Assert.Equal(2, objects.Fetched.Count);
    }

    [Theory]
    [InlineData(EnsurePriority.Interactive, FetchPriority.Interactive)]
    [InlineData(EnsurePriority.Background, FetchPriority.Background)]
    public async Task The_priority_reaches_layer_2(EnsurePriority priority, FetchPriority expected)
    {
        var (fs, _, objects) = FilesFixture.Real(FilesFixture.Build, EnUs, Distinct(3));
        await using var files = await OpenAsync(fs);
        await files.EnsureAsync(EnsureSelection.Matching(FileFilter.All), priority, null, Ct);
        Assert.Equal(3, objects.Asked.Count);
        Assert.All(objects.Asked, a => Assert.Equal(expected, a.Priority));
    }

    [Fact]
    public async Task Requests_carry_the_declared_size_location_and_cdn_context()
    {
        var builds = new L3.InMemoryBuilds();
        var location = new ArchiveLocation(Key('a', 1), 4096, 64);
        builds.Add(FilesFixture.Build, new L3.FakeBuild(FilesFixture.Metadata(EnUs, 2),
        [
            new(1, null, [new FileVariant(0x2, 0, Key('c', 1), 1000, Key('e', 1), 64, location)]),
            new(2, null, [new FileVariant(0x2, 0, Key('c', 2), 1000, Key('e', 2), 80, null)]),
        ], Cdn));
        var spy = new SpyObjects(new InMemoryObjectSource());
        await using var files = await OpenAsync(new FileSystem(builds, spy));

        var result = await files.EnsureAsync(EnsureSelection.Many([1, 2]), EnsurePriority.Interactive, null, Ct);

        var (requests, cdn, priority) = Assert.Single(spy.Calls);
        Assert.Equal((Cdn, FetchPriority.Interactive), (cdn, priority));
        Assert.Equal(new ObjectRequest(FilesFixture.ObjectKeyOf(1), 64, [location]).Key, requests[0].Key);
        Assert.Equal((64L, location), (requests[0].DeclaredLength, Assert.Single(requests[0].Locations!)));
        Assert.Equal((80L, null), (requests[1].DeclaredLength, requests[1].Locations));
        // The fake layer 2 has no bytes for them: a dated 404.
        Assert.All(result.Files, f => Assert.Equal((FileEnsureState.Missing, "CDN_NOT_FOUND"), (f.State, f.Code)));
        Assert.Equal((144L, 0L), (result.Final.BytesTotal, result.Final.BytesDone));
    }

    [Fact]
    public async Task Bytes_are_the_encoded_sizes_encoding_declares()
    {
        var real = FilesFixture.Real(FilesFixture.Build, EnUs, Distinct(3, id => id == 3 ? FileStorage.Stored : FileStorage.NotStored));
        await using var files = await OpenAsync(real.Files);
        var result = await files.EnsureAsync(EnsureSelection.Many([1, 2, 3]), EnsurePriority.Background, null, Ct);
        var build = (await real.Builds.OpenAsync(FilesFixture.Build, Ct)).Opened!;
        var sizes = new uint[] { 1, 2, 3 }.Sum(id => build.Resolve(id).Variants[0].EncodedSize!.Value);
        Assert.Equal((sizes, sizes), (result.Final.BytesTotal, result.Final.BytesDone));
        Assert.Equal(0, result.Final.CdnBytes);
    }

    [Fact]
    public async Task Each_batch_is_one_index_check_and_one_fetch_call()
    {
        var spy = SpyOver(Distinct(5, id => id == 4 ? FileStorage.Stored : FileStorage.NotStored), out var fs, presenceBatch: 2);
        await using var files = await OpenAsync(fs);

        var result = await files.EnsureAsync(EnsureSelection.Many([1, 2, 3, 4, 5]), EnsurePriority.Background, null, Ct);

        Assert.Equal(EnsureOutcome.Completed, result.Outcome);
        Assert.Equal(3, spy.HasKeysCalls);
        // Batches {1, 2}, {3, 4} (4 is stored) and {5}.
        Assert.Equal([2, 1, 1], spy.Calls.Select(c => c.Requests.Count));

        // Matching uses the listing's own index checks: one per listing batch, none more.
        spy.Reset();
        var again = await files.EnsureAsync(EnsureSelection.Matching(FileFilter.All), EnsurePriority.Background, null, Ct);
        Assert.All(again.Files, f => Assert.Equal(FileEnsureState.AlreadyStored, f.State));
        Assert.Equal(3, spy.HasKeysCalls);
        Assert.Empty(spy.Calls);
    }

    [Fact]
    public async Task Matching_reads_no_stored_bytes()
    {
        var (fs, _, _) = FilesFixture.Real(FilesFixture.Build, EnUs, Distinct(4, id => id % 2 == 0 ? FileStorage.Stored : FileStorage.NotStored));
        await using var files = await OpenAsync(fs);
        var result = await files.EnsureAsync(EnsureSelection.Matching(FileFilter.All), EnsurePriority.Background, null, Ct);
        Assert.Equal(EnsureOutcome.Completed, result.Outcome);
        Assert.Equal(0, ((BuildFiles)files).DecodedChunks);
    }

    [Fact]
    public async Task An_unsupported_object_is_unsupported_not_refused()
    {
        var real = FilesFixture.Real(FilesFixture.Build, EnUs, Distinct(2));
        real.Objects.Unsupported(real.KeyOf(1));
        await using var files = await OpenAsync(real.Files);
        var result = await files.EnsureAsync(EnsureSelection.Many([1, 2]), EnsurePriority.Background, null, Ct);
        Assert.Equal(EnsureOutcome.Incomplete, result.Outcome);
        Assert.Equal([(FileEnsureState.Unsupported, "OBJECT_LOCATION_TOO_LARGE"), (FileEnsureState.Stored, (string?)null)], result.Files.Select(f => (f.State, f.Code)));
    }

    [Fact]
    public async Task A_refused_object_is_refused_with_its_code()
    {
        var real = FilesFixture.Real(FilesFixture.Build, EnUs, Distinct(2));
        real.Objects.Refuse(real.KeyOf(1), "OBJECT_KEY_MISMATCH");
        await using var files = await OpenAsync(real.Files);
        var result = await files.EnsureAsync(EnsureSelection.Many([1, 2]), EnsurePriority.Background, null, Ct);
        Assert.Equal(EnsureOutcome.Incomplete, result.Outcome);
        Assert.Equal([(FileEnsureState.Refused, "OBJECT_KEY_MISMATCH"), (FileEnsureState.Stored, (string?)null)], result.Files.Select(f => (f.State, f.Code)));
    }

    [Fact]
    public async Task A_layer_2_failure_that_is_not_cancellation_throws()
    {
        var spy = SpyOver(Distinct(2), out var fs);
        spy.FailGetKeys = new IOException("The store is gone.");
        await using var files = await OpenAsync(fs);
        var reports = new List<EnsureProgress>();
        var thrown = await Assert.ThrowsAsync<IOException>(() => files.EnsureAsync(EnsureSelection.Many([1, 2]), EnsurePriority.Background,
            new SyncProgress(reports), Ct));
        Assert.Equal("The store is gone.", thrown.Message);
    }

    [Fact]
    public async Task Canceled_while_listing_a_matching_selection_keeps_what_was_listed()
    {
        var spy = SpyOver(Distinct(5), out var fs, presenceBatch: 2);
        await using var files = await OpenAsync(fs);
        using var cts = new CancellationTokenSource();
        // The second fetch call (files 3 and 4) cancels.
        spy.OnGetKeys = n => { if (n == 2) cts.Cancel(); };
        var result = await files.EnsureAsync(EnsureSelection.Matching(FileFilter.All), EnsurePriority.Background, null, cts.Token);

        Assert.Equal(EnsureOutcome.Canceled, result.Outcome);
        Assert.False(result.Final.Listed);
        Assert.Equal((uint?[])[1, 2, 3, 4], result.Files.Select(f => f.FileDataId));
        Assert.Equal([FileEnsureState.Stored, FileEnsureState.Stored, FileEnsureState.Canceled, FileEnsureState.Canceled], result.Files.Select(f => f.State));
    }

    [Fact]
    public async Task A_blocked_subscriber_holds_one_report_and_the_final_not_a_backlog()
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var seen = new List<EnsureProgress>();
        var blocking = new BlockingProgress(seen, first, release);
        // A clock that moves a second per read: every offer passes the interval.
        var tally = new EnsureTally(blocking, new StepClock(), TimeSpan.FromMilliseconds(100));
        tally.Offer();
        await first.Task.WaitAsync(TimeSpan.FromSeconds(30));
        for (var i = 0; i < 10_000; i++) { tally.Settle(tally.Add((uint)i, (uint)i, 1), FileEnsureState.Stored, null, 1); tally.Offer(); }
        tally.Listed();
        var finishing = tally.FinishAsync(canceled: false);
        // Only the report in flight holds the call up.
        Assert.False(finishing.IsCompleted);
        release.Set();
        var result = await finishing.WaitAsync(TimeSpan.FromSeconds(30));

        // The first report, then only the final: every report in between was replaced while the subscriber was blocked.
        lock (seen)
        {
            Assert.Equal(2, seen.Count);
            Assert.Equal(new EnsureProgress(0, 0, 0, 0, 0, false), seen[0]);
            Assert.Equal(result.Final, seen[1]);
        }
        Assert.Equal(new EnsureProgress(10_000, 10_000, 10_000, 10_000, 0, true), result.Final);
    }

    [Fact]
    public void A_progress_interval_must_be_positive()
    {
        var (builds, objects) = (new L3.InMemoryBuilds(), new InMemoryObjectSource());
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileSystem(builds, objects, new FileSystemOptions { ProgressInterval = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FileSystem(builds, objects, new FileSystemOptions { ProgressInterval = TimeSpan.FromMilliseconds(-1) }));
        var fake = new InMemoryFileSystem();
        Assert.Throws<ArgumentOutOfRangeException>(() => fake.ProgressInterval = TimeSpan.Zero);
    }

    private sealed class StepClock : TimeProvider
    {
        private long now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Add(ref now, TimeSpan.TicksPerSecond);
    }

    /// <summary>Blocks its first report until released.</summary>
    private sealed class BlockingProgress(List<EnsureProgress> seen, TaskCompletionSource first, ManualResetEventSlim release) : IProgress<EnsureProgress>
    {
        public void Report(EnsureProgress value)
        {
            bool isFirst;
            lock (seen) { seen.Add(value); isFirst = seen.Count == 1; }
            if (!isFirst) return;
            first.SetResult();
            // The drain runs on a pool thread, so blocking it here stands in for a slow subscriber.
            release.Wait(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public void A_file_outcome_is_40_bytes()
    {
        Assert.Equal(40, Unsafe.SizeOf<FileEnsured>());
    }

    private static SpyObjects SpyOver(IReadOnlyList<FakeFile> fakes, out FileSystem fs, int presenceBatch = 4096)
    {
        var real = FilesFixture.Real(FilesFixture.Build, EnUs, fakes);
        var spy = new SpyObjects(real.Objects);
        fs = new FileSystem(real.Builds, spy, new FileSystemOptions { PresenceBatch = presenceBatch });
        return spy;
    }

    private sealed class SyncProgress(List<EnsureProgress> seen) : IProgress<EnsureProgress>
    {
        public void Report(EnsureProgress value) { lock (seen) seen.Add(value); }
    }

    /// <summary>Layer 2 that records index checks and fetch calls, and can fail or act on a fetch call.</summary>
    private sealed class SpyObjects(IObjectSource inner) : IObjectSource
    {
        private readonly List<(IReadOnlyList<ObjectRequest> Requests, CdnContext? Cdn, FetchPriority Priority)> calls = [];
        private int hasKeys;

        public Exception? FailGetKeys { get; set; }
        /// <summary>Called with the 1-based number of each fetch call, before it runs.</summary>
        public Action<int>? OnGetKeys { get; set; }
        public int HasKeysCalls => Volatile.Read(ref hasKeys);
        public IReadOnlyList<(IReadOnlyList<ObjectRequest> Requests, CdnContext? Cdn, FetchPriority Priority)> Calls { get { lock (calls) return [.. calls]; } }
        public void Reset() { lock (calls) calls.Clear(); Volatile.Write(ref hasKeys, 0); }

        public Task<IReadOnlyList<bool>> HasKeysAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref hasKeys);
            return inner.HasKeysAsync(keys, cancellationToken);
        }

        public Task<IReadOnlyList<ObjectResult>> GetKeysAsync(IReadOnlyList<ObjectRequest> requests, CdnContext? cdn, FetchPriority priority,
            IProgress<ObjectProgress>? progress, CancellationToken cancellationToken)
        {
            int n;
            lock (calls) { calls.Add(([.. requests], cdn, priority)); n = calls.Count; }
            OnGetKeys?.Invoke(n);
            if (FailGetKeys is { } failure) throw failure;
            return inner.GetKeysAsync(requests, cdn, priority, progress, cancellationToken);
        }

        public Task<ObjectResult> GetKeyAsync(ObjectRequest request, CdnContext? cdn, FetchPriority priority, CancellationToken cancellationToken) =>
            inner.GetKeyAsync(request, cdn, priority, cancellationToken);

        public Task<ObjectResult> GetRangeAsync(ObjectRequest request, long offset, Memory<byte> destination, CdnContext? cdn, FetchPriority priority,
            CancellationToken cancellationToken) =>
            inner.GetRangeAsync(request, offset, destination, cdn, priority, cancellationToken);

        public Task<ObservationResult> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken) => inner.ObserveAsync(request, cancellationToken);

        public Task<ObservationResult> RecordAsync(Observation observation, CancellationToken cancellationToken) => inner.RecordAsync(observation, cancellationToken);
        public Task<long> CountPresentAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken) => inner.CountPresentAsync(keys, cancellationToken);

        public Task<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) => inner.ReadHistoryAsync(query, cancellationToken);
    }
}
