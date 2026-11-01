using System.Buffers;
using VibeMiner.Formats.Generated;
using VibeMiner.Generated;
using VibeMiner.Generated.RecordFiles;
using VibeMiner.Generated.Testing;
using Xunit;
using static VibeMiner.Formats.Tests.Generated.GeneratedFixtures;

namespace VibeMiner.Formats.Tests.Generated;

public sealed class GeneratedRowsTests
{
    private static IReadOnlyList<Db2Row> Shapes()
    {
        var many = Enumerable.Range(0, 300).Select(i => (i * 7).ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        return
        [
            new Db2Row(1, false, null, ["1", "plain"]),
            new Db2Row(2, false, null, []),
            new Db2Row(3, false, null, ["", "", ""]),
            new Db2Row(5, false, null, ["café", "日本語", "😀 emoji", "\0nul inside"]),
            new Db2Row(7, false, null, ["x", "bad"], [new Db2TextIssue(1, "TEXT_INVALID_UTF8", "C3")]),
            new Db2Row(8, false, null, ["y"], []),
            new Db2Row(9, false, null, many),
            new Db2Row(10, false, null, [new string('z', 200_000), "after"]),
            new Db2Row(uint.MaxValue, false, null, ["max"], [new Db2TextIssue(0, "A", ""), new Db2TextIssue(3, "B", "FFFE")]),
            new Db2Row(0, false, null, ["zero id"]),
            new Db2Row(100, true, 1, ["1", "plain"]),
            new Db2Row(101, true, null, ["copy without source"]),
            new Db2Row(102, false, 5, ["source without copy flag"]),
            new Db2Row(103, true, uint.MaxValue, ["copy"], [new Db2TextIssue(0, "C", "00")]),
        ];
    }

    [Fact]
    public void Codec_round_trips_every_row_shape_exactly()
    {
        var buffer = new ArrayBufferWriter<byte>();
        foreach (var row in Shapes())
        {
            RowCodec.Encode(row, buffer);
            SameRow(row, RowCodec.Decode(row.Id, buffer.WrittenSpan));
        }
    }

    [Fact]
    public void Codec_shares_short_values_without_changing_any()
    {
        string[] values = ["", "0", "7", "10", "42", "999", "1000", "007", "01", "-1", "a", " ", "\u00e9", "\u20ac", "9x", "12.5", "\0"];
        var buffer = new ArrayBufferWriter<byte>();
        RowCodec.Encode(new Db2Row(1, false, null, values), buffer);
        var first = RowCodec.Decode(1, buffer.WrittenSpan);
        var second = RowCodec.Decode(1, buffer.WrittenSpan);
        Assert.Equal(values, first.Values);
        foreach (var shared in new[] { 0, 1, 2, 3, 4, 5, 10, 11 }) Assert.Same(first.Values[shared], second.Values[shared]);
        Assert.NotSame(first.Values[6], second.Values[6]); // "1000" is decoded each time
    }

    [Fact]
    public void Codec_refuses_records_out_of_shape()
    {
        var buffer = new ArrayBufferWriter<byte>();
        RowCodec.Encode(new Db2Row(1, false, null, ["abc"]), buffer);
        var good = buffer.WrittenSpan.ToArray();
        Assert.Equal(GeneratedStoreCodes.Corrupt, Assert.Throws<GeneratedStoreException>(() => RowCodec.Decode(1, [])).Code);
        Assert.Equal(GeneratedStoreCodes.Corrupt, Assert.Throws<GeneratedStoreException>(() => RowCodec.Decode(1, [0x80, .. good[1..]])).Code);
        Assert.Equal(GeneratedStoreCodes.Corrupt, Assert.Throws<GeneratedStoreException>(() => RowCodec.Decode(1, good[..^1])).Code);
        Assert.Equal(GeneratedStoreCodes.Corrupt, Assert.Throws<GeneratedStoreException>(() => RowCodec.Decode(1, [.. good, 0])).Code);
        Assert.Equal(GeneratedStoreCodes.Corrupt, Assert.Throws<GeneratedStoreException>(() => RowCodec.Decode(1, [0, 1, 1, 0xFF])).Code);
        Assert.Throws<ArgumentException>(() => RowCodec.Encode(new Db2Row(1, false, null, ["\uD800"]), buffer));
    }

    [Fact]
    public async Task Stored_rows_read_back_identical_by_page_id_and_in_order()
    {
        var rows = Shapes().Concat(Enumerable.Range(1000, 500).Select(i => new Db2Row((uint)i, false, null, [$"r{i}", new string('q', i % 50)]))).ToArray();
        var store = new InMemoryGeneratedStore();
        // Small blocks so pages cross block edges.
        var tables = new GeneratedTables(store, new RecordFileOptions { BlockBytes = 256, MaxRecordBytes = 16 << 20 });
        var table = await tables.StoreAsync(Source(), TactKeyStatus.None, Content(rows));

        Assert.Equal(rows.Length, table.RowCount);
        SameRows(rows, await All(table));
        SameRows(rows, (await tables.OpenAsync(Source(), TactKeyStatus.None) is { } reopened ? await All(reopened) : []));

        SameRows(rows[..50], await table.ReadPageAsync(0, 50));
        SameRows(rows[13..64], await table.ReadPageAsync(13, 51));
        SameRows(rows[^3..], await table.ReadPageAsync(rows.Length - 3, 50));
        Assert.Empty(await table.ReadPageAsync(rows.Length, 50));
        Assert.Empty(await table.ReadPageAsync(0, 0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => table.ReadPageAsync(rows.Length + 1, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => table.ReadPageAsync(-1, 1));

        foreach (var row in rows) SameRow(row, await table.FindAsync(row.Id));
        Assert.Null(await table.FindAsync(4));
        Assert.Null(await table.FindAsync(999));
        Assert.Null(await table.FindAsync(uint.MaxValue - 1));
    }

    // D291: an absent external parent ("" with DB2_RELATION_ABSENT, empty hex) survives the codec and the store, on a
    // physical row and on its copy, and parent 0 stays "0" with no issue.
    [Fact]
    public async Task Absent_relation_parents_round_trip_with_their_issue()
    {
        var absent = new Db2TextIssue(2, Db2Reader.RelationAbsent, "");
        Db2Row[] rows =
        [
            new(900, false, null, ["900", "1000", "0", "2000"]),
            new(901, false, null, ["901", "1001", "", "2001"], [absent]),
            new(950, true, 901, ["950", "1001", "", "2001"], [absent]),
        ];
        var buffer = new ArrayBufferWriter<byte>();
        foreach (var row in rows)
        {
            buffer.Clear(); RowCodec.Encode(row, buffer);
            var decoded = RowCodec.Decode(row.Id, buffer.WrittenSpan);
            SameRow(row, decoded); Assert.Equal(row.TextIssues, decoded.TextIssues);
        }
        var tables = new GeneratedTables(new InMemoryGeneratedStore());
        var table = await tables.StoreAsync(Source(), TactKeyStatus.None, Content(rows));
        var page = await table.ReadPageAsync(0, 10);
        SameRows(rows, page);
        Assert.Equal([null, [absent], [absent]], page.Select(r => r.TextIssues?.ToArray()));
        Assert.Equal([absent], (await table.FindAsync(950))!.TextIssues!);
        Assert.Null((await table.FindAsync(900))!.TextIssues);
    }

    [Fact]
    public async Task Header_sections_completion_and_an_empty_table_read_back()
    {
        var store = new InMemoryGeneratedStore();
        var tables = new GeneratedTables(store);
        var content = Content([], K1);
        var table = await tables.StoreAsync(Source(), TactKeyStatus.None, content);
        var reopened = await tables.OpenAsync(Source(), TactKeyStatus.None);
        Assert.NotNull(reopened);
        foreach (var t in new[] { table, reopened })
        {
            Assert.Equal(0, t.RowCount);
            Assert.Empty(await All(t));
            Assert.Null(await t.FindAsync(1));
            Assert.Equal(content.Header with { Columns = [] }, t.Header with { Columns = [] });
            Assert.Equal(content.Header.Columns, t.Header.Columns);
            Assert.Equal(content.Sections, t.Sections);
            Assert.Equal(content.Completion, t.Completion);
            Assert.Equal([K1], t.KeysUsed);
            Assert.Equal(Source(), t.Source);
        }
    }

    [Fact]
    public async Task Bad_inputs_are_refused()
    {
        var tables = new GeneratedTables(new InMemoryGeneratedStore());
        Assert.Throws<ArgumentException>(() => Source(locale: "xxXX"));
        Assert.Throws<ArgumentException>(() => Source(contentKey: "00112233445566778899AABBCCDDEEFF"));
        Assert.Throws<ArgumentException>(() => Source(contentKey: "0011"));
        Assert.Throws<ArgumentException>(() => Source(definition: ""));
        Assert.Throws<ArgumentException>(() => Source(definition: "a\nb"));
        Assert.Throws<ArgumentException>(() => new TactKeyStatus(new Dictionary<string, string> { ["111111111111111a"] = "x" }));
        Assert.Throws<ArgumentException>(() => new TactKeyStatus(new Dictionary<string, string> { [K1] = "?" }));
        Assert.Throws<ArgumentException>(() => new TactKeyStatus(new Dictionary<string, string> { [K1] = "has space" }));
        Assert.Equal("~!x", new TactKeyStatus(new Dictionary<string, string> { [K1] = "~!x" }).FingerprintOf(K1));
        await Assert.ThrowsAsync<ArgumentException>(() => tables.StoreAsync(Source(), TactKeyStatus.None, Content([], "abc")));
        await Assert.ThrowsAsync<ArgumentException>(() => tables.StoreAsync(Source(), TactKeyStatus.None,
            Content([new Db2Row(1, false, null, []), new Db2Row(1, false, null, [])])));
        Assert.Null(await tables.OpenAsync(Source(), TactKeyStatus.None));
    }

    [Fact]
    public async Task Cancellation_stops_a_store_and_leaves_no_table()
    {
        var store = new InMemoryGeneratedStore();
        var tables = new GeneratedTables(store);
        using var cancel = new CancellationTokenSource();
        async IAsyncEnumerable<Db2Row> Rows([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
        {
            yield return new Db2Row(1, false, null, ["a"]);
            await cancel.CancelAsync();
            token.ThrowIfCancellationRequested();
            yield return new Db2Row(2, false, null, ["b"]);
        }
        var content = Content([]) with { Rows = Rows() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tables.StoreAsync(Source(), TactKeyStatus.None, content, cancel.Token));
        Assert.Null(await tables.OpenAsync(Source(), TactKeyStatus.None));
    }
}
