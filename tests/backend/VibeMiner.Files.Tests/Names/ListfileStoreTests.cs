using System.Security.Cryptography;
using System.Text;
using VibeMiner.Files.Names;
using VibeMiner.Generated;
using VibeMiner.Generated.RecordFiles;
using VibeMiner.Generated.Testing;
using Xunit;

namespace VibeMiner.Files.Tests.Names;

/// <summary>The persisted listfile names index (#186) over the in-memory generated store, with authored listfiles only.</summary>
public sealed class ListfileStoreTests
{
    private const string Sample = "1;Interface/Icons/Alpha.blp\n5;world/maps/Azeroth/Azeroth.wdt\n900;Sound/Music/Theme.mp3\n901;SOUND\\music\\theme.MP3\n";
    private static readonly DateTimeOffset Published = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Fetched = new(2026, 10, 2, 8, 30, 0, TimeSpan.Zero);

    private static ListfileStoreRelease Release(byte[] csv, string tag = "202610011200") =>
        new(tag, Convert.ToHexStringLower(SHA256.HashData(csv)), Published, Fetched);

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks(byte[] csv, int size = 7)
    {
        for (var at = 0; at < csv.Length; at += size)
        {
            await Task.Yield();
            yield return csv.AsMemory(at, Math.Min(size, csv.Length - at));
        }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Unread()
    {
        await Task.Yield();
        throw new InvalidOperationException("The CSV was read.");
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static Task<ListfileStore> Build(InMemoryGeneratedStore store, string text, ListfileStore.PathHasher? hasher = null) =>
        Build(store, Encoding.UTF8.GetBytes(text), hasher);

    private static Task<ListfileStore> Build(InMemoryGeneratedStore store, byte[] csv, ListfileStore.PathHasher? hasher = null, int chunk = 7) =>
        ListfileStore.BuildAsync(store, Release(csv), Chunks(csv, chunk), hasher: hasher);

    private static async Task<List<(uint Id, string Name)>> Walk(ListfileStore index, uint from, int max = int.MaxValue)
    {
        var result = new List<(uint, string)>();
        await foreach (var entry in index.FromAsync(from))
        {
            result.Add(entry);
            if (result.Count == max) break;
        }
        return result;
    }

    [Fact]
    public async Task BuildsAndOpensWithItsMetadata()
    {
        var store = new InMemoryGeneratedStore();
        var csv = Encoding.UTF8.GetBytes(Sample);
        var built = await Build(store, csv);
        var opened = await ListfileStore.OpenAsync(store, built.Tag, built.Sha256);
        Assert.Equal(GeneratedReadState.Present, opened.State);
        var index = opened.Index!;
        Assert.Equal(4, index.Count);
        Assert.Equal("202610011200", index.Tag);
        Assert.Equal(Release(csv).Sha256, index.Sha256);
        Assert.Equal(Published, index.PublishedAt);
        Assert.Equal(Fetched, index.FetchedAt);
        Assert.Equal(built.Seed, index.Seed);
        var (names, paths) = ListfileStore.ManifestKeys(index.Tag, index.Sha256);
        Assert.Contains(names, index.Keys);
        Assert.Contains(paths, index.Keys);
        Assert.Equal(store.Keys.OrderBy(k => k), index.Keys.OrderBy(k => k));
    }

    [Fact]
    public async Task NamesFileDataIds()
    {
        var index = await Build(new InMemoryGeneratedStore(), Sample + "4294967295;last\n");
        Assert.Equal("Interface/Icons/Alpha.blp", await index.NameOfAsync(1));
        Assert.Equal("world/maps/Azeroth/Azeroth.wdt", await index.NameOfAsync(5));
        Assert.Equal("SOUND\\music\\theme.MP3", await index.NameOfAsync(901));
        Assert.Equal("last", await index.NameOfAsync(uint.MaxValue));
        foreach (var miss in new uint[] { 0, 2, 4, 6, 899, 902, uint.MaxValue - 1 }) Assert.Null(await index.NameOfAsync(miss));
    }

    [Fact]
    public async Task FindsPathsIgnoringCaseAndSlashes()
    {
        var index = await Build(new InMemoryGeneratedStore(), Sample);
        Assert.Equal([1u], await index.IdsOfAsync("interface\\ICONS\\alpha.BLP"));
        Assert.Equal([5u], await index.IdsOfAsync("World/Maps/azeroth/azeroth.wdt"));
        // Two FileDataIDs share one folded name: both, ascending.
        Assert.Equal([900u, 901u], await index.IdsOfAsync("sound/music/theme.mp3"));
        Assert.Empty(await index.IdsOfAsync("sound/music/theme.mp"));
        Assert.Empty(await index.IdsOfAsync("nothing"));
        Assert.Empty(await index.IdsOfAsync(""));
        Assert.Empty(await index.IdsOfAsync(new string('a', ListfileParser.MaxNameBytes + 1)));
        Assert.Empty(await index.IdsOfAsync("\ud800"));
    }

    [Fact]
    public async Task CollisionsNeverGiveAWrongId()
    {
        // Every name lands on one hash: the paths file holds a single record and each candidate is confirmed by its name.
        var store = new InMemoryGeneratedStore();
        var index = await Build(store, Sample, (_, _) => 7);
        Assert.Equal([1u], await index.IdsOfAsync("INTERFACE/icons/alpha.blp"));
        Assert.Equal([900u, 901u], await index.IdsOfAsync("Sound/Music/Theme.mp3"));
        Assert.Empty(await index.IdsOfAsync("Sound/Music/Theme.ogg"));
        // A reader with another hash finds nothing rather than something wrong.
        var other = (await ListfileStore.OpenAsync(store, index.Tag, index.Sha256)).Index!;
        Assert.Empty(await other.IdsOfAsync("Sound/Music/Theme.mp3"));
    }

    [Fact]
    public async Task WalksFromAnyFileDataId()
    {
        var index = await Build(new InMemoryGeneratedStore(), Sample);
        Assert.Equal([1u, 5u, 900u, 901u], (await Walk(index, 0)).Select(e => e.Id));
        Assert.Equal([5u, 900u, 901u], (await Walk(index, 5)).Select(e => e.Id));
        Assert.Equal([900u, 901u], (await Walk(index, 6)).Select(e => e.Id));
        Assert.Equal(("Sound/Music/Theme.mp3"), (await Walk(index, 6))[0].Name);
        Assert.Empty(await Walk(index, 902));
        Assert.Empty(await Walk(index, uint.MaxValue));
    }

    [Fact]
    public async Task APresentIndexIsKeptWithoutReadingTheCsv()
    {
        var store = new InMemoryGeneratedStore();
        var csv = Encoding.UTF8.GetBytes(Sample);
        var first = await Build(store, csv);
        var again = await ListfileStore.BuildAsync(store, Release(csv), Unread());
        Assert.Equal(first.Seed, again.Seed);
    }

    [Fact]
    public async Task ARebuildRepairsACorruptReadUnderIntactManifests()
    {
        var store = new InMemoryGeneratedStore();
        var csv = Encoding.UTF8.GetBytes(Sample);
        var index = await Build(store, csv);
        var (names, paths) = ListfileStore.ManifestKeys(index.Tag, index.Sha256);
        foreach (var key in store.Keys.Where(k => k != names && k != paths)) store.Damage(key);
        // The manifests still open, so the index reads Present and only its lookups fail.
        Assert.Equal(GeneratedReadState.Present, (await ListfileStore.OpenAsync(store, index.Tag, index.Sha256)).State);
        await Assert.ThrowsAsync<GeneratedStoreException>(() => index.NameOfAsync(1));
        var repaired = await ListfileStore.BuildAsync(store, Release(csv), Chunks(csv), rebuild: true);
        Assert.NotEqual(index.Seed, repaired.Seed);
        Assert.Equal("Interface/Icons/Alpha.blp", await repaired.NameOfAsync(1));
        Assert.Equal([900u, 901u], await repaired.IdsOfAsync("sound/music/theme.mp3"));
        Assert.Equal(GeneratedReadState.Present, (await ListfileStore.OpenAsync(store, index.Tag, index.Sha256)).State);
    }

    private static readonly RecordFileOptions TinyNames = ListfileStore.NamesOptions with { BlockBytes = 256, SegmentBytes = 1024 };

    private static (byte[] A, byte[] B) TwoReleasesSharingTheirStart()
    {
        var shared = new StringBuilder();
        for (var i = 1; i <= 400; i++) shared.Append(i).Append(";World/Maps/Shared/tile_").Append(i * 7919 % 1000).Append(".adt\n");
        string Tail(string zone) { var t = new StringBuilder(); for (var i = 401; i <= 500; i++) t.Append(i).Append($";World/{zone}/x_").Append(i).Append(".m2\n"); return t.ToString(); }
        return (Encoding.UTF8.GetBytes(shared + Tail("A")), Encoding.UTF8.GetBytes(shared + Tail("B")));
    }

    private static Task<ListfileStore> BuildTiny(IGeneratedStore store, byte[] csv, string tag, bool rebuild = false) =>
        ListfileStore.BuildAsync(store, Release(csv, tag), Chunks(csv, 1000), rebuild: rebuild, namesOptions: TinyNames);

    [Fact]
    public async Task RebuildingOneReleaseLeavesAnotherThatSharesItsSegmentsReadable()
    {
        var store = new InMemoryGeneratedStore();
        var (a, b) = TwoReleasesSharingTheirStart();
        var indexB = await BuildTiny(store, b, "b");
        var indexA = await BuildTiny(store, a, "a");
        Assert.NotEmpty(indexA.Keys.Intersect(indexB.Keys));
        // A's build stopped before its commit point: only its names manifest is left.
        await store.DeleteAsync([ListfileStore.ManifestKeys("a", indexA.Sha256).Paths]);
        Assert.Equal(GeneratedReadState.Absent, (await ListfileStore.OpenAsync(store, "a", indexA.Sha256)).State);
        // A rebuild that clears A's leftovers and then fails puts nothing back, so B must survive the clearing on its own.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ListfileStore.BuildAsync(store, Release(a, "a"), Unread(), namesOptions: TinyNames));
        Assert.Equal(500, (await Walk((await ListfileStore.OpenAsync(store, "b", indexB.Sha256)).Index!, 0)).Count);
        var rebuiltA = await BuildTiny(store, a, "a");
        Assert.Equal("World/A/x_401.m2", await rebuiltA.NameOfAsync(401));
        var reopenedB = (await ListfileStore.OpenAsync(store, "b", indexB.Sha256)).Index!;
        Assert.Equal(500, (await Walk(reopenedB, 0)).Count);
        Assert.Equal("World/B/x_401.m2", await reopenedB.NameOfAsync(401));
    }

    [Fact]
    public async Task RepairingOneReleaseRestoresADamagedSegmentItSharesWithAnother()
    {
        var store = new InMemoryGeneratedStore();
        var (a, b) = TwoReleasesSharingTheirStart();
        var indexB = await BuildTiny(store, b, "b");
        var indexA = await BuildTiny(store, a, "a");
        var sharedKey = indexA.Keys.Intersect(indexB.Keys).First();
        store.Damage(sharedKey);
        await Assert.ThrowsAsync<GeneratedStoreException>(() => Walk(indexB, 0));
        // Repairing A deletes the broken shared segment and puts the same bytes back, which mends B too.
        await BuildTiny(store, a, "a", rebuild: true);
        Assert.Equal(500, (await Walk(indexB, 0)).Count);
        Assert.Equal(500, (await Walk((await ListfileStore.OpenAsync(store, "a", indexA.Sha256)).Index!, 0)).Count);
    }

    [Fact]
    public async Task AWipedStoreIsAbsentAndRebuilds()
    {
        var store = new InMemoryGeneratedStore();
        var csv = Encoding.UTF8.GetBytes(Sample);
        var release = Release(csv);
        await Build(store, csv);
        store.Clear();
        Assert.Equal(GeneratedReadState.Absent, (await ListfileStore.OpenAsync(store, release.Tag, release.Sha256)).State);
        var rebuilt = await Build(store, csv);
        Assert.Equal("Sound/Music/Theme.mp3", await rebuilt.NameOfAsync(900));
        Assert.Equal(GeneratedReadState.Present, (await ListfileStore.OpenAsync(store, release.Tag, release.Sha256)).State);
    }

    [Fact]
    public async Task OtherReleasesAreAbsent()
    {
        var store = new InMemoryGeneratedStore();
        var index = await Build(store, Sample);
        Assert.Equal(GeneratedReadState.Absent, (await ListfileStore.OpenAsync(store, "other", index.Sha256)).State);
        Assert.Equal(GeneratedReadState.Absent, (await ListfileStore.OpenAsync(store, index.Tag, new string('0', 64))).State);
    }

    [Fact]
    public async Task DamagedSegmentsReadCorruptNeverWrongNames()
    {
        var store = new InMemoryGeneratedStore();
        var index = await Build(store, Sample);
        var (names, paths) = ListfileStore.ManifestKeys(index.Tag, index.Sha256);
        foreach (var key in store.Keys.Where(k => k != names && k != paths)) store.Damage(key);
        Assert.Equal(GeneratedStoreCodes.Corrupt, (await Assert.ThrowsAsync<GeneratedStoreException>(() => index.NameOfAsync(1))).Code);
        Assert.Equal(GeneratedStoreCodes.Corrupt, (await Assert.ThrowsAsync<GeneratedStoreException>(() => index.IdsOfAsync("Sound/Music/Theme.mp3"))).Code);
        Assert.Equal(GeneratedStoreCodes.Corrupt, (await Assert.ThrowsAsync<GeneratedStoreException>(() => Walk(index, 0))).Code);

        // A rebuild deletes the damaged segments first, so content addressing doesn't keep them.
        store.Damage(paths);
        Assert.Equal(GeneratedReadState.Corrupt, (await ListfileStore.OpenAsync(store, index.Tag, index.Sha256)).State);
        var rebuilt = await Build(store, Sample);
        Assert.Equal("Interface/Icons/Alpha.blp", await rebuilt.NameOfAsync(1));
    }

    [Fact]
    public async Task ADamagedNamesManifestIsCorrupt()
    {
        var store = new InMemoryGeneratedStore();
        var index = await Build(store, Sample);
        store.Damage(ListfileStore.ManifestKeys(index.Tag, index.Sha256).Names);
        Assert.Equal(GeneratedReadState.Corrupt, (await ListfileStore.OpenAsync(store, index.Tag, index.Sha256)).State);
    }

    [Fact]
    public async Task APathsFileWithoutNamesIsCorrupt()
    {
        var store = new InMemoryGeneratedStore();
        var index = await Build(store, Sample);
        await store.DeleteAsync([ListfileStore.ManifestKeys(index.Tag, index.Sha256).Names]);
        Assert.Equal(GeneratedReadState.Corrupt, (await ListfileStore.OpenAsync(store, index.Tag, index.Sha256)).State);
    }

    [Theory]
    [InlineData("5;a\n3;b\n")]
    [InlineData("1;a\tb\n")]
    [InlineData("")]
    public async Task ARefusedCsvWritesNoManifest(string text)
    {
        var store = new InMemoryGeneratedStore();
        var csv = Encoding.UTF8.GetBytes(text);
        var release = Release(csv);
        await Assert.ThrowsAsync<ListfileFormatException>(() => ListfileStore.BuildAsync(store, release, Chunks(csv)));
        var (names, paths) = ListfileStore.ManifestKeys(release.Tag, release.Sha256);
        Assert.False(await store.HasAsync(names));
        Assert.False(await store.HasAsync(paths));
        Assert.Equal(GeneratedReadState.Absent, (await ListfileStore.OpenAsync(store, release.Tag, release.Sha256)).State);
    }

    [Fact]
    public async Task AWrongSha256WritesNoManifest()
    {
        var store = new InMemoryGeneratedStore();
        var csv = Encoding.UTF8.GetBytes(Sample);
        var release = Release(csv) with { Sha256 = new string('a', 64) };
        await Assert.ThrowsAsync<ListfileFormatException>(() => ListfileStore.BuildAsync(store, release, Chunks(csv)));
        var (names, paths) = ListfileStore.ManifestKeys(release.Tag, release.Sha256);
        Assert.False(await store.HasAsync(names));
        Assert.False(await store.HasAsync(paths));
    }

    [Fact]
    public async Task ABuildStoppedBeforeItsPathsIsAbsentAndRebuilds()
    {
        var store = new InMemoryGeneratedStore();
        var csv = Encoding.UTF8.GetBytes(Sample);
        var index = await Build(store, csv);
        var (_, paths) = ListfileStore.ManifestKeys(index.Tag, index.Sha256);
        await store.DeleteAsync([paths]);
        Assert.Equal(GeneratedReadState.Absent, (await ListfileStore.OpenAsync(store, index.Tag, index.Sha256)).State);
        // The leftover names file has the old seed; the rebuild replaces it, so both files agree.
        var rebuilt = await Build(store, csv);
        Assert.Equal([900u, 901u], await rebuilt.IdsOfAsync("sound/music/theme.mp3"));
        Assert.Equal(GeneratedReadState.Present, (await ListfileStore.OpenAsync(store, index.Tag, index.Sha256)).State);
    }

    public static TheoryData<string> Mismatches => ["seed", "count", "tag", "sha", "version", "short"];

    [Theory]
    [MemberData(nameof(Mismatches))]
    public async Task MetadataThatDoesNotMatchIsNotPresent(string field)
    {
        var store = new InMemoryGeneratedStore();
        var index = await Build(store, Sample);
        var good = new ListfileStore.Metadata(index.Seed, index.Count, index.Tag, index.Sha256, Published, Fetched);
        var bad = field switch
        {
            "seed" => (good with { Seed = good.Seed + 1 }).Encode(),
            "count" => (good with { Count = 3 }).Encode(),
            "tag" => (good with { Tag = "other" }).Encode(),
            "sha" => (good with { Sha256 = new string('b', 64) }).Encode(),
            "version" => [.. new byte[] { 2, 0 }, .. good.Encode().AsSpan(2)],
            _ => good.Encode()[..^1],
        };
        // Replace the paths manifest with one carrying the bad metadata.
        var (_, paths) = ListfileStore.ManifestKeys(index.Tag, index.Sha256);
        await store.DeleteAsync([paths]);
        using (var writer = new RecordFileWriter(store, ListfileStore.PathsOptions))
        {
            await writer.AddAsync(1, new byte[] { 1 });
            await writer.CompleteAsync(paths, bad);
        }
        Assert.Equal(GeneratedReadState.Corrupt, (await ListfileStore.OpenAsync(store, index.Tag, index.Sha256)).State);
    }

    [Fact]
    public async Task APathsRecordOutOfShapeIsCorrupt()
    {
        var store = new InMemoryGeneratedStore();
        var index = await Build(store, Sample, (_, _) => 7);
        var good = new ListfileStore.Metadata(index.Seed, index.Count, index.Tag, index.Sha256, Published, Fetched).Encode();
        var (_, paths) = ListfileStore.ManifestKeys(index.Tag, index.Sha256);
        foreach (var record in new byte[][] { [1, 0], [0x80], [77] })
        {
            await store.DeleteAsync([paths]);
            using (var writer = new RecordFileWriter(store, ListfileStore.PathsOptions))
            {
                await writer.AddAsync(7, record);
                await writer.CompleteAsync(paths, good);
            }
            var reopened = (await ListfileStore.OpenAsync(store, index.Tag, index.Sha256, hasher: (_, _) => 7)).Index!;
            Assert.Equal(GeneratedStoreCodes.Corrupt, (await Assert.ThrowsAsync<GeneratedStoreException>(() => reopened.IdsOfAsync("a"))).Code);
        }
    }

    [Fact]
    public async Task StreamingParserSendsEachEntryAndKeepsNone()
    {
        var seen = new List<(uint, string)>();
        var parser = new ListfileParser((id, name) => seen.Add((id, Encoding.UTF8.GetString(name))));
        parser.Feed(Encoding.UTF8.GetBytes("1;a‎b\r\n\n7;c"));
        Assert.Equal([(1u, "ab")], seen);
        Assert.Equal(2, parser.Finish());
        Assert.Equal([(1u, "ab"), (7u, "c")], seen);
        Assert.Throws<InvalidOperationException>(() => parser.Feed("8;d\n"u8));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task AnswersExactlyAsAPlainModel()
    {
        var random = new Random(186);
        var text = new StringBuilder();
        var written = new List<string>();
        uint id = 0;
        for (var i = 0; i < 5000; i++)
        {
            id += (uint)random.Next(1, 40);
            // Some names repeat with other case and slashes, so several ids share one folded name.
            var name = written.Count > 0 && random.Next(10) == 0
                ? Mangle(written[random.Next(written.Count)], random)
                : $"World/Maps/Zone{random.Next(1000)}/tile_{random.Next(100000)}.adt";
            written.Add(name);
            text.Append(id).Append(';').Append(name).Append('\n');
        }
        var csv = Encoding.UTF8.GetBytes(text.ToString());
        // The model: every line as written, matched by the folding rules one name at a time.
        var model = new SortedDictionary<uint, string>();
        var parser = new ListfileParser((fileDataId, name) => model.Add(fileDataId, Encoding.UTF8.GetString(name)));
        parser.Feed(csv);
        parser.Finish();
        var index = await Build(new InMemoryGeneratedStore(), csv, chunk: 4093);
        Assert.Equal(model.Count, index.Count);

        for (var i = 0; i < 2000; i++)
        {
            var probe = (uint)random.Next(0, (int)id + 50);
            Assert.Equal(model.TryGetValue(probe, out var name) ? name : null, await index.NameOfAsync(probe));
        }
        foreach (var name in written.Take(1500).Select(n => Mangle(n, random)).Append("World/Maps/none.adt"))
        {
            var query = Encoding.UTF8.GetBytes(name);
            Assert.Equal(model.Where(n => NameFolding.SameName(Encoding.UTF8.GetBytes(n.Value), query)).Select(n => n.Key), await index.IdsOfAsync(name));
        }
        foreach (var from in new uint[] { 0, 1, id / 3, id / 2 + 1, id, id + 1 })
            Assert.Equal(model.Where(n => n.Key >= from).Select(n => (n.Key, n.Value)).ToList(), await Walk(index, from));
    }

    private static string Mangle(string name, Random random) =>
        new([.. name.Select(ch => ch == '/' && random.Next(2) == 0 ? '\\' : random.Next(2) == 0 ? char.ToUpperInvariant(ch) : char.ToLowerInvariant(ch))]);
}
