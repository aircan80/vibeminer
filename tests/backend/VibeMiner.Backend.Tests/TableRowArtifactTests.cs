using System.Security.Cryptography;
using System.Text;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class TableRowArtifactTests
{
    private static readonly TableArtifactBinding Binding = new(new('a', 64), new('b', 64), new('c', 64), "provider-pin", "enUS", Wdc5TableReader.Revision, new('d', 64));
    private static readonly Wdc5TableHeader Header = new(1, "50238EC2", "9A2A4834", 3, 1, 8,
        [new("ID", "integer", true), new("Exact", "integer", false)]);
    private static readonly Wdc5Completion Completion = new(3, 3, 1, 0, 0, 0, "complete", 100);

    [Fact]
    public async Task Durable_rows_sort_unsigned_ids_and_keep_copies_and_exact_values_across_restart()
    {
        await using var h = new Harness(); var artifact = await h.Build();
        await using var reader = new TableRowArtifacts(h.Objects);
        var first = await reader.ReadPageAsync(artifact, 0, 2, null, default);
        Assert.Equal(4, first.TotalCount); Assert.Equal(2, first.NextCursor);
        Assert.Equal([1u, 2u], first.Rows.Select(r => r.Id)); Assert.False(first.Rows[1].IsCopy);
        Assert.Equal("18446744073709551615", first.Rows[0].Values[1]);
        var next = await reader.ReadPageAsync(artifact, first.NextCursor!.Value, 2, null, default);
        Assert.Null(next.NextCursor); Assert.True(next.Rows[0].IsCopy); Assert.Equal(1u, next.Rows[0].CopySourceId);
        Assert.Equal(uint.MaxValue, next.Rows[1].Id);
        var exact = await reader.ReadPageAsync(artifact, 0, 50, 2, default);
        Assert.Equal(2, exact.TotalCount); Assert.Equal([false, true], exact.Rows.Select(r => r.IsCopy));
        Assert.Empty((await reader.ReadPageAsync(artifact, 0, 50, 3, default)).Rows);
    }

    [Fact]
    public async Task Artifact_identity_binds_interpretation_and_query_rejects_stale_geometry_or_cursor()
    {
        await using var h = new Harness(); var artifact = await h.Build();
        Assert.NotEqual(artifact.ContentKey, TableRowArtifacts.ContentKey(Binding with { Locale = "deDE" }));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Rows.ReadPageAsync(artifact, 5, 2, null, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Rows.ReadPageAsync(artifact, 0, 51, null, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Rows.ReadPageAsync(artifact with { Binding = Binding with { DefinitionSha256 = new('e', 64) } }, 0, 2, null, default));
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Rows.ReadPageAsync(artifact with { Completion = Completion with { PhysicalRows = 2 } }, 0, 2, null, default));
    }

    [Fact]
    public async Task Provisional_rows_are_not_published_on_incomplete_or_cancelled_parse()
    {
        await using var h = new Harness();
        await using (var writer = await h.Rows.CreateWriterAsync(Binding, default))
        {
            await writer.HeaderAsync(Header, default); await writer.RowAsync(new(1, false, null, ["1", "10"]), default);
            await Assert.ThrowsAsync<DiscoveryException>(() => writer.CompleteAsync(Completion, default));
        }
        using var cts = new CancellationTokenSource();
        await using (var writer = await h.Rows.CreateWriterAsync(Binding, default))
        {
            await writer.HeaderAsync(Header, default); cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await writer.RowAsync(new(1, false, null, ["1", "10"]), cts.Token));
        }
        Assert.Empty(Directory.GetFiles(Path.Combine(h.Directory, "staging-v1")));
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(h.Directory, "objects-v1"), "*", SearchOption.AllDirectories), path => Path.GetFileName(path) != ".owner");
    }

    [Fact]
    public async Task Actual_partial_scope_and_exclusions_survive_without_becoming_complete()
    {
        await using var h = new Harness(); await using var writer = await h.Rows.CreateWriterAsync(Binding, default);
        await writer.HeaderAsync(Header, default); await writer.RowAsync(new(7, false, null, ["7", "42"]), default);
        var partial = new Wdc5Completion(3, 1, 0, 1, 2, 1, "unencryptedSections", 100);
        var artifact = await writer.CompleteAsync(partial, default);
        Assert.Equal("unencryptedSections", artifact.Completion.Scope); Assert.Equal(2, artifact.Completion.ExcludedPhysicalRecords);
        Assert.Single((await h.Rows.ReadPageAsync(artifact, 0, 50, null, default)).Rows);
    }

    [Fact]
    public async Task Fresh_reader_rejects_mutated_cas_bytes_and_cancelled_reads()
    {
        await using var h = new Harness(); var artifact = await h.Build();
        var path = Path.Combine(h.Directory, "objects-v1", artifact.Sha256[..2], artifact.Sha256[2..]);
        var bytes = await File.ReadAllBytesAsync(path); bytes[70] ^= 1; await File.WriteAllBytesAsync(path, bytes);
        await Assert.ThrowsAsync<DiscoveryException>(() => h.Rows.ReadPageAsync(artifact, 0, 50, null, default));
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Rows.ReadPageAsync(artifact, 0, 50, null, cts.Token));
    }

    [Fact]
    public async Task Large_cells_reduce_page_size_without_losing_cursor_progress()
    {
        await using var h = new Harness(); await using var writer = await h.Rows.CreateWriterAsync(Binding, default);
        var header = Header with { DeclaredCopyRecords = 0, Columns = [new("ID", "integer", true), new("A", "string", false), new("B", "string", false), new("C", "string", false), new("D", "string", false)] };
        await writer.HeaderAsync(header, default); var text = new string('x', 60000);
        for (uint i = 1; i <= 3; i++) await writer.RowAsync(new(i, false, null, [i.ToString(), text, text, text, text]), default);
        var artifact = await writer.CompleteAsync(Completion with { PhysicalRows = 3, CopyRows = 0 }, default);
        var page = await h.Rows.ReadPageAsync(artifact, 0, 50, null, default);
        Assert.Equal(2, page.Rows.Count); Assert.Equal(2, page.NextCursor); Assert.Equal(3, page.TotalCount);
        var last = await h.Rows.ReadPageAsync(artifact, page.NextCursor!.Value, 50, null, default);
        Assert.Single(last.Rows); Assert.Equal(3u, last.Rows[0].Id); Assert.Null(last.NextCursor);
    }

    [Fact]
    public async Task Async_header_progress_is_awaited_and_row_schema_failures_stay_provisional()
    {
        await using var h = new Harness(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var writer = await h.Rows.CreateWriterAsync(Binding, default, async _ => { entered.SetResult(); await release.Task; });
        var header = writer.HeaderAsync(Header, default).AsTask(); await entered.Task; Assert.False(header.IsCompleted);
        release.SetResult(); await header;
        await Assert.ThrowsAsync<DiscoveryException>(async () => await writer.RowAsync(new(1, true, null, ["1", "2"]), default));
        await Assert.ThrowsAsync<DiscoveryException>(async () => await writer.RowAsync(new(1, false, null, ["1"]), default));
    }

    [Fact]
    public void Reuse_key_tracks_selected_field_semantics_and_ignores_build_selector_text()
    {
        var first = DbdDefinitionReader.Parse("COLUMNS\nint ID\nint Value\n\nLAYOUT AABBCCDD\nBUILD 1.0.0.1\nID<u32>\nValue<u32>\n\nLAYOUT AABBCCDD\nBUILD 1.0.0.2\nID<u32>\nValue<u16>\n"u8);
        Assert.NotEqual(TableRowArtifacts.SchemaSelectionKey(first, "1.0.0.1"), TableRowArtifacts.SchemaSelectionKey(first, "1.0.0.2"));
        var same = DbdDefinitionReader.Parse("COLUMNS\nint ID\nint Value\n\nLAYOUT AABBCCDD\nBUILD 1.0.0.1, 1.0.0.2\nID<u32>\nValue<u32>\n"u8);
        Assert.Equal(TableRowArtifacts.SchemaSelectionKey(same, "1.0.0.1"), TableRowArtifacts.SchemaSelectionKey(same, "1.0.0.2"));
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "vm-table-rows-" + Guid.NewGuid().ToString("N"));
        internal VerifiedObjectStore Objects { get; }
        internal TableRowArtifacts Rows { get; }
        internal Harness() { Objects = new(Directory); Rows = new(Objects); }
        internal async Task<TableRowsArtifact> Build()
        {
            await using var writer = await Rows.CreateWriterAsync(Binding, default); await writer.HeaderAsync(Header, default);
            foreach (var row in new Db2Row[] { new(uint.MaxValue, false, null, [uint.MaxValue.ToString(), "3"]),
                new(2, true, 1, ["2", "10"]), new(2, false, null, ["2", "20"]), new(1, false, null, ["1", "18446744073709551615"]) })
                await writer.RowAsync(row, default);
            return await writer.CompleteAsync(Completion, default);
        }
        public async ValueTask DisposeAsync() { await Rows.DisposeAsync(); await Objects.DisposeAsync(); if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
    }
}
