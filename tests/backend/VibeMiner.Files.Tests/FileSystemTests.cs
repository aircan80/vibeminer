using System.Diagnostics;
using System.Globalization;
using VibeMiner.Builds;
using VibeMiner.Cdn;
using VibeMiner.Files.Testing;
using VibeMiner.ObjectAccess;
using VibeMiner.ObjectAccess.Testing;
using VibeMiner.Objects;
using Xunit;
using Xunit.Abstractions;
using L3 = VibeMiner.Builds.Testing;

namespace VibeMiner.Files.Tests;

/// <summary>The real <see cref="FileSystem"/> over the layer 3 and layer 2 fakes (D137): batching, variant pick, open states, failures.</summary>
public sealed class FileSystemTests(ITestOutputHelper output)
{
    private static CancellationToken Ct => CancellationToken.None;
    private static readonly string[] EnUs = ["enUS"];
    private const uint EnUsBit = 0x2, DeDeBit = 0x20;
    private const ulong LowViolence = 0x80, Encrypted = 0x8000000;

    private static FakeFile[] Files(int count, Func<uint, FileStorage>? storage = null) =>
        Enumerable.Range(1, count).Select(i => new FakeFile((uint)i, Size: i, Storage: storage?.Invoke((uint)i) ?? FileStorage.NotStored)).ToArray();

    private static async Task<IBuildFiles> OpenAsync(IFileSystem files, BuildRef build, string locale = "enUS")
    {
        var opened = await files.ForAsync(build, locale, Ct);
        Assert.Equal(FilesOpenState.Open, opened.State);
        return opened.Files!;
    }

    private static async Task<List<BuildFile>> ListAllAsync(IBuildFiles files, FileFilter? filter = null)
    {
        var all = new List<BuildFile>();
        await foreach (var f in files.ListAsync(filter ?? FileFilter.All, Ct)) all.Add(f);
        return all;
    }

    private static CdnKey Key(char prefix, uint n) => CdnKey.Parse(prefix + n.ToString("x31", CultureInfo.InvariantCulture));

    private static FileVariant Variant(uint locales, uint n, bool encoded = true, ulong flags = 0) =>
        new(locales, flags, Key('c', n), 1000 + n, encoded ? Key('e', n) : null, encoded ? 64 : null, null);

    /// <summary>The real file system over a hand-built layer 3 build of the given files.</summary>
    private static (FileSystem Files, L3.InMemoryBuilds Builds, InMemoryObjectSource Objects) RealOf(params L3.FakeFile[] files)
    {
        var builds = new L3.InMemoryBuilds();
        builds.Add(FilesFixture.Build, new L3.FakeBuild(FilesFixture.Metadata(["enUS", "deDE"], files.Length), files));
        var objects = new InMemoryObjectSource();
        return (new FileSystem(builds, objects), builds, objects);
    }

    [Fact]
    public async Task Listing_checks_stored_state_in_batches_and_never_fetches_content()
    {
        var (fs, builds, objects) = FilesFixture.Real(FilesFixture.Build, EnUs, Files(1000, id => id % 3 == 0 ? FileStorage.Stored : FileStorage.NotStored),
            new FileSystemOptions { PresenceBatch = 100 });
        var files = await OpenAsync(fs, FilesFixture.Build);

        var all = await ListAllAsync(files);
        Assert.Equal(1000, all.Count);
        Assert.Equal(Enumerable.Range(1, 1000).Select(i => (uint)i), all.Select(f => f.FileDataId));
        Assert.Equal(333, all.Count(f => f.Storage == FileStorage.Stored));
        Assert.Equal(10, objects.HasKeysCalls);

        var stat = await files.StatAsync(3u, Ct);
        Assert.Equal(FileStorage.Stored, stat.File!.Storage);
        Assert.Equal(11, objects.HasKeysCalls);

        Assert.True(await files.ExistsAsync(4u, Ct));
        Assert.False(await files.ExistsAsync(5000u, Ct));
        Assert.Equal(11, objects.HasKeysCalls);

        // Only stored copies are read (for their format), and nothing is fetched.
        Assert.Empty(objects.Fetched);
        var stored = Enumerable.Range(1, 1000).Where(i => i % 3 == 0).Select(i => FilesFixture.ObjectKeyOf((uint)i)).ToHashSet();
        Assert.All(objects.Asked, a => Assert.Contains(a.Key, stored));

        await OpenAsync(fs, FilesFixture.Build);
        Assert.Equal(1, builds.Opens);
    }

