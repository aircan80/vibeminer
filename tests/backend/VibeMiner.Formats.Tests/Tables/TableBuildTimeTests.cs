using System.Diagnostics;
using System.Globalization;
using System.Text;
using VibeMiner.Files;
using VibeMiner.Files.Testing;
using VibeMiner.Formats.Definitions;
using VibeMiner.Formats.Tables;
using Xunit;
using Xunit.Abstractions;

namespace VibeMiner.Formats.Tests.Tables;

/// <summary>
/// Building a big generated table over a layer 4 stream that keeps one decoded chunk, as the real one does (#192): a row's record
/// and its string lie in different chunks, so reading rows straight through the stream decoded two chunks per row.
/// </summary>
public sealed class TableBuildTimeTests(ITestOutputHelper output)
{
    private const uint Spells = 700;
    private static readonly ScenarioColumn[] Columns =
        [ScenarioColumn.Id(), ScenarioColumn.Text("Name_lang"), ScenarioColumn.Int("Flags"), ScenarioColumn.Int("Icon")];

    /// <summary>SpellName-shaped rows: an ID, a name of 20-odd bytes and two integers.</summary>
    private static ScenarioRow[] Rows(int count) => [.. Enumerable.Range(1, count).Select(i => new ScenarioRow((uint)i,
        [i.ToString(CultureInfo.InvariantCulture), "Synthetic spell " + i.ToString(CultureInfo.InvariantCulture), (i % 7).ToString(CultureInfo.InvariantCulture),
         (i * 3).ToString(CultureInfo.InvariantCulture)]))];

    private static async Task<(SyntheticWorld World, ChunkedFileSystem Chunked, byte[] Bytes)> WorldAsync(int rows, int chunkSize)
    {
        var (bytes, _) = SyntheticTables.Wdc5(Columns, Rows(rows), null, Spells);
        ChunkedFileSystem? chunked = null;
        var world = await SyntheticTables.CreateAsync(TablesContract.Build,
            [new(Spells, "SpellName", SyntheticTables.Dbd(Columns), new FakeFile(Spells, Content: bytes, Storage: FileStorage.Stored))],
            wrap: files => chunked = new ChunkedFileSystem(files, chunkSize));
        return (world, chunked!, bytes);
    }

    [Fact]
    public async Task A_first_page_builds_the_table_decoding_each_chunk_a_bounded_number_of_times()
    {
        const int chunk = 16 << 10;
        var (world, chunked, bytes) = await WorldAsync(15_000, chunk);
        var tables = (await world.Service.ForAsync(TablesContract.Build, "enUS", default)).Tables!;
        await using (tables)
        {
            var page = await tables.PageAsync(new(Spells), TablePriority.Interactive, default);
            Assert.Equal(TablePageState.Ok, page.State);
            Assert.Equal(TableState.Ready, page.Table.State);
            Assert.Equal(["1", "Synthetic spell 1", "1", "3"], page.Rows[0].Row.Values);
            Assert.Equal(15_000, page.Table.Counts!.ReadableRows);
            // Each chunk once, give or take; straight through the stream it was two per row (30,000 here).
            var chunks = (bytes.Length + chunk - 1) / chunk;
            Assert.InRange(chunked.Decodes, chunks, 3L * chunks);
        }
    }

    [Fact]
    public async Task A_failing_chunk_fails_at_the_same_row_as_an_exact_range_read()
    {
        const int chunk = 16 << 10;
        var (bytes, _) = SyntheticTables.Wdc5(Columns, Rows(15_000), null, Spells);
        var definition = DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes(SyntheticTables.Dbd(Columns)));
        // A chunk well inside the records, which start after a short header and end before the strings.
        const int failing = 5;

        var exact = new FailingSource(bytes, failing * (long)chunk, (failing + 1) * (long)chunk);
        var table = await Db2Reader.OpenAsync(exact, definition, SyntheticTables.Version, new());
        var (expected, expectedFailure) = await ReadUntilFailureAsync(table);

