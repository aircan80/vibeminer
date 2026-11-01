using System.Runtime.CompilerServices;
using System.Text.Json;
using VibeMiner.Builds;
using VibeMiner.Formats.Generated;
using VibeMiner.Generated;
using VibeMiner.Generated.RecordFiles;
using VibeMiner.Generated.Testing;
using Xunit;

namespace VibeMiner.Formats.Tests.Generated;

public static class GeneratedFixtures
{
    public const string ContentKey = "00112233445566778899aabbccddeeff";
    public const string K1 = "1111111111111111", K2 = "2222222222222222";

    public static GeneratedTableSource Source(string? locale = "enUS", string contentKey = ContentKey, string definition = "def-sha256+01020304",
        Db2Limits? limits = null) =>
        new(locale, contentKey, definition, limits ?? new Db2Limits());

    public static TactKeyStatus Known(params string[] names) => new(names.ToDictionary(n => n, n => "fp-" + n));

    public static Db2TableHeader Header(long rows) =>
        new(5, "11223344", "01020304", rows, 0, 4, [new Db2Column("ID", "int", true), new Db2Column("Name", "string", false)], "WDC5");

    public static GeneratedTableContent Content(IReadOnlyList<Db2Row> rows, params string[] keys) =>
        new(Header(rows.Count), [new Db2Section(0, rows.Count, 0, keys.Length > 0 ? keys[0] : "0000000000000000", keys.Length > 0)],
            new Db2Completion(rows.Count, rows.Count, 0, keys.Length > 0 ? 1 : 0, 0, 0, keys.Length > 0 ? "partial" : "complete", 1234), keys, Stream(rows));

    public static IReadOnlyList<Db2Row> SimpleRows(int n, string tag = "row") =>
        [.. Enumerable.Range(1, n).Select(i => new Db2Row((uint)i, false, null, [i.ToString(System.Globalization.CultureInfo.InvariantCulture), $"{tag} {i}"]))];

    public static async IAsyncEnumerable<Db2Row> Stream(IEnumerable<Db2Row> rows, [EnumeratorCancellation] CancellationToken token = default)
    {
        foreach (var row in rows)
        {
            token.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return row;
        }
    }

    public static async Task<List<Db2Row>> All(GeneratedTable table)
    {
        var rows = new List<Db2Row>();
        await foreach (var row in table.ReadAllAsync()) rows.Add(row);
        return rows;
    }

    public static void SameRow(Db2Row expected, Db2Row? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.IsCopy, actual.IsCopy);
        Assert.Equal(expected.CopySourceId, actual.CopySourceId);
        Assert.Equal(expected.Values, actual.Values);
        if (expected.TextIssues is null) Assert.Null(actual.TextIssues);
        else Assert.Equal(expected.TextIssues, actual.TextIssues);
    }

    public static void SameRows(IReadOnlyList<Db2Row> expected, IReadOnlyList<Db2Row> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++) SameRow(expected[i], actual[i]);
    }

    public enum Kind { Node, Manifest, Segment, Layout }

    /// <summary>Sorts the store's objects into nodes and layout records (JSON), manifests (open as record files) and segments (the rest).</summary>
    public static async Task<Dictionary<GeneratedKey, Kind>> ClassifyAsync(InMemoryGeneratedStore store)
    {
        var kinds = new Dictionary<GeneratedKey, Kind>();
        foreach (var key in store.Keys)
        {
            var read = await store.ReadAsync(key);
            if (read.Bytes is { } bytes && FormatOf(bytes) == "vibeminer-generated-table-node v1") kinds[key] = Kind.Node;
            else if (read.Bytes is { } layout && FormatOf(layout) == "vibeminer-generated-table-layout v1") kinds[key] = Kind.Layout;
            else if ((await RecordFile.OpenAsync(store, key)).State == GeneratedReadState.Present) kinds[key] = Kind.Manifest;
            else kinds[key] = Kind.Segment;
        }
        return kinds;
    }

    private static string? FormatOf(byte[] bytes)
    {
        try
        {
            using var json = JsonDocument.Parse(bytes);
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("Format", out var f) ? f.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
