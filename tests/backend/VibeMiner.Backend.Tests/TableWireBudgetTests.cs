using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class TableWireBudgetTests
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions ArtifactJson = new(WireJson) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly TableArtifactBinding Binding = new(new('a', 64), new('b', 64), new('c', 64), "provider", "enUS", "reader", new('d', 64));
    private static readonly Wdc5TableHeader Header = new(5, "50238EC2", "996192AA", 1, 2, 4,
        [new("ID", "integer", true), new("RawA", "string", false), new("RawB", "string", false), new("A", "string", false), new("B", "string", false)]);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Writer_refuses_a_near_limit_artifact_row_whose_final_wire_shape_is_too_large(bool copy, bool evidence)
    {
        await using var h = new Harness(); var row = SizedRow(2147483647, copy, evidence, TableRowProjection.MaxRowBytes - 1);
        Assert.Equal(TableRowProjection.MaxRowBytes - 1, Bytes(row, ArtifactJson));
        Assert.True(Bytes(UncheckedRow(row), WireJson) > TableRowProjection.MaxRowBytes);
        await using (var writer = await h.Rows.CreateWriterAsync(Binding, default))
        {
            await writer.HeaderAsync(Header, default);
            var error = await Assert.ThrowsAsync<DiscoveryException>(async () => await writer.RowAsync(row, default));
            Assert.Equal("TABLE_ROW_LIMIT", error.Code);
        }
        Assert.Empty(Directory.GetFiles(Path.Combine(h.Directory, "staging-v1")));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Exact_wire_row_boundary_remains_accepted_with_nulls_text_evidence_and_copy_IDs(bool copy, bool evidence)
    {
        await using var h = new Harness();
        var probe = SizedRow(2147483647, copy, evidence, TableRowProjection.MaxRowBytes - 128);
        var growth = Bytes(UncheckedRow(probe), WireJson) - Bytes(probe, ArtifactJson);
        var row = SizedRow(2147483647, copy, evidence, TableRowProjection.MaxRowBytes - growth);
        Assert.Equal(TableRowProjection.MaxRowBytes, Bytes(UncheckedRow(row), WireJson));
        await using var writer = await h.Rows.CreateWriterAsync(Binding, default); await writer.HeaderAsync(Header, default); await writer.RowAsync(row, default);
        var artifact = await writer.CompleteAsync(Completion([row]), default);
        var page = await h.Rows.ReadPageAsync(artifact, 0, 50, null, default);
        Assert.Equal(TableRowProjection.MaxRowBytes, Bytes(TableRowProjection.Row(Assert.Single(page.Rows)), WireJson));
    }

    [Fact]
    public async Task Full_page_envelope_trims_rows_and_continues_at_the_first_unreturned_ordinal()
    {
        await using var h = new Harness();
        var first = SizedRow(2147483647, true, true, 262120);
        var second = SizedRow(2147483648, true, true, 262120);
        var physical = new Db2Row(uint.MaxValue, false, null, [uint.MaxValue.ToString(CultureInfo.InvariantCulture), "", "", "source", ""]);
        await using var writer = await h.Rows.CreateWriterAsync(Binding, default); await writer.HeaderAsync(Header, default);
        foreach (var row in new[] { physical, second, first }) await writer.RowAsync(row, default);
        var artifact = await writer.CompleteAsync(Completion([physical, second, first]), default);
        var request = Request(artifact); var raw = await h.Rows.ReadPageAsync(artifact, 0, 50, null, default);
        Assert.Equal(2, raw.Rows.Count);
        var oversized = new TableDataPage(request.ProductId, request.BuildIdentity, request.SourceRegion, request.TableId, request.ResearchLocale,
            request.ArtifactId, raw.Rows.Select(UncheckedRow).ToArray(), "3", "2");
        Assert.True(Bytes(oversized, WireJson) > TableRowProjection.MaxPageBytes);
        var seen = new List<TableDataRow>(); string? after = null;
        do
        {
            var start = after is null ? 0 : long.Parse(after, CultureInfo.InvariantCulture);
            var rows = await h.Rows.ReadPageAsync(artifact, start, 50, null, default);
            var page = TableRowProjection.Page(request with { After = after }, rows, default);
            Assert.InRange(Bytes(page, WireJson), 1, TableRowProjection.MaxPageBytes);
            Assert.All(page.Rows, row => Assert.InRange(Bytes(row, WireJson), 1, TableRowProjection.MaxRowBytes));
            Assert.NotEmpty(page.Rows); Assert.Equal("3", page.TotalCount);
            if (after is null) { Assert.Single(page.Rows); Assert.Equal("1", page.NextCursor); }
            seen.AddRange(page.Rows); after = page.NextCursor;
        } while (after is not null);
        Assert.Equal(["2147483647", "2147483648", "4294967295"], seen.Select(row => row.Id));
        Assert.All(seen.Take(2), row => { Assert.True(row.IsCopy); Assert.Equal("4294967295", row.CopySourceId); Assert.Equal(2, row.TextIssues!.Count); });
    }

    [Fact]
    public void Trimmed_filtered_page_cursor_uses_requested_after_and_filtered_total()
    {
        var rows = new[] { SizedRow(123, true, true, 262120), SizedRow(123, true, true, 262120) };
        var request = new TableRowsRequest("wow", new('a', 64), "us", "50238ec2", "enUS", new('b', 64), After: "17", RecordId: "123");
        var page = TableRowProjection.Page(request, new(rows, 19, null), default);
        Assert.Single(page.Rows); Assert.Equal("18", page.NextCursor); Assert.Equal("19", page.TotalCount);
        var last = TableRowProjection.Page(request with { After = "18" }, new([rows[1]], 19, null), default);
        Assert.Single(last.Rows); Assert.Null(last.NextCursor);
    }

    [Fact]
    public async Task Historical_artifact_oversize_row_is_a_typed_error_without_an_empty_nonadvancing_page()
    {
        await using var h = new Harness(); var row = SizedRow(2147483647, true, true, TableRowProjection.MaxRowBytes - 1);
        var artifact = await h.Historical(row);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var error = await Assert.ThrowsAsync<DiscoveryException>(() => h.Rows.ReadPageAsync(artifact, 0, 50, null, default));
            Assert.Equal("TABLE_ROW_LIMIT", error.Code);
        }
        Assert.Equal("TABLE_ROW_LIMIT", Assert.Throws<DiscoveryException>(() => TableRowProjection.Row(row)).Code);
        Assert.Equal("TABLE_ROW_LIMIT", Assert.Throws<DiscoveryException>(() => TableRowProjection.Page(Request(artifact), new([row], 1, null), default)).Code);
    }

    [Fact]
    public void An_unrepresentable_page_context_fails_instead_of_dropping_the_only_row_or_spinning()
    {
        var row = new Db2Row(1, false, null, ["1", "", "", "", ""]);
        var request = new TableRowsRequest(new('a', TableRowProjection.MaxPageBytes), new('b', 64), "us", "50238ec2", "enUS", new('c', 64));
        Assert.Equal("TABLE_PAGE_LIMIT", Assert.Throws<DiscoveryException>(() => TableRowProjection.Page(request, new([row], 1, null), default)).Code);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => TableRowProjection.Page(request, new([row], 1, null), cts.Token));
    }

    private static Db2Row SizedRow(uint id, bool copy, bool evidence, int artifactBytes)
    {
        IReadOnlyList<Db2TextIssue>? issues = evidence ? [new(1, TableTextEvidence.InvalidUtf8, new string('f', 65534)), new(2, TableTextEvidence.InvalidUtf8, new string('f', 65536))] : null;
        var values = new[] { id.ToString(CultureInfo.InvariantCulture), "", "", "", "" };
        var row = new Db2Row(id, copy, copy ? uint.MaxValue : null, values, issues);
        var remaining = artifactBytes - Bytes(row, ArtifactJson);
        var targets = evidence ? new[] { 3, 4 } : new[] { 1, 2, 3, 4 };
        foreach (var target in targets) { var count = Math.Min(65536, remaining); values[target] = new('x', count); remaining -= count; }
        Assert.Equal(0, remaining); Assert.Equal(artifactBytes, Bytes(row, ArtifactJson)); return row;
    }
    private static TableDataRow UncheckedRow(Db2Row row) => new(row.Id.ToString(CultureInfo.InvariantCulture), row.IsCopy,
        row.CopySourceId?.ToString(CultureInfo.InvariantCulture), row.Values, row.TextIssues?.Select(issue => new TableTextIssue(issue.ColumnIndex, issue.Code, issue.RawBytesHex)).ToArray());
    private static int Bytes<T>(T value, JsonSerializerOptions options) => JsonSerializer.SerializeToUtf8Bytes(value, options).Length;
    private static TableRowsRequest Request(TableRowsArtifact artifact) => new("wow", new('a', 64), "us", "50238ec2", "enUS", artifact.ContentKey);
    private static Wdc5Completion Completion(IReadOnlyList<Db2Row> rows) => new(1, rows.Count(row => !row.IsCopy), rows.Count(row => row.IsCopy), 0, 0, 0, "complete", 100,
        rows.Sum(row => row.TextIssues?.Count ?? 0));
    private sealed class Harness : IAsyncDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "vm-table-wire-" + Guid.NewGuid().ToString("N"));
        private VerifiedObjectStore objects;
        internal TableRowArtifacts Rows { get; }
        internal Harness() { objects = new(Directory); Rows = new(objects); }
        internal async Task<TableRowsArtifact> Historical(Db2Row row)
        {
            var rowBytes = JsonSerializer.SerializeToUtf8Bytes(row, ArtifactJson); var completion = Completion([row]);
            var metadata = JsonSerializer.SerializeToUtf8Bytes(new { binding = Binding, header = Header, completion }, WireJson);
            var metaOffset = 64 + rowBytes.Length; var indexOffset = metaOffset + metadata.Length;
            await using var staged = await objects.CreateStagingFileAsync(default);
            var prefix = new byte[64]; "VMTR001\n"u8.CopyTo(prefix); BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(8), 1);
            BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(12), metadata.Length); BinaryPrimitives.WriteInt64LittleEndian(prefix.AsSpan(16), metaOffset);
            BinaryPrimitives.WriteInt64LittleEndian(prefix.AsSpan(24), indexOffset); BinaryPrimitives.WriteInt64LittleEndian(prefix.AsSpan(32), 1);
            var entry = new byte[32]; BinaryPrimitives.WriteUInt32LittleEndian(entry, row.Id); BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), row.IsCopy ? 1u : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(8), row.CopySourceId ?? 0); BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(12), rowBytes.Length); BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(16), 64);
            await staged.WriteAsync(prefix); await staged.WriteAsync(rowBytes); await staged.WriteAsync(metadata); await staged.WriteAsync(entry);
            var length = staged.Length; var path = staged.Name; await staged.DisposeAsync();
            var sha = await objects.PublishFileAsync(path, length, default);
            return new(TableRowArtifacts.ContentKey(Binding), sha, length, Binding, Header, completion);
        }
        public async ValueTask DisposeAsync() { await Rows.DisposeAsync(); await objects.DisposeAsync(); System.IO.Directory.Delete(Directory, true); }
    }
}
