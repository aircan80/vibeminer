using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeMiner.Files;
using VibeMiner.Files.Testing;
using VibeMiner.Formats.Generated;
using VibeMiner.Formats.Tables;
using Xunit;
using static VibeMiner.Formats.Tests.Tables.TablesContract;

namespace VibeMiner.Formats.Tests.Tables;

/// <summary>
/// A direct read (D330, #194) answers exactly what the generated store answers once the table is built: the entry, every page
/// (rows, ordinals, NextAfter, Identity, Scanned, State), every row by ID and the schema. The direct side holds the table's build
/// at a gate that never opens until the end, so no answer can come from the store; the generated side asks the same after the
/// build (Interactive and Background) and in a fresh world at Background priority. Synthetic data only; the key is generated.
/// </summary>
public sealed class DirectReadContractTests
{
    private const TablePriority Now = TablePriority.Interactive;
    private const TablePriority Later = TablePriority.Background;
    private static CancellationToken Ct => CancellationToken.None;
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlyList<ScenarioColumn> Plain = [ScenarioColumn.Id(), ScenarioColumn.Int("Value")];
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };

    /// <summary>A generated key name (D310), never a real one; its section is decrypted by layer 4 with a known key.</summary>
    private static readonly string DecryptedKey = MemoryByteSource.TestKeyName(194).ToString("X16", CultureInfo.InvariantCulture);
    private static readonly string DecryptedFingerprint = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Seeded(194)));

    private static byte[] Seeded(int seed)
    {
        var bytes = new byte[16];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    /// <summary>A second synthetic key name, for a hole layer 4 names differently from the section's own key.</summary>
    private const string OtherKey = "FEDCBA9876543210";

    private static ScenarioRow Row(uint id, params string[] values) => new(id, [id.ToString(CultureInfo.InvariantCulture), .. values]);

    /// <summary>
    /// The contract scenario's 100 Alpha, 300 Gamma, 600 Epsilon (partial), 700 Zeta (every section keyed) and 900 Empty, plus:
    /// 1000 Copies (5 rows, copies 6 of 1 and 7 of 3), 1100 BadText (sparse, one invalid text cell), 1200 Decrypted (rows 1-2
    /// clear, 3-4 in a keyed section layer 4 decrypted with a known key), 1300 Big (40000 rows, past one 256 KiB source block) and
    /// 1400 Misnamed (partial, its keyed section's hole named by another key, so both names are the reason's).
    /// </summary>
    private static IReadOnlyList<SyntheticTable> Tables()
    {
        var contract = Scenario().Tables.Where(t => t.FileDataId is 100 or 300 or 600 or 700 or 900).Select(SyntheticTables.From);
        var copies = WithCopies(SyntheticTables.Wdc5(Plain, [.. Enumerable.Range(1, 5).Select(i => Row((uint)i, $"{i * 11}"))], null, 1000).Bytes, (6, 1), (7, 3));
        var bad = SyntheticTables.Wdc5([ScenarioColumn.Id(), ScenarioColumn.Text("Label"), ScenarioColumn.Int("Value")],
            [Row(1, "one", "1"), new(2, ["2", "", "2"], [1]), Row(3, "three", "3")], null, 1100).Bytes;
        var decrypted = SyntheticTables.Wdc5(Plain, [Row(1, "5"), Row(2, "6")], new ScenarioKeyedSection(DecryptedKey, [3, 4]), 1200).Bytes;
        var big = SyntheticTables.Wdc5(Plain, [.. Enumerable.Range(1, 40000).Select(i => Row((uint)i, $"{i * 3}"))], null, 1300).Bytes;
        var (misnamed, misnamedHole) = SyntheticTables.Wdc5(Plain, [Row(1, "7"), Row(2, "8")], new ScenarioKeyedSection(Key, [10, 11]), 1400);
        return
        [
            .. contract,
            Synthetic(1000, "Copies", Plain, copies),
            Synthetic(1100, "BadText", [ScenarioColumn.Id(), ScenarioColumn.Text("Label"), ScenarioColumn.Int("Value")], bad),
            // No hole: layer 4 gives the keyed section's plaintext, as it does once the key is known.
            Synthetic(1200, "Decrypted", Plain, decrypted),
            Synthetic(1300, "Big", Plain, big),
            // The keyed section's hole named by another key than the section's own.
            Synthetic(1400, "Misnamed", Plain, misnamed) with { File = new FakeFile(1400, Content: misnamed, Storage: FileStorage.Stored,
                Holes: [misnamedHole! with { KeyName = OtherKey }]) },
        ];
    }

    private static SyntheticTable Synthetic(uint id, string name, IReadOnlyList<ScenarioColumn> columns, byte[] bytes) =>
        new(id, name, SyntheticTables.Dbd(columns), new FakeFile(id, Content: bytes, Storage: FileStorage.Stored));

    /// <summary>Adds a copy table to a one-section synthetic WDC5 file: the section's copy count, then (new ID, source ID) pairs at its end.</summary>
    private static byte[] WithCopies(byte[] bytes, params (uint Id, uint Source)[] copies)
    {
        var output = new byte[bytes.Length + copies.Length * 8];
        bytes.CopyTo(output, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(204 + 36), (uint)copies.Length);
        for (var i = 0; i < copies.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(bytes.Length + i * 8), copies[i].Id);
            BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(bytes.Length + i * 8 + 4), copies[i].Source);
        }
        return output;
    }

    private static Task<SyntheticWorld> WorldAsync() => SyntheticTables.CreateAsync(Build, Tables(),
        keys: _ => new TactKeyStatus(new Dictionary<string, string> { [DecryptedKey] = DecryptedFingerprint }));

    private static async Task<IBuildTables> OpenAsync(SyntheticWorld world) => (await world.Service.ForAsync(Build, "enUS", Ct)).Tables!;

    /// <summary>The pages asked of a table of <paramref name="rows"/> readable rows: first, middle, last, one limit over all of it,
    /// two past the end, a wrong identity and the first page by name.</summary>
    private static IEnumerable<TablePageRequest> Pages(uint table, string name, long rows)
    {
        yield return new(table, -1, 3);
        yield return new(table, 2, 3);
        yield return new(table, Math.Max(rows - 3, -1), 3);
        yield return new(table, rows / 2, 1000);
        yield return new(table, -1, TableQueries.MaxLimit);
        yield return new(table, rows, 3);
        yield return new(table, rows + 10, 3);
        yield return new(table, -1, 3, Identity: "0000000000000000wrong");
        yield return new(TableRef.ForName(name.ToUpperInvariant()), -1, 3);
    }

    private sealed record Answers(IReadOnlyList<TablePage> Pages, IReadOnlyList<TableRowResult> Rows, TableSchemaResult Schema);

    private static async Task<Answers> AskAsync(IBuildTables tables, uint table, string name, long rows, IReadOnlyList<uint> ids, TablePriority priority)
    {
        var pages = new List<TablePage>();
        foreach (var request in Pages(table, name, rows)) pages.Add(await tables.PageAsync(request, priority, Ct).WaitAsync(Guard));
        var found = new List<TableRowResult>();
        foreach (var id in ids) found.Add(await tables.GetAsync(table, id, priority, Ct).WaitAsync(Guard));
        return new(pages, found, await tables.SchemaAsync(table, priority, Ct).WaitAsync(Guard));
    }

    private static string Text(object? value) => JsonSerializer.Serialize(value, Json);

    /// <summary>Everything compared by content: pages, rows (values and text issues), entries (key names included) and columns.</summary>
    private static void Same(Answers expected, Answers actual, string side)
    {
        Assert.Equal(expected.Pages.Count, actual.Pages.Count);
        for (var i = 0; i < expected.Pages.Count; i++) Assert.True(Text(expected.Pages[i]) == Text(actual.Pages[i]),
            $"{side} page {i}:\n direct    {Text(expected.Pages[i])}\n generated {Text(actual.Pages[i])}");
        Assert.Equal(expected.Rows.Count, actual.Rows.Count);
        for (var i = 0; i < expected.Rows.Count; i++) Assert.True(Text(expected.Rows[i]) == Text(actual.Rows[i]),
            $"{side} row {i}:\n direct    {Text(expected.Rows[i])}\n generated {Text(actual.Rows[i])}");
        Assert.True(Text(expected.Schema.Table) == Text(actual.Schema.Table),
            $"{side} schema entry:\n direct    {Text(expected.Schema.Table)}\n generated {Text(actual.Schema.Table)}");
        Assert.True(Text(expected.Schema.Columns) == Text(actual.Schema.Columns),
            $"{side} columns:\n direct    {Text(expected.Schema.Columns)}\n generated {Text(actual.Schema.Columns)}");
        Assert.Equal(expected.Schema.Schema, actual.Schema.Schema);
    }

    private static async Task<TableEntry> ListedAsync(IBuildTables tables, uint table)
    {
        await foreach (var entry in tables.ListAsync(TableListFilter.All, Now, Ct))
            if (entry.FileDataId == table) return entry;
        throw new InvalidOperationException($"Table {table} isn't listed.");
    }

    [Theory]
    [InlineData(100u, "Alpha", 7, TableState.Ready, "10=Found 40=Found 70=Found 15=Absent")]
    [InlineData(300u, "Gamma", 2, TableState.Ready, "1=Found 2=Found 3=Absent")]
    [InlineData(600u, "Epsilon", 2, TableState.Partial, "1=Found 2=Found 10=Protected 11=Protected 5=Unknown")]
    [InlineData(700u, "Zeta", 0, TableState.Encrypted, "20=Protected 1=Unknown")]
    [InlineData(900u, "Empty", 0, TableState.Ready, "1=Absent")]
    [InlineData(1000u, "Copies", 7, TableState.Ready, "1=Found 5=Found 6=Found 7=Found 8=Absent")]
    [InlineData(1100u, "BadText", 3, TableState.Ready, "1=Found 2=Found 3=Found 4=Absent")]
    [InlineData(1200u, "Decrypted", 4, TableState.Ready, "1=Found 3=Found 4=Found 5=Absent")]
    [InlineData(1300u, "Big", 40000, TableState.Ready, "1=Found 20000=Found 40000=Found 40001=Absent")]
    [InlineData(1400u, "Misnamed", 2, TableState.Partial, "1=Found 2=Found 10=Protected 11=Protected 5=Unknown")]
    public async Task ADirectReadAnswersWhatTheGeneratedStoreAnswers(uint table, string name, int rows, TableState state, string outcomes)
    {
        var expected = outcomes.Split(' ').Select(pair => pair.Split('='))
            .Select(pair => (Id: uint.Parse(pair[0], CultureInfo.InvariantCulture), Outcome: Enum.Parse<TableRowOutcome>(pair[1]))).ToArray();
        var ids = expected.Select(e => e.Id).ToArray();

        // Direct: the build is held for the whole phase, so a generated-path answer would never come.
        var world = await WorldAsync();
        await using var tables = await OpenAsync(world);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        world.Generated.Decoding = _ => gate.Task;
        Answers direct;
        try
        {
            direct = await AskAsync(tables, table, name, rows, ids, Now);
            Assert.False((await ListedAsync(tables, table)).Checked);
            Assert.True(world.Generated.Decodes <= 1);
        }
        finally { gate.TrySetResult(); }

        // The direct answers themselves are the fixture's.
        Assert.Equal((table, name, state, true), (direct.Schema.Table.FileDataId, direct.Schema.Table.Name, direct.Schema.Table.State, direct.Schema.Table.Checked));
        Assert.All(direct.Pages, page => Assert.Equal(direct.Schema.Table, page.Table, EntryComparer));
        var readable = TableQueries.IsReadable(state);
        Assert.Equal(readable ? TablePageState.Ok : TablePageState.NotReadable, direct.Pages[0].State);
        if (readable)
        {
            Assert.Equal(Math.Min(rows, 3), direct.Pages[0].Rows.Count);
            Assert.Equal(Math.Min(rows, TableQueries.MaxLimit), direct.Pages[4].Rows.Count);
            Assert.Equal(Enumerable.Range(0, Math.Min(rows, TableQueries.MaxLimit)).Select(i => (long)i), direct.Pages[4].Rows.Select(row => row.Ordinal));
            Assert.Null(direct.Pages[2].NextAfter);
            Assert.Equal(TablePageState.InvalidCursor, direct.Pages[5].State);
            Assert.Equal(TablePageState.InvalidCursor, direct.Pages[6].State);
            Assert.Equal(TablePageState.Stale, direct.Pages[7].State);
            Assert.Equal(direct.Pages[0].Identity, direct.Pages[7].Identity);
        }
        Assert.Equal(expected.Select(e => e.Outcome), direct.Rows.Select(r => r.Outcome));
        Assert.All(direct.Rows.Where(r => r.Outcome == TableRowOutcome.Protected), r => Assert.Equal(Key, r.ProtectedKey));
        Assert.NotNull(direct.Schema.Schema);
        if (table == 1000)
            Assert.Equal((true, 3u), (direct.Rows[3].Row!.Row.IsCopy, direct.Rows[3].Row!.Row.CopySourceId));
        if (table == 1100) Assert.NotEmpty(direct.Rows[1].Row!.Row.TextIssues!);
        if (table == 1200)
            Assert.Equal(new TableCounts(4, 4, 0, 0, "WDC5", "01020304"), direct.Schema.Table.Counts);
        if (table == 1400) Assert.Equal([Key, OtherKey], direct.Schema.Table.Reason!.KeyNames);

        // Generated: the held build ends and stores the table; no call decodes again.
        Assert.Equal(state, (await tables.CheckAsync(table, Now, Ct).WaitAsync(Guard)).State);
        Assert.True((await ListedAsync(tables, table)).Checked);
        var decodes = world.Generated.Decodes;
        Same(direct, await AskAsync(tables, table, name, rows, ids, Now), "Interactive after the build");
        Same(direct, await AskAsync(tables, table, name, rows, ids, Later), "Background after the build");
        Assert.Equal(decodes, world.Generated.Decodes);

        // The direct page's identity pages on from the store.
        if (readable)
        {
            var next = await tables.PageAsync(new(table, -1, 3, Identity: direct.Pages[0].Identity), Now, Ct);
            Assert.Equal(Text(direct.Pages[0]), Text(next));
        }

        // A fresh world at Background priority builds on the way and answers the same.
        var fresh = await WorldAsync();
        await using var later = await OpenAsync(fresh);
        Same(direct, await AskAsync(later, table, name, rows, ids, Later), "Background in a fresh world");
        if (readable)
            Assert.Equal(TablePageState.Ok, (await later.PageAsync(new(table, Identity: direct.Pages[0].Identity), Later, Ct)).State);
    }

    private static readonly IEqualityComparer<TableEntry> EntryComparer = new ByText<TableEntry>();

    private sealed class ByText<T> : IEqualityComparer<T>
    {
        public bool Equals(T? x, T? y) => Text(x) == Text(y);
        public int GetHashCode(T obj) => Text(obj).GetHashCode(StringComparison.Ordinal);
    }
}