    [Fact]
    public async Task The_locale_reads_the_first_encoded_variant_that_is_not_low_violence()
    {
        var (fs, _, _) = RealOf(new L3.FakeFile(1, null, [
            Variant(DeDeBit, 1), Variant(EnUsBit, 2, encoded: false), Variant(EnUsBit, 3, flags: LowViolence), Variant(EnUsBit | DeDeBit, 4),
            Variant(EnUsBit, 5)]));
        var files = await OpenAsync(fs, FilesFixture.Build);

        var stat = await files.StatAsync(1u, Ct);
        Assert.Equal(FileLookupState.Found, stat.State);
        Assert.Equal(Key('c', 4).Hex, stat.File!.ContentKey);
        Assert.Equal(1004, stat.File.Size);
    }

    [Fact]
    public async Task Without_a_normal_variant_the_locale_reads_the_first_encoded_one()
    {
        var (fs, _, _) = RealOf(new L3.FakeFile(1, null, [
            Variant(DeDeBit, 1), Variant(EnUsBit, 2, encoded: false), Variant(EnUsBit, 3, flags: LowViolence), Variant(EnUsBit, 4, flags: LowViolence)]));
        var files = await OpenAsync(fs, FilesFixture.Build);

        var stat = await files.StatAsync(1u, Ct);
        Assert.Equal(FileLookupState.Found, stat.State);
        Assert.Equal(Key('c', 3).Hex, stat.File!.ContentKey);
        Assert.Equal(1003, stat.File.Size);
    }

    [Fact]
    public async Task Without_an_encoded_variant_in_the_locale_the_first_one_reads_as_not_in_encoding()
    {
        // The deDE variant is encoded, so layer 3 calls the file Found; enUS still has no encoded copy.
        var (fs, _, _) = RealOf(new L3.FakeFile(1, null, [
            Variant(DeDeBit, 1), Variant(EnUsBit, 2, encoded: false), Variant(EnUsBit, 3, encoded: false)]));
        var files = await OpenAsync(fs, FilesFixture.Build);

        var stat = await files.StatAsync(1u, Ct);
        Assert.Equal(FileLookupState.NotInEncoding, stat.State);
        Assert.Equal(Key('c', 2).Hex, stat.File!.ContentKey);
        Assert.Null(stat.File.Size);
        Assert.Equal(FileStorage.NoEncodedCopy, stat.File.Storage);

        var de = await OpenAsync(fs, FilesFixture.Build, "deDE");
        Assert.Equal(Key('c', 1).Hex, (await de.StatAsync(1u, Ct)).File!.ContentKey);
    }

    [Fact]
    public async Task A_file_only_in_another_locale_is_not_in_locale()
    {
        var (fs, _, objects) = RealOf(new L3.FakeFile(1, null, [Variant(DeDeBit, 1)]));
        var files = await OpenAsync(fs, FilesFixture.Build);

        Assert.Equal(new FileStat(FileLookupState.NotInLocale, null), await files.StatAsync(1u, Ct));
        Assert.False(await files.ExistsAsync(1u, Ct));
        Assert.Empty(await ListAllAsync(files));
        Assert.Equal(0, objects.HasKeysCalls);
    }

