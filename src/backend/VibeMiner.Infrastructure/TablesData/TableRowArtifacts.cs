using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using VibeMiner.Application;
using VibeMiner.Domain;
using VibeMiner.Formats.Wow;

namespace VibeMiner.Infrastructure;

internal sealed record TableArtifactBinding(string SourceSha256, string DecodedSha256, string DefinitionSha256,
    string ProviderRevision, string Locale, string DecoderRevision, string SchemaSelectionSha256);
internal sealed record TableRowsArtifact(string ContentKey, string Sha256, long ByteLength,
    TableArtifactBinding Binding, Wdc5TableHeader Header, Wdc5Completion Completion);
internal sealed record TableArtifactPage(IReadOnlyList<Db2Row> Rows, long TotalCount, long? NextCursor);
internal sealed record TableUniqueRow(long MatchCount, Db2Row? Row);

/// <summary>One immutable row file, with a sorted fixed-width ID directory; no per-cell database state.</summary>
internal sealed class TableRowArtifacts(VerifiedObjectStore objects) : IAsyncDisposable
{
    // Opt-in only after callers release outer CPU scopes; the compatibility constructor stays ungated.
    private readonly AcquisitionResources? resources;
    private readonly IAcquisitionWorkContext? workContext;
    internal TableRowArtifacts(VerifiedObjectStore objects, AcquisitionResources resources, IAcquisitionWorkContext? workContext) : this(objects)
    { this.resources = resources ?? throw new ArgumentNullException(nameof(resources)); this.workContext = workContext; }
    internal const string Revision = "table-rows-v1";
    internal const int MaxRows = 1_000_000;
    private const int FixedHeader = 64, IndexBytes = 32, MaxRowBytes = 256 * 1024, MaxMetadataBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };
    private static readonly JsonSerializerOptions RowJson = new(Json) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private readonly SemaphoreSlim gate = new(1, 1);
    private FileStream? reading;
    private TableRowsArtifact? readingArtifact;
    private long readingIndex;
    private bool disposed;
    private sealed record Metadata(TableArtifactBinding Binding, Wdc5TableHeader Header, Wdc5Completion Completion);
    private readonly record struct Entry(uint Id, bool Copy, uint Source, int Length, long Offset, long Ordinal);

    // Definitions are long-lived (bundled, or one verified fetched copy per table), and the key is read on every row
    // page, so it is computed once per definition and build.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DbdDocument, System.Collections.Concurrent.ConcurrentDictionary<string, string>> Keys = new();

    internal static string SchemaSelectionKey(DbdDocument definition, string buildVersion, CancellationToken token = default)
    {
        var keys = Keys.GetValue(definition, _ => new(StringComparer.Ordinal));
        if (keys.TryGetValue(buildVersion, out var known)) return known;
        // Measured from the blocks, not taken from the parser, so no definition gets an unbounded key.
        if (DbdDefinitionReader.EstimateSelection(definition) > new DbdLimits().MaxSelectionBytes)
            throw new DiscoveryException("DEFINITION_LIMIT", "This definition is too large to select a layout from.");
        var key = ComputeSelectionKey(definition, buildVersion, token);
        if (keys.Count < 64) keys.TryAdd(buildVersion, key);
        return key;
    }

    private static string ComputeSelectionKey(DbdDocument definition, string buildVersion, CancellationToken token)
    {
        var selected = definition.SelectBuild(buildVersion, token: token);
        if (selected.Blocks.Count == 0)
        {
            // An unlisted build opens by layout (D080): the key is every layout-to-block choice for this build.
            var choices = definition.LayoutCandidates(buildVersion, token).Select(value => new { layout = value.LayoutHash, fields = value.Block.Fields }).ToArray();
            return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { match = "layout", choices }, Json)));
        }
        // Build selectors establish eligibility; only the selected interpretations form the reusable key.
        var interpretations = selected.Blocks.Select(block => JsonSerializer.Serialize(new
            { layouts = block.LayoutHashes.OrderBy(hash => hash, StringComparer.Ordinal).ToArray(), fields = block.Fields }, Json))
            .Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(interpretations, Json)));
    }

    internal static string ContentKey(TableArtifactBinding binding)
    {
        ValidateBinding(binding);
        return Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { revision = Revision, binding }, Json)));
    }

    internal async Task<TableRowsArtifact> BuildAsync(Stream decoded, DbdDocument definition, string buildVersion,
        TableArtifactBinding binding, Func<string, Task> progress, CancellationToken token, Wdc5Limits? limits = null)
    {
        if (binding.SchemaSelectionSha256 != SchemaSelectionKey(definition, buildVersion, token))
            throw Invalid("The table binding differs from the exact build-selected definition interpretations.");
        await using var writer = await CreateWriterAsync(binding, token, progress);
        var completion = await Wdc5TableReader.ReadAsync(decoded, definition, buildVersion, limits ?? new Wdc5Limits(), writer, token);
        await progress("Publishing verified table rows");
        return await writer.CompleteAsync(completion, token);
    }

    internal async Task<Writer> CreateWriterAsync(TableArtifactBinding binding, CancellationToken token, Func<string, Task>? progress = null)
    {
        using (resources is null ? null : await resources.EnterCpuAsync(workContext, token))
        { ValidateBinding(binding); token.ThrowIfCancellationRequested(); }
        var file = await objects.CreateStagingFileAsync(token);
        try { await file.WriteAsync(new byte[FixedHeader], token); return new(objects, file, binding, progress, resources, workContext); }
        catch { var path = file.Name; await file.DisposeAsync(); File.Delete(path); throw; }
    }

    internal sealed class Writer : IWdc5Consumer, IAsyncDisposable
    {
        private readonly VerifiedObjectStore objects;
        private readonly FileStream file;
        private readonly TableArtifactBinding binding;
        private readonly Func<string, Task>? progress;
        private readonly AcquisitionResources? resources;
        private readonly IAcquisitionWorkContext? workContext;
        private readonly List<Entry> entries = new();
        private Wdc5TableHeader? header;
        private bool completed, closed;
        private long textIssueCount, rawTextBytes;
        internal Writer(VerifiedObjectStore objects, FileStream file, TableArtifactBinding binding, Func<string, Task>? progress,
            AcquisitionResources? resources, IAcquisitionWorkContext? workContext)
        { this.objects = objects; this.file = file; this.binding = binding; this.progress = progress; this.resources = resources; this.workContext = workContext; }
        public async ValueTask HeaderAsync(Wdc5TableHeader value, CancellationToken token)
        {
            using (resources is null ? null : await resources.EnterCpuAsync(workContext, token))
            {
                token.ThrowIfCancellationRequested();
                if (closed || completed || header is not null || entries.Count != 0 || value.Columns.Count is < 1 or > 512 ||
                    value.Columns.Any(c => c.Name.Length is < 1 or > 144 || c.Kind is not ("integer" or "float" or "string")) ||
                    value.TableHash.Length != 8 || !value.TableHash.All(char.IsAsciiHexDigit) ||
                    value.LayoutHash.Length != 8 || !value.LayoutHash.All(char.IsAsciiHexDigit)) throw Invalid("The table row schema exceeds its supported bounds.");
                header = value with { Columns = Array.AsReadOnly(value.Columns.ToArray()) };
            }
            if (progress is not null) await progress("Decoding verified table records");
        }
        public async ValueTask RowAsync(Db2Row row, CancellationToken token)
        {
            byte[] bytes;
            using (resources is null ? null : await resources.EnterCpuAsync(workContext, token))
            {
                token.ThrowIfCancellationRequested();
                if (closed || completed || header is null || entries.Count >= MaxRows || row.Values.Count != header.Columns.Count ||
                    row.IsCopy != row.CopySourceId.HasValue || row.Values.Any(value => value is null || value.Length > 65536) || row.Values.Sum(value => (long)value.Length) > MaxRowBytes)
                    throw Invalid("A decoded table row exceeds its schema or row budget.");
                var rawBytes = ValidateTextEvidence(row, header);
                if (rawTextBytes > TableTextEvidence.MaximumEmittedRawBytes - rawBytes)
                    throw Invalid("The table raw text evidence exceeds its aggregate byte budget.");
                rawTextBytes += rawBytes; textIssueCount += row.TextIssues?.Count ?? 0;
                bytes = JsonSerializer.SerializeToUtf8Bytes(row, RowJson);
                if (bytes.Length > MaxRowBytes || file.Position + bytes.Length + (entries.Count + 1L) * IndexBytes + MaxMetadataBytes > VerifiedObjectStore.MaxObjectBytes)
                    throw Invalid("The table row artifact exceeds its bounded storage budget.");
                TableRowProjection.RequireRowBudget(row, bytes.Length);
                entries.Add(new(row.Id, row.IsCopy, row.CopySourceId ?? 0, bytes.Length, file.Position, entries.Count));
            }
            await file.WriteAsync(bytes, token);
            if (progress is not null && entries.Count % 4096 == 0) await progress($"Decoded {entries.Count:N0} table records");
        }
        internal async Task<TableRowsArtifact> CompleteAsync(Wdc5Completion completion, CancellationToken token)
        {
            byte[] metadata;
            using (resources is null ? null : await resources.EnterCpuAsync(workContext, token))
            {
                token.ThrowIfCancellationRequested();
                if (closed || completed || header is null || completion.PhysicalRows < 0 || completion.CopyRows < 0 ||
                    completion.PhysicalRows + completion.CopyRows != entries.Count || completion.TextIssueCount != textIssueCount ||
                    entries.LongCount(e => e.Copy) != completion.CopyRows || completion.Scope is not ("complete" or "unencryptedSections"))
                    throw Invalid("The complete table row proof differs from the staged row counts.");
                metadata = JsonSerializer.SerializeToUtf8Bytes(new Metadata(binding, header, completion), Json);
                if (metadata.Length > MaxMetadataBytes) throw Invalid("The table artifact metadata exceeds its bound.");
            }
            var metadataOffset = file.Position; await file.WriteAsync(metadata, token); var indexOffset = file.Position;
            // Existing-unit exception: one in-place <=1m-entry sort, not a cooperative merge/parser rewrite.
            using (resources is null ? null : await resources.EnterCpuAsync(workContext, token))
            {
                token.ThrowIfCancellationRequested(); var comparisons = 0;
                try { entries.Sort((a, b) => { if ((++comparisons & 4095) == 0) token.ThrowIfCancellationRequested();
                    var n = a.Id.CompareTo(b.Id); if (n == 0) n = a.Copy.CompareTo(b.Copy); return n == 0 ? a.Ordinal.CompareTo(b.Ordinal) : n; }); }
                catch (InvalidOperationException ex) when (ex.InnerException is OperationCanceledException) { token.ThrowIfCancellationRequested(); throw; }
            }
            var buffer = new byte[IndexBytes * 2048]; var ordinal = 0;
            while (ordinal < entries.Count)
            {
                var used = 0;
                using (resources is null ? null : await resources.EnterCpuAsync(workContext, token))
                    while (ordinal < entries.Count && used < buffer.Length)
                    { token.ThrowIfCancellationRequested(); WriteEntry(buffer.AsSpan(used, IndexBytes), entries[ordinal++]); used += IndexBytes; }
                await file.WriteAsync(buffer.AsMemory(0, used), token);
                if (resources is not null) { await Task.Yield(); token.ThrowIfCancellationRequested(); }
            }
            var length = file.Position; var prefix = new byte[FixedHeader]; string contentKey;
            using (resources is null ? null : await resources.EnterCpuAsync(workContext, token))
            {
                "VMTR001\n"u8.CopyTo(prefix);
                BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(8), 1);
                BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(12), metadata.Length);
                BinaryPrimitives.WriteInt64LittleEndian(prefix.AsSpan(16), metadataOffset);
                BinaryPrimitives.WriteInt64LittleEndian(prefix.AsSpan(24), indexOffset);
                BinaryPrimitives.WriteInt64LittleEndian(prefix.AsSpan(32), entries.Count);
                contentKey = ContentKey(binding);
            }
            file.Position = 0; await file.WriteAsync(prefix, token); await file.FlushAsync(token);
            var path = file.Name; await file.DisposeAsync(); closed = true;
            var sha = resources is null ? await objects.PublishFileAsync(path, length, token) :
                await objects.PublishFileAsync(path, length, resources, workContext, token);
            completed = true; return new(contentKey, sha, length, binding, header!, completion);
        }
        public async ValueTask DisposeAsync()
        { if (!closed) { await file.DisposeAsync(); closed = true; } if (!completed) File.Delete(file.Name); }
    }

    internal async Task<TableArtifactPage> ReadPageAsync(TableRowsArtifact artifact, long after, int limit, uint? exactId, CancellationToken token)
    {
        if (after < 0 || limit is < 1 or > 50) throw new DiscoveryException("INVALID_REQUEST", "Choose a bounded exact-ID table row page.");
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this); await OpenAsync(artifact, token);
            var count = checked(artifact.Completion.PhysicalRows + artifact.Completion.CopyRows);
            var first = exactId is { } id ? await BoundAsync(id, false, count, token) : 0;
            var end = exactId is { } last ? await BoundAsync(last, true, count, token) : count;
            var total = end - first;
            if (after > total) throw new DiscoveryException("INVALID_CURSOR", "The table cursor exceeds this exact artifact and ID filter.");
            var rows = new List<Db2Row>(); var pageBytes = 0; var take = (int)Math.Min(limit, total - after);
            for (var i = 0; i < take; i++)
            {
                var entry = await ReadEntryAsync(first + after + i, token);
                ValidateRowRegion(entry);
                if (pageBytes + entry.Length > 512 * 1024) break;
                pageBytes += entry.Length;
                rows.Add(await ReadRowAsync(artifact, entry, token));
            }
            var next = after + rows.Count; return new(rows.AsReadOnly(), total, next < total ? next : null);
        }
        finally { gate.Release(); }
    }

    internal async Task<TableUniqueRow> ReadUniqueAsync(TableRowsArtifact artifact, uint id, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this); await OpenAsync(artifact, token);
            var count = artifact.Completion.PhysicalRows + artifact.Completion.CopyRows;
            var first = await BoundAsync(id, false, count, token);
            var end = await BoundAsync(id, true, count, token); var matches = end - first;
            if (matches < 0 || matches > count) throw Invalid("The exact table row range is invalid.");
            if (matches != 1) return new(matches, null);
            var entry = await ReadEntryAsync(first, token);
            if (entry.Id != id) throw Invalid("The unique table row differs from the requested ID.");
            return new(1, await ReadRowAsync(artifact, entry, token));
        }
        finally { gate.Release(); }
    }

    private void ValidateRowRegion(Entry entry)
    {
        if (entry.Offset < FixedHeader || entry.Length is < 1 or > MaxRowBytes || entry.Offset > readingIndex - entry.Length)
            throw Invalid("A table row directory points outside the immutable row region.");
    }
    private async Task<Db2Row> ReadRowAsync(TableRowsArtifact artifact, Entry entry, CancellationToken token)
    {
        ValidateRowRegion(entry);
        var bytes = new byte[entry.Length]; reading!.Position = entry.Offset; await reading.ReadExactlyAsync(bytes, token);
        using var cpu = resources is null ? null : await resources.EnterCpuAsync(workContext, token);
        token.ThrowIfCancellationRequested();
        Db2Row row;
        try { row = JsonSerializer.Deserialize<Db2Row>(bytes, RowJson) ?? throw Invalid("A table row is empty."); }
        catch (JsonException) { throw Invalid("A table row has malformed immutable framing."); }
        if (row.Id != entry.Id || row.IsCopy != entry.Copy || row.CopySourceId != (entry.Copy ? entry.Source : null) || row.Values is null ||
            row.Values.Count != artifact.Header.Columns.Count || row.Values.Any(v => v is null || v.Length > 65536))
            throw Invalid("A table row differs from its immutable directory or schema.");
        _ = ValidateTextEvidence(row, artifact.Header);
        TableRowProjection.RequireRowBudget(row, entry.Length);
        return row with { Values = Array.AsReadOnly(row.Values.ToArray()),
            TextIssues = row.TextIssues is null ? null : Array.AsReadOnly(row.TextIssues.ToArray()) };
    }

    private async Task OpenAsync(TableRowsArtifact artifact, CancellationToken token)
    {
        using (resources is null ? null : await resources.EnterCpuAsync(workContext, token))
        {
            ValidateBinding(artifact.Binding);
            if (artifact.ContentKey != ContentKey(artifact.Binding) || artifact.Completion.PhysicalRows < 0 || artifact.Completion.CopyRows < 0 ||
                artifact.Completion.PhysicalRows > MaxRows || artifact.Completion.CopyRows > MaxRows - artifact.Completion.PhysicalRows) throw Invalid("The table artifact descriptor is invalid.");
            if (readingArtifact is not null && reading is not null && JsonSerializer.Serialize(readingArtifact, Json) == JsonSerializer.Serialize(artifact, Json)) return;
        }
        if (reading is not null) { await reading.DisposeAsync(); reading = null; readingArtifact = null; }
        var stream = resources is null ? await objects.OpenVerifiedAsync(artifact.Sha256, artifact.ByteLength, token) :
            await objects.OpenVerifiedAsync(artifact.Sha256, artifact.ByteLength, resources, workContext, token);
        try
        {
            var prefix = new byte[FixedHeader]; await stream.ReadExactlyAsync(prefix, token);
            int metadataLength; long metadataOffset, indexOffset;
            using (resources is null ? null : await resources.EnterCpuAsync(workContext, token))
            {
                metadataLength = BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(12));
                metadataOffset = BinaryPrimitives.ReadInt64LittleEndian(prefix.AsSpan(16));
                indexOffset = BinaryPrimitives.ReadInt64LittleEndian(prefix.AsSpan(24));
                var count = BinaryPrimitives.ReadInt64LittleEndian(prefix.AsSpan(32));
                if (!prefix.AsSpan(0, 8).SequenceEqual("VMTR001\n"u8) || BinaryPrimitives.ReadInt32LittleEndian(prefix.AsSpan(8)) != 1 ||
                    prefix.AsSpan(40).ContainsAnyExcept((byte)0) || metadataLength is < 1 or > MaxMetadataBytes || metadataOffset < FixedHeader ||
                    indexOffset != metadataOffset + metadataLength || count != artifact.Completion.PhysicalRows + artifact.Completion.CopyRows ||
                    indexOffset > artifact.ByteLength || artifact.ByteLength - indexOffset != count * IndexBytes)
                    throw Invalid("The table artifact has invalid bounded geometry.");
            }
            stream.Position = metadataOffset; var bytes = new byte[metadataLength]; await stream.ReadExactlyAsync(bytes, token);
            using (resources is null ? null : await resources.EnterCpuAsync(workContext, token))
            {
                var expected = JsonSerializer.SerializeToUtf8Bytes(new Metadata(artifact.Binding, artifact.Header, artifact.Completion), Json);
                if (!bytes.AsSpan().SequenceEqual(expected)) throw Invalid("The table artifact differs from its exact interpretation descriptor.");
            }
            reading = stream; readingArtifact = artifact; readingIndex = indexOffset;
        }
        catch { await stream.DisposeAsync(); throw; }
    }
    private async Task<long> BoundAsync(uint id, bool upper, long count, CancellationToken token)
    {
        long lo = 0, hi = count;
        while (lo < hi) { token.ThrowIfCancellationRequested(); var mid = lo + (hi - lo) / 2; var entry = await ReadEntryAsync(mid, token);
            if (entry.Id < id || upper && entry.Id == id) lo = mid + 1; else hi = mid; }
        return lo;
    }
    private async Task<Entry> ReadEntryAsync(long ordinal, CancellationToken token)
    {
        var b = new byte[IndexBytes]; reading!.Position = readingIndex + ordinal * IndexBytes; await reading.ReadExactlyAsync(b, token);
        using var cpu = resources is null ? null : await resources.EnterCpuAsync(workContext, token);
        token.ThrowIfCancellationRequested();
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)); if (flags > 1) throw Invalid("The table row directory has unsupported flags.");
        return new(BinaryPrimitives.ReadUInt32LittleEndian(b), flags == 1, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(8)),
            BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(12)), BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(16)), BinaryPrimitives.ReadInt64LittleEndian(b.AsSpan(24)));
    }
    private static void WriteEntry(Span<byte> b, Entry e)
    { BinaryPrimitives.WriteUInt32LittleEndian(b, e.Id); BinaryPrimitives.WriteUInt32LittleEndian(b[4..], e.Copy ? 1u : 0u);
        BinaryPrimitives.WriteUInt32LittleEndian(b[8..], e.Source); BinaryPrimitives.WriteInt32LittleEndian(b[12..], e.Length);
        BinaryPrimitives.WriteInt64LittleEndian(b[16..], e.Offset); BinaryPrimitives.WriteInt64LittleEndian(b[24..], e.Ordinal); }
    private static long ValidateTextEvidence(Db2Row row, Wdc5TableHeader header)
    {
        if (row.TextIssues is null || row.TextIssues.Count == 0) return 0;
        return TableTextEvidence.Validate(row.TextIssues.Select(issue =>
            issue is null ? null! : new TableTextIssue(issue.ColumnIndex, issue.Code, issue.RawBytesHex)).ToArray(),
            header.Columns.Select(column => new TableDataColumn(column.Name, column.Kind, column.IsId, null)).ToArray(), row.Values);
    }

    private static void ValidateBinding(TableArtifactBinding b)
    {
        if (new[] { b.SourceSha256, b.DecodedSha256, b.DefinitionSha256, b.SchemaSelectionSha256 }.Any(v => v is null || v.Length != 64 || v.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))) ||
            b.ProviderRevision is null || b.ProviderRevision.Length is < 1 or > 128 || b.DecoderRevision is null || b.DecoderRevision.Length is < 1 or > 128 ||
            b.Locale is null || b.Locale.Length is < 1 or > 16 || b.Locale.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw Invalid("The exact table interpretation binding is invalid.");
    }
    private static DiscoveryException Invalid(string message) => new("TABLE_ARTIFACT_INVALID", message);
    public async ValueTask DisposeAsync()
    { await gate.WaitAsync(); try { if (disposed) return; disposed = true; if (reading is not null) await reading.DisposeAsync(); reading = null; readingArtifact = null; } finally { gate.Release(); } }
}