        var files = new InMemoryFileSystem().Add(TablesContract.Build, ["enUS"], [new FakeFile(Spells, Content: bytes, Storage: FileStorage.Stored)]);
        var chunked = new ChunkedFileSystem(files, chunk, failing);
        await using var handle = (await chunked.ForAsync(TablesContract.Build, "enUS", default)).Files!;
        await using var opened = await Db2Reader.OpenAsync(handle, FileRef.Id(Spells), definition, SyntheticTables.Version, new());
        Assert.Equal(Db2OpenState.Open, opened.State);
        var (read, failure) = await ReadUntilFailureAsync(opened.Table!);

        Assert.InRange(expected, 1, 14_999);
        Assert.Equal(expected, read);
        Assert.Equal(expectedFailure, failure);
        Assert.Equal(Db2OpenState.Corrupt, failure.State);
    }

    private static async Task<(int Rows, (Db2OpenState State, string? Code) Failure)> ReadUntilFailureAsync(Db2Table table)
    {
        var rows = 0;
        try { await foreach (var _ in table.ReadAllAsync()) rows++; }
        catch (Db2SourceException failed) { return (rows, (failed.State, failed.Code)); }
        throw new Xunit.Sdk.XunitException("The read didn't fail.");
    }

    /// <summary>Fails every read that touches [start, end) as layer 4's corrupt chunk would, and reads exactly the range asked for.</summary>
    private sealed class FailingSource(byte[] bytes, long start, long end) : IDb2ByteSource
    {
        public long Length => bytes.Length;
        public IReadOnlyList<Db2Hole> Holes => [];
        public ValueTask ReadRangeAsync(long offset, Memory<byte> destination, CancellationToken token)
        {
            if (offset < end && start < offset + destination.Length) throw new Db2SourceException(Db2OpenState.Corrupt, "FILES_CORRUPT", "A synthetic chunk is corrupt.");
            bytes.AsMemory((int)offset, destination.Length).CopyTo(destination);
            return ValueTask.CompletedTask;
        }
    }

    private const int RetailRows = 400_000, RetailChunk = 256 << 10;

    private static long ChunksOf(byte[] bytes, int chunk) => (bytes.Length + chunk - 1) / chunk;

    /// <summary>
    /// A Retail SpellName-sized table (400,000 rows) over 256 KiB chunks, not built yet: an Interactive first page reads the DB2
    /// directly while the build runs in the background (#194). The bounds are generous for slow CI runners.
    /// </summary>
    [Fact, Trait("Speed", "Slow")]
    public async Task A_retail_sized_table_pages_directly_while_it_builds()
    {
        var (world, chunked, bytes) = await WorldAsync(RetailRows, RetailChunk);
        var chunks = ChunksOf(bytes, RetailChunk);
        await OpenAloneAsync(bytes);
        var tables = (await world.Service.ForAsync(TablesContract.Build, "enUS", default)).Tables!;
        await using (tables)
        {
            var clock = Stopwatch.StartNew();
            var page = await tables.PageAsync(new(Spells), TablePriority.Interactive, default);
            var direct = clock.Elapsed;
            var directDecodes = chunked.Decodes;
            Assert.Equal(TablePageState.Ok, page.State);
            Assert.Equal(RetailRows, page.Table.Counts!.ReadableRows);
            Assert.Equal(["1", "Synthetic spell 1", "1", "3"], page.Rows[0].Row.Values);

            // A Background check joins the build the page started.
            var entry = await tables.CheckAsync(Spells, TablePriority.Background, default);
            var built = clock.Elapsed;
            Assert.Equal(TableState.Ready, entry.State);

            clock.Restart();
            var warm = await tables.PageAsync(new(Spells, After: RetailRows / 2), TablePriority.Interactive, default);
            var warmTime = clock.Elapsed;
            Assert.Equal(TablePageState.Ok, warm.State);
            Assert.Equal(page.Identity, warm.Identity);

            output.WriteLine($"cold direct first page {direct.TotalMilliseconds:N0} ms ({directDecodes} decodes); built after {built.TotalMilliseconds:N0} ms; " +
                $"warm page {warmTime.TotalMilliseconds:N0} ms; {chunked.Decodes} decodes of {chunks} chunks");
            Assert.True(direct < TimeSpan.FromSeconds(5), $"The direct first page took {direct.TotalSeconds:N1} s.");
            // The direct page reads the header, indexes and the first rows, not the file (11 of 60 chunks measured); the direct
            // open and the build together decode each chunk little more than once (74 measured).
            Assert.InRange(directDecodes, 1, chunks / 2);
            Assert.InRange(chunked.Decodes, chunks, 2L * chunks);
        }
    }

    /// <summary>
    /// The same table at Background priority: the first page waits for the build. Before #192 every row decoded two chunks,
    /// minutes here; the bound is generous for slow CI runners.
    /// </summary>
    [Fact, Trait("Speed", "Slow")]
    public async Task A_retail_sized_table_builds_its_first_page_in_seconds()
    {
        var (world, chunked, bytes) = await WorldAsync(RetailRows, RetailChunk);
        var tables = (await world.Service.ForAsync(TablesContract.Build, "enUS", default)).Tables!;
        await using (tables)
        {
            var clock = Stopwatch.StartNew();
            var page = await tables.PageAsync(new(Spells), TablePriority.Background, default);
            var cold = clock.Elapsed;
            Assert.Equal(TablePageState.Ok, page.State);
            Assert.Equal(TableState.Ready, page.Table.State);
            Assert.Equal(RetailRows, page.Table.Counts!.ReadableRows);
            Assert.Equal(["1", "Synthetic spell 1", "1", "3"], page.Rows[0].Row.Values);
            var decodes = chunked.Decodes;

            clock.Restart();
            var warm = await tables.PageAsync(new(Spells, After: RetailRows / 2), TablePriority.Interactive, default);
            var warmTime = clock.Elapsed;
            Assert.Equal(TablePageState.Ok, warm.State);
            Assert.Equal(decodes, chunked.Decodes);

            var chunks = ChunksOf(bytes, RetailChunk);
            output.WriteLine($"cold built first page {cold.TotalMilliseconds:N0} ms; warm page {warmTime.TotalMilliseconds:N0} ms; {decodes} decodes of {chunks} chunks");
            Assert.True(cold < TimeSpan.FromSeconds(30), $"The first page took {cold.TotalSeconds:N1} s.");
            Assert.InRange(decodes, 1, 3L * chunks);
        }
    }

    /// <summary>For reference: Db2Reader.OpenAsync alone on the same bytes, from memory and through the chunked fake (whose open
    /// compresses the whole file first).</summary>
    private async Task OpenAloneAsync(byte[] bytes)
    {
        var definition = DbdDefinitionReader.Parse(Encoding.UTF8.GetBytes(SyntheticTables.Dbd(Columns)));
        var clock = Stopwatch.StartNew();
        var table = await Db2Reader.OpenAsync(new FailingSource(bytes, 0, 0), definition, SyntheticTables.Version, new());
        var memory = clock.Elapsed;
        Assert.Equal(RetailRows, table.ReadableRowCount);

        var files = new InMemoryFileSystem().Add(TablesContract.Build, ["enUS"], [new FakeFile(Spells, Content: bytes, Storage: FileStorage.Stored)]);
        var chunked = new ChunkedFileSystem(files, RetailChunk);
        await using var handle = (await chunked.ForAsync(TablesContract.Build, "enUS", default)).Files!;
        clock.Restart();
        await using var opened = await Db2Reader.OpenAsync(handle, FileRef.Id(Spells), definition, SyntheticTables.Version, new());
        var throughChunks = clock.Elapsed;
        Assert.Equal(Db2OpenState.Open, opened.State);
        output.WriteLine($"Db2Reader.OpenAsync alone: {memory.TotalMilliseconds:N0} ms from memory, {throughChunks.TotalMilliseconds:N0} ms through " +
            $"the chunked fake ({chunked.Decodes} decodes)");
    }
}
