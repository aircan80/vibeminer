using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;
using VibeMiner.Infrastructure;
using Xunit;

namespace VibeMiner.Backend.Tests;

public sealed class TableUniqueRowTests
{
    private static readonly TableArtifactBinding Binding = new(new('a', 64), new('b', 64), new('c', 64), "provider-pin", "enUS", Wdc5TableReader.Revision, new('d', 64));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };
    private static readonly Wdc5TableHeader Header = new(1, "50238EC2", "9A2A4834", 1, 0, 8, [new("ID", "integer", true), new("Name", "string", false)]);

    [Fact]
    public async Task UniqueMissingAndAmbiguousIdsPreserveExactUnsignedAndCopyIdentity()
    {
        await using var h = new Harness();
        var artifact = await h.RowsArtifact([new(uint.MaxValue, true, 0, ["4294967295", "Copy name"]), new(0, false, null, ["0", "Zero"]), new(7, false, null, ["7", "First"]), new(7, true, 0, ["7", "Second"])]);
        var zero = await h.Rows.ReadUniqueAsync(artifact, 0, default); Assert.Equal(1, zero.MatchCount); Assert.Equal("Zero", zero.Row!.Values[1]);
        var max = await h.Rows.ReadUniqueAsync(artifact, uint.MaxValue, default); Assert.Equal(1, max.MatchCount); Assert.True(max.Row!.IsCopy); Assert.Equal(0u, max.Row.CopySourceId);
        var missing = await h.Rows.ReadUniqueAsync(artifact, 6, default); Assert.Equal(0, missing.MatchCount); Assert.Null(missing.Row);
        var duplicate = await h.Rows.ReadUniqueAsync(artifact, 7, default); Assert.Equal(2, duplicate.MatchCount); Assert.Null(duplicate.Row);
        var page = await h.Rows.ReadPageAsync(artifact, 0, 50, 7, default); Assert.Equal(2, page.TotalCount); Assert.Equal(["First", "Second"], page.Rows.Select(r => r.Values[1]));
    }

    [Fact]
    public async Task OneMillionDuplicateMatchesDoNotDecodeAnyMalformedRowJson()
    {
        await using var h = new Harness(); var artifact = await h.RawArtifact(1_000_000, 7, "{malformed row}");
        var duplicate = await h.Rows.ReadUniqueAsync(artifact, 7, default); Assert.Equal(1_000_000, duplicate.MatchCount); Assert.Null(duplicate.Row);
        var missing = await h.Rows.ReadUniqueAsync(artifact, 8, default); Assert.Equal(0, missing.MatchCount); Assert.Null(missing.Row);
        Assert.Equal("TABLE_ARTIFACT_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Rows.ReadPageAsync(artifact, 0, 1, 7, default))).Code);
    }

    [Fact]
    public async Task EmptyArtifactHasNoRowAndNoDirectoryRead()
    {
        await using var h = new Harness(); var artifact = await h.RowsArtifact([]);
        var result = await h.Rows.ReadUniqueAsync(artifact, 0, default); Assert.Equal(0, result.MatchCount); Assert.Null(result.Row);
    }

    [Theory]
    [InlineData("{")] [InlineData("null")]
    [InlineData("{\"id\":7,\"isCopy\":false,\"values\":null}")]
    [InlineData("{\"id\":7,\"isCopy\":false,\"values\":[\"7\"]}")]
    [InlineData("{\"id\":7,\"isCopy\":false,\"values\":[\"7\",null]}")]
    [InlineData("{\"id\":8,\"isCopy\":false,\"values\":[\"8\",\"different ID\"]}")]
    [InlineData("{\"id\":7,\"isCopy\":true,\"copySourceId\":0,\"values\":[\"7\",\"wrong copy flag\"]}")]
    [InlineData("{\"id\":7,\"isCopy\":false,\"copySourceId\":0,\"values\":[\"7\",\"unexpected copy source\"]}")]
    public async Task MalformedUniqueRowUsesSameTypedFramingAndSchemaValidationAsPages(string json)
    {
        await using var h = new Harness(); var artifact = await h.RawArtifact(1, 7, json);
        Assert.Equal("TABLE_ARTIFACT_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Rows.ReadUniqueAsync(artifact, 7, default))).Code);
        Assert.Equal("TABLE_ARTIFACT_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Rows.ReadPageAsync(artifact, 0, 1, 7, default))).Code);
    }

    [Fact]
    public async Task ValidUnavailableTextEvidenceIsPreservedAndInvalidEvidenceIsRefused()
    {
        await using var h = new Harness();
        var artifact = await h.RowsArtifact([new(7, false, null, ["7", ""], [new(1, TableTextEvidence.InvalidUtf8, "ff")])]);
        var result = await h.Rows.ReadUniqueAsync(artifact, 7, default);
        Assert.Equal("", result.Row!.Values[1]); Assert.Equal("ff", Assert.Single(result.Row.TextIssues!).RawBytesHex);
        var invalid = await h.RawArtifact(1, 7, "{\"id\":7,\"isCopy\":false,\"values\":[\"7\",\"not unavailable\"],\"textIssues\":[{\"columnIndex\":1,\"code\":\"DB2_TEXT_ENCODING_INVALID\",\"rawBytesHex\":\"ff\"}]}");
        Assert.Equal("TABLE_TEXT_EVIDENCE_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Rows.ReadUniqueAsync(invalid, 7, default))).Code);
    }

    [Theory]
    [InlineData("beforeRows")] [InlineData("zeroLength")] [InlineData("tooLong")] [InlineData("pastRows")] [InlineData("flags")]
    public async Task UniqueLookupRetainsDirectoryBoundsAndFlagValidation(string defect)
    {
        await using var h = new Harness();
        var artifact = await h.RawArtifact(1, 7, "{\"id\":7,\"isCopy\":false,\"values\":[\"7\",\"Good\"]}", entry =>
        {
            if (defect == "beforeRows") BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(16), 0);
            if (defect == "zeroLength") BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(12), 0);
            if (defect == "tooLong") BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(12), 262145);
            if (defect == "pastRows") BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(16), long.MaxValue);
            if (defect == "flags") BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(4), 2);
        });
        Assert.Equal("TABLE_ARTIFACT_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Rows.ReadUniqueAsync(artifact, 7, default))).Code);
    }

    [Theory]
    [InlineData(-1L, 0L)] [InlineData(0L, -1L)] [InlineData(1000001L, 0L)] [InlineData(0L, 1000001L)] [InlineData(long.MaxValue, long.MaxValue)]
    public async Task DescriptorRowBoundsRejectNegativeExcessAndOverflowBeforeOpening(long physical, long copies)
    {
        await using var h = new Harness(); var artifact = await h.RowsArtifact([]);
        File.Delete(h.PathFor(artifact)); // A bad descriptor must fail before trying the absent CAS object.
        var changed = artifact with { Completion = artifact.Completion with { PhysicalRows = physical, CopyRows = copies } };
        Assert.Equal("TABLE_ARTIFACT_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Rows.ReadUniqueAsync(changed, 7, default))).Code);
    }

    [Fact]
    public async Task UniqueCellsAndPublicRowExpansionKeepExistingBudgets()
    {
        await using var h = new Harness();
        var cell = await h.RawArtifact(1, 7, JsonSerializer.Serialize(new { id = 7, isCopy = false, values = new[] { "7", new string('x', 65537) } }, Json));
        Assert.Equal("TABLE_ARTIFACT_INVALID", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Rows.ReadUniqueAsync(cell, 7, default))).Code);
        var values = new[] { "7", new string('x', 65515), new string('x', 65515), new string('x', 65515), new string('x', 65515) };
        var json = JsonSerializer.Serialize(new { id = 7, isCopy = false, values }, Json); Assert.InRange(Encoding.UTF8.GetByteCount(json), 262081, 262144);
        var schema = Header with { Columns = [new("ID", "integer", true), new("A", "string", false), new("B", "string", false), new("C", "string", false), new("D", "string", false)] };
        var row = await h.RawArtifact(1, 7, json, schema: schema);
        Assert.Equal("TABLE_ROW_LIMIT", (await Assert.ThrowsAsync<DiscoveryException>(() => h.Rows.ReadUniqueAsync(row, 7, default))).Code);
    }

    [Fact]
    public async Task AlternatingExactDescriptorsCannotReuseDifferentRowsUnderTheSameBindingKey()
    {
        await using var h = new Harness(); var first = await h.RowsArtifact([new(7, false, null, ["7", "First"])]);
        var second = await h.RowsArtifact([new(7, false, null, ["7", "Second"])]);
        Assert.Equal(first.ContentKey, second.ContentKey); Assert.NotEqual(first.Sha256, second.Sha256);
        Assert.Equal("First", Assert.Single((await h.Rows.ReadPageAsync(first, 0, 1, null, default)).Rows).Values[1]);
        Assert.Equal("Second", (await h.Rows.ReadUniqueAsync(second, 7, default)).Row!.Values[1]);
        Assert.Equal("First", (await h.Rows.ReadUniqueAsync(first, 7, default)).Row!.Values[1]);
        Assert.Equal("First", Assert.Single((await h.Rows.ReadPageAsync(first, 0, 1, null, default)).Rows).Values[1]);
    }

    [Fact]
    public async Task CanceledUniqueWaitDoesNotDisturbPageOwnerAndGateRemainsUsableAfterItsFailure()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = new Harness(async (_, _, _, _) => { entered.TrySetResult(); await release.Task; });
        var corrupt = await h.RowsArtifact([new(7, false, null, ["7", "Original"])]);
        var bytes = await File.ReadAllBytesAsync(h.PathFor(corrupt)); bytes[40] = 1; await File.WriteAllBytesAsync(h.PathFor(corrupt), bytes);
        var first = h.Rows.ReadPageAsync(corrupt, 0, 1, null, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var stop = new CancellationTokenSource(); var queued = h.Rows.ReadUniqueAsync(corrupt, 7, stop.Token); Assert.False(queued.IsCompleted);
            stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(10))); Assert.False(first.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            Assert.Equal("LOCAL_OBJECT_CORRUPT", (await Assert.ThrowsAsync<DiscoveryException>(() => first.WaitAsync(TimeSpan.FromSeconds(10)))).Code);
        }
        var good = await h.RowsArtifact([new(8, false, null, ["8", "Recovered"])]);
        Assert.Equal("Recovered", (await h.Rows.ReadUniqueAsync(good, 8, default)).Row!.Values[1]);
    }

    [Fact]
    public async Task CancellationBeforeAdmissionAndDisposedReaderNeverReturnAStaleRow()
    {
        await using var h = new Harness(); var artifact = await h.RowsArtifact([new(7, false, null, ["7", "Good"])]);
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Rows.ReadUniqueAsync(artifact, 7, stop.Token));
        Assert.Equal(1, (await h.Rows.ReadUniqueAsync(artifact, 7, default)).MatchCount);
        await h.Rows.DisposeAsync(); await Assert.ThrowsAsync<ObjectDisposedException>(() => h.Rows.ReadUniqueAsync(artifact, 7, default));
    }

    private sealed class Harness : IAsyncDisposable
    {
        internal readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "vm-table-unique-" + Guid.NewGuid().ToString("N"));
        internal readonly VerifiedObjectStore Objects; internal readonly TableRowArtifacts Rows;
        internal Harness(Func<string, long, string, CancellationToken, Task>? corruption = null) { Objects = new(DirectoryPath, corruption); Rows = new(Objects); }
        internal string PathFor(TableRowsArtifact artifact) => Path.Combine(DirectoryPath, "objects-v1", artifact.Sha256[..2], artifact.Sha256[2..]);
        internal async Task<TableRowsArtifact> RowsArtifact(Db2Row[] values)
        {
            var copies = values.Count(v => v.IsCopy); var header = Header with { DeclaredRecords = values.Length - copies, DeclaredCopyRecords = copies };
            await using var writer = await Rows.CreateWriterAsync(Binding, default); await writer.HeaderAsync(header, default);
            foreach (var row in values) await writer.RowAsync(row, default);
            return await writer.CompleteAsync(new(header.DeclaredRecords, values.Length - copies, copies, 0, 0, 0, "complete", 100, values.Sum(v => v.TextIssues?.Count ?? 0)), default);
        }
        internal async Task<TableRowsArtifact> RawArtifact(int count, uint id, string rowJson, Action<byte[]>? directoryMutation = null, Wdc5TableHeader? schema = null)
        {
            var header = (schema ?? Header) with { DeclaredRecords = count }; var completion = new Wdc5Completion(count, count, 0, 0, 0, 0, "complete", 100);
            var row = Encoding.UTF8.GetBytes(rowJson); var metadata = JsonSerializer.SerializeToUtf8Bytes(new { binding = Binding, header, completion }, Json);
            var metadataOffset = 64L + row.Length; var indexOffset = metadataOffset + metadata.Length; var prefix = new byte[64];
            "VMTR001\n"u8.CopyTo(prefix); BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(8), 1); BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(12), metadata.Length);
            BinaryPrimitives.WriteInt64LittleEndian(prefix.AsSpan(16), metadataOffset); BinaryPrimitives.WriteInt64LittleEndian(prefix.AsSpan(24), indexOffset); BinaryPrimitives.WriteInt64LittleEndian(prefix.AsSpan(32), count);
            var entry = new byte[32]; BinaryPrimitives.WriteUInt32LittleEndian(entry, id); BinaryPrimitives.WriteInt32LittleEndian(entry.AsSpan(12), row.Length); BinaryPrimitives.WriteInt64LittleEndian(entry.AsSpan(16), 64);
            directoryMutation?.Invoke(entry); var block = new byte[32 * 1024];
            for (var i = 0; i < 1024; i++) entry.CopyTo(block, i * 32);
            string path; await using (var file = await Objects.CreateStagingFileAsync(default))
            {
                path = file.Name; await file.WriteAsync(prefix); await file.WriteAsync(row); await file.WriteAsync(metadata);
                for (var written = 0; written < count; written += 1024) await file.WriteAsync(block.AsMemory(0, Math.Min(1024, count - written) * 32));
            }
            var length = indexOffset + count * 32L; var sha = await Objects.PublishFileAsync(path, length, default);
            return new(TableRowArtifacts.ContentKey(Binding), sha, length, Binding, header, completion);
        }
        public async ValueTask DisposeAsync() { await Rows.DisposeAsync(); await Objects.DisposeAsync(); Directory.Delete(DirectoryPath, true); }
    }
}