    [Fact]
    public async Task Encrypted_comes_from_the_picked_variant_only()
    {
        var (fs, _, _) = RealOf(
            new L3.FakeFile(1, null, [Variant(DeDeBit, 1, flags: Encrypted), Variant(EnUsBit, 2, flags: Encrypted | LowViolence), Variant(EnUsBit, 3)]),
            new L3.FakeFile(2, null, [Variant(DeDeBit, 4), Variant(EnUsBit, 5, flags: LowViolence), Variant(EnUsBit, 6, flags: Encrypted)]));
        var files = await OpenAsync(fs, FilesFixture.Build);

        Assert.False((await files.StatAsync(1u, Ct)).File!.Encrypted);
        Assert.True((await files.StatAsync(2u, Ct)).File!.Encrypted);
        Assert.Equal([2u], (await ListAllAsync(files, new FileFilter { Encrypted = true })).Select(f => f.FileDataId));
        Assert.Equal([1u], (await ListAllAsync(files, new FileFilter { Encrypted = false })).Select(f => f.FileDataId));
    }

    [Theory]
    [InlineData(BuildOpenState.NeedsNetwork)]
    [InlineData(BuildOpenState.Unavailable)]
    [InlineData(BuildOpenState.Missing)]
    [InlineData(BuildOpenState.Encrypted)]
    [InlineData(BuildOpenState.Corrupt)]
    [InlineData(BuildOpenState.Unsupported)]
    [InlineData(BuildOpenState.StoreFailed)]
    public async Task A_layer_3_open_failure_maps_to_the_same_state_with_its_code_and_time(BuildOpenState state)
    {
        var at = new DateTimeOffset(2026, 10, 30, 8, 0, 0, TimeSpan.Zero);
        var builds = new L3.InMemoryBuilds().Fail(FilesFixture.Build, state, BuildPart.Root, "CODE_" + state, at);
        var fs = new FileSystem(builds, new InMemoryObjectSource());

        var opened = await fs.ForAsync(FilesFixture.Build, "enUS", Ct);
        Assert.Equal(Enum.Parse<FilesOpenState>(state.ToString()), opened.State);
        Assert.Null(opened.Files);
        Assert.Equal("CODE_" + state, opened.Code);
        Assert.Equal(at, opened.ObservedAt);
        Assert.Equal(FilesFixture.Build, opened.Build);
        Assert.Equal("enUS", opened.Locale);
    }

    [Fact]
    public async Task Cancelling_mid_list_ends_the_next_read()
    {
        var (fs, _, _) = FilesFixture.Real(FilesFixture.Build, EnUs, Files(1000), new FileSystemOptions { PresenceBatch = 100 });
        var files = await OpenAsync(fs, FilesFixture.Build);
        using var cts = new CancellationTokenSource();

        var items = files.ListAsync(FileFilter.All, cts.Token).GetAsyncEnumerator(Ct);
        try
        {
            Assert.True(await items.MoveNextAsync());
            Assert.Equal(1u, items.Current.FileDataId);
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await items.MoveNextAsync());
        }
        finally { await items.DisposeAsync(); }
    }

    [Fact]
    public async Task A_narrow_filter_stays_cancellable_while_it_walks()
    {
        using var cts = new CancellationTokenSource();
        var walked = 0;
        var builds = new CountingBuilds(new L3.InMemoryBuilds().Add(FilesFixture.Build,
            new L3.FakeBuild(FilesFixture.Metadata(EnUs, 50_000), Files(50_000).Select(FilesFixture.ToLayer3).ToArray())),
            () => { if (++walked == 2000) cts.Cancel(); });
        var files = await OpenAsync(new FileSystem(builds, new InMemoryObjectSource()), FilesFixture.Build);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in files.ListAsync(FileFilter.All with { Encrypted = true }, cts.Token)) { }
        });
        Assert.InRange(walked, 2000, 2000 + 1024);
    }

    [Fact]
    public async Task The_handle_reads_the_variant_that_is_not_a_high_resolution_texture()
    {
        const ulong HighRes = 0x1;
        var (fs, _, _) = RealOf(
            new L3.FakeFile(1, null, [Variant(EnUsBit, 1, flags: HighRes), Variant(EnUsBit, 2), Variant(EnUsBit, 3, flags: LowViolence)]),
            new L3.FakeFile(2, null, [Variant(EnUsBit, 4, flags: LowViolence), Variant(EnUsBit, 5, flags: HighRes)]));
        var files = await OpenAsync(fs, FilesFixture.Build);

        Assert.Equal(Key('c', 2).Hex, (await files.StatAsync(1u, Ct)).File!.ContentKey);
        Assert.Equal(Key('c', 5).Hex, (await files.StatAsync(2u, Ct)).File!.ContentKey);
    }

    [Fact]
    public async Task Every_locale_name_has_the_same_bit_as_layer_3()
    {
        Assert.Equal(15, FileLocales.Names.Count);
        foreach (var locale in FileLocales.Names)
        {
            var builds = new L3.InMemoryBuilds().Add(FilesFixture.Build, new L3.FakeBuild(FilesFixture.Metadata([locale], 1),
                [new L3.FakeFile(7, null, [Variant(FileLocales.BitOf(locale), 7)])]));
            var files = await OpenAsync(new FileSystem(builds, new InMemoryObjectSource()), FilesFixture.Build, locale);
            var listed = await ListAllAsync(files);
            Assert.Equal((uint[])[7u], listed.Select(f => f.FileDataId).ToArray());
        }
    }

    /// <summary>Layer 3's fake with a callback on every Resolve.</summary>
    private sealed class CountingBuilds(IBuilds inner, Action onResolve) : IBuilds
    {
        public async Task<BuildOpenResult> OpenAsync(BuildRef build, CancellationToken cancellationToken)
        {
            var result = await inner.OpenAsync(build, cancellationToken);
            return result.Opened is { } opened ? result with { Opened = new Counting(opened, onResolve) } : result;
        }

        private sealed class Counting(IBuild inner, Action onResolve) : IBuild
        {
            public BuildRef Build => inner.Build;
            public BuildMetadata Metadata => inner.Metadata;
            public VibeMiner.ObjectAccess.CdnContext? Cdn => inner.Cdn;
            public FileResolution Resolve(uint fileDataId) { onResolve(); return inner.Resolve(fileDataId); }
            public FileResolution Resolve(string path) => inner.Resolve(path);
            public IEnumerable<uint> FileDataIds(string locale, uint fromId = 0) => inner.FileDataIds(locale, fromId);
        }
    }

    [Fact]
    public async Task A_layer_2_failure_while_checking_stored_state_propagates()
    {
        var builds = new L3.InMemoryBuilds();
        builds.Add(FilesFixture.Build, new L3.FakeBuild(FilesFixture.Metadata(EnUs, 10), Files(10).Select(FilesFixture.ToLayer3).ToArray()));
        var fs = new FileSystem(builds, new FailingHasKeys(new InMemoryObjectSource()), new FileSystemOptions { PresenceBatch = 4 });
        var files = await OpenAsync(fs, FilesFixture.Build);

        var listed = await Assert.ThrowsAsync<IOException>(() => ListAllAsync(files));
        Assert.Equal(FailingHasKeys.Message, listed.Message);
        var stat = await Assert.ThrowsAsync<IOException>(() => files.StatAsync(1u, Ct));
        Assert.Equal(FailingHasKeys.Message, stat.Message);
        Assert.True(await files.ExistsAsync(1u, Ct));
    }

    [Fact]
    [Trait("Speed", "Slow")]
    public async Task Measure_a_build_of_a_million_files()
    {
        const int count = 1_000_000;
        var builds = new L3.InMemoryBuilds();
        var layer3 = new L3.FakeFile[count];
        var objects = new InMemoryObjectSource();
        for (var i = 0; i < count; i++)
        {
            var id = (uint)(i + 1);
            layer3[i] = new(id, null, [new FileVariant(EnUsBit, 0, Key('c', id), id, FilesFixture.EKeyOf(id), 64, null)]);
            if (id % 10 == 0) objects.AddStored(FilesFixture.ObjectKeyOf(id), [1]);
        }
        builds.Add(FilesFixture.Build, new L3.FakeBuild(FilesFixture.Metadata(EnUs, count), layer3));
        var fs = new FileSystem(builds, objects);

        var watch = Stopwatch.StartNew();
        var files = await OpenAsync(fs, FilesFixture.Build);
        var open = watch.Elapsed;

        const int stats = 100_000;
        var random = new Random(171);
        var ids = Enumerable.Range(0, stats).Select(_ => (uint)random.Next(1, count + 1)).ToArray();
        var stored = 0;
        watch.Restart();
        foreach (var id in ids)
            if ((await files.StatAsync(id, Ct)).File!.Storage == FileStorage.Stored) stored++;
        var statMean = watch.Elapsed / stats;
        Assert.Equal(ids.Count(id => id % 10 == 0), stored);

        var before = objects.HasKeysCalls;
        long listed = 0, listedStored = 0;
        watch.Restart();
        await foreach (var f in files.ListAsync(FileFilter.All, Ct))
        {
            listed++;
            if (f.Storage == FileStorage.Stored) listedStored++;
        }
        var list = watch.Elapsed;
        var calls = objects.HasKeysCalls - before;

        output.WriteLine($"open: {open.TotalMilliseconds:F1} ms");
        output.WriteLine($"mean stat over {stats:N0} random ids: {statMean.TotalMicroseconds:F2} us");
        output.WriteLine($"full list of {listed:N0} files: {list.TotalMilliseconds:F1} ms, {calls} HasKeysAsync calls");

        Assert.Equal(count, listed);
        Assert.Equal(count / 10, listedStored);
        Assert.Equal((count + 4095) / 4096, calls);
        Assert.Empty(objects.Fetched);
        // Loose guards against a per-file re-verify or a hang, not performance claims.
        Assert.True(statMean < TimeSpan.FromMicroseconds(50), $"Mean stat took {statMean.TotalMicroseconds:F2} us.");
        Assert.True(list < TimeSpan.FromSeconds(60), $"The full list took {list.TotalSeconds:F1} s.");
    }

    /// <summary>Layer 2 whose index check fails; everything else goes to the inner source.</summary>
    private sealed class FailingHasKeys(IObjectSource inner) : IObjectSource
    {
        public const string Message = "The object index can't be read.";

        public Task<IReadOnlyList<bool>> HasKeysAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken) =>
            throw new IOException(Message);

        public Task<ObjectResult> GetKeyAsync(ObjectRequest request, CdnContext? cdn, FetchPriority priority, CancellationToken cancellationToken) =>
            inner.GetKeyAsync(request, cdn, priority, cancellationToken);

        public Task<IReadOnlyList<ObjectResult>> GetKeysAsync(IReadOnlyList<ObjectRequest> requests, CdnContext? cdn, FetchPriority priority,
            IProgress<ObjectProgress>? progress, CancellationToken cancellationToken) =>
            inner.GetKeysAsync(requests, cdn, priority, progress, cancellationToken);

        public Task<ObjectResult> GetRangeAsync(ObjectRequest request, long offset, Memory<byte> destination, CdnContext? cdn, FetchPriority priority,
            CancellationToken cancellationToken) =>
            inner.GetRangeAsync(request, offset, destination, cdn, priority, cancellationToken);

        public Task<ObservationResult> ObserveAsync(ObservationRequest request, CancellationToken cancellationToken) =>
            inner.ObserveAsync(request, cancellationToken);

        public Task<long> CountPresentAsync(IReadOnlyList<ObjectKey> keys, CancellationToken cancellationToken) =>
            inner.CountPresentAsync(keys, cancellationToken);

        public Task<IReadOnlyList<HistoryEntry>> ReadHistoryAsync(HistoryQuery query, CancellationToken cancellationToken) =>
            inner.ReadHistoryAsync(query, cancellationToken);
    }
}
